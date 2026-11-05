// Inviting friends: share your invite link, and when enough friends install Clip Engine on PCs of their own, you get a
// month of Pro. One Durable Object for the whole service, so a PC can only ever count once.
//
// How a friend counts:
//   1. They open your link (/r/<code>), which remembers their address for a week, or type your code in the app.
//   2. Clip Engine starts for the first time on their PC and tells the service (POST /v1/referrals/activate). Only an
//      install that's new counts: the app says so on its first start, and the service has never seen the PC before.
//   3. The PC must be another one than yours (by its Windows machine id, and the app's own id), and on another network
//      than any you've used, with one friend per network. Addresses are kept only as salted hashes.
// Every REFERRAL_FRIENDS friends that count give one reward, up to REFERRAL_MAX_REWARDS: a special key (src/plans.js)
// for REFERRAL_REWARD_DAYS of Pro, made in the directory like the key maker's, that starts when it's first used.

import { randomCode, randomToken, sameSecret, sha256Hex } from "./codes.js";

export const REFERRAL_CODE_LENGTH = 8;
const DAY = 24 * 3600_000;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS referrers (code TEXT PRIMARY KEY, secret TEXT NOT NULL, name TEXT NOT NULL DEFAULT '', created INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS referrer_pcs (code TEXT NOT NULL, pc TEXT NOT NULL, PRIMARY KEY (code, pc));
CREATE INDEX IF NOT EXISTS referrer_pcs_pc ON referrer_pcs (pc);
CREATE TABLE IF NOT EXISTS referrer_ips (code TEXT NOT NULL, ip TEXT NOT NULL, PRIMARY KEY (code, ip));
CREATE TABLE IF NOT EXISTS installs (pc TEXT PRIMARY KEY, device TEXT NOT NULL, ip TEXT NOT NULL, first INTEGER NOT NULL,
  fresh INTEGER NOT NULL, code TEXT, counted INTEGER NOT NULL DEFAULT 0, at INTEGER);
CREATE INDEX IF NOT EXISTS installs_device ON installs (device);
CREATE INDEX IF NOT EXISTS installs_code ON installs (code);
CREATE TABLE IF NOT EXISTS clicks (ip TEXT PRIMARY KEY, code TEXT NOT NULL, at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS rewards (code TEXT NOT NULL, n INTEGER NOT NULL, grant TEXT, at INTEGER NOT NULL, PRIMARY KEY (code, n));
`;

function json(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function wholeNumber(value, min, max, fallback) {
  const n = Number(value);
  return Number.isInteger(n) && n >= min && n <= max ? n : fallback;
}

// The settings in wrangler.toml [vars], with their defaults.
export function referralSettings(env) {
  return {
    friends: wholeNumber(env?.REFERRAL_FRIENDS, 1, 50, 3),
    days: wholeNumber(env?.REFERRAL_REWARD_DAYS, 1, 366, 30),
    maxRewards: wholeNumber(env?.REFERRAL_MAX_REWARDS, 0, 100, 1),
    claimDays: wholeNumber(env?.REFERRAL_CLAIM_DAYS, 1, 60, 7),
    ipCheck: String(env?.REFERRAL_IP_CHECK ?? "true") !== "false",
  };
}

// What someone typed or the end of a link, as the 8 characters of an invite code; null when it isn't one.
export function normalizeReferral(text) {
  if (typeof text !== "string") return null;
  const code = text.trim().split(/[/#?=]/).pop().toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
  return code.length === REFERRAL_CODE_LENGTH && /^[0-9A-HJKMNP-TV-Z]+$/.test(code) ? code : null;
}

export function formatReferral(code) {
  return `${code.slice(0, 4)}-${code.slice(4)}`;
}

// A PC's id from the app: its hashed Windows machine id, or the app's own id. Letters, digits, '-' and '_'.
function cleanPc(value) {
  return typeof value === "string" && /^[A-Za-z0-9_-]{8,128}$/.test(value) ? value : "";
}

export class Referrals {
  constructor(ctx, env) {
    this.sql = ctx.storage.sql;
    this.env = env;
    this.sql.exec(SCHEMA);
  }

  async fetch(request) {
    const url = new URL(request.url);
    const body = await request.json().catch(() => null);
    if (!body) return json({ error: "Bad request." }, 400);
    const now = Date.now();
    this.settings = referralSettings(this.env);
    const ip = await this.hashIp(body.ip);
    switch (url.pathname) {
      case "/create": return this.create(body, ip, now);
      case "/status": return this.status(body, ip, now);
      case "/activate": return this.activate(body, ip, now);
      case "/click": return this.click(body, ip, now);
      default: return json({ error: "Not found." }, 404);
    }
  }

  // Addresses are kept as hashes with a salt made once for this service, so the table can't be turned back into them.
  async hashIp(ip) {
    if (typeof ip !== "string" || ip.length === 0 || ip === "unknown") return "";
    let salt = this.sql.exec("SELECT value FROM meta WHERE key = 'salt'").toArray()[0]?.value;
    if (!salt) {
      salt = randomToken();
      this.sql.exec("INSERT INTO meta (key, value) VALUES ('salt', ?)", salt);
    }
    return (await sha256Hex(`${salt}:${ip}`)).slice(0, 32);
  }

  referrer(code) {
    return this.sql.exec("SELECT * FROM referrers WHERE code = ?", code).toArray()[0] ?? null;
  }

  remember(code, pcs, ip) {
    for (const pc of pcs) if (pc) this.sql.exec("INSERT OR IGNORE INTO referrer_pcs (code, pc) VALUES (?, ?)", code, pc);
    if (ip) this.sql.exec("INSERT OR IGNORE INTO referrer_ips (code, ip) VALUES (?, ?)", code, ip);
  }

  // This PC's invite code. Asking again from the same PC gives the same code with a new secret, so a reinstall
  // keeps its friends.
  async create(body, ip, now) {
    const pc = cleanPc(body.pc);
    const device = cleanPc(body.device);
    if (!pc && !device) return json({ error: "Which PC is this?" }, 400);
    const name = typeof body.name === "string" ? body.name.trim().replace(/\s+/g, " ").slice(0, 32) : "";
    const found = this.sql.exec("SELECT code FROM referrer_pcs WHERE pc IN (?, ?) LIMIT 1", pc || device, device || pc).toArray()[0];
    const secret = randomToken();
    const hash = await sha256Hex(secret);
    let code = found?.code;
    if (code) {
      this.sql.exec("UPDATE referrers SET secret = ?, name = CASE WHEN ? = '' THEN name ELSE ? END WHERE code = ?", hash, name, name, code);
    } else {
      do code = randomCode(REFERRAL_CODE_LENGTH);
      while (this.referrer(code));
      this.sql.exec("INSERT INTO referrers (code, secret, name, created) VALUES (?, ?, ?, ?)", code, hash, name, now);
    }
    this.remember(code, [pc, device], ip);
    return json({ ...(await this.statusJson(code, body.origin, now)), secret });
  }

  async status(body, ip, now) {
    const code = normalizeReferral(body.code);
    const row = code && this.referrer(code);
    if (!row || typeof body.secret !== "string" || !sameSecret(await sha256Hex(body.secret), row.secret)) {
      return json({ error: "That invite code isn't yours." }, 403);
    }
    this.remember(code, [cleanPc(body.pc), cleanPc(body.device)], ip);
    return json(await this.statusJson(code, body.origin, now));
  }

  async statusJson(code, origin, now) {
    await this.reward(code, now);
    const { friends: needed, days, maxRewards } = this.settings;
    const friends = this.sql.exec("SELECT COUNT(*) AS n FROM installs WHERE code = ? AND counted = 1", code).toArray()[0].n;
    const notCounted = this.sql.exec("SELECT COUNT(*) AS n FROM installs WHERE code = ? AND counted = 0", code).toArray()[0].n;
    const rewards = this.sql.exec("SELECT grant, at FROM rewards WHERE code = ? AND grant IS NOT NULL ORDER BY n", code).toArray()
      .map((r) => ({ key: r.grant, at: r.at }));
    const done = rewards.length >= maxRewards;
    return {
      code: formatReferral(code),
      link: typeof origin === "string" && origin ? `${origin}/r/${code}` : "",
      friends,
      notCounted,
      needed,
      // Friends still to go for the next reward; 0 once every reward is earned.
      toGo: done ? 0 : needed * (rewards.length + 1) - friends,
      rewardDays: days,
      maxRewards,
      rewards,
      done,
    };
  }

  // Makes the special keys earned so far. A reward is put down before its key is made, so two friends landing at
  // once can't make two; one whose key couldn't be made is tried again on the next check.
  async reward(code, now) {
    const { friends: needed, days, maxRewards } = this.settings;
    this.sql.exec("DELETE FROM rewards WHERE code = ? AND grant IS NULL AND at < ?", code, now - 60_000);
    const friends = this.sql.exec("SELECT COUNT(*) AS n FROM installs WHERE code = ? AND counted = 1", code).toArray()[0].n;
    const earned = Math.min(maxRewards, Math.floor(friends / needed));
    for (let n = 1; n <= earned; n++) {
      if (this.sql.exec("SELECT 1 FROM rewards WHERE code = ? AND n = ?", code, n).toArray().length) continue;
      this.sql.exec("INSERT INTO rewards (code, n, grant, at) VALUES (?, ?, NULL, ?)", code, n, now);
      const key = await this.makeKey(code, days, needed * n);
      if (key) this.sql.exec("UPDATE rewards SET grant = ? WHERE code = ? AND n = ?", key, code, n);
      else this.sql.exec("DELETE FROM rewards WHERE code = ? AND n = ?", code, n);
    }
  }

  async makeKey(code, days, friends) {
    if (!this.env?.HANDLES) return null;
    try {
      const directory = this.env.HANDLES.get(this.env.HANDLES.idFromName("handles"));
      const answer = await directory.fetch(new Request("https://handles/grant/create", {
        method: "POST",
        body: JSON.stringify({ plan: "pro", days, maxDevices: 2, note: `Invited ${friends} friends (invite ${formatReferral(code)})` }),
      }));
      if (!answer.ok) return null;
      return (await answer.json()).grants?.[0]?.code ?? null;
    } catch {
      return null;
    }
  }

  // Someone opened an invite link: their address points at the code for a while, so the app they install from it
  // knows who invited them without typing the code.
  click(body, ip, now) {
    const code = normalizeReferral(body.code);
    const row = code && this.referrer(code);
    if (!row) return json({ error: "That invite isn't valid." }, 404);
    this.sql.exec("DELETE FROM clicks WHERE at < ?", now - this.settings.claimDays * DAY);
    if (ip && body.record !== false) {
      this.sql.exec(
        "INSERT INTO clicks (ip, code, at) VALUES (?, ?, ?) ON CONFLICT(ip) DO UPDATE SET code = excluded.code, at = excluded.at",
        ip, code, now);
    }
    return json({ code: formatReferral(code), name: row.name, needed: this.settings.friends, rewardDays: this.settings.days });
  }

  // Clip Engine started on a PC. Every PC is put down the first time, so it can never count as new again. With an
  // invite code (typed, or from a link opened on the same network), it counts for that code if it passes the checks.
  // Answers { counted, reason, inviter }.
  activate(body, ip, now) {
    const pc = cleanPc(body.pc) || cleanPc(body.device);
    const device = cleanPc(body.device) || pc;
    if (!pc) return json({ error: "Which PC is this?" }, 400);
    const typed = body.code === undefined || body.code === null || body.code === "" ? null : normalizeReferral(body.code);
    if (body.code && !typed) return json({ error: "That isn't an invite code. It looks like ABCD-1234." }, 400);
    if (typed && !this.referrer(typed)) return json({ error: "That invite code wasn't found. Check it with your friend." }, 404);

    let install = this.sql.exec("SELECT * FROM installs WHERE pc IN (?, ?) OR device IN (?, ?) LIMIT 1", pc, device, pc, device).toArray()[0];
    if (!install) {
      this.sql.exec("INSERT INTO installs (pc, device, ip, first, fresh) VALUES (?, ?, ?, ?, ?)", pc, device, ip, now, body.fresh === true ? 1 : 0);
      install = this.sql.exec("SELECT * FROM installs WHERE pc = ?", pc).toArray()[0];
    }

    const linked = !typed && ip
      ? this.sql.exec("SELECT code FROM clicks WHERE ip = ? AND at >= ?", ip, now - this.settings.claimDays * DAY).toArray()[0]?.code
      : null;
    const code = typed ?? linked ?? null;
    if (!code) return json({ counted: false, reason: "" });
    const inviter = this.referrer(code)?.name ?? "";
    const answer = (counted, reason) => json({ counted, reason, inviter, code: formatReferral(code) });

    if (install.code) {
      return install.code === code
        ? answer(install.counted === 1, install.counted === 1 ? "" : "This PC was already checked for that invite and didn't count.")
        : answer(false, "This PC already used another friend's invite.");
    }
    // Your own code from your own PC: say so, and leave it free for a friend's code.
    const own = this.sql.exec("SELECT 1 FROM referrer_pcs WHERE code = ? AND pc IN (?, ?)", code, pc, device).toArray().length > 0;
    if (own) return answer(false, "That's your own invite code. Send it to a friend.");
    if (!install.fresh) return answer(false, "Invites count when Clip Engine is new on a PC, and it was already on this one.");
    if (now - install.first > this.settings.claimDays * DAY) {
      return answer(false, `An invite code can be added in the first ${this.settings.claimDays} days after installing.`);
    }

    let reason = "";
    if (this.settings.ipCheck && ip) {
      const sameNetwork = this.sql.exec("SELECT 1 FROM referrer_ips WHERE code = ? AND ip = ?", code, ip).toArray().length > 0;
      const taken = this.sql.exec("SELECT 1 FROM installs WHERE code = ? AND counted = 1 AND ip = ? AND pc != ?", code, ip, install.pc)
        .toArray().length > 0;
      if (sameNetwork) reason = "This PC is on the same network as the friend who invited you, so it doesn't count. Invites count from other homes.";
      else if (taken) reason = "Another friend on this network already counted for this invite. One PC per network counts.";
    }
    this.sql.exec("UPDATE installs SET code = ?, counted = ?, ip = CASE WHEN ? = '' THEN ip ELSE ? END, at = ? WHERE pc = ?",
      code, reason ? 0 : 1, ip, ip, now, install.pc);
    if (!typed) this.sql.exec("DELETE FROM clicks WHERE ip = ?", ip);
    return answer(!reason, reason);
  }
}
