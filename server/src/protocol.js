// The messages the desktop app and the squad service send each other over the WebSocket, and
// checks on what comes in. Everything from a client is checked here before the squad uses it.

export const MOMENT_TYPES = ["kill", "assist", "death", "revive", "match_start", "match_end", "highlight"];
export const MAX_TEXT = 200;
export const MAX_GAME = 80;
export const MAX_NAME = 32;
// A moment more than this far from the server's clock is a broken clock or a replay.
export const MAX_CLOCK_SKEW_MS = 5 * 60_000;

function cleanText(value, max) {
  if (typeof value !== "string") return "";
  // No control characters; they'd only break a log line or a file name.
  return value.replace(/[\u0000-\u001f\u007f]/g, " ").trim().slice(0, max);
}

export function cleanName(value) {
  return cleanText(value, MAX_NAME);
}

export function cleanGame(value) {
  return cleanText(value, MAX_GAME);
}

// A message from a client, or null when it's malformed. now is the server's clock.
export function parseClientMessage(raw, now) {
  if (typeof raw !== "string" || raw.length > 4096) return null;
  let m;
  try {
    m = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!m || typeof m !== "object" || typeof m.t !== "string") return null;
  switch (m.t) {
    case "hello":
      return { t: "hello", app: cleanText(m.app, 32) };
    case "ping":
      return Number.isFinite(m.c) ? { t: "ping", c: m.c } : null;
    case "presence": {
      const game = cleanGame(m.game);
      const match = Number.isFinite(m.match) && Math.abs(m.match - now) < 24 * 3600_000 ? Math.round(m.match) : null;
      return { t: "presence", game, match: game ? match : null };
    }
    case "moment": {
      if (!MOMENT_TYPES.includes(m.type)) return null;
      if (!Number.isFinite(m.at) || Math.abs(m.at - now) > MAX_CLOCK_SKEW_MS) return null;
      const game = cleanGame(m.game);
      if (!game) return null;
      return { t: "moment", ref: cleanText(m.ref, 32), type: m.type, at: Math.round(m.at), game, text: cleanText(m.text, MAX_TEXT) };
    }
    case "view": {
      if (typeof m.moment !== "string" || !/^[A-Za-z0-9_-]{1,64}$/.test(m.moment)) return null;
      if (!Number.isFinite(m.utc_start) || !Number.isFinite(m.seconds) || m.seconds <= 0 || m.seconds > 600) return null;
      return { t: "view", moment: m.moment, utc_start: Math.round(m.utc_start), seconds: Math.round(m.seconds * 100) / 100 };
    }
    case "want": {
      if (typeof m.moment !== "string" || !/^[A-Za-z0-9_-]{1,64}$/.test(m.moment)) return null;
      if (typeof m.member !== "string" || !/^p[0-9A-Z]{10}$/.test(m.member)) return null;
      return { t: "want", moment: m.moment, member: m.member };
    }
    case "unlink": {
      if (typeof m.member !== "string" || !/^p[0-9A-Z]{10}$/.test(m.member)) return null;
      return { t: "unlink", member: m.member, undo: m.undo === true };
    }
    default:
      return null;
  }
}

// A sliding one-minute limit on how many moments a member can send, so a broken rule that
// fires every frame can't flood the squad.
export class RateLimit {
  constructor(perMinute) {
    this.perMinute = perMinute;
    this.times = new Map();
  }

  allow(key, now) {
    const recent = (this.times.get(key) ?? []).filter((t) => now - t < 60_000);
    if (recent.length >= this.perMinute) {
      this.times.set(key, recent);
      return false;
    }
    recent.push(now);
    this.times.set(key, recent);
    return true;
  }
}
