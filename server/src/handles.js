// The service's one directory, a single Durable Object: which account each public profile name
// (@handle) belongs to, so two accounts can't take the same one; who's online and what they're
// playing, which friends see; which cloud account each subscription is on; and the names
// subscribers asked to have in the credits. Only the Worker and the libraries talk to it.

const SCHEMA = `
CREATE TABLE IF NOT EXISTS handles (handle TEXT PRIMARY KEY, account TEXT NOT NULL UNIQUE, claimed INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS presence (account TEXT PRIMARY KEY, name TEXT NOT NULL, handle TEXT, game TEXT NOT NULL,
  invite TEXT, until INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS licenses (license TEXT PRIMARY KEY, account TEXT NOT NULL, claimed INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS credits (license TEXT PRIMARY KEY, name TEXT NOT NULL, plan TEXT NOT NULL, since INTEGER NOT NULL,
  seen INTEGER NOT NULL);
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
    if (!/^\/(presence|license|credits)(\/|$)/.test(path)) return null;
    const body = request.method === "POST" ? await request.json().catch(() => null) : null;
    const now = Date.now();

    // ---- presence ----
    if (path === "/presence/set" && typeof body?.account === "string") {
      this.sql.exec(
        `INSERT INTO presence (account, name, handle, game, invite, until) VALUES (?, ?, ?, ?, ?, ?)
         ON CONFLICT(account) DO UPDATE SET name = excluded.name, handle = excluded.handle, game = excluded.game,
           invite = excluded.invite, until = excluded.until`,
        body.account, String(body.name ?? "").slice(0, 32), body.handle ?? null, String(body.game ?? "").slice(0, 64),
        typeof body.invite === "string" ? body.invite.slice(0, 32) : null, now + PRESENCE_MS);
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
        if (row) online[row.account] = { name: row.name, handle: row.handle, game: row.game, invite: row.invite };
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
    if (path === "/credits" && request.method === "GET") {
      this.sql.exec("DELETE FROM credits WHERE seen < ?", now - CREDIT_KEEP_MS);
      const rows = this.sql.exec("SELECT name, plan FROM credits ORDER BY since").toArray();
      return json({ ultra: rows.filter((r) => r.plan === "ultra").map((r) => r.name), pro: rows.filter((r) => r.plan === "pro").map((r) => r.name) });
    }
    return json({ error: "Not found." }, 404);
  }
}
