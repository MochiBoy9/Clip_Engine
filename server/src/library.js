// One player's cloud account: their clips in R2, which are private, unlisted (anyone with the
// short link) or public (also on their profile), their profile name, their plan, which sets how
// much they can keep, and their friends. A Durable Object per account; a clip's short code starts
// with the account id, so a link finds its library without a global index.

import { isSquadId, normalizeInvite, randomCode, randomToken, sameSecret, sha256Hex, SQUAD_ID_LENGTH } from "./codes.js";
import { normalizeHandle } from "./handles.js";
import { checkLicense, cleanKey, OFFLINE_GRACE_MS, PLANS, planName, RECHECK_MS, storageBytes } from "./plans.js";
import { cleanGame, cleanName, MOMENT_TYPES } from "./protocol.js";

const MAX_UPLOAD_BYTES = 100 * 1024 * 1024;
const MAX_THUMB_BYTES = 512 * 1024;
const LINK_CODE_MS = 10 * 60_000;
export const CLIP_CODE_LENGTH = 6;
const VISIBILITY = ["private", "unlisted", "public"];

export const MAX_FRIENDS = 100;
const MAX_REQUESTS = 50;

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
CREATE TABLE IF NOT EXISTS friends (account TEXT PRIMARY KEY, name TEXT NOT NULL, handle TEXT, state TEXT NOT NULL,
  since INTEGER NOT NULL);
`;

// "K7QM-2X9D", an account's id as people pass it on to be added as a friend.
export function formatFriendCode(id) {
  return id.match(/.{1,4}/g).join("-");
}

export function normalizeFriendCode(text) {
  if (typeof text !== "string") return null;
  const code = text.trim().toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
  return code.length === SQUAD_ID_LENGTH && isSquadId(code) ? code : null;
}

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

  // The plan, with what a special key adds: extra storage and a different highest resolution.
  plan() {
    const name = planName(this.meta("plan"));
    const features = this.features();
    return {
      name,
      label: PLANS[name].label,
      lines: features.lines ?? PLANS[name].lines,
      storage: storageBytes(this.env, name) + (features.extraMb ?? 0) * 1024 * 1024,
    };
  }

  features() {
    try {
      return JSON.parse(this.meta("plan_features") ?? "{}") ?? {};
    } catch {
      return {};
    }
  }

  setFeatures(features) {
    this.setMeta("plan_features", JSON.stringify({ extraMb: features?.extraMb ?? 0, lines: features?.lines ?? null }));
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
      planKey: this.meta("plan_display") ?? null,
      lines: plan.lines,
      friendCode: formatFriendCode(this.meta("id")),
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
    if (path.startsWith("/peer/") && method === "POST") return this.peer(path.slice(6), await readJson(request));
    if (path === "/admin/plan" && method === "POST") {
      const body = await readJson(request);
      if (!PLANS[body?.plan]) return fail(400, "Unknown plan.");
      await this.forgetLicense();
      this.setMeta("plan", body.plan);
      return json(this.account());
    }

    if (!(await this.authorized(request))) return fail(401, "This PC isn't signed in to that account any more.");
    await this.recheckPlan();

    if (path === "/plan" && method === "POST") return this.claimPlan(request);
    if (path === "/plan" && method === "DELETE") {
      await this.forgetLicense();
      this.setMeta("plan", "free");
      return json(this.account());
    }
    if (path === "/friends" && method === "GET") return this.friendList();
    if (path === "/friends" && method === "POST") return this.addFriend(request);
    const f = path.match(/^\/friends\/([0-9A-Z]{8})(\/accept)?$/);
    if (f && method === "POST" && f[2]) return this.acceptFriend(f[1]);
    if (f && method === "DELETE" && !f[2]) return this.removeFriend(f[1]);
    if (path === "/presence" && method === "POST") return this.setPresence(request);
    if (path === "/presence" && method === "DELETE") {
      await this.directory("/presence/clear", { account: this.meta("id") });
      return json({ ok: true });
    }

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

  async directory(path, body) {
    return (await this.handles(path, body)).json();
  }

  // ---- plan ----

  // A Polar license key or a special key put on this account: its plan sets the storage. One
  // subscription or special key is on one account at a time.
  async claimPlan(request) {
    const body = await readJson(request);
    const key = cleanKey(body?.key);
    if (!key) return fail(400, "That doesn't look like a license key. Copy it from your receipt email.");
    const check = await checkLicense(this.env, key);
    if (!check.ok) return fail(check.rejected ? 403 : 503, check.error);
    const claimed = await this.handles("/license/claim", { license: check.license, account: this.meta("id") });
    if (!claimed.ok) return fail(claimed.status, (await claimed.json()).error);
    const previous = this.meta("plan_license");
    if (previous && previous !== check.license) await this.handles("/license/release", { license: previous, account: this.meta("id") });
    this.setMeta("plan", check.plan);
    this.setFeatures(check.features);
    this.setMeta("plan_key", key);
    this.setMeta("plan_license", check.license);
    this.setMeta("plan_display", check.display || `${key.slice(0, 4)}…${key.slice(-4)}`);
    this.setMeta("plan_checked", String(Date.now()));
    this.setMeta("plan_ok", String(Date.now()));
    return json(this.account());
  }

  async forgetLicense() {
    const license = this.meta("plan_license");
    if (license) await this.handles("/license/release", { license, account: this.meta("id") });
    this.sql.exec("DELETE FROM meta WHERE key IN ('plan_key', 'plan_license', 'plan_display', 'plan_checked', 'plan_ok', 'plan_features')");
  }

  // Checks the key with Polar again twice a day, so a cancelled subscription goes back to Free.
  // While Polar can't be reached the plan keeps working for two weeks.
  async recheckPlan() {
    const key = this.meta("plan_key");
    if (!key || Date.now() - Number(this.meta("plan_checked") ?? 0) < RECHECK_MS) return;
    this.setMeta("plan_checked", String(Date.now()));
    const check = await checkLicense(this.env, key);
    if (check.ok) {
      this.setMeta("plan", check.plan);
      this.setFeatures(check.features);
      this.setMeta("plan_ok", String(Date.now()));
    } else if (check.rejected) {
      await this.forgetLicense();
      this.setMeta("plan", "free");
    } else if (Date.now() - Number(this.meta("plan_ok") ?? 0) > OFFLINE_GRACE_MS) {
      this.setMeta("plan", "free");
    }
  }

  // ---- friends ----

  me() {
    return { account: this.meta("id"), name: this.meta("name"), handle: this.meta("handle") };
  }

  friendRow(account) {
    return this.sql.exec("SELECT * FROM friends WHERE account = ?", account).toArray()[0] ?? null;
  }

  peerStub(account) {
    return this.env.LIBRARIES.get(this.env.LIBRARIES.idFromName(account));
  }

  async tellPeer(account, what, body = {}) {
    const answer = await this.peerStub(account).fetch(new Request(`https://library/peer/${what}`, {
      method: "POST",
      body: JSON.stringify({ ...this.me(), ...body }),
    }));
    return answer;
  }

  async friendList() {
    const rows = this.sql.exec("SELECT * FROM friends ORDER BY name COLLATE NOCASE").toArray();
    const friends = rows.filter((r) => r.state === "friends");
    const { online = {} } = friends.length > 0 ? await this.directory("/presence/get", { accounts: friends.map((r) => r.account) }) : {};
    const row = (r) => ({ account: r.account, name: r.name, handle: r.handle, since: r.since });
    return json({
      friendCode: formatFriendCode(this.meta("id")),
      friends: friends.map((r) => {
        const here = online[r.account];
        return { ...row(r), name: here?.name || r.name, handle: here?.handle ?? r.handle, online: Boolean(here), game: here?.game ?? "", invite: here?.invite ?? null };
      }),
      received: rows.filter((r) => r.state === "received").map(row),
      sent: rows.filter((r) => r.state === "sent").map(row),
    });
  }

  async addFriend(request) {
    const body = await readJson(request);
    let account = normalizeFriendCode(body?.code);
    if (!account && body?.handle !== undefined) {
      const handle = normalizeHandle(body.handle);
      if (!handle) return fail(400, "Type their profile name, like @kai, or their friend code.");
      const found = await (await this.peerHandles(handle)).json();
      account = found.account ?? null;
      if (!account) return fail(404, "No one has that profile name.");
    }
    if (!account) return fail(400, "Type their profile name, like @kai, or their friend code.");
    if (account === this.meta("id")) return fail(400, "That's you.");
    const known = this.friendRow(account);
    if (known?.state === "friends" || known?.state === "sent") return this.friendList();
    if (known?.state === "received") return this.acceptFriend(account);
    if (this.sql.exec("SELECT COUNT(*) AS n FROM friends").toArray()[0].n >= MAX_FRIENDS) return fail(409, `You can have up to ${MAX_FRIENDS} friends.`);

    const answer = await this.tellPeer(account, "request");
    if (!answer.ok) return fail(answer.status, (await answer.json()).error);
    const { state, name, handle } = await answer.json();
    this.sql.exec("INSERT OR REPLACE INTO friends (account, name, handle, state, since) VALUES (?, ?, ?, ?, ?)",
      account, name, handle ?? null, state === "friends" ? "friends" : "sent", Date.now());
    return this.friendList();
  }

  peerHandles(handle) {
    const stub = this.env.HANDLES.get(this.env.HANDLES.idFromName("handles"));
    return stub.fetch(new Request(`https://handles/lookup?handle=${encodeURIComponent(handle)}`));
  }

  async acceptFriend(account) {
    const row = this.friendRow(account);
    if (!row || row.state !== "received") return fail(404, "That friend request isn't here any more.");
    const answer = await this.tellPeer(account, "accepted");
    if (!answer.ok) {
      this.sql.exec("DELETE FROM friends WHERE account = ?", account);
      return fail(404, "That account isn't there any more.");
    }
    this.sql.exec("UPDATE friends SET state = 'friends', since = ? WHERE account = ?", Date.now(), account);
    return this.friendList();
  }

  // Unfriends, cancels a request you sent or turns down one you got: gone from both lists.
  async removeFriend(account) {
    if (!this.friendRow(account)) return this.friendList();
    this.sql.exec("DELETE FROM friends WHERE account = ?", account);
    await this.tellPeer(account, "removed");
    return this.friendList();
  }

  // What other accounts tell this one: a friend request, an accepted one, an unfriending.
  async peer(what, body) {
    const from = isSquadId(body?.account) ? body.account : null;
    if (!from || from === this.meta("id")) return fail(400, "Who's asking?");
    const name = cleanName(body?.name) || "Player";
    const handle = normalizeHandle(body?.handle ?? "") ?? null;
    const row = this.friendRow(from);
    const me = { name: this.meta("name"), handle: this.meta("handle") };
    if (what === "request") {
      if (row?.state === "friends") return json({ state: "friends", ...me });
      if (row?.state === "sent") {
        // Both asked: that's a yes from each.
        this.sql.exec("UPDATE friends SET state = 'friends', name = ?, handle = ?, since = ? WHERE account = ?", name, handle, Date.now(), from);
        return json({ state: "friends", ...me });
      }
      if (!row) {
        const pending = this.sql.exec("SELECT COUNT(*) AS n FROM friends WHERE state = 'received'").toArray()[0].n;
        const total = this.sql.exec("SELECT COUNT(*) AS n FROM friends").toArray()[0].n;
        if (pending >= MAX_REQUESTS || total >= MAX_FRIENDS) return fail(409, "They can't take more friend requests right now.");
        this.sql.exec("INSERT INTO friends (account, name, handle, state, since) VALUES (?, ?, ?, 'received', ?)", from, name, handle, Date.now());
      }
      return json({ state: "sent", ...me });
    }
    if (what === "accepted") {
      if (!row || row.state !== "sent") return fail(404, "There's no request to accept.");
      this.sql.exec("UPDATE friends SET state = 'friends', name = ?, handle = ?, since = ? WHERE account = ?", name, handle, Date.now(), from);
      return json({ ok: true });
    }
    if (what === "removed") {
      this.sql.exec("DELETE FROM friends WHERE account = ?", from);
      return json({ ok: true });
    }
    return fail(404, "Not found.");
  }

  // Online for friends while the app runs, with the game being played and, if they chose to
  // share it, their squad's invite so friends can join.
  async setPresence(request) {
    const body = await readJson(request);
    const invite = body?.invite ? normalizeInvite(String(body.invite)) : null;
    await this.directory("/presence/set", {
      ...this.me(),
      game: cleanGame(body?.game) || "",
      invite: invite ? invite.squadId + invite.secret : null,
    });
    return json({ ok: true });
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

