// The hub's index of public clips. A clip lives in its owner's library (src/library.js); when the owner makes it
// public, the library puts a small copy of its details here, so the hub can list everyone's clips newest first or
// most watched, by game, without asking every library. It's kept in the directory object (src/handles.js).
//
// Nothing is listed unless its owner made it public, and making it private or unlisted again takes it off at once.
// Anyone can report a clip; the operator can hide one (admin routes in src/index.js).

import { MOMENT_TYPES } from "./protocol.js";

export const FEED_SCHEMA = `
CREATE TABLE IF NOT EXISTS feed (code TEXT PRIMARY KEY, account TEXT NOT NULL, title TEXT NOT NULL, game TEXT NOT NULL,
  seconds REAL NOT NULL, views INTEGER NOT NULL DEFAULT 0, thumb INTEGER NOT NULL DEFAULT 0, width INTEGER NOT NULL DEFAULT 0,
  height INTEGER NOT NULL DEFAULT 0, moments TEXT NOT NULL DEFAULT '{}', recorded INTEGER NOT NULL, published INTEGER NOT NULL,
  name TEXT NOT NULL, handle TEXT, badge TEXT, hidden INTEGER NOT NULL DEFAULT 0, reports INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS feed_new ON feed(hidden, published DESC, code);
CREATE INDEX IF NOT EXISTS feed_game ON feed(hidden, game, published DESC);
CREATE INDEX IF NOT EXISTS feed_top ON feed(hidden, views DESC);
CREATE INDEX IF NOT EXISTS feed_account ON feed(account);
CREATE TABLE IF NOT EXISTS feed_reports (id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT NOT NULL, reason TEXT NOT NULL,
  text TEXT NOT NULL DEFAULT '', created INTEGER NOT NULL);
`;

export const FEED_PAGE = 24;
export const REPORT_REASONS = ["spam", "cheating", "hateful", "sexual", "violent", "other"];
const REPORTS_PER_CLIP = 200;

// How many of each moment a clip has, { kill: 3, revive: 1 }, from its markers.
export function momentCounts(markers) {
  const counts = {};
  for (const m of markers ?? []) if (MOMENT_TYPES.includes(m?.type)) counts[m.type] = (counts[m.type] ?? 0) + 1;
  return counts;
}

function parse(text, fallback) {
  try {
    return JSON.parse(text) ?? fallback;
  } catch {
    return fallback;
  }
}

export function feedJson(row) {
  return {
    code: row.code,
    title: row.title,
    game: row.game,
    seconds: row.seconds,
    views: row.views,
    thumb: row.thumb === 1,
    width: row.width,
    height: row.height,
    moments: parse(row.moments, {}),
    recorded: row.recorded,
    published: row.published,
    owner: { name: row.name, handle: row.handle ?? null, badge: parse(row.badge, null) },
  };
}

function cleanBadge(badge) {
  return badge && typeof badge.kind === "string" && typeof badge.label === "string"
    ? JSON.stringify({ kind: badge.kind.slice(0, 20), label: badge.label.slice(0, 40) })
    : null;
}

// Answers the directory's /feed/... requests, or null for any other path.
export function feedRequest(sql, path, body, now) {
  if (path === "/feed/put" && typeof body?.account === "string" && typeof body?.code === "string") {
    const owner = body.owner ?? {};
    sql.exec(
      `INSERT INTO feed (code, account, title, game, seconds, views, thumb, width, height, moments, recorded, published, name, handle, badge)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(code) DO UPDATE SET title = excluded.title, game = excluded.game, seconds = excluded.seconds, views = excluded.views,
         thumb = excluded.thumb, width = excluded.width, height = excluded.height, moments = excluded.moments, name = excluded.name,
         handle = excluded.handle, badge = excluded.badge`,
      body.code.slice(0, 20), body.account.slice(0, 20), String(body.title ?? "").slice(0, 120), String(body.game ?? "Clip").slice(0, 80),
      Number(body.seconds) || 0, Number.isInteger(body.views) ? body.views : 0, body.thumb ? 1 : 0,
      Number.isInteger(body.width) ? body.width : 0, Number.isInteger(body.height) ? body.height : 0,
      JSON.stringify(momentCounts(body.markers)), Number.isFinite(body.recorded) ? body.recorded : now, now,
      String(owner.name ?? "Player").slice(0, 32), typeof owner.handle === "string" ? owner.handle.slice(0, 24) : null, cleanBadge(owner.badge));
    return { ok: true };
  }
  if (path === "/feed/remove" && typeof body?.code === "string") {
    sql.exec("DELETE FROM feed WHERE code = ?", body.code);
    return { ok: true };
  }
  if (path === "/feed/forget" && typeof body?.account === "string") {
    sql.exec("DELETE FROM feed WHERE account = ?", body.account);
    return { ok: true };
  }
  // The owner's name, username or badge changed: every one of their clips in the hub shows the new one.
  if (path === "/feed/owner" && typeof body?.account === "string") {
    sql.exec("UPDATE feed SET name = ?, handle = ?, badge = ? WHERE account = ?",
      String(body.name ?? "Player").slice(0, 32), typeof body.handle === "string" ? body.handle.slice(0, 24) : null,
      cleanBadge(body.badge), body.account);
    return { ok: true };
  }
  if (path === "/feed/views" && typeof body?.code === "string" && Number.isInteger(body.views)) {
    sql.exec("UPDATE feed SET views = ? WHERE code = ?", body.views, body.code);
    return { ok: true };
  }
  if (path === "/feed/list") return listFeed(sql, body ?? {});
  if (path === "/feed/games") {
    const rows = sql.exec(
      "SELECT game, COUNT(*) AS clips FROM feed WHERE hidden = 0 GROUP BY game ORDER BY clips DESC, game LIMIT 40").toArray();
    return { games: rows.map((r) => ({ game: r.game, clips: r.clips })) };
  }
  if (path === "/feed/report" && typeof body?.code === "string") {
    const row = sql.exec("SELECT reports FROM feed WHERE code = ?", body.code).toArray()[0];
    if (!row) return { error: "That clip isn't in the hub.", status: 404 };
    if (row.reports < REPORTS_PER_CLIP) {
      const reason = REPORT_REASONS.includes(body.reason) ? body.reason : "other";
      sql.exec("INSERT INTO feed_reports (code, reason, text, created) VALUES (?, ?, ?, ?)",
        body.code, reason, String(body.text ?? "").trim().slice(0, 500), now);
      sql.exec("UPDATE feed SET reports = reports + 1 WHERE code = ?", body.code);
    }
    return { ok: true };
  }
  // ---- the operator's (admin routes only) ----
  if (path === "/feed/reports") {
    const rows = sql.exec(
      `SELECT f.*, (SELECT json_group_array(json_object('reason', r.reason, 'text', r.text, 'created', r.created))
         FROM (SELECT * FROM feed_reports r WHERE r.code = f.code ORDER BY created DESC LIMIT 20) r) AS list
       FROM feed f WHERE f.reports > 0 ORDER BY f.hidden, f.reports DESC LIMIT 200`).toArray();
    return { clips: rows.map((r) => ({ ...feedJson(r), account: r.account, hidden: r.hidden === 1, reports: r.reports, list: parse(r.list, []) })) };
  }
  if (path === "/feed/hide" && typeof body?.code === "string") {
    const row = sql.exec("SELECT code FROM feed WHERE code = ?", body.code).toArray()[0];
    if (!row) return { error: "That clip isn't in the hub.", status: 404 };
    sql.exec("UPDATE feed SET hidden = ? WHERE code = ?", body.hidden === false ? 0 : 1, body.code);
    if (body.clearReports) {
      sql.exec("DELETE FROM feed_reports WHERE code = ?", body.code);
      sql.exec("UPDATE feed SET reports = 0 WHERE code = ?", body.code);
    }
    return { ok: true };
  }
  return null;
}

// A page of the hub: newest first (sort "new", the cursor is the last clip's "published.code") or most watched
// (sort "top", within the last week, month or ever; the cursor is how many were already shown). By game, and
// matching words in the title, game, name or username.
function listFeed(sql, { sort = "new", game = "", q = "", cursor = "", limit = FEED_PAGE, within = "" }) {
  const where = ["hidden = 0"];
  const args = [];
  if (typeof game === "string" && game) {
    where.push("game = ? COLLATE NOCASE");
    args.push(game.slice(0, 80));
  }
  const words = typeof q === "string" ? q.trim().replace(/^@/, "").slice(0, 60).split(/\s+/).filter(Boolean).slice(0, 4) : [];
  for (const word of words) {
    const like = `%${word.replace(/[\\%_]/g, (c) => `\\${c}`)}%`;
    where.push("(title LIKE ? ESCAPE '\\' OR game LIKE ? ESCAPE '\\' OR name LIKE ? ESCAPE '\\' OR handle LIKE ? ESCAPE '\\')");
    args.push(like, like, like, like);
  }
  const size = Number.isInteger(limit) && limit > 0 && limit <= 60 ? limit : FEED_PAGE;
  let rows;
  if (sort === "top") {
    const days = { week: 7, month: 30 }[within];
    if (days) {
      where.push("published >= ?");
      args.push(Date.now() - days * 24 * 3600_000);
    }
    const offset = Math.min(Math.max(0, Number.parseInt(cursor, 10) || 0), 2000);
    rows = sql.exec(`SELECT * FROM feed WHERE ${where.join(" AND ")} ORDER BY views DESC, published DESC LIMIT ? OFFSET ?`,
      ...args, size + 1, offset).toArray();
    const more = rows.length > size;
    return { clips: rows.slice(0, size).map(feedJson), next: more ? String(offset + size) : null };
  }
  const m = typeof cursor === "string" && cursor.match(/^(\d+)\.([0-9A-Z]+)$/);
  if (m) {
    where.push("(published < ? OR (published = ? AND code > ?))");
    args.push(Number(m[1]), Number(m[1]), m[2]);
  }
  rows = sql.exec(`SELECT * FROM feed WHERE ${where.join(" AND ")} ORDER BY published DESC, code LIMIT ?`, ...args, size + 1).toArray();
  const more = rows.length > size;
  const page = rows.slice(0, size);
  const last = page[page.length - 1];
  return { clips: page.map(feedJson), next: more && last ? `${last.published}.${last.code}` : null };
}
