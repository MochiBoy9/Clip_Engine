import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { fakeEnv, request, signUpBody } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);

// Cloudflare's Cache API as a Map: stores whole responses and answers ranges from them.
function fakeCache() {
  const stored = new Map();
  return {
    stored,
    async put(key, response) {
      stored.set(typeof key === "string" ? key : key.url, { bytes: new Uint8Array(await response.arrayBuffer()), headers: response.headers });
    },
    async match(request) {
      const hit = stored.get(request.url);
      if (!hit) return undefined;
      const m = request.headers.get("range")?.match(/^bytes=(\d+)-(\d*)$/);
      if (!m) return new Response(hit.bytes, { headers: { "content-type": hit.headers.get("content-type"), "content-length": String(hit.bytes.length) } });
      const start = Number(m[1]);
      const end = m[2] ? Math.min(Number(m[2]), hit.bytes.length - 1) : hit.bytes.length - 1;
      return new Response(hit.bytes.slice(start, end + 1), {
        status: 206,
        headers: { "content-type": hit.headers.get("content-type"), "content-length": String(end - start + 1),
                   "content-range": `bytes ${start}-${end}/${hit.bytes.length}` },
      });
    },
  };
}

async function sharedClip(env) {
  const made = await (await call(env, "/v1/accounts", { method: "POST", body: signUpBody("Enterro") })).json();
  const base = `/v1/accounts/${made.account.id}`;
  const clip = await (await call(env, `${base}/clips`, { method: "POST", token: made.token, body: { seconds: 15, visibility: "unlisted" } })).json();
  await call(env, `${base}/clips/${clip.id}/video`, { method: "PUT", token: made.token, body: new Uint8Array(1000).map((_, i) => i % 256) });
  await call(env, `${base}/clips/${clip.id}/thumb`, { method: "PUT", token: made.token, body: new Uint8Array(50).fill(9), headers: { "content-type": "image/png" } });
  return { clip, base, token: made.token };
}

test("a watched clip is read from R2 once per location, and a browser that has it reads nothing", async () => {
  const env = fakeEnv();
  const { clip, base, token } = await sharedClip(env);
  const cache = fakeCache();
  globalThis.caches = { default: cache };
  try {
    const reads = env.CLIPS.reads;
    const first = await call(env, `/c/${clip.code}/video`, { headers: { range: "bytes=0-99" } });
    assert.equal(first.status, 206);
    assert.equal(first.headers.get("content-range"), "bytes 0-99/1000");
    assert.equal(first.headers.get("content-type"), "video/mp4");
    assert.match(first.headers.get("cache-control"), /^public/);
    const etag = first.headers.get("etag");
    assert.ok(etag);
    // Seeking, and another viewer: all from the cache.
    const seek = await call(env, `/c/${clip.code}/video`, { headers: { range: "bytes=500-599" } });
    assert.equal(new Uint8Array(await seek.arrayBuffer())[0], 500 % 256);
    assert.equal((await (await call(env, `/c/${clip.code}/video`)).arrayBuffer()).byteLength, 1000);
    assert.equal(env.CLIPS.reads - reads, 1, "one R2 read for three requests");

    const thumb = await call(env, `/c/${clip.code}/thumb`);
    assert.equal(thumb.headers.get("content-type"), "image/png");
    assert.equal(env.CLIPS.reads - reads, 2);

    const fresh = await call(env, `/c/${clip.code}/video`, { headers: { "if-none-match": etag } });
    assert.equal(fresh.status, 304);
    assert.equal(env.CLIPS.reads - reads, 2);

    // Private now: refused before the cache is looked at.
    await call(env, `${base}/clips/${clip.id}`, { method: "PATCH", token, body: { visibility: "private" } });
    assert.equal((await call(env, `/c/${clip.code}/video`)).status, 404);
    const own = await call(env, `${base}/clips/${clip.id}/video`, { token });
    assert.equal(own.status, 200);
    assert.match(own.headers.get("cache-control"), /^private/);

    // A new upload has a new ETag, so the cached copy isn't used for it.
    await call(env, `${base}/clips/${clip.id}/video`, { method: "PUT", token, body: new Uint8Array(10).fill(1) });
    const replaced = await call(env, `${base}/clips/${clip.id}/video`, { token });
    assert.notEqual(replaced.headers.get("etag"), etag);
    assert.equal((await replaced.arrayBuffer()).byteLength, 10);
  } finally {
    delete globalThis.caches;
  }
});

test("on workers.dev, where the Cache API does nothing, clips are read straight from R2", async () => {
  const env = fakeEnv();
  const { clip } = await sharedClip(env);
  globalThis.caches = { default: fakeCache() };
  try {
    const reads = env.CLIPS.reads;
    const r = await worker.fetch(new Request(`https://clip-engine.example.workers.dev/c/${clip.code}/video`, { headers: { range: "bytes=10-19" } }), env);
    assert.equal(r.status, 206);
    assert.equal(new Uint8Array(await r.arrayBuffer())[0], 10);
    assert.equal(env.CLIPS.reads - reads, 1);
  } finally {
    delete globalThis.caches;
  }
});
