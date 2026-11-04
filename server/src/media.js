// Serving clip videos and thumbnails from R2, with as few R2 reads as possible.
//
// The Durable Object that owns a clip checks who may see it, then answers with a pointer: the
// R2 key, its type, the cache policy and an ETag naming this upload. The Worker serves the bytes
// itself, so the Durable Object isn't kept busy (and billed) while a video streams, and:
//   - a browser that already has this upload gets 304 Not Modified, with no R2 read at all;
//   - on a custom domain, the first request in a Cloudflare location reads the whole object
//     once into the Cache API and every later one there (any range of it) comes from the cache;
//   - on *.workers.dev, where the Cache API does nothing, each request reads R2 directly.
// The cache is only consulted after the Durable Object said yes, so a clip made private or
// removed stops being served at once; its cached bytes just age out.

const POINTER = "x-media-key";
// Kept in a location's cache this long, at most, after the last read there.
const CACHE_SECONDS = 7 * 24 * 3600;
// Cloudflare caches objects up to 512 MB; uploads are at most 100 MB.
const CACHE_MAX_BYTES = 512 * 1024 * 1024;

export function mediaPointer(key, { type, cacheControl, version, size }) {
  const headers = new Headers({ [POINTER]: key, "content-type": type, "cache-control": cacheControl });
  if (version) headers.set("etag", `"${version}"`);
  if (Number.isFinite(size) && size > 0) headers.set("x-media-size", String(size));
  return new Response(null, { status: 200, headers });
}

export function isMediaPointer(response) {
  return response.headers.has(POINTER);
}

function cacheable(url) {
  return typeof caches !== "undefined" && caches?.default && !url.hostname.endsWith(".workers.dev");
}

// The headers a client gets: what the Durable Object decided, plus the body's own.
function answer(body, status, pointer, extra = {}) {
  const headers = new Headers();
  for (const name of ["content-type", "cache-control", "etag"]) {
    const value = pointer.headers.get(name);
    if (value) headers.set(name, value);
  }
  headers.set("accept-ranges", "bytes");
  headers.set("x-content-type-options", "nosniff");
  for (const [name, value] of Object.entries(extra)) if (value) headers.set(name, value);
  return new Response(body, { status, headers });
}

function fromCache(hit, pointer) {
  return answer(hit.body, hit.status, pointer, {
    "content-type": hit.headers.get("content-type"),
    "content-length": hit.headers.get("content-length"),
    "content-range": hit.headers.get("content-range"),
  });
}

function fromR2(object, request, pointer) {
  const type = object.httpMetadata?.contentType;
  if (object.range && request.headers.has("range")) {
    const offset = object.range.offset ?? (object.range.suffix !== undefined ? object.size - object.range.suffix : 0);
    const length = object.range.length ?? object.size - offset;
    return answer(object.body, 206, pointer, {
      "content-range": `bytes ${offset}-${offset + length - 1}/${object.size}`,
      "content-length": String(length),
      "content-type": type,
    });
  }
  return answer(object.body, 200, pointer, { "content-length": String(object.size), "content-type": type });
}

export async function serveMedia(request, env, pointer) {
  const key = pointer.headers.get(POINTER);
  const etag = pointer.headers.get("etag");
  const notFound = () => new Response(JSON.stringify({ error: "Not found." }), { status: 404, headers: { "content-type": "application/json" } });

  // The browser has this very upload already.
  const known = request.headers.get("if-none-match");
  if (etag && known && known.split(",").map((t) => t.trim().replace(/^W\//, "")).includes(etag)) return answer(null, 304, pointer);

  if (request.method === "HEAD") {
    return answer(null, 200, pointer, { "content-length": pointer.headers.get("x-media-size") });
  }

  const url = new URL(request.url);
  const size = Number(pointer.headers.get("x-media-size"));
  const useCache = etag && cacheable(url) && !(size > CACHE_MAX_BYTES);
  if (useCache) {
    const cache = caches.default;
    const cacheUrl = `${url.origin}/__media/${encodeURIComponent(key)}?v=${encodeURIComponent(etag.replace(/"/g, ""))}`;
    const asked = new Request(cacheUrl, { headers: request.headers.has("range") ? { range: request.headers.get("range") } : {} });
    const hit = await cache.match(asked);
    if (hit) return fromCache(hit, pointer);

    // First read in this location: the whole object once, into the cache, then the asked part from it.
    const object = await env.CLIPS.get(key);
    if (!object) return notFound();
    if (object.size <= CACHE_MAX_BYTES) {
      await cache.put(cacheUrl, new Response(object.body, {
        headers: {
          "content-type": object.httpMetadata?.contentType ?? pointer.headers.get("content-type") ?? "application/octet-stream",
          "content-length": String(object.size),
          "cache-control": `public, max-age=${CACHE_SECONDS}`,
          etag,
        },
      }));
      const fresh = await cache.match(asked);
      if (fresh) return fromCache(fresh, pointer);
    }
    // The cache didn't take it: read again, as asked.
  }

  const object = await env.CLIPS.get(key, { range: request.headers });
  if (!object) return notFound();
  return fromR2(object, request, pointer);
}
