// One player's cloud account: their clips in R2, which are private, unlisted (anyone with the
// short link) or public (also on their profile), their profile name, their plan, which sets how
// much they can keep, and their friends. A Durable Object per account; a clip's short code starts
// with the account id, so a link finds its library without a global index.

import { addColumns, isSquadId, normalizeInvite, randomCode, randomToken, sameSecret, sha256Hex, SQUAD_ID_LENGTH } from "./codes.js";
import { normalizeEmail, sendEmail } from "./email.js";
import { normalizeHandle } from "./handles.js";
import { mediaPointer } from "./media.js";
import { badgeFor, checkLicense, cleanKey, clipBytes, OFFLINE_GRACE_MS, PLANS, planName, RECHECK_MS, roleLabel, storageBytes } from "./plans.js";
import { cleanGame, cleanName, MOMENT_TYPES } from "./protocol.js";

// A Worker takes a request body of at most 100 MB, so a clip bigger than that (Pro and Ultra take them) goes up in
// parts of PART_BYTES each, put together in R2 as a multipart upload. R2 wants every part but the last the same size,
// and at least 5 MB. UPLOAD_PART_BYTES in the Worker's vars changes it (the tests use small parts).
const MAX_UPLOAD_BYTES = 100 * 1024 * 1024;
export const PART_BYTES = 64 * 1024 * 1024;
const MIN_PART_BYTES = 5 * 1024 * 1024;
const MAX_THUMB_BYTES = 512 * 1024;
const LINK_CODE_MS = 10 * 60_000;
// How often a username (@handle) can be changed: once a month on Free, once a week with a plan.
export const HANDLE_EVERY_FREE_MS = 30 * 24 * 3600_000;
export const HANDLE_EVERY_PAID_MS = 7 * 24 * 3600_000;
// Wrong tries before a sign-in code stops working.
const SIGN_IN_TRIES = 5;
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
CREATE TABLE IF NOT EXISTS uploads (clip TEXT PRIMARY KEY, upload_id TEXT NOT NULL, size INTEGER NOT NULL,
  part_bytes INTEGER NOT NULL, parts TEXT NOT NULL DEFAULT '[]', started INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS tokens (hash TEXT PRIMARY KEY, kind TEXT NOT NULL, email TEXT, created INTEGER NOT NULL,
  expires INTEGER NOT NULL);
`;

// Columns added after the first release. video_v and thumb_v name each upload (its ETag), so a
// browser or Cloudflare's cache that already has it doesn't read R2 again.
const COLUMNS = ["clips ADD COLUMN video_v INTEGER NOT NULL DEFAULT 0", "clips ADD COLUMN thumb_v INTEGER NOT NULL DEFAULT 0",
  // Sign-ins on the hub (the website) end after WEB_SESSION_MS; the app's never do.
  "devices ADD COLUMN expires INTEGER",
  // What each sign-in is, for the owner's list of devices: the browser or app, the country Cloudflare saw (never the
  // address) and when it was last used.
  "devices ADD COLUMN agent TEXT", "devices ADD COLUMN country TEXT", "devices ADD COLUMN seen INTEGER"];

// Links in account emails: confirming an address lasts a day, a password reset 30 minutes. Each works once.
const VERIFY_MS = 24 * 3600_000;
const RESET_MS = 30 * 60_000;
// At most one email of a kind a minute, so a form can't be used to flood someone's inbox.
const EMAIL_EVERY_MS = 60_000;
const SEEN_EVERY_MS = 3600_000;

// A cloud account reset: a new ACCOUNTS_EPOCH (a var) names every account's Durable Object and R2 folder afresh, so
// the old ones are never reached again, and the directory forgets every handle, email and Google link on it
// (src/handles.js). Empty is the first epoch, as before resets existed.
export function accountsEpoch(env) {
  return String(env?.ACCOUNTS_EPOCH ?? "");
}

export function libraryStub(env, account) {
  const epoch = accountsEpoch(env);
  return env.LIBRARIES.get(env.LIBRARIES.idFromName(epoch ? `${epoch}:${account}` : account));
}

// Who's signing in, for the device list: the user agent (cut short) and the country Cloudflare saw.
export function clientOf(request) {
  return {
    agent: (request.headers.get("user-agent") ?? "").slice(0, 160),
    country: (request.cf?.country ?? request.headers.get("cf-ipcountry") ?? "").slice(0, 2),
  };
}

// The hub: how long a sign-in on the website lasts, how a web password is kept and how many wrong ones lock it.
export const WEB_SESSION_MS = 30 * 24 * 3600_000;
const PASSWORD_ITERATIONS = 100_000;
const PASSWORD_TRIES = 5;
const PASSWORD_LOCK_MS = 15 * 60_000;
export const MIN_PASSWORD = 8;
const MAX_PASSWORD = 200;

const LOCKED = "Too many wrong passwords. Try again in 15 minutes, or reset your password from the sign-in page.";

function goodPassword(password) {
  return typeof password === "string" && password.length >= MIN_PASSWORD && password.length <= MAX_PASSWORD;
}

function validDevice(device) {
  return typeof device === "string" && /^[A-Za-z0-9_-]{8,64}$/.test(device) ? device : null;
}

// PBKDF2-SHA256 with a random salt per password. 100,000 rounds is the most Workers' WebCrypto runs.
async function passwordHash(password, salt, iterations = PASSWORD_ITERATIONS) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(password), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits({ name: "PBKDF2", hash: "SHA-256", salt: new TextEncoder().encode(salt), iterations }, key, 256);
  return [...new Uint8Array(bits)].map((b) => b.toString(16).padStart(2, "0")).join("");
}



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

function lineName(lines) {
  return lines >= 2160 ? "4K" : `${lines}p`;
}

function durationName(seconds) {
  return seconds >= 60 ? `${Math.round(seconds / 60)} minutes` : `${seconds} seconds`;
}

function sizeName(bytes) {
  const mb = bytes / (1024 * 1024);
  return mb >= 1024 ? `${Math.round((mb / 1024) * 10) / 10} GB` : `${Math.round(mb)} MB`;
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
    addColumns(this.sql, COLUMNS);
  }

  meta(key) {
    return this.sql.exec("SELECT value FROM meta WHERE key = ?", key).toArray()[0]?.value ?? null;
  }

  setMeta(key, value) {
    this.sql.exec("INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value);
  }

  // The plan, with what a special key adds: extra storage, a different highest cloud resolution and a role. lines is
  // the highest resolution a clip goes up at and clipBytes the biggest clip; recording on the PC isn't limited by plan.
  plan() {
    const name = planName(this.meta("plan"));
    const features = this.features();
    return {
      name,
      label: PLANS[name].label,
      roleLabel: roleLabel(features.role),
      lines: features.lines ?? PLANS[name].lines,
      clipBytes: clipBytes(name),
      clipSeconds: PLANS[name].clipSeconds,
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
    this.setMeta("plan_features", JSON.stringify({ extraMb: features?.extraMb ?? 0, lines: features?.lines ?? null, role: features?.role ?? null }));
  }

  used() {
    return this.sql.exec("SELECT COALESCE(SUM(size), 0) AS used FROM clips WHERE uploaded = 1").toArray()[0].used;
  }

  // The badge others see (null when there's none or it's hidden), and the one the account has.
  badge() {
    return badgeFor(this.plan().name, this.features().role);
  }

  shownBadge() {
    return this.meta("hide_badge") === "1" ? null : this.badge();
  }

  account() {
    const plan = this.plan();
    return {
      id: this.meta("id"),
      name: this.meta("name"),
      handle: this.meta("handle"),
      plan: plan.name,
      planLabel: plan.label,
      roleLabel: plan.roleLabel,
      planKey: this.meta("plan_display") ?? null,
      lines: plan.lines,
      maxClip: plan.clipBytes,
      maxSeconds: plan.clipSeconds,
      badge: this.badge(),
      hideBadge: this.meta("hide_badge") === "1",
      email: this.meta("email"),
      emailVerified: this.meta("email_verified") === "1",
      google: Boolean(this.meta("google_sub")),
      hasPassword: Boolean(this.meta("web_password")),
      friendCode: formatFriendCode(this.meta("id")),
      handleChangeAt: this.nextHandleChange(),
      storage: { used: this.used(), limit: plan.storage },
      clips: this.sql.exec("SELECT COUNT(*) AS n FROM clips WHERE uploaded = 1").toArray()[0].n,
    };
  }

  // A sign-in: a PC running the app, which lasts until it signs out, or the hub, which lasts WEB_SESSION_MS.
  async addDevice(device, web = false, client = {}) {
    const token = randomToken();
    const now = Date.now();
    this.sql.exec("DELETE FROM devices WHERE expires IS NOT NULL AND expires < ?", now);
    this.sql.exec("INSERT INTO devices (token_hash, device, added, expires, agent, country, seen) VALUES (?, ?, ?, ?, ?, ?, ?)",
      await sha256Hex(token), device, now, web ? now + WEB_SESSION_MS : null, String(client.agent ?? "").slice(0, 160),
      String(client.country ?? "").slice(0, 2), now);
    // The first device ever to reach the account, kept for its owner (GET /security).
    if (!this.meta("first_device")) {
      this.setMeta("first_device", JSON.stringify({ kind: web ? "web" : "app", agent: client.agent ?? "", country: client.country ?? "", at: now }));
    }
    return token;
  }

  async tokenHash(request) {
    const header = request.headers.get("authorization") ?? "";
    const token = header.startsWith("Bearer ") ? header.slice(7).trim() : "";
    return token && token.length <= 128 ? sha256Hex(token) : null;
  }

  async authorized(request) {
    const hash = await this.tokenHash(request);
    if (!hash) return false;
    const now = Date.now();
    const row = this.sql.exec("SELECT seen FROM devices WHERE token_hash = ? AND (expires IS NULL OR expires > ?)", hash, now).toArray()[0];
    if (!row) return false;
    if (!row.seen || now - row.seen > SEEN_EVERY_MS) this.sql.exec("UPDATE devices SET seen = ? WHERE token_hash = ?", now, hash);
    return true;
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

  // After a reset the videos go in a folder of their own, so the old library/ folder can be emptied with an R2
  // lifecycle rule (docs/DEVELOPMENT.md).
  key(id, kind) {
    const epoch = accountsEpoch(this.env);
    return `library${epoch ? `-${epoch}` : ""}/${this.meta("id")}/${id}.${kind === "thumb" ? "img" : "mp4"}`;
  }

  async fetch(request) {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

    if (path === "/create" && method === "POST") return this.create(request);
    // A PC still signed in to an account that was reset hears it's signed out.
    if (!this.meta("id")) return request.headers.has("authorization") ? fail(401, "This account isn't here any more. Sign in again.") : fail(404, "There's no such account.");
    if (path === "/join" && method === "POST") return this.join(request);
    if (path === "/auth/login" && method === "POST") return this.passwordLogin(request);
    if (path === "/auth/forgot" && method === "POST") return this.forgot(request);
    if (path === "/auth/use" && method === "POST") return this.useToken(request);
    if (path === "/auth/google" && method === "POST") return this.googleSignIn(request);
    if (path.startsWith("/public/")) return this.publicRequest(request, path.slice(8));
    if (path.startsWith("/peer/") && method === "POST") return this.peer(path.slice(6), await readJson(request));
    if (path === "/admin/plan" && method === "POST") {
      const body = await readJson(request);
      if (!PLANS[body?.plan]) return fail(400, "Unknown plan.");
      await this.forgetLicense();
      this.setMeta("plan", body.plan);
      await this.syncOwner();
      return json(this.account());
    }

    if (!(await this.authorized(request))) return fail(401, "This PC isn't signed in to that account any more.");
    await this.recheckPlan();
    await this.backfillFeed();

    if (path === "/plan" && method === "POST") return this.claimPlan(request);
    if (path === "/plan" && method === "DELETE") {
      await this.forgetLicense();
      this.setMeta("plan", "free");
      await this.syncOwner();
      return json(this.account());
    }
    // Signing this sign-in out (the hub's Sign out).
    if (path === "/session" && method === "DELETE") {
      this.sql.exec("DELETE FROM devices WHERE token_hash = ?", await this.tokenHash(request));
      return json({ ok: true });
    }
    if (path === "/password" && method === "POST") return this.setPassword(request);
    if (path === "/security" && method === "GET") return this.security(request);
    if (path === "/email" && method === "POST") return this.changeEmail(request);
    if (path === "/verify" && method === "POST") {
      const sent = this.meta("email") && this.meta("email_verified") !== "1" ? await this.sendVerify(request, this.meta("email")) : false;
      return json({ sent });
    }
    const d = path.match(/^\/devices\/([0-9a-f]{12})$/);
    if (d && method === "DELETE") {
      this.sql.exec("DELETE FROM devices WHERE substr(token_hash, 1, 12) = ?", d[1]);
      return this.security(request);
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
    if (path === "/link" && method === "POST") return json(this.linkCode());
    if (path === "/clips" && method === "GET") {
      const rows = this.sql.exec("SELECT * FROM clips ORDER BY recorded DESC").toArray();
      return json({ clips: rows.map((r) => this.clipJson(r)), account: this.account() });
    }
    if (path === "/clips" && method === "POST") return this.addClip(request);

    const m = path.match(/^\/clips\/([0-9A-Z]{6})(?:\/(video|thumb))?(?:\/parts(?:\/(\d{1,4}|done))?)?$/);
    if (m) {
      const [, id, kind, part] = m;
      const parts = path.includes("/parts");
      const row = this.clipRow(id);
      if (!row) return fail(404, "That clip isn't in your cloud library.");
      if (parts) {
        if (kind !== "video") return fail(404, "Not found.");
        if (!part && method === "POST") return this.startParts(request, row);
        if (part === "done" && method === "POST") return this.finishParts(row);
        if (part && part !== "done" && method === "PUT") return this.uploadPart(request, row, Number(part));
        return fail(404, "Not found.");
      }
      if (!kind && method === "PATCH") return this.updateClip(request, row);
      if (!kind && method === "DELETE") {
        await this.abortParts(id);
        await this.env.CLIPS.delete([this.key(id, "video"), this.key(id, "thumb")]);
        this.sql.exec("DELETE FROM clips WHERE id = ?", id);
        await this.syncClip(id);
        return json({ ok: true });
      }
      if (kind && method === "PUT") return this.upload(request, row, kind);
      if (kind && method === "GET") return this.stream(request, row, kind);
    }
    return fail(404, "Not found.");
  }

  // A new account: a name, an email and a password, or (from src/accounts.js) a Google account instead of the
  // password. The Worker builds the body, so `google` never comes from a client.
  async create(request) {
    if (this.meta("id")) return fail(409, "That account already exists.");
    const body = await readJson(request);
    const id = request.headers.get("x-account-id");
    const name = cleanName(body?.name);
    const device = validDevice(body?.device);
    const email = normalizeEmail(body?.email);
    const google = typeof body?.google === "string" && body.google.length > 0 && body.google.length <= 255 ? body.google : null;
    if (!isSquadId(id) || !name || !device) return fail(400, "An account needs your name and this PC's id.");
    if (!email) return fail(400, "Type your email address. It's how you get back in if you lose this PC.");
    if (!google && !goodPassword(body?.password)) return fail(400, `Pick a password of ${MIN_PASSWORD} to ${MAX_PASSWORD} characters.`);
    const claimed = await this.handles("/email/claim", { email, account: id });
    if (!claimed.ok) return fail(claimed.status, (await claimed.json()).error);
    if (google) {
      const linked = await this.handles("/google/claim", { sub: google, account: id });
      if (!linked.ok) {
        await this.handles("/email/release", { email, account: id });
        return fail(linked.status, (await linked.json()).error);
      }
    }
    this.setMeta("id", id);
    this.setMeta("name", name);
    this.setMeta("plan", "free");
    this.setMeta("created", String(Date.now()));
    this.setMeta("email", email);
    if (google) {
      this.setMeta("google_sub", google);
      this.setMeta("email_verified", "1");
    } else {
      await this.storePassword(body.password);
    }
    const token = await this.addDevice(device, body?.web === true, clientOf(request));
    if (!google) await this.sendVerify(request, email);
    return json({ token, account: this.account() });
  }

  // Signing another PC in with a code made on one that is signed in.
  async join(request) {
    const body = await readJson(request);
    const device = validDevice(body?.device);
    const link = JSON.parse(this.meta("link") ?? "null");
    if (!device || !link || Date.now() > link.until || !sameSecret(String(body?.secret ?? ""), link.secret)) {
      // A few wrong guesses and the code is gone, so it can't be guessed by trying them all.
      if (link && device) {
        link.fails = (link.fails ?? 0) + 1;
        if (link.fails >= SIGN_IN_TRIES) this.sql.exec("DELETE FROM meta WHERE key = 'link'");
        else this.setMeta("link", JSON.stringify(link));
      }
      return fail(403, "That sign-in code isn't right or has run out. Make a new one on the PC that's signed in.");
    }
    this.sql.exec("DELETE FROM meta WHERE key = 'link'");
    const token = await this.addDevice(device, body?.web === true, clientOf(request));
    return json({ token, account: this.account() });
  }

  // A one-time code that signs another PC (or the hub) in, for LINK_CODE_MS.
  linkCode() {
    const secret = randomCode(6);
    this.setMeta("link", JSON.stringify({ secret, until: Date.now() + LINK_CODE_MS }));
    return { code: (this.meta("id") + secret).match(/.{1,4}/g).join("-"), expires: Date.now() + LINK_CODE_MS };
  }

  // ---- email and password ----

  async storePassword(password) {
    const salt = randomToken();
    this.setMeta("web_password", JSON.stringify({ salt, iterations: PASSWORD_ITERATIONS, hash: await passwordHash(password, salt) }));
    this.sql.exec("DELETE FROM meta WHERE key = 'web_fails'");
  }

  // "ok", "wrong" or "locked": five wrong passwords in a row lock it for 15 minutes, even for the right one.
  async checkPassword(password) {
    const saved = JSON.parse(this.meta("web_password") ?? "null");
    if (!saved || typeof password !== "string" || password.length > MAX_PASSWORD) return "wrong";
    const fails = JSON.parse(this.meta("web_fails") ?? "null") ?? { count: 0, until: 0 };
    if (fails.until > Date.now()) return "locked";
    if (sameSecret(await passwordHash(password, saved.salt, saved.iterations), saved.hash)) {
      this.sql.exec("DELETE FROM meta WHERE key = 'web_fails'");
      return "ok";
    }
    fails.count += 1;
    if (fails.count >= PASSWORD_TRIES) {
      fails.count = 0;
      fails.until = Date.now() + PASSWORD_LOCK_MS;
    }
    this.setMeta("web_fails", JSON.stringify(fails));
    return "wrong";
  }

  // Changing the password needs the current one; taking it off is only for an account that signs in with Google.
  async setPassword(request) {
    const body = await readJson(request);
    if (this.meta("web_password")) {
      const check = await this.checkPassword(body?.current);
      if (check !== "ok") return check === "locked" ? fail(429, LOCKED) : fail(403, "Your current password isn't right.");
    }
    if (body?.password === null || body?.password === "") {
      if (!this.meta("google_sub")) return fail(400, "Keep a password, so you can still sign in. Add Google sign-in first to go without one.");
      this.sql.exec("DELETE FROM meta WHERE key IN ('web_password', 'web_fails')");
      return json(this.account());
    }
    if (!goodPassword(body?.password)) return fail(400, `Pick a password of ${MIN_PASSWORD} to ${MAX_PASSWORD} characters.`);
    await this.storePassword(body.password);
    return json(this.account());
  }

  // Signing in with the email (looked up by the Worker) and password: the app (device) or the hub (web).
  async passwordLogin(request) {
    const body = await readJson(request);
    const check = await this.checkPassword(body?.password);
    if (check === "locked") return fail(429, LOCKED);
    if (check !== "ok") return fail(403, "That email and password don't match.");
    const web = body?.web === true;
    const device = web ? `web-${randomCode(10)}` : validDevice(body?.device);
    if (!device) return fail(400, "Signing in needs this PC's id.");
    const token = await this.addDevice(device, web, clientOf(request));
    return json({ token, account: this.account() });
  }

  // A link for an account email, kept hashed, replacing any earlier one of its kind. Null when one went out less
  // than a minute ago.
  async newToken(request, kind, email, ms) {
    const now = Date.now();
    this.sql.exec("DELETE FROM tokens WHERE expires < ?", now);
    if (this.sql.exec("SELECT 1 FROM tokens WHERE kind = ? AND created > ?", kind, now - EMAIL_EVERY_MS).toArray().length > 0) return null;
    this.sql.exec("DELETE FROM tokens WHERE kind = ?", kind);
    const secret = randomToken();
    this.sql.exec("INSERT INTO tokens (hash, kind, email, created, expires) VALUES (?, ?, ?, ?, ?)", await sha256Hex(secret), kind, email, now, now + ms);
    // The token is after the #, so it never reaches a server log; the page posts it (src/hub.js).
    return `${new URL(request.url).origin}/hub/${kind === "reset" ? "reset" : "confirm"}#t=${this.meta("id")}.${secret}`;
  }

  async sendVerify(request, email) {
    const link = await this.newToken(request, "verify", email, VERIFY_MS);
    if (!link) return false;
    return sendEmail(this.env, {
      to: email,
      subject: "Confirm your email for Clip Engine",
      text: `Hi ${this.meta("name")},\n\nConfirm this is your email for your Clip Engine cloud account, so you can get back in if you ever lose your password:\n\n${link}\n\nThe link works for 24 hours. If you didn't make a Clip Engine account, ignore this email.`,
    });
  }

  // "Forgot your password?": a reset link to the account's email. The Worker answers the same whether or not the
  // email has an account.
  async forgot(request) {
    const email = this.meta("email");
    const link = email && (await this.newToken(request, "reset", email, RESET_MS));
    if (link) {
      await sendEmail(this.env, {
        to: email,
        subject: "Reset your Clip Engine password",
        text: `Someone (hopefully you) asked to reset the password for your Clip Engine account.\n\n${link}\n\nThe link works once, for 30 minutes. Resetting signs out every PC and browser on the account. If it wasn't you, ignore this email; your password stays as it is.`,
      });
    }
    return json({ ok: true });
  }

  // Opening a link from an email: confirming the address (verify), a new one (email), or a new password (reset).
  async useToken(request) {
    const body = await readJson(request);
    const kinds = Array.isArray(body?.kinds) ? body.kinds : [];
    const secret = typeof body?.secret === "string" && body.secret.length <= 128 ? body.secret : "";
    const row = secret && this.sql.exec("SELECT * FROM tokens WHERE hash = ? AND expires > ?", await sha256Hex(secret), Date.now()).toArray()[0];
    if (!row || !kinds.includes(row.kind)) return fail(403, "That link isn't right or has run out. Ask for a new one.");
    if (row.kind === "reset") {
      if (!goodPassword(body.password)) return fail(400, `Pick a password of ${MIN_PASSWORD} to ${MAX_PASSWORD} characters.`);
      await this.storePassword(body.password);
      // Whoever had the old password is signed out everywhere.
      this.sql.exec("DELETE FROM devices");
      this.sql.exec("DELETE FROM meta WHERE key = 'link'");
      this.sql.exec("DELETE FROM tokens WHERE kind = 'reset'");
      if (row.email === this.meta("email")) this.setMeta("email_verified", "1");
      return json({ ok: true, kind: "reset", email: this.meta("email") });
    }
    if (row.kind === "email" && row.email !== this.meta("email")) {
      // Claiming the new one lets the old one go.
      const claimed = await this.handles("/email/claim", { email: row.email, account: this.meta("id") });
      if (!claimed.ok) return fail(claimed.status, (await claimed.json()).error);
      this.setMeta("email", row.email);
    }
    if (row.email !== this.meta("email")) return fail(403, "That link is for an email this account doesn't use any more.");
    this.setMeta("email_verified", "1");
    this.sql.exec("DELETE FROM tokens WHERE hash = ?", row.hash);
    return json({ ok: true, kind: row.kind, email: row.email });
  }

  // A new email for the account (or a first one, for an account made before emails): it changes once the link sent
  // to it is opened, and the old address hears about it.
  async changeEmail(request) {
    const body = await readJson(request);
    const email = normalizeEmail(body?.email);
    if (!email) return fail(400, "That isn't an email address.");
    if (this.meta("web_password")) {
      const check = await this.checkPassword(body?.password);
      if (check !== "ok") return check === "locked" ? fail(429, LOCKED) : fail(403, "Your password isn't right.");
    }
    const old = this.meta("email");
    if (email === old) {
      const sent = this.meta("email_verified") !== "1" ? await this.sendVerify(request, email) : false;
      return json({ ...this.account(), sent });
    }
    if ((await this.handles("/email/lookup", { email })).ok) return fail(409, "That email is already on a Clip Engine account.");
    const link = await this.newToken(request, "email", email, VERIFY_MS);
    if (!link) return fail(429, "An email just went out. Wait a minute and try again.");
    const sent = await sendEmail(this.env, {
      to: email,
      subject: "Confirm your new email for Clip Engine",
      text: `Open this link to use ${email} for your Clip Engine cloud account:\n\n${link}\n\nThe link works for 24 hours. If you didn't ask for this, ignore this email.`,
    });
    if (old) {
      await sendEmail(this.env, {
        to: old,
        subject: "Your Clip Engine email is changing",
        text: `Someone signed in to your Clip Engine account asked to change its email to ${email}. It changes once they open the link sent there.\n\nIf this wasn't you, reset your password now: ${new URL(request.url).origin}/hub/signin`,
      });
    }
    return json({ ...this.account(), pendingEmail: email, sent });
  }

  // Signing in with Google (src/accounts.js has checked Google's answer). An account found by its email is linked to
  // the Google account. When that email was never confirmed, someone else may have typed it in to be there when its
  // owner arrives, so the password and every sign-in on it go and only the Google account's owner gets in.
  async googleSignIn(request) {
    const body = await readJson(request);
    const sub = typeof body?.sub === "string" && body.sub.length > 0 && body.sub.length <= 255 ? body.sub : null;
    const email = normalizeEmail(body?.email);
    if (!sub || !email) return fail(400, "Google didn't say who you are.");
    const linked = this.meta("google_sub");
    if (linked && linked !== sub) return fail(409, "This Clip Engine account is linked to a different Google account.");
    if (!linked) {
      if (this.meta("email") !== email) return fail(409, "This Clip Engine account uses a different email.");
      const claimed = await this.handles("/google/claim", { sub, account: this.meta("id") });
      if (!claimed.ok) return fail(claimed.status, (await claimed.json()).error);
      if (this.meta("email_verified") !== "1") {
        this.sql.exec("DELETE FROM meta WHERE key IN ('web_password', 'web_fails', 'link')");
        this.sql.exec("DELETE FROM devices");
        this.sql.exec("DELETE FROM tokens");
      }
      this.setMeta("google_sub", sub);
      this.setMeta("email_verified", "1");
    }
    // The app gets a one-time code back through a clipengine:// link and signs in with it, as with Link another PC.
    if (body.app === true) return json(this.linkCode());
    const token = await this.addDevice(`web-${randomCode(10)}`, true, clientOf(request));
    return json({ token, account: this.account() });
  }

  // The owner's view of who can get in: the email, Google, the first device that ever reached the account and every
  // sign-in now, newest first. Each can be signed out (DELETE /devices/<id>).
  async security(request) {
    const now = Date.now();
    const mine = await this.tokenHash(request);
    const rows = this.sql.exec("SELECT * FROM devices WHERE expires IS NULL OR expires > ? ORDER BY COALESCE(seen, added) DESC", now).toArray();
    const device = (r) => ({
      id: r.token_hash.slice(0, 12), kind: r.expires ? "web" : "app", agent: r.agent ?? "", country: r.country ?? "",
      added: r.added, seen: r.seen ?? r.added, current: r.token_hash === mine,
    });
    let first = JSON.parse(this.meta("first_device") ?? "null");
    if (!first) {
      // Accounts made before devices were logged: the oldest sign-in still on it.
      const oldest = this.sql.exec("SELECT * FROM devices ORDER BY added LIMIT 1").toArray()[0];
      first = oldest ? { kind: oldest.expires ? "web" : "app", agent: oldest.agent ?? "", country: oldest.country ?? "", at: oldest.added } : null;
    }
    return json({
      email: this.meta("email"), emailVerified: this.meta("email_verified") === "1", google: Boolean(this.meta("google_sub")),
      hasPassword: Boolean(this.meta("web_password")), firstDevice: first, devices: rows.map(device),
    });
  }

  async updateAccount(request) {
    const body = await readJson(request);
    let renamed = false;
    if (typeof body?.hideBadge === "boolean") {
      this.setMeta("hide_badge", body.hideBadge ? "1" : "0");
      await this.syncOwner();
    }
    if (body?.name !== undefined) {
      const name = cleanName(body.name);
      if (!name) return fail(400, "Pick a name to show on your profile.");
      renamed = name !== this.meta("name");
      this.setMeta("name", name);
    }
    if (body?.handle !== undefined) {
      if (body.handle === null || body.handle === "") {
        if (this.meta("handle")) {
          await this.handles("/release", { account: this.meta("id") });
          this.sql.exec("DELETE FROM meta WHERE key = 'handle'");
          renamed = true;
        }
      } else {
        const handle = normalizeHandle(body.handle);
        if (!handle) return fail(400, "Usernames are 3 to 24 letters, numbers, _ or .");
        const current = this.meta("handle");
        if (handle !== current) {
          // Picking the first one is free; after that, changes are limited.
          const next = this.nextHandleChange();
          // Turning the profile off and on again with a new name is still a change.
          const hadOne = Boolean(current || this.meta("handle_ever"));
          if (hadOne && next && Date.now() < next) {
            const when = new Date(next).toLocaleDateString("en-US", { month: "long", day: "numeric", timeZone: "UTC" });
            const how = this.plan().name === "free" ? "once a month, or once a week with Pro or Ultra" : "once a week";
            return fail(429, `You can change your username ${how}. Next change: ${when}.`);
          }
          const claimed = await this.handles("/claim", { handle, account: this.meta("id") });
          if (!claimed.ok) return fail(claimed.status, (await claimed.json()).error);
          this.setMeta("handle", handle);
          if (hadOne) this.setMeta("handle_changed", String(Date.now()));
          this.setMeta("handle_ever", "1");
          renamed = true;
        }
      }
    }
    if (renamed) {
      await this.tellFriendsRenamed();
      await this.syncOwner();
    }
    return json(this.account());
  }

  // When the username can next be changed, or null if it can be changed now.
  nextHandleChange() {
    const last = Number(this.meta("handle_changed") ?? 0);
    if (!last) return null;
    const next = last + (this.plan().name === "free" ? HANDLE_EVERY_FREE_MS : HANDLE_EVERY_PAID_MS);
    return next > Date.now() ? next : null;
  }

  // Friends keep a copy of your name and username; a change is sent to each of them.
  async tellFriendsRenamed() {
    const rows = this.sql.exec("SELECT account FROM friends").toArray();
    await Promise.allSettled(rows.map((r) => this.tellPeer(r.account, "renamed")));
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
    this.setFeatures({ ...check.features, role: check.role });
    this.setMeta("plan_key", key);
    this.setMeta("plan_license", check.license);
    this.setMeta("plan_display", check.display || `${key.slice(0, 4)}…${key.slice(-4)}`);
    this.setMeta("plan_checked", String(Date.now()));
    this.setMeta("plan_ok", String(Date.now()));
    await this.syncOwner();
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
    const badge = JSON.stringify(this.shownBadge());
    const check = await checkLicense(this.env, key);
    if (check.ok) {
      this.setMeta("plan", check.plan);
      this.setFeatures({ ...check.features, role: check.role });
      this.setMeta("plan_ok", String(Date.now()));
    } else if (check.rejected) {
      await this.forgetLicense();
      this.setMeta("plan", "free");
    } else if (Date.now() - Number(this.meta("plan_ok") ?? 0) > OFFLINE_GRACE_MS) {
      this.setMeta("plan", "free");
    }
    if (JSON.stringify(this.shownBadge()) !== badge) await this.syncOwner();
  }

  // ---- the hub (src/feed.js) ----

  owner() {
    return { name: this.meta("name"), handle: this.meta("handle"), badge: this.shownBadge() };
  }

  // Puts a clip in the hub when it's public and uploaded, and takes it off otherwise.
  async syncClip(id) {
    const row = this.clipRow(id);
    const code = this.meta("id") + id;
    if (row && row.visibility === "public" && row.uploaded === 1) {
      await this.handles("/feed/put", { ...this.clipJson(row), account: this.meta("id"), owner: this.owner() });
    } else {
      await this.handles("/feed/remove", { code });
    }
  }

  // Clips made public before the hub existed go in the first time the account is used after it.
  async backfillFeed() {
    if (this.meta("feed_v")) return;
    this.setMeta("feed_v", "1");
    const rows = this.sql.exec("SELECT id FROM clips WHERE visibility = 'public' AND uploaded = 1").toArray();
    for (const row of rows) await this.syncClip(row.id);
  }

  // The name, username or badge on every one of this account's clips in the hub.
  async syncOwner() {
    const any = this.sql.exec("SELECT 1 FROM clips WHERE visibility = 'public' AND uploaded = 1 LIMIT 1").toArray().length > 0;
    if (any) await this.handles("/feed/owner", { account: this.meta("id"), ...this.owner() });
  }

  // ---- friends ----

  me() {
    return { account: this.meta("id"), name: this.meta("name"), handle: this.meta("handle") };
  }

  friendRow(account) {
    return this.sql.exec("SELECT * FROM friends WHERE account = ?", account).toArray()[0] ?? null;
  }

  peerStub(account) {
    return libraryStub(this.env, account);
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
        return {
          ...row(r), name: here?.name || r.name, handle: here?.handle ?? r.handle, online: Boolean(here), game: here?.game ?? "",
          invite: here?.invite ?? null, onlineSince: here?.onlineSince ?? null, gameSince: here?.gameSince ?? null,
        };
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
    if (what === "renamed") {
      this.sql.exec("UPDATE friends SET name = ?, handle = ? WHERE account = ?", name, handle, from);
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
      if (known && !known.uploaded && Number.isInteger(body?.width) && Number.isInteger(body?.height)) {
        // Not up yet: the copy that goes up this time may be a different size, for a plan that changed since.
        this.sql.exec("UPDATE clips SET width = ?, height = ? WHERE id = ?", body.width, body.height, known.id);
        return json(this.clipJson(this.clipRow(known.id)));
      }
      if (known) return json(this.clipJson(known));
    }
    const tooLong = this.refuseLength(seconds);
    if (tooLong) return tooLong;
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
    await this.syncClip(row.id);
    return json(this.clipJson(this.clipRow(row.id)));
  }

  async upload(request, row, kind) {
    const length = Number(request.headers.get("content-length"));
    if (!Number.isFinite(length) || length <= 0) return fail(411, "Uploads need their length.");
    if (kind === "thumb") {
      if (length > MAX_THUMB_BYTES) return fail(413, "Thumbnails are at most 512 KB.");
      const type = request.headers.get("content-type") === "image/png" ? "image/png" : "image/jpeg";
      await this.env.CLIPS.put(this.key(row.id, "thumb"), withLength(request.body, length), { httpMetadata: { contentType: type } });
      this.sql.exec("UPDATE clips SET thumb = 1, thumb_v = ? WHERE id = ?", Math.max(Date.now(), row.thumb_v + 1), row.id);
      if (row.visibility === "public" && row.uploaded) await this.syncClip(row.id);
      return json({ ok: true });
    }
    const refused = this.refuseVideo(row, length);
    if (refused) return refused;
    if (length > MAX_UPLOAD_BYTES) return fail(413, "Clips over 100 MB go up in parts. Update Clip Engine to upload this one.");
    const object = await this.env.CLIPS.put(this.key(row.id, "video"), withLength(request.body, length), {
      httpMetadata: { contentType: "video/mp4" },
    });
    await this.abortParts(row.id);
    return this.uploaded(row, object?.size ?? length);
  }

  // Why the plan's cloud library won't take this clip's video, or null when it will: a clip above the plan's
  // resolution (4K needs Pro or Ultra), bigger than its biggest clip, or one that doesn't fit in what's left.
  refuseVideo(row, length) {
    const tooLong = this.refuseLength(row.seconds);
    if (tooLong) return tooLong;
    const plan = this.plan();
    if (row.height > plan.lines) {
      return fail(403, `Your ${plan.label} plan puts clips in the cloud at up to ${lineName(plan.lines)}. `
        + "Pro and Ultra take 4K clips. Recording in 4K on your PC works on every plan.");
    }
    if (length > plan.clipBytes) {
      return fail(413, `Your ${plan.label} plan takes clips of up to ${sizeName(plan.clipBytes)} in the cloud. `
        + (plan.name === "ultra" ? "Upload a shorter clip." : "Pro and Ultra take bigger ones."));
    }
    if (this.used() - (row.uploaded ? row.size : 0) + length > plan.storage) {
      return fail(507, `Your ${plan.label} cloud library is full. Remove some clips to make room.`);
    }
    return null;
  }

  // A clip longer than the plan's cloud library takes: 40 seconds on Free, 10 minutes on Pro and 30 on Ultra. A
  // second over is let through, for how lengths round.
  refuseLength(seconds) {
    const plan = this.plan();
    if (seconds <= plan.clipSeconds + 1) return null;
    return fail(403, `Your ${plan.label} plan takes clips of up to ${durationName(plan.clipSeconds)} in the cloud. `
      + (plan.name === "ultra" ? "Trim this one to upload it." : "Trim this one, or Pro and Ultra take longer ones."));
  }

  async uploaded(row, size) {
    this.sql.exec("UPDATE clips SET uploaded = 1, size = ?, video_v = ? WHERE id = ?", size, Math.max(Date.now(), row.video_v + 1), row.id);
    if (row.visibility === "public") await this.syncClip(row.id);
    return json(this.clipJson(this.clipRow(row.id)));
  }

  // ---- uploads in parts (clips over 100 MB) ----
  //
  //   POST /clips/<clip>/video/parts         { size }  starts one: { partBytes, parts }
  //   PUT  /clips/<clip>/video/parts/<n>     part n, from 1; each is partBytes but the last
  //   POST /clips/<clip>/video/parts/done    puts the parts together; answers the clip
  // Starting again drops one that didn't finish, as does removing the clip.

  async startParts(request, row) {
    const body = await readJson(request);
    const size = Number(body?.size);
    if (!Number.isInteger(size) || size <= 0) return fail(400, "An upload in parts needs the clip's size.");
    const refused = this.refuseVideo(row, size);
    if (refused) return refused;
    await this.abortParts(row.id);
    const configured = Number(this.env?.UPLOAD_PART_BYTES);
    const partBytes = Number.isInteger(configured) && configured >= MIN_PART_BYTES && configured <= MAX_UPLOAD_BYTES ? configured : PART_BYTES;
    const upload = await this.env.CLIPS.createMultipartUpload(this.key(row.id, "video"), {
      httpMetadata: { contentType: "video/mp4" },
    });
    this.sql.exec("INSERT INTO uploads (clip, upload_id, size, part_bytes, parts, started) VALUES (?, ?, ?, ?, '[]', ?)",
      row.id, upload.uploadId, size, partBytes, Date.now());
    return json({ partBytes, parts: Math.ceil(size / partBytes) });
  }

  async uploadPart(request, row, number) {
    const upload = this.sql.exec("SELECT * FROM uploads WHERE clip = ?", row.id).toArray()[0];
    if (!upload) return fail(409, "That upload isn't going on any more. Start it again.");
    const count = Math.ceil(upload.size / upload.part_bytes);
    if (!Number.isInteger(number) || number < 1 || number > count) return fail(400, `Parts are numbered 1 to ${count}.`);
    const expected = number < count ? upload.part_bytes : upload.size - upload.part_bytes * (count - 1);
    const length = Number(request.headers.get("content-length"));
    if (length !== expected) return fail(400, `Part ${number} is ${expected} bytes.`);
    const part = await this.env.CLIPS.resumeMultipartUpload(this.key(row.id, "video"), upload.upload_id)
      .uploadPart(number, withLength(request.body, length));
    // Read again: other parts may have finished while this one went up.
    const now = this.sql.exec("SELECT parts FROM uploads WHERE clip = ? AND upload_id = ?", row.id, upload.upload_id).toArray()[0];
    if (!now) return fail(409, "That upload isn't going on any more. Start it again.");
    const done = JSON.parse(now.parts).filter((p) => p.partNumber !== number);
    done.push({ partNumber: number, etag: part.etag });
    this.sql.exec("UPDATE uploads SET parts = ? WHERE clip = ?", JSON.stringify(done), row.id);
    return json({ ok: true, part: number });
  }

  async finishParts(row) {
    const upload = this.sql.exec("SELECT * FROM uploads WHERE clip = ?", row.id).toArray()[0];
    if (!upload) return fail(409, "That upload isn't going on any more. Start it again.");
    const count = Math.ceil(upload.size / upload.part_bytes);
    const parts = JSON.parse(upload.parts).sort((a, b) => a.partNumber - b.partNumber);
    if (parts.length !== count) return fail(409, `${count - parts.length} of ${count} parts haven't gone up yet.`);
    // The plan may have changed while it went up.
    const refused = this.refuseVideo(row, upload.size);
    if (refused) {
      await this.abortParts(row.id);
      return refused;
    }
    const object = await this.env.CLIPS.resumeMultipartUpload(this.key(row.id, "video"), upload.upload_id).complete(parts);
    this.sql.exec("DELETE FROM uploads WHERE clip = ?", row.id);
    return this.uploaded(row, object?.size ?? upload.size);
  }

  async abortParts(id) {
    const upload = this.sql.exec("SELECT * FROM uploads WHERE clip = ?", id).toArray()[0];
    if (!upload) return;
    this.sql.exec("DELETE FROM uploads WHERE clip = ?", id);
    try {
      await this.env.CLIPS.resumeMultipartUpload(this.key(id, "video"), upload.upload_id).abort();
    } catch {
      // Gone already; R2 drops unfinished uploads after a week anyway.
    }
  }

  // Says the Worker may serve this clip's video or thumbnail (src/media.js), without reading R2 here.
  stream(request, row, kind) {
    if (kind === "video" && !row.uploaded) return fail(404, "That clip is still uploading.");
    if (kind === "thumb" && !row.thumb) return fail(404, "No thumbnail.");
    const version = kind === "video" ? row.video_v : row.thumb_v;
    return mediaPointer(this.key(row.id, kind), {
      type: kind === "thumb" ? "image/jpeg" : "video/mp4",
      cacheControl: row.visibility === "private" ? "private, max-age=3600" : "public, max-age=3600",
      version: version ? `${row.id}-${kind}-${version}` : null,
      size: kind === "video" ? row.size : null,
    });
  }

  // What anyone can see: an unlisted or public clip by its code, and the public profile.
  async publicRequest(request, rest) {
    if (rest === "profile") {
      await this.backfillFeed();
      const rows = this.sql.exec(
        "SELECT * FROM clips WHERE visibility = 'public' AND uploaded = 1 ORDER BY featured DESC, recorded DESC LIMIT 60").toArray();
      return json({ name: this.meta("name"), handle: this.meta("handle"), badge: this.shownBadge(), clips: rows.map((r) => this.clipJson(r)) });
    }
    const m = rest.match(/^clip\/([0-9A-Z]{6})(?:\/(video|thumb))?$/);
    const row = m && this.clipRow(m[1]);
    if (!row || row.visibility === "private" || !row.uploaded) return fail(404, "That clip isn't shared.");
    if (m[2]) return this.stream(request, row, m[2]);
    if (new URL(request.url).searchParams.get("view") === "1") {
      this.sql.exec("UPDATE clips SET views = views + 1 WHERE id = ?", row.id);
      // The hub's count follows along: every view at first, then every tenth, so a popular clip doesn't keep the directory busy.
      const views = row.views + 1;
      if (row.visibility === "public" && (views <= 20 || views % 10 === 0)) {
        await this.handles("/feed/views", { code: this.meta("id") + row.id, views });
      }
    }
    return json({
      ...this.clipJson(this.clipRow(row.id)), ownerName: this.meta("name"), ownerHandle: this.meta("handle"), ownerBadge: this.shownBadge(),
    });
  }
}

