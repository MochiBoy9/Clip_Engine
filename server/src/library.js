// One player's cloud library: their clips in R2, which are private, unlisted (anyone with the
// short link) or public (also on their profile), their profile name, and their plan, which
// sets how much they can keep. A Durable Object per account; a clip's short code starts with
// the account id, so a link finds its library without a global index.

import { isSquadId, randomCode, randomToken, sameSecret, sha256Hex } from "./codes.js";
import { normalizeHandle } from "./handles.js";
import { cleanGame, cleanName, MOMENT_TYPES } from "./protocol.js";

const MAX_UPLOAD_BYTES = 100 * 1024 * 1024;
const MAX_THUMB_BYTES = 512 * 1024;
const LINK_CODE_MS = 10 * 60_000;
export const CLIP_CODE_LENGTH = 6;
const VISIBILITY = ["private", "unlisted", "public"];

// What each plan gets. Plans are set by the operator (PATCH /v1/admin/plan), so a payment
// provider's webhook or the supporter keys can drive them later.
export const PLANS = {
  free: { storageMb: 2048, label: "Free" },
  plus: { storageMb: 102_400, label: "Plus" },
};

const SCHEMA = `
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS devices (token_hash TEXT PRIMARY KEY, device TEXT NOT NULL, added INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS clips (
  id TEXT PRIMARY KEY, local_id TEXT, title TEXT NOT NULL, game TEXT NOT NULL, created INTEGER NOT NULL,
  recorded INTEGER NOT NULL, seconds REAL NOT NULL, width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0,
  size INTEGER NOT NULL DEFAULT 0, uploaded INTEGER NOT NULL DEFAULT 0, thumb INTEGER NOT NULL DEFAULT 0,
  markers TEXT NOT NULL, visibility TEXT NOT NULL DEFAULT 'private', featured INTEGER NOT NULL DEFAULT 0,
  views INTEGER NOT NULL DEFAULT 0);
CREATE UNIQUE INDEX IF NOT EXISTS clips_local ON clips(local_id);
`;

function json(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function fail(status, error) {
  return json({ error }, status);
}

async function readJson(request) {
  try {
    const text = await request.text();
    if (text.length > 65_536) return null;
    const body = JSON.parse(text);
    return body && typeof body === "object" ? body : null;
  } catch {
    return null;
  }
}

function withLength(body, length) {
  if (typeof FixedLengthStream === "undefined" || !body) return body;
  const { readable, writable } = new FixedLengthStream(length);
  body.pipeTo(writable).catch(() => {});
  return readable;
}

// A clip's moments as the desktop app sends them, checked: at most 200, each a known type with
// a time inside the clip.
export function cleanMarkers(value, seconds) {
  if (!Array.isArray(value)) return [];
  return value
    .slice(0, 200)
    .filter((m) => m && MOMENT_TYPES.includes(m.type) && Number.isFinite(m.time) && m.time >= 0 && m.time <= seconds + 1)
    .map((m) => ({ type: m.type, time: Math.round(m.time * 100) / 100, text: typeof m.text === "string" ? m.text.slice(0, 200) : "" }));
}

export class Library {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.sql = ctx.storage.sql;
    this.sql.exec(SCHEMA);
  }

  meta(key) {
    return this.sql.exec("SELECT value FROM meta WHERE key = ?", key).toArray()[0]?.value ?? null;
  }

  setMeta(key, value) {
    this.sql.exec("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value);
  }

  plan() {
    const name = this.meta("plan") ?? "free";
    const plan = PLANS[name] ?? PLANS.free;
    const override = Number(this.env?.[`${name.toUpperCase()}_STORAGE_MB`]);
    return { name, label: plan.label, storage: (Number.isFinite(override) && override > 0 ? override : plan.storageMb) * 1024 * 1024 };
  }

  used() {
    return this.sql.exec("SELECT COALESCE(SUM(size), 0) AS used FROM clips WHERE uploaded = 1").toArray()[0].used;
  }

  account() {
    const plan = this.plan();
    return {
      id: this.meta("id"),
      name: this.meta("name"),
      handle: this.meta("handle"),
      plan: plan.name,
      planLabel: plan.label,
      storage: { used: this.used(), limit: plan.storage },
      clips: this.sql.exec("SELECT COUNT(*) AS n FROM clips WHERE uploaded = 1").toArray()[0].n,
    };
  }

  async addDevice(device) {
    const token = randomToken();
    this.sql.exec("INSERT INTO devices (token_hash, device, added) VALUES (?, ?, ?)", await sha256Hex(token), device, Date.now());
    return token;
  }

  async authorized(request) {
    const header = request.headers.get("authorization") ?? "";
    const token = header.startsWith("Bearer ") ? header.slice(7).trim() : "";
    if (!token || token.length > 128) return false;
    return this.sql.exec("SELECT 1 FROM devices WHERE token_hash = ?", await sha256Hex(token)).toArray().length > 0;
  }

  clipRow(id) {
    return this.sql.exec("SELECT * FROM clips WHERE id = ?", id).toArray()[0] ?? null;
  }

  clipJson(row) {
    return {
      id: row.id,
      code: this.meta("id") + row.id,
      localId: row.local_id,
      title: row.title,
      game: row.game,
      created: row.created,
      recorded: row.recorded,
      seconds: row.seconds,
      width: row.width,
      height: row.height,
      size: row.size,
      uploaded: row.uploaded === 1,
      thumb: row.thumb === 1,
      markers: JSON.parse(row.markers),
      visibility: row.visibility,
      featured: row.featured === 1,
      views: row.views,
    };
  }

  key(id, kind) {
    return `library/${this.meta("id")}/${id}.${kind === "thumb" ? "img" : "mp4"}`;
  }

  async fetch(request) {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

    if (path === "/create" && method === "POST") return this.create(request);
    if (!this.meta("id")) return fail(404, "There's no such account.");
    if (path === "/join" && method === "POST") return this.join(request);
    if (path.startsWith("/public/")) return this.publicRequest(request, path.slice(8));
    if (path === "/admin/plan" && method === "POST") {
      const body = await readJson(request);
      if (!PLANS[body?.plan]) return fail(400, "Unknown plan.");
      this.setMeta("plan", body.plan);
      return json(this.account());
    }

    if (!(await this.authorized(request))) return fail(401, "This PC isn't signed in to that account any more.");

    if (path === "/account" && method === "GET") return json(this.account());
    if (path === "/account" && method === "PATCH") return this.updateAccount(request);
    if (path === "/link" && method === "POST") {
      const secret = randomCode(6);
      this.setMeta("link", JSON.stringify({ secret, until: Date.now() + LINK_CODE_MS }));
      const code = (this.meta("id") + secret).match(/.{1,4}/g).join("-");
      return json({ code, expires: Date.now() + LINK_CODE_MS });
    }
    if (path === "/clips" && method === "GET") {
      const rows = this.sql.exec("SELECT * FROM clips ORDER BY recorded DESC").toArray();
      return json({ clips: rows.map((r) => this.clipJson(r)), account: this.account() });
    }
    if (path === "/clips" && method === "POST") return this.addClip(request);

    const m = path.match(/^\/clips\/([0-9A-Z]{6})(?:\/(video|thumb))?$/);
    if (m) {
      const [, id, kind] = m;
      const row = this.clipRow(id);
      if (!row) return fail(404, "That clip isn't in your cloud library.");
      if (!kind && method === "PATCH") return this.updateClip(request, row);
      if (!kind && method === "DELETE") {
        await this.env.CLIPS.delete([this.key(id, "video"), this.key(id, "thumb")]);
        this.sql.exec("DELETE FROM clips WHERE id = ?", id);
        return json({ ok: true });
      }
      if (kind && method === "PUT") return this.upload(request, row, kind);
      if (kind && method === "GET") return this.stream(request, row, kind);
    }
    return fail(404, "Not found.");
  }

  async create(request) {
    if (this.meta("id")) return fail(409, "That account already exists.");
    const body = await readJson(request);
    const id = request.headers.get("x-account-id");
    const name = cleanName(body?.name);
    const device = typeof body?.device === "string" && /^[A-Za-z0-9_-]{8,64}$/.test(body.device) ? body.device : null;
    if (!isSquadId(id) || !name || !device) return fail(400, "An account needs your name and this PC's id.");
    this.setMeta("id", id);
    this.setMeta("name", name);
    this.setMeta("plan", "free");
    this.setMeta("created", String(Date.now()));
    const token = await this.addDevice(device);
    return json({ token, account: this.account() });
  }

  // Signing another PC in with a code made on one that is signed in.
  async join(request) {
    const body = await readJson(request);
    const device = typeof body?.device === "string" && /^[A-Za-z0-9_-]{8,64}$/.test(body.device) ? body.device : null;
    const link = JSON.parse(this.meta("link") ?? "null");
    if (!device || !link || Date.now() > link.until || !sameSecret(String(body?.secret ?? ""), link.secret)) {
      return fail(403, "That sign-in code isn't right or has run out. Make a new one on the PC that's signed in.");
    }
    this.sql.exec("DELETE FROM meta WHERE key = 'link'");
    const token = await this.addDevice(device);
    return json({ token, account: this.account() });
  }

  async updateAccount(request) {
    const body = await readJson(request);
    if (body?.name !== undefined) {
      const name = cleanName(body.name);
      if (!name) return fail(400, "Pick a name to show on your profile.");
      this.setMeta("name", name);
    }
    if (body?.handle !== undefined) {
      if (body.handle === null || body.handle === "") {
        await this.handles("/release", { account: this.meta("id") });
        this.sql.exec("DELETE FROM meta WHERE key = 'handle'");
      } else {
        const handle = normalizeHandle(body.handle);
        if (!handle) return fail(400, "Profile names are 3 to 24 letters, numbers, _ or .");
        const claimed = await this.handles("/claim", { handle, account: this.meta("id") });
        if (!claimed.ok) return fail(claimed.status, (await claimed.json()).error);
        this.setMeta("handle", handle);
      }
    }
    return json(this.account());
  }

  handles(path, body) {
    const stub = this.env.HANDLES.get(this.env.HANDLES.idFromName("handles"));
    return stub.fetch(new Request(`https://handles${path}`, { method: "POST", body: JSON.stringify(body) }));
  }

  async addClip(request) {
    const body = await readJson(request);
    const seconds = Number(body?.seconds);
    if (!Number.isFinite(seconds) || seconds <= 0 || seconds > 24 * 3600) return fail(400, "A clip needs its length.");
    const localId = typeof body?.localId === "string" ? body.localId.slice(0, 200) : null;
    if (localId) {
      // The same clip uploaded again from the same PC: the clip it already has.
      const known = this.sql.exec("SELECT * FROM clips WHERE local_id = ?", localId).toArray()[0];
      if (known) return json(this.clipJson(known));
    }
    let id;
    do id = randomCode(6);
    while (this.clipRow(id));
    const visibility = VISIBILITY.includes(body?.visibility) ? body.visibility : "unlisted";
    this.sql.exec(
      `INSERT INTO clips (id, local_id, title, game, created, recorded, seconds, width, height, markers, visibility)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      id, localId, String(body?.title ?? "").slice(0, 120), cleanGame(body?.game) || "Clip", Date.now(),
      Number.isFinite(body?.recorded) ? Math.round(body.recorded) : Date.now(), seconds,
      Number.isInteger(body?.width) ? body.width : 0, Number.isInteger(body?.height) ? body.height : 0,
      JSON.stringify(cleanMarkers(body?.markers, seconds)), visibility);
    return json(this.clipJson(this.clipRow(id)));
  }

  async updateClip(request, row) {
    const body = await readJson(request);
    if (typeof body?.title === "string") this.sql.exec("UPDATE clips SET title = ? WHERE id = ?", body.title.slice(0, 120), row.id);
    if (VISIBILITY.includes(body?.visibility)) this.sql.exec("UPDATE clips SET visibility = ? WHERE id = ?", body.visibility, row.id);
    if (typeof body?.featured === "boolean") this.sql.exec("UPDATE clips SET featured = ? WHERE id = ?", body.featured ? 1 : 0, row.id);
    return json(this.clipJson(this.clipRow(row.id)));
  }

  async upload(request, row, kind) {
    const length = Number(request.headers.get("content-length"));
    if (!Number.isFinite(length) || length <= 0) return fail(411, "Uploads need their length.");
    if (kind === "thumb") {
      if (length > MAX_THUMB_BYTES) return fail(413, "Thumbnails are at most 512 KB.");
      const type = request.headers.get("content-type") === "image/png" ? "image/png" : "image/jpeg";
      await this.env.CLIPS.put(this.key(row.id, "thumb"), withLength(request.body, length), { httpMetadata: { contentType: type } });
      this.sql.exec("UPDATE clips SET thumb = 1 WHERE id = ?", row.id);
      return json({ ok: true });
    }
    if (length > MAX_UPLOAD_BYTES) return fail(413, "That clip is over 100 MB. Upload a smaller copy.");
    const plan = this.plan();
    if (this.used() - (row.uploaded ? row.size : 0) + length > plan.storage) {
      return fail(507, `Your ${plan.label} cloud library is full. Remove some clips to make room.`);
    }
    const object = await this.env.CLIPS.put(this.key(row.id, "video"), withLength(request.body, length), {
      httpMetadata: { contentType: "video/mp4" },
    });
    this.sql.exec("UPDATE clips SET uploaded = 1, size = ? WHERE id = ?", object?.size ?? length, row.id);
    return json(this.clipJson(this.clipRow(row.id)));
  }

  async stream(request, row, kind) {
    if (kind === "video" && !row.uploaded) return fail(404, "That clip is still uploading.");
    if (kind === "thumb" && !row.thumb) return fail(404, "No thumbnail.");
    const object = await this.env.CLIPS.get(this.key(row.id, kind), { range: request.headers });
    if (!object) return fail(404, "Not found.");
    const headers = new Headers();
    headers.set("content-type", object.httpMetadata?.contentType ?? (kind === "thumb" ? "image/jpeg" : "video/mp4"));
    headers.set("accept-ranges", "bytes");
    headers.set("cache-control", row.visibility === "private" ? "private, max-age=3600" : "public, max-age=3600");
    let status = 200;
    if (object.range && request.headers.has("range")) {
      const offset = object.range.offset ?? (object.range.suffix !== undefined ? object.size - object.range.suffix : 0);
      const length = object.range.length ?? object.size - offset;
      headers.set("content-range", `bytes ${offset}-${offset + length - 1}/${object.size}`);
      headers.set("content-length", String(length));
      status = 206;
    } else {
      headers.set("content-length", String(object.size));
    }
    return new Response(object.body, { status, headers });
  }

  // What anyone can see: an unlisted or public clip by its code, and the public profile.
  async publicRequest(request, rest) {
    if (rest === "profile") {
      const rows = this.sql.exec(
        "SELECT * FROM clips WHERE visibility = 'public' AND uploaded = 1 ORDER BY featured DESC, recorded DESC LIMIT 60").toArray();
      return json({ name: this.meta("name"), handle: this.meta("handle"), clips: rows.map((r) => this.clipJson(r)) });
    }
    const m = rest.match(/^clip\/([0-9A-Z]{6})(?:\/(video|thumb))?$/);
    const row = m && this.clipRow(m[1]);
    if (!row || row.visibility === "private" || !row.uploaded) return fail(404, "That clip isn't shared.");
    if (m[2]) return this.stream(request, row, m[2]);
    if (new URL(request.url).searchParams.get("view") === "1") this.sql.exec("UPDATE clips SET views = views + 1 WHERE id = ?", row.id);
    return json({ ...this.clipJson(this.clipRow(row.id)), ownerName: this.meta("name"), ownerHandle: this.meta("handle") });
  }
}

