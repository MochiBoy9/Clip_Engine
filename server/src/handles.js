// Public profile names: one Durable Object holds which account each @handle belongs to, so two
// accounts can't take the same one.

const SCHEMA = `CREATE TABLE IF NOT EXISTS handles (handle TEXT PRIMARY KEY, account TEXT NOT NULL UNIQUE, claimed INTEGER NOT NULL);`;

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
}
