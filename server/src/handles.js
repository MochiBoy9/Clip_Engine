// The service's one directory, a single Durable Object: which account each public profile name
// (@handle) belongs to, so two accounts can't take the same one; who's online and what they're
// playing, which friends see; which cloud account each subscription is on; the names subscribers
// asked to have in the credits; and the special keys made in the key maker. Only the Worker and
// the libraries talk to it. It also keeps the messages and bug reports special-key holders send the
// operator, and the operator's answers.

import { addColumns } from "./codes.js";
import { cleanGrant, CREDIT_GROUPS, formatGrant, newGrantCode, PERKS, PLANS, roleLabel, ROLES } from "./plans.js";

const SCHEMA = `
CREATE TABLE IF NOT EXISTS handles (handle TEXT PRIMARY KEY, account TEXT NOT NULL UNIQUE, claimed INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS presence (account TEXT PRIMARY KEY, name TEXT NOT NULL, handle TEXT, game TEXT NOT NULL,
  invite TEXT, until INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS licenses (license TEXT PRIMARY KEY, account TEXT NOT NULL, claimed INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS credits (license TEXT PRIMARY KEY, name TEXT NOT NULL, plan TEXT NOT NULL, since INTEGER NOT NULL,
  seen INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS grants (code TEXT PRIMARY KEY, plan TEXT NOT NULL, days INTEGER, extra_mb INTEGER NOT NULL DEFAULT 0,
  themes TEXT, lines INTEGER, max_devices INTEGER NOT NULL DEFAULT 3, note TEXT NOT NULL DEFAULT '', created INTEGER NOT NULL,
  first_used INTEGER, last_used INTEGER, revoked INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS grant_devices (code TEXT NOT NULL, device TEXT NOT NULL, first INTEGER NOT NULL,
  PRIMARY KEY (code, device));
CREATE TABLE IF NOT EXISTS grant_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT NOT NULL, kind TEXT NOT NULL,
  name TEXT NOT NULL DEFAULT '', text TEXT NOT NULL, created INTEGER NOT NULL, reply TEXT, replied INTEGER,
  done INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS grant_messages_code ON grant_messages (code, created);
`;

// Which perk each kind of message needs, and how many a key can send a day.
const MESSAGE_KINDS = { message: "contact", bug: "bugs" };
const MESSAGES_PER_DAY = 20;
const MESSAGE_LENGTH = 2000;
const MINUTE = 60_000;

// A credit stays listed this long after the subscriber's app last checked their key.
const CREDIT_KEEP_MS = 30 * 24 * 3600_000;
// Presence lasts this long unless the app says it's still there.
export const PRESENCE_MS = 3 * 60_000;

// 3 to 24 letters, digits, '_' or '.', not starting with a dot; compared without case.
export function normalizeHandle(value) {
  if (typeof value !== "string") return null;
  const handle = value.trim().replace(/^@/, "").toLowerCase();
  return /^[a-z0-9_][a-z0-9_.]{2,23}$/.test(handle) ? handle : null;
}

// Names nobody gets, so a profile can't pass itself off as the app.
const RESERVED = new Set(["admin", "api", "clipengine", "clip_engine", "help", "support", "settings", "squad", "squads", "www", "about"]);

function json(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

export class Handles {
  constructor(ctx) {
    this.sql = ctx.storage.sql;
    this.sql.exec(SCHEMA);
    // When someone came online, and started the game they're playing.
    addColumns(this.sql, ["presence ADD COLUMN online_since INTEGER", "presence ADD COLUMN game_since INTEGER"]);
    // Special keys' roles, perks, credits, reason, a length in minutes and a fixed end. Keys made before
    // lengths were in minutes had days; they move over once.
    addColumns(this.sql, [
      "grants ADD COLUMN role TEXT", "grants ADD COLUMN perks TEXT", "grants ADD COLUMN credits INTEGER NOT NULL DEFAULT 0",
      "grants ADD COLUMN reason TEXT NOT NULL DEFAULT ''", "grants ADD COLUMN show_reason INTEGER NOT NULL DEFAULT 0",
      "grants ADD COLUMN minutes INTEGER", "grants ADD COLUMN ends_at INTEGER",
    ]);
    this.sql.exec("UPDATE grants SET minutes = days * 1440, days = NULL WHERE days IS NOT NULL AND minutes IS NULL");
  }

  async fetch(request) {
    const url = new URL(request.url);
    const answer = await this.directory(request, url);
    if (answer) return answer;
    if (url.pathname === "/lookup") {
      const handle = normalizeHandle(url.searchParams.get("handle"));
      const row = handle && this.sql.exec("SELECT account FROM handles WHERE handle = ?", handle).toArray()[0];
      return row ? json({ account: row.account }) : json({ error: "No one has that name." }, 404);
    }
    if (url.pathname === "/claim" && request.method === "POST") {
      const body = await request.json().catch(() => null);
      const handle = normalizeHandle(body?.handle);
      const account = typeof body?.account === "string" ? body.account : null;
      if (!handle || !account) return json({ error: "Names are 3 to 24 letters, numbers, _ or ." }, 400);
      if (RESERVED.has(handle)) return json({ error: "That name is taken." }, 409);
      const owner = this.sql.exec("SELECT account FROM handles WHERE handle = ?", handle).toArray()[0];
      if (owner && owner.account !== account) return json({ error: "That name is taken." }, 409);
      // One name per account: taking a new one gives the old one up.
      this.sql.exec("DELETE FROM handles WHERE account = ?", account);
      this.sql.exec("INSERT INTO handles (handle, account, claimed) VALUES (?, ?, ?)", handle, account, Date.now());
      return json({ handle });
    }
    if (url.pathname === "/release" && request.method === "POST") {
      const body = await request.json().catch(() => null);
      if (typeof body?.account === "string") this.sql.exec("DELETE FROM handles WHERE account = ?", body.account);
      return json({ ok: true });
    }
    return json({ error: "Not found." }, 404);
  }

  async directory(request, url) {
    const path = url.pathname;
    if (!/^\/(presence|license|credits|grant)(\/|$)/.test(path)) return null;
    const body = request.method === "POST" ? await request.json().catch(() => null) : null;
    const now = Date.now();

    // ---- presence ----
    if (path === "/presence/set" && typeof body?.account === "string") {
      const game = String(body.game ?? "").slice(0, 64);
      const was = this.sql.exec("SELECT * FROM presence WHERE account = ? AND until >= ?", body.account, now).toArray()[0];
      const onlineSince = was?.online_since ?? now;
      const gameSince = !game ? null : was && was.game === game && was.game_since ? was.game_since : now;
      this.sql.exec(
        `INSERT INTO presence (account, name, handle, game, invite, until, online_since, game_since) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT(account) DO UPDATE SET name = excluded.name, handle = excluded.handle, game = excluded.game,
           invite = excluded.invite, until = excluded.until, online_since = excluded.online_since, game_since = excluded.game_since`,
        body.account, String(body.name ?? "").slice(0, 32), body.handle ?? null, game,
        typeof body.invite === "string" ? body.invite.slice(0, 32) : null, now + PRESENCE_MS, onlineSince, gameSince);
      return json({ ok: true });
    }
    if (path === "/presence/clear" && typeof body?.account === "string") {
      this.sql.exec("DELETE FROM presence WHERE account = ?", body.account);
      return json({ ok: true });
    }
    if (path === "/presence/get" && Array.isArray(body?.accounts)) {
      this.sql.exec("DELETE FROM presence WHERE until < ?", now);
      const online = {};
      for (const account of body.accounts.slice(0, 500)) {
        const row = this.sql.exec("SELECT * FROM presence WHERE account = ?", String(account)).toArray()[0];
        if (row) {
          online[row.account] = {
            name: row.name, handle: row.handle, game: row.game, invite: row.invite,
            onlineSince: row.online_since ?? null, gameSince: row.game_since ?? null,
          };
        }
      }
      return json({ online });
    }

    // ---- which account a subscription is on ----
    if (path === "/license/claim" && typeof body?.license === "string" && typeof body?.account === "string") {
      const owner = this.sql.exec("SELECT account FROM licenses WHERE license = ?", body.license).toArray()[0];
      if (owner && owner.account !== body.account) {
        return json({ error: "That subscription is already on another cloud account. Remove it there first (Cloud, Plan)." }, 409);
      }
      if (!owner) this.sql.exec("INSERT INTO licenses (license, account, claimed) VALUES (?, ?, ?)", body.license, body.account, now);
      return json({ ok: true });
    }
    if (path === "/license/release" && typeof body?.license === "string" && typeof body?.account === "string") {
      this.sql.exec("DELETE FROM licenses WHERE license = ? AND account = ?", body.license, body.account);
      return json({ ok: true });
    }

    // ---- credits ----
    if (path === "/credits/set" && typeof body?.license === "string") {
      const name = typeof body.name === "string" ? body.name.trim().replace(/\s+/g, " ").slice(0, 40) : "";
      if (!name) {
        this.sql.exec("DELETE FROM credits WHERE license = ?", body.license);
        return json({ ok: true });
      }
      this.sql.exec(
        `INSERT INTO credits (license, name, plan, since, seen) VALUES (?, ?, ?, ?, ?)
         ON CONFLICT(license) DO UPDATE SET name = excluded.name, plan = excluded.plan, seen = excluded.seen`,
        body.license, name, CREDIT_GROUPS.includes(body.plan) ? body.plan : "pro", now, now);
      return json({ ok: true });
    }
    if (path === "/credits/remove" && typeof body?.license === "string") {
      this.sql.exec("DELETE FROM credits WHERE license = ?", body.license);
      return json({ ok: true });
    }
    // ---- special keys ----
    if (path === "/grant/check" && typeof body?.code === "string") return this.checkGrant(body.code, String(body.device ?? ""), now);
    if (path === "/grant/list") {
      return json({ grants: this.sql.exec("SELECT * FROM grants ORDER BY created DESC").toArray().map((r) => this.grantJson(r, now)) });
    }
    if (path === "/grant/create" && body) {
      const { grant, error } = cleanGrant(body, {
        plan: "pro", role: null, minutes: 30 * 1440, endsAt: null, extraMb: 0, themes: null, lines: null, maxDevices: 3,
        perks: [], credits: false, reason: "", showReason: false, note: "",
      });
      if (error) return json({ error }, 400);
      const count = Number.isInteger(body.count) && body.count >= 1 && body.count <= 100 ? body.count : 1;
      const made = [];
      for (let i = 0; i < count; i++) {
        let code;
        do code = newGrantCode();
        while (this.grantRow(code));
        this.sql.exec(
          `INSERT INTO grants (code, plan, role, minutes, ends_at, extra_mb, themes, lines, max_devices, perks, credits, reason,
             show_reason, note, created) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
          code, grant.plan, grant.role, grant.minutes, grant.endsAt, grant.extraMb, grant.themes ? JSON.stringify(grant.themes) : null,
          grant.lines, grant.maxDevices, JSON.stringify(grant.perks), grant.credits ? 1 : 0, grant.reason, grant.showReason ? 1 : 0,
          grant.note, now + i);
        made.push(this.grantJson(this.grantRow(code), now));
      }
      return json({ grants: made });
    }
    if (path === "/grant/update" && typeof body?.code === "string") {
      const row = this.grantRow(body.code);
      if (!row) return json({ error: "There's no such key." }, 404);
      const { grant, error } = cleanGrant(body, this.grantFields(row));
      if (error) return json({ error }, 400);
      this.sql.exec(
        `UPDATE grants SET plan = ?, role = ?, days = NULL, minutes = ?, ends_at = ?, extra_mb = ?, themes = ?, lines = ?, max_devices = ?,
           perks = ?, credits = ?, reason = ?, show_reason = ?, note = ?, revoked = ? WHERE code = ?`,
        grant.plan, grant.role, grant.minutes, grant.endsAt, grant.extraMb, grant.themes ? JSON.stringify(grant.themes) : null,
        grant.lines, grant.maxDevices, JSON.stringify(grant.perks), grant.credits ? 1 : 0, grant.reason, grant.showReason ? 1 : 0,
        grant.note, grant.revoked ? 1 : 0, row.code);
      if (body.resetDevices) this.sql.exec("DELETE FROM grant_devices WHERE code = ?", row.code);
      if (body.restart) this.sql.exec("UPDATE grants SET first_used = NULL WHERE code = ?", row.code);
      return json(this.grantJson(this.grantRow(row.code), now));
    }

    // ---- messages and bug reports from special keys with a perk for them ----
    if (path === "/grant/message/send" && typeof body?.code === "string") return this.sendMessage(body, now);
    if (path === "/grant/message/list" && typeof body?.code === "string") {
      const { row, error } = this.holder(body.code, String(body.device ?? ""));
      if (error) return json({ error }, 403);
      return json({ messages: this.messages(row.code), perks: this.grantFields(row).perks });
    }
    if (path === "/grant/inbox") {
      const rows = this.sql.exec(
        `SELECT m.*, g.role, g.plan, g.note FROM grant_messages m LEFT JOIN grants g ON g.code = m.code
         ORDER BY m.done, m.created DESC LIMIT 500`).toArray();
      return json({
        messages: rows.map((r) => ({
          ...this.messageJson(r), code: formatGrant(r.code), name: r.name, done: r.done === 1,
          role: r.role ?? null, roleLabel: roleLabel(r.role), plan: r.plan ?? null, note: r.note ?? "",
        })),
      });
    }
    if (path === "/grant/inbox/update" && Number.isInteger(body?.id)) {
      const row = this.sql.exec("SELECT * FROM grant_messages WHERE id = ?", body.id).toArray()[0];
      if (!row) return json({ error: "There's no such message." }, 404);
      if (body.reply !== undefined) {
        const reply = String(body.reply ?? "").trim().slice(0, MESSAGE_LENGTH);
        this.sql.exec("UPDATE grant_messages SET reply = ?, replied = ? WHERE id = ?", reply || null, reply ? now : null, row.id);
      }
      if (body.done !== undefined) this.sql.exec("UPDATE grant_messages SET done = ? WHERE id = ?", body.done ? 1 : 0, row.id);
      return json({ ok: true });
    }

    if (path === "/credits" && request.method === "GET") {
      this.sql.exec("DELETE FROM credits WHERE seen < ?", now - CREDIT_KEEP_MS);
      const rows = this.sql.exec("SELECT name, plan FROM credits ORDER BY since").toArray();
      return json(Object.fromEntries(CREDIT_GROUPS.map((group) => [group, rows.filter((r) => r.plan === group).map((r) => r.name)])));
    }
    return json({ error: "Not found." }, 404);
  }

  grantRow(code) {
    return this.sql.exec("SELECT * FROM grants WHERE code = ?", code).toArray()[0] ?? null;
  }

  grantFields(row) {
    let perks = [];
    try {
      perks = (JSON.parse(row.perks ?? "[]") ?? []).filter((p) => PERKS.includes(p));
    } catch {
      // Unreadable: no perks.
    }
    return {
      plan: row.plan, role: ROLES[row.role] ? row.role : null, minutes: row.minutes ?? (row.days ? row.days * 1440 : null),
      endsAt: row.ends_at ?? null, extraMb: row.extra_mb, themes: row.themes ? JSON.parse(row.themes) : null,
      lines: row.lines, maxDevices: row.max_devices, perks, credits: row.credits === 1, reason: row.reason ?? "",
      showReason: row.show_reason === 1, note: row.note, revoked: row.revoked === 1,
    };
  }

  // A key's end: its length counted from the first time it was used, or its fixed end date, whichever is
  // sooner; null for no end (or a length that hasn't started).
  grantExpires(row) {
    const f = this.grantFields(row);
    const fromUse = f.minutes && row.first_used ? row.first_used + f.minutes * MINUTE : null;
    if (fromUse && f.endsAt) return Math.min(fromUse, f.endsAt);
    return fromUse ?? f.endsAt;
  }

  // Why a key doesn't give anything now, or null when it does.
  grantProblem(row, now) {
    if (!row) return "That key wasn't found. Check every letter.";
    if (row.revoked) return "This key was turned off.";
    const expires = this.grantExpires(row);
    if (expires && expires <= now) return "This key has run out.";
    return null;
  }

  grantJson(row, now) {
    const expires = this.grantExpires(row);
    const devices = this.sql.exec("SELECT COUNT(*) AS n FROM grant_devices WHERE code = ?", row.code).toArray()[0].n;
    const status = row.revoked ? "revoked" : expires && expires <= now ? "expired" : !row.first_used ? "unused" : "active";
    const f = this.grantFields(row);
    const unread = this.sql.exec("SELECT COUNT(*) AS n FROM grant_messages WHERE code = ? AND done = 0", row.code).toArray()[0].n;
    return {
      code: formatGrant(row.code), plan: f.plan, role: f.role, roleLabel: roleLabel(f.role),
      minutes: f.minutes, days: f.minutes && f.minutes % 1440 === 0 ? f.minutes / 1440 : null, endsAt: f.endsAt,
      extraStorageGb: Math.round((f.extraMb / 1024) * 100) / 100, themes: f.themes, lines: f.lines, maxDevices: f.maxDevices,
      perks: f.perks, credits: f.credits, reason: f.reason, showReason: f.showReason, note: f.note, created: row.created,
      firstUsed: row.first_used, lastUsed: row.last_used, expires, devices, status, openMessages: unread,
    };
  }

  checkGrant(code, device, now) {
    const row = this.grantRow(code);
    const problem = this.grantProblem(row, now);
    if (problem) return json({ ok: false, rejected: true, error: problem });
    if (device) {
      const known = this.sql.exec("SELECT 1 FROM grant_devices WHERE code = ? AND device = ?", code, device).toArray().length > 0;
      if (!known) {
        const used = this.sql.exec("SELECT COUNT(*) AS n FROM grant_devices WHERE code = ?", code).toArray()[0].n;
        if (used >= row.max_devices) {
          return json({ ok: false, rejected: true, error: `This key is already used on ${row.max_devices} ${row.max_devices === 1 ? "PC" : "PCs"}.` });
        }
        this.sql.exec("INSERT INTO grant_devices (code, device, first) VALUES (?, ?, ?)", code, device, now);
      }
    }
    if (!row.first_used) this.sql.exec("UPDATE grants SET first_used = ? WHERE code = ?", now, code);
    this.sql.exec("UPDATE grants SET last_used = ? WHERE code = ?", now, code);
    const fresh = this.grantRow(code);
    const f = this.grantFields(fresh);
    return json({
      ok: true,
      plan: PLANS[f.plan] ? f.plan : "free",
      license: `grant:${code}`,
      display: `${formatGrant(code).slice(0, 8)}…${code.slice(-4)}`,
      expires: this.grantExpires(fresh),
      grant: true,
      features: { extraMb: f.extraMb, themes: f.themes, lines: f.lines },
      role: f.role,
      perks: f.perks,
      credits: f.credits,
      reason: f.showReason ? f.reason : "",
    });
  }

  // A key that works now, on a PC it was already used on: what may send and read messages.
  holder(code, device) {
    const row = this.grantRow(code);
    const problem = this.grantProblem(row, Date.now());
    if (problem) return { error: problem };
    const known = device && this.sql.exec("SELECT 1 FROM grant_devices WHERE code = ? AND device = ?", row.code, device).toArray().length > 0;
    if (!known) return { error: "Turn the key on in the app on this PC first." };
    return { row };
  }

  sendMessage(body, now) {
    const { row, error } = this.holder(body.code, String(body.device ?? ""));
    if (error) return json({ error }, 403);
    const kind = MESSAGE_KINDS[body.kind] ? body.kind : null;
    if (!kind) return json({ error: "A message is a message or a bug report." }, 400);
    if (!this.grantFields(row).perks.includes(MESSAGE_KINDS[kind])) {
      return json({ error: kind === "bug" ? "Your key doesn't include bug reports." : "Your key doesn't include a direct line." }, 403);
    }
    const text = typeof body.text === "string" ? body.text.trim().slice(0, MESSAGE_LENGTH) : "";
    if (!text) return json({ error: "Write something first." }, 400);
    const sent = this.sql.exec("SELECT COUNT(*) AS n FROM grant_messages WHERE code = ? AND created > ?", row.code, now - 24 * 3600_000)
      .toArray()[0].n;
    if (sent >= MESSAGES_PER_DAY) return json({ error: `That's ${MESSAGES_PER_DAY} today. Try again tomorrow.` }, 429);
    const name = typeof body.name === "string" ? body.name.trim().replace(/\s+/g, " ").slice(0, 40) : "";
    this.sql.exec("INSERT INTO grant_messages (code, kind, name, text, created) VALUES (?, ?, ?, ?, ?)", row.code, kind, name, text, now);
    return json({ messages: this.messages(row.code) });
  }

  messages(code) {
    return this.sql.exec("SELECT * FROM grant_messages WHERE code = ? ORDER BY created DESC, id DESC LIMIT 50", code).toArray()
      .map((r) => this.messageJson(r));
  }

  messageJson(r) {
    return { id: r.id, kind: r.kind, text: r.text, created: r.created, reply: r.reply ?? null, replied: r.replied ?? null };
  }
}
