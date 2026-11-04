// One squad: its members, who's online and what they're playing, the moments they share and
// the shared library. A Durable Object, so every member of a squad talks to the same instance;
// its SQLite storage keeps the squad, and R2 keeps uploaded clips. WebSockets use hibernation,
// so a squad nobody is using costs nothing.

import {
  formatInvite,
  INVITE_SECRET_LENGTH,
  isMemberId,
  isMomentId,
  newMemberId,
  newMomentId,
  randomCode,
  randomToken,
  sameSecret,
  sha256Hex,
} from "./codes.js";
import { groups, linkedMembers } from "./linking.js";
import { cleanName, parseClientMessage, RateLimit } from "./protocol.js";

const MAX_UPLOAD_BYTES = 100 * 1024 * 1024;  // a Worker request body's limit
const MAX_THUMB_BYTES = 512 * 1024;
const DAY_MS = 24 * 3600_000;
const MOMENTS_PER_MINUTE = 40;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS members (
  id TEXT PRIMARY KEY, name TEXT NOT NULL, device TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE,
  joined INTEGER NOT NULL, owner INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS moments (
  id TEXT PRIMARY KEY, game TEXT NOT NULL, type TEXT NOT NULL, at INTEGER NOT NULL, by TEXT NOT NULL,
  text TEXT NOT NULL, linked TEXT NOT NULL, star INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS moments_at ON moments(at);
CREATE TABLE IF NOT EXISTS views (
  moment TEXT NOT NULL, member TEXT NOT NULL, utc_start INTEGER NOT NULL, seconds REAL NOT NULL,
  size INTEGER NOT NULL DEFAULT 0, uploaded INTEGER NOT NULL DEFAULT 0, thumb INTEGER NOT NULL DEFAULT 0,
  added INTEGER NOT NULL, PRIMARY KEY (moment, member));
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
    if (text.length > 16_384) return null;
    const body = JSON.parse(text);
    return body && typeof body === "object" ? body : null;
  } catch {
    return null;
  }
}

// R2 needs to know a streamed body's length up front; FixedLengthStream also stops an upload
// that sends more than it said. (Only Workers have it; the tests pass the body as it is.)
function withLength(body, length) {
  if (typeof FixedLengthStream === "undefined" || !body) return body;
  const { readable, writable } = new FixedLengthStream(length);
  body.pipeTo(writable).catch(() => {});
  return readable;
}

function cleanDevice(value) {
  return typeof value === "string" && /^[A-Za-z0-9_-]{8,64}$/.test(value) ? value : null;
}

export class Squad {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.sql = ctx.storage.sql;
    this.sql.exec(SCHEMA);
    this.rate = new RateLimit(MOMENTS_PER_MINUTE);
  }

  // ---- storage helpers ----

  meta(key) {
    const row = this.sql.exec("SELECT value FROM meta WHERE key = ?", key).toArray()[0];
    return row ? row.value : null;
  }

  setMeta(key, value) {
    this.sql.exec("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value);
  }

  members() {
    return this.sql.exec("SELECT id, name, owner, joined FROM members ORDER BY joined").toArray();
  }

  number(name, fallback) {
    const n = Number(this.env?.[name]);
    return Number.isFinite(n) && n > 0 ? n : fallback;
  }

  async memberFor(request) {
    const header = request.headers.get("authorization") ?? "";
    const token = header.startsWith("Bearer ") ? header.slice(7).trim() : "";
    if (!token || token.length > 128) return null;
    const hash = await sha256Hex(token);
    return this.sql.exec("SELECT id, name, owner FROM members WHERE token_hash = ?", hash).toArray()[0] ?? null;
  }

  async addMember(name, device, owner) {
    const token = randomToken();
    const hash = await sha256Hex(token);
    const existing = this.sql.exec("SELECT id FROM members WHERE device = ?", device).toArray()[0];
    if (existing) {
      // The same PC joining again (a reinstall, or the app lost its token): same member, new token.
      this.sql.exec("UPDATE members SET token_hash = ?, name = ? WHERE id = ?", hash, name, existing.id);
      return { id: existing.id, token, rejoined: true };
    }
    const id = newMemberId();
    this.sql.exec("INSERT INTO members (id, name, device, token_hash, joined, owner) VALUES (?, ?, ?, ?, ?, ?)",
      id, name, device, hash, Date.now(), owner ? 1 : 0);
    return { id, token, rejoined: false };
  }

  squadInfo(memberId) {
    const online = new Set(this.presences().map((p) => p.member));
    return {
      id: this.meta("id"),
      name: this.meta("name"),
      invite: formatInvite(this.meta("id"), this.meta("secret")),
      you: memberId,
      members: this.members().map((m) => ({ id: m.id, name: m.name, owner: m.owner === 1, online: online.has(m.id) })),
      storage: { used: this.storageUsed(), limit: this.number("SQUAD_STORAGE_MB", 5120) * 1024 * 1024 },
    };
  }

  storageUsed() {
    return this.sql.exec("SELECT COALESCE(SUM(size), 0) AS used FROM views WHERE uploaded = 1").toArray()[0].used;
  }

  // ---- sockets ----

  sockets(member) {
    return member ? this.ctx.getWebSockets(member) : this.ctx.getWebSockets();
  }

  attachment(ws) {
    try {
      return ws.deserializeAttachment() ?? {};
    } catch {
      return {};
    }
  }

  // Everyone online, with what they're playing. A member with the app open twice counts once,
  // from whichever socket says they're in a game.
  presences(except) {
    const byMember = new Map();
    for (const ws of this.sockets()) {
      if (ws === except) continue;
      const a = this.attachment(ws);
      if (!a.member) continue;
      const p = { member: a.member, game: a.game ?? "", match: a.match ?? null, unlinked: a.unlinked ?? [] };
      if (!byMember.has(a.member) || (!byMember.get(a.member).game && p.game)) byMember.set(a.member, p);
    }
    return [...byMember.values()];
  }

  send(ws, message) {
    try {
      ws.send(JSON.stringify(message));
    } catch {
      // A socket that's closing; its close handler tidies up.
    }
  }

  broadcast(message, except) {
    for (const ws of this.sockets()) if (ws !== except) this.send(ws, message);
  }

  sendTo(member, message) {
    let sent = false;
    for (const ws of this.sockets(member)) {
      this.send(ws, message);
      sent = true;
    }
    return sent;
  }

  presenceMessage(member, except) {
    const presences = this.presences(except);
    const p = presences.find((x) => x.member === member);
    return {
      t: "presence",
      member,
      online: Boolean(p),
      game: p?.game ?? "",
      match: p?.match ?? null,
      groups: groups(presences),
    };
  }

  // Overridden by tests, which have no WebSocketPair.
  openSocket(member) {
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.ctx.acceptWebSocket(server, [member]);
    server.serializeAttachment({ member, game: "", match: null, unlinked: [] });
    return new Response(null, { status: 101, webSocket: client });
  }

  // ---- HTTP ----

  async fetch(request) {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

    if (path === "/create" && method === "POST") return this.create(request);
    if (path === "/join" && method === "POST") return this.join(request);
    if (!this.meta("id")) return fail(404, "There's no squad with that code.");
    if (path === "/public/invite") {
      // What the join page shows: only with the right code.
      if (!sameSecret(url.searchParams.get("secret") ?? "", this.meta("secret"))) return fail(404, "That invite isn't valid any more.");
      return json({ name: this.meta("name"), members: this.members().map((m) => m.name), max: this.number("MAX_MEMBERS", 8) });
    }

    const me = await this.memberFor(request);
    if (!me) return fail(401, "You're not in this squad any more.");

    if (path === "/socket") {
      if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return fail(426, "Expected a WebSocket.");
      return this.openSocket(me.id);
    }
    if (path === "/squad" && method === "GET") return json(this.squadInfo(me.id));
    if (path === "/invite/rotate" && method === "POST") {
      if (!me.owner) return fail(403, "Only the squad's owner can change its invite code.");
      this.setMeta("secret", randomCode(INVITE_SECRET_LENGTH));
      return json(this.squadInfo(me.id));
    }
    if (path === "/members/me" && method === "PATCH") {
      const body = await readJson(request);
      const name = cleanName(body?.name);
      if (!name) return fail(400, "Pick a name to show your squad.");
      this.sql.exec("UPDATE members SET name = ? WHERE id = ?", name, me.id);
      this.broadcast({ t: "member", member: me.id, name });
      return json(this.squadInfo(me.id));
    }
    let m = path.match(/^\/members\/(me|p[0-9A-Z]{10})$/);
    if (m && method === "DELETE") {
      const target = m[1] === "me" ? me.id : m[1];
      if (target !== me.id && !me.owner) return fail(403, "Only the squad's owner can remove someone.");
      await this.removeMember(target);
      return json({ ok: true });
    }
    if (path === "/library" && method === "GET") return json(this.library(url, me.id));

    m = path.match(/^\/moments\/([A-Za-z0-9_-]{1,64})\/star$/);
    if (m && method === "POST") {
      const body = await readJson(request);
      this.sql.exec("UPDATE moments SET star = ? WHERE id = ?", body?.star === false ? 0 : 1, m[1]);
      this.broadcast({ t: "star", moment: m[1], star: body?.star !== false });
      return json({ ok: true });
    }

    m = path.match(/^\/moments\/([A-Za-z0-9_-]{1,64})\/views\/(me|p[0-9A-Z]{10})\/(video|thumb)$/);
    if (m) {
      const [, moment, who, kind] = m;
      const member = who === "me" ? me.id : who;
      if (method === "PUT") {
        if (member !== me.id) return fail(403, "You can only upload your own view.");
        return kind === "video" ? this.uploadVideo(request, moment, me.id) : this.uploadThumb(request, moment, me.id);
      }
      if (method === "GET") return this.download(request, moment, member, kind);
      if (method === "DELETE" && kind === "video") {
        if (member !== me.id) return fail(403, "You can only remove your own view.");
        await this.removeView(moment, me.id);
        return json({ ok: true });
      }
    }
    return fail(404, "Not found.");
  }

  async create(request) {
    if (this.meta("id")) return fail(409, "That squad already exists.");
    const body = await readJson(request);
    const id = request.headers.get("x-squad-id");
    const name = cleanName(body?.name);
    const squadName = cleanName(body?.squad) || (name ? `${name}'s squad` : "Squad");
    const device = cleanDevice(body?.device);
    if (!id || !name || !device) return fail(400, "A squad needs your name and this PC's id.");
    this.setMeta("id", id);
    this.setMeta("name", squadName);
    this.setMeta("secret", randomCode(INVITE_SECRET_LENGTH));
    this.setMeta("created", String(Date.now()));
    const member = await this.addMember(name, device, true);
    await this.scheduleCleanup();
    return json({ token: member.token, squad: this.squadInfo(member.id) });
  }

  async join(request) {
    if (!this.meta("id")) return fail(404, "There's no squad with that code.");
    const body = await readJson(request);
    const name = cleanName(body?.name);
    const device = cleanDevice(body?.device);
    if (!name || !device) return fail(400, "Joining needs your name and this PC's id.");
    if (!sameSecret(String(body?.secret ?? ""), this.meta("secret"))) return fail(403, "That invite code isn't right, or it was changed.");
    const known = this.sql.exec("SELECT id FROM members WHERE device = ?", device).toArray()[0];
    if (!known && this.members().length >= this.number("MAX_MEMBERS", 8)) return fail(409, "That squad is full.");
    const member = await this.addMember(name, device, false);
    if (!member.rejoined) this.broadcast({ t: "joined", member: member.id, name });
    return json({ token: member.token, squad: this.squadInfo(member.id) });
  }

  library(url, me) {
    const before = Number(url.searchParams.get("before")) || Number.MAX_SAFE_INTEGER;
    const limit = Math.min(Math.max(Number(url.searchParams.get("limit")) || 30, 1), 100);
    const game = url.searchParams.get("game");
    const rows = this.sql.exec(
      `SELECT * FROM moments WHERE at < ? AND (? IS NULL OR game = ?)
         AND EXISTS (SELECT 1 FROM views v WHERE v.moment = moments.id)
       ORDER BY at DESC LIMIT ?`, before, game, game, limit).toArray();
    const names = new Map(this.members().map((m) => [m.id, m.name]));
    const moments = rows.map((r) => ({
      id: r.id,
      game: r.game,
      type: r.type,
      at: r.at,
      by: r.by,
      byName: names.get(r.by) ?? "Someone who left",
      text: r.text,
      star: r.star === 1,
      views: this.sql.exec("SELECT * FROM views WHERE moment = ? ORDER BY member = ? DESC, added", r.id, r.by).toArray()
        .map((v) => ({
          member: v.member,
          name: names.get(v.member) ?? "Someone who left",
          utc_start: v.utc_start,
          seconds: v.seconds,
          size: v.size,
          uploaded: v.uploaded === 1,
          thumb: v.thumb === 1,
          mine: v.member === me,
        })),
    }));
    return { moments, next: rows.length === limit ? rows[rows.length - 1].at : null };
  }

  key(moment, member, kind) {
    return `${this.meta("id")}/${moment}/${member}.${kind === "thumb" ? "img" : "mp4"}`;
  }

  async uploadVideo(request, moment, member) {
    const view = this.sql.exec("SELECT * FROM views WHERE moment = ? AND member = ?", moment, member).toArray()[0];
    if (!view) return fail(404, "Tell the squad about this view before uploading it.");
    const length = Number(request.headers.get("content-length"));
    if (!Number.isFinite(length) || length <= 0) return fail(411, "Uploads need their length.");
    if (length > MAX_UPLOAD_BYTES) return fail(413, "That clip is too big for the squad library.");
    const limit = this.number("SQUAD_STORAGE_MB", 5120) * 1024 * 1024;
    if (!(await this.makeRoom(length - (view.uploaded ? view.size : 0), limit))) {
      return fail(507, "The squad library is full of starred clips. Unstar some to make room.");
    }
    const object = await this.env.CLIPS.put(this.key(moment, member, "video"), withLength(request.body, length), {
      httpMetadata: { contentType: "video/mp4" },
    });
    const size = object?.size ?? length;
    this.sql.exec("UPDATE views SET uploaded = 1, size = ? WHERE moment = ? AND member = ?", size, moment, member);
    this.broadcast({ t: "uploaded", moment, member, size });
    return json({ ok: true, size });
  }

  async uploadThumb(request, moment, member) {
    const view = this.sql.exec("SELECT 1 FROM views WHERE moment = ? AND member = ?", moment, member).toArray()[0];
    if (!view) return fail(404, "Tell the squad about this view first.");
    const length = Number(request.headers.get("content-length"));
    if (!Number.isFinite(length) || length <= 0 || length > MAX_THUMB_BYTES) return fail(413, "Thumbnails are at most 512 KB.");
    const type = request.headers.get("content-type") === "image/png" ? "image/png" : "image/jpeg";
    await this.env.CLIPS.put(this.key(moment, member, "thumb"), withLength(request.body, length), { httpMetadata: { contentType: type } });
    this.sql.exec("UPDATE views SET thumb = 1 WHERE moment = ? AND member = ?", moment, member);
    return json({ ok: true });
  }

  async download(request, moment, member, kind) {
    if (!isMomentId(moment) || !isMemberId(member)) return fail(404, "Not found.");
    const object = await this.env.CLIPS.get(this.key(moment, member, kind), { range: request.headers });
    if (!object) return fail(404, "That view isn't in the squad library (yet).");
    const headers = new Headers();
    headers.set("content-type", object.httpMetadata?.contentType ?? (kind === "thumb" ? "image/jpeg" : "video/mp4"));
    headers.set("accept-ranges", "bytes");
    headers.set("cache-control", "private, max-age=86400");
    let status = 200;
    const range = object.range;
    if (range && request.headers.has("range")) {
      const offset = range.offset ?? (range.suffix !== undefined ? object.size - range.suffix : 0);
      const length = range.length ?? object.size - offset;
      headers.set("content-range", `bytes ${offset}-${offset + length - 1}/${object.size}`);
      headers.set("content-length", String(length));
      status = 206;
    } else {
      headers.set("content-length", String(object.size));
    }
    return new Response(object.body, { status, headers });
  }

  // Frees space for `bytes` more by removing the oldest unstarred uploads. False when even
  // that isn't enough.
  async makeRoom(bytes, limit) {
    let used = this.storageUsed();
    if (used + bytes <= limit) return true;
    const candidates = this.sql.exec(
      `SELECT v.moment, v.member, v.size FROM views v JOIN moments m ON m.id = v.moment
       WHERE v.uploaded = 1 AND m.star = 0 ORDER BY m.at`).toArray();
    for (const c of candidates) {
      await this.dropUpload(c.moment, c.member);
      used -= c.size;
      if (used + bytes <= limit) return true;
    }
    return used + bytes <= limit;
  }

  // Removes an uploaded copy; the view stays listed, on its owner's PC.
  async dropUpload(moment, member) {
    await this.env.CLIPS.delete([this.key(moment, member, "video"), this.key(moment, member, "thumb")]);
    this.sql.exec("UPDATE views SET uploaded = 0, size = 0, thumb = 0 WHERE moment = ? AND member = ?", moment, member);
  }

  async removeView(moment, member) {
    await this.env.CLIPS.delete([this.key(moment, member, "video"), this.key(moment, member, "thumb")]);
    this.sql.exec("DELETE FROM views WHERE moment = ? AND member = ?", moment, member);
    this.broadcast({ t: "removed", moment, member });
  }

  async removeMember(member) {
    const views = this.sql.exec("SELECT moment FROM views WHERE member = ?", member).toArray();
    const keys = views.flatMap((v) => [this.key(v.moment, member, "video"), this.key(v.moment, member, "thumb")]);
    for (let i = 0; i < keys.length; i += 1000) await this.env.CLIPS.delete(keys.slice(i, i + 1000));
    this.sql.exec("DELETE FROM views WHERE member = ?", member);
    const wasOwner = this.sql.exec("SELECT owner FROM members WHERE id = ?", member).toArray()[0]?.owner === 1;
    this.sql.exec("DELETE FROM members WHERE id = ?", member);
    for (const ws of this.sockets(member)) {
      try {
        ws.close(4001, "Left the squad");
      } catch {}
    }
    const left = this.members();
    if (left.length === 0) {
      // The last one out: nothing of the squad is kept.
      await this.ctx.storage.deleteAlarm?.();
      await this.ctx.storage.deleteAll();
      this.sql.exec(SCHEMA);  // empty again, so the squad's code now finds nothing
      return;
    }
    if (wasOwner) this.sql.exec("UPDATE members SET owner = 1 WHERE id = ?", left[0].id);
    this.broadcast({ t: "left", member, owner: wasOwner ? left[0].id : null });
  }

  // ---- WebSocket messages ----

  async webSocketMessage(ws, raw) {
    const now = Date.now();
    const message = parseClientMessage(typeof raw === "string" ? raw : "", now);
    const a = this.attachment(ws);
    if (!message || !a.member) return;
    switch (message.t) {
      case "hello": {
        const presences = this.presences().map(({ member, game, match }) => ({ member, game, match }));
        this.send(ws, { t: "welcome", now, squad: this.squadInfo(a.member), presences, groups: groups(this.presences()) });
        this.broadcast(this.presenceMessage(a.member), ws);
        return;
      }
      case "ping":
        this.send(ws, { t: "pong", c: message.c, s: Date.now() });
        return;
      case "presence": {
        ws.serializeAttachment({ ...a, game: message.game, match: message.match });
        this.broadcast(this.presenceMessage(a.member));
        return;
      }
      case "unlink": {
        const unlinked = new Set(a.unlinked ?? []);
        if (message.undo) unlinked.delete(message.member);
        else unlinked.add(message.member);
        ws.serializeAttachment({ ...a, unlinked: [...unlinked] });
        this.broadcast(this.presenceMessage(a.member));
        return;
      }
      case "moment":
        return this.onMoment(ws, a, message, now);
      case "view":
        return this.onView(ws, a, message, now);
      case "want": {
        const exists = this.sql.exec("SELECT uploaded FROM views WHERE moment = ? AND member = ?", message.moment, message.member).toArray()[0];
        if (!exists) {
          this.send(ws, { t: "want-ok", moment: message.moment, member: message.member, online: false, missing: true });
          return;
        }
        const online = exists.uploaded === 1 || this.sendTo(message.member, { t: "upload-request", moment: message.moment, from: a.member });
        this.send(ws, { t: "want-ok", moment: message.moment, member: message.member, online, uploaded: exists.uploaded === 1 });
        return;
      }
    }
  }

  onMoment(ws, a, message, now) {
    if (!this.rate.allow(a.member, now)) {
      this.send(ws, { t: "error", ref: message.ref, error: "Too many moments in a minute; slowing down." });
      return;
    }
    const from = { member: a.member, game: message.game, match: a.match ?? null, unlinked: a.unlinked ?? [] };
    const linked = linkedMembers(from, this.presences());
    const id = newMomentId(message.at);
    this.sql.exec("INSERT INTO moments (id, game, type, at, by, text, linked) VALUES (?, ?, ?, ?, ?, ?, ?)",
      id, message.game, message.type, message.at, a.member, message.text, JSON.stringify(linked));
    this.send(ws, { t: "moment-ok", ref: message.ref, id, linked });
    const name = this.sql.exec("SELECT name FROM members WHERE id = ?", a.member).toArray()[0]?.name ?? "";
    for (const member of linked) {
      this.sendTo(member, { t: "moment", id, type: message.type, at: message.at, game: message.game, text: message.text, by: a.member, byName: name });
    }
  }

  onView(ws, a, message, now) {
    const moment = this.sql.exec("SELECT by, linked FROM moments WHERE id = ?", message.moment).toArray()[0];
    if (!moment) return;
    // Only the one who made the moment, and the squadmates it was sent to, have a view of it.
    const linked = JSON.parse(moment.linked);
    if (moment.by !== a.member && !linked.includes(a.member)) return;
    this.sql.exec(
      `INSERT INTO views (moment, member, utc_start, seconds, added) VALUES (?, ?, ?, ?, ?)
       ON CONFLICT(moment, member) DO UPDATE SET utc_start = excluded.utc_start, seconds = excluded.seconds`,
      message.moment, a.member, message.utc_start, message.seconds, now);
    this.broadcast({ t: "view", moment: message.moment, member: a.member, utc_start: message.utc_start, seconds: message.seconds });
  }

  async webSocketClose(ws) {
    const a = this.attachment(ws);
    try {
      ws.close();
    } catch {}
    // The closing socket may still be listed while this runs, so it's left out by hand.
    if (a.member) this.broadcast(this.presenceMessage(a.member, ws), ws);
  }

  async webSocketError(ws) {
    return this.webSocketClose(ws);
  }

  // ---- cleanup ----

  async scheduleCleanup() {
    if (!(await this.ctx.storage.getAlarm())) await this.ctx.storage.setAlarm(Date.now() + DAY_MS);
  }

  async alarm() {
    await this.cleanup(Date.now());
    if (this.meta("id")) await this.ctx.storage.setAlarm(Date.now() + DAY_MS);
  }

  // Uploads older than KEEP_DAYS go unless starred, and moments nobody saved a view of go
  // after a day.
  async cleanup(now) {
    const keepMs = this.number("KEEP_DAYS", 30) * DAY_MS;
    const old = this.sql.exec(
      `SELECT v.moment, v.member FROM views v JOIN moments m ON m.id = v.moment
       WHERE v.uploaded = 1 AND m.star = 0 AND m.at < ?`, now - keepMs).toArray();
    for (const v of old) await this.dropUpload(v.moment, v.member);
    this.sql.exec(
      `DELETE FROM views WHERE moment IN (SELECT id FROM moments WHERE star = 0 AND at < ?)`, now - keepMs);
    this.sql.exec(
      `DELETE FROM moments WHERE star = 0 AND at < ? AND NOT EXISTS (SELECT 1 FROM views v WHERE v.moment = moments.id)`,
      now - DAY_MS);
  }
}
