// The service's one directory, a single Durable Object: which account each public profile name
// (@handle) belongs to, so two accounts can't take the same one; who's online and what they're
// playing, which friends see; which cloud account each subscription is on; the names subscribers
// asked to have in the credits; and the special keys made in the key maker. Only the Worker and
// the libraries talk to it.

import { addColumns } from "./codes.js";
import { cleanGrant, formatGrant, newGrantCode, PLANS } from "./plans.js";

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
`;

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
        body.license, name, body.plan === "ultra" ? "ultra" : "pro", now, now);
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
      const { grant, error } = cleanGrant(body, { plan: "pro", days: 30, extraMb: 0, themes: null, lines: null, maxDevices: 3, note: "" });
      if (error) return json({ error }, 400);
      const count = Number.isInteger(body.count) && body.count >= 1 && body.count <= 100 ? body.count : 1;
      const made = [];
      for (let i = 0; i < count; i++) {
        let code;
        do code = newGrantCode();
        while (this.grantRow(code));
        this.sql.exec(
          `INSERT INTO grants (code, plan, days, extra_mb, themes, lines, max_devices, note, created) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
          code, grant.plan, grant.days, grant.extraMb, grant.themes ? JSON.stringify(grant.themes) : null, grant.lines,
          grant.maxDevices, grant.note, now + i);
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
        `UPDATE grants SET plan = ?, days = ?, extra_mb = ?, themes = ?, lines = ?, max_devices = ?, note = ?, revoked = ? WHERE code = ?`,
        grant.plan, grant.days, grant.extraMb, grant.themes ? JSON.stringify(grant.themes) : null, grant.lines, grant.maxDevices,
        grant.note, grant.revoked ? 1 : 0, row.code);
      if (body.resetDevices) this.sql.exec("DELETE FROM grant_devices WHERE code = ?", row.code);
      if (body.restart) this.sql.exec("UPDATE grants SET first_used = NULL WHERE code = ?", row.code);
      return json(this.grantJson(this.grantRow(row.code), now));
    }

    if (path === "/credits" && request.method === "GET") {
      this.sql.exec("DELETE FROM credits WHERE seen < ?", now - CREDIT_KEEP_MS);
      const rows = this.sql.exec("SELECT name, plan FROM credits ORDER BY since").toArray();
      return json({ ultra: rows.filter((r) => r.plan === "ultra").map((r) => r.name), pro: rows.filter((r) => r.plan === "pro").map((r) => r.name) });
    }
    return json({ error: "Not found." }, 404);
  }

  grantRow(code) {
    return this.sql.exec("SELECT * FROM grants WHERE code = ?", code).toArray()[0] ?? null;
  }

  grantFields(row) {
    return {
      plan: row.plan, days: row.days, extraMb: row.extra_mb, themes: row.themes ? JSON.parse(row.themes) : null,
      lines: row.lines, maxDevices: row.max_devices, note: row.note, revoked: row.revoked === 1,
    };
  }

  // A key's end: its days counted from the first time it was used; null for no end or not used yet.
  grantExpires(row) {
    return row.days && row.first_used ? row.first_used + row.days * 24 * 3600_000 : null;
  }

  grantJson(row, now) {
    const expires = this.grantExpires(row);
    const devices = this.sql.exec("SELECT COUNT(*) AS n FROM grant_devices WHERE code = ?", row.code).toArray()[0].n;
    const status = row.revoked ? "revoked" : !row.first_used ? "unused" : expires && expires <= now ? "expired" : "active";
    const f = this.grantFields(row);
    return {
      code: formatGrant(row.code), plan: f.plan, days: f.days, extraStorageGb: Math.round((f.extraMb / 1024) * 100) / 100,
      themes: f.themes, lines: f.lines, maxDevices: f.maxDevices, note: f.note, created: row.created,
      firstUsed: row.first_used, lastUsed: row.last_used, expires, devices, status,
    };
  }

  checkGrant(code, device, now) {
    const row = this.grantRow(code);
    if (!row) return json({ ok: false, rejected: true, error: "That key wasn't found. Check every letter." });
    if (row.revoked) return json({ ok: false, rejected: true, error: "This key was turned off." });
    const expires = this.grantExpires(row);
    if (expires && expires <= now) return json({ ok: false, rejected: true, error: "This key has run out." });
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
    });
  }
}
