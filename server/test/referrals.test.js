import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { normalizeReferral, referralSettings } from "../src/referrals.js";
import { fakeEnv, request } from "./fakes.js";

const DAY = 24 * 3600_000;

// A call from an address: the Worker sees it in cf-connecting-ip.
const call = (env, path, { ip = "203.0.113.1", ...options } = {}) =>
  worker.fetch(request(path, { ...options, headers: { "cf-connecting-ip": ip, ...(options.headers ?? {}) } }), env);

const pc = (n) => `machine-${String(n).padStart(4, "0")}`;

async function inviter(env, { ip = "198.51.100.7", name = "Enterro", machine = pc(1) } = {}) {
  const res = await call(env, "/v1/referrals", { method: "POST", ip, body: { pc: machine, device: `app-${machine}`, name } });
  assert.equal(res.status, 200);
  return res.json();
}

const status = async (env, me, ip = "198.51.100.7") =>
  (await call(env, `/v1/referrals/${me.code}`, { ip, token: me.secret })).json();

const activate = async (env, body, ip) =>
  call(env, "/v1/referrals/activate", { method: "POST", ip, body: { fresh: true, ...body } });

const friend = async (env, code, n, ip = `192.0.2.${n}`) =>
  (await activate(env, { pc: pc(100 + n), device: `app-friend-${n}`, code }, ip)).json();

test("three friends on their own PCs and networks earn a month of Pro, once", async () => {
  const env = fakeEnv();
  const me = await inviter(env);
  assert.match(me.code, /^[0-9A-Z]{4}-[0-9A-Z]{4}$/);
  assert.equal(me.link, `https://squads.test/r/${me.code.replace("-", "")}`);
  assert.equal(me.needed, 3);
  assert.equal(me.toGo, 3);
  assert.ok(me.secret.length > 20);

  assert.equal((await friend(env, me.code, 1)).counted, true);
  assert.equal((await friend(env, me.code.toLowerCase(), 2)).counted, true);
  let mine = await status(env, me);
  assert.equal(mine.friends, 2);
  assert.equal(mine.toGo, 1);
  assert.deepEqual(mine.rewards, []);

  const third = await friend(env, me.code, 3);
  assert.equal(third.counted, true);
  assert.equal(third.inviter, "Enterro");
  mine = await status(env, me);
  assert.equal(mine.friends, 3);
  assert.equal(mine.rewards.length, 1);
  assert.equal(mine.done, true);
  assert.equal(mine.toGo, 0);

  // The reward is a special key for 30 days of Pro, which starts when it's used.
  const key = mine.rewards[0].key;
  assert.match(key, /^CEK-/);
  const check = await (await call(env, "/v1/license", { method: "POST", body: { key, device: "app-machine-0001" } })).json();
  assert.equal(check.plan, "pro");
  assert.equal(check.grant, true);
  assert.ok(check.expires > Date.now() + 29 * DAY && check.expires <= Date.now() + 30 * DAY);

  // More friends don't make more keys past REFERRAL_MAX_REWARDS, and asking again doesn't either.
  for (const n of [4, 5, 6]) await friend(env, me.code, n);
  mine = await status(env, me);
  assert.equal(mine.friends, 6);
  assert.equal(mine.rewards.length, 1);
  assert.equal(mine.rewards[0].key, key);
});

test("the number of friends and rewards come from the settings", async () => {
  const env = fakeEnv({ REFERRAL_FRIENDS: "5", REFERRAL_MAX_REWARDS: "2", REFERRAL_REWARD_DAYS: "14" });
  const me = await inviter(env);
  assert.equal(me.needed, 5);
  for (let n = 1; n <= 4; n++) await friend(env, me.code, n);
  assert.equal((await status(env, me)).rewards.length, 0);
  await friend(env, me.code, 5);
  let mine = await status(env, me);
  assert.equal(mine.rewards.length, 1);
  assert.equal(mine.toGo, 5);
  for (let n = 6; n <= 10; n++) await friend(env, me.code, n);
  mine = await status(env, me);
  assert.equal(mine.rewards.length, 2);
  assert.notEqual(mine.rewards[0].key, mine.rewards[1].key);
  const check = await (await call(env, "/v1/license", { method: "POST", body: { key: mine.rewards[1].key, device: "x-device-1" } })).json();
  assert.ok(check.expires <= Date.now() + 14 * DAY);

  assert.deepEqual(referralSettings({}), { friends: 3, days: 30, maxRewards: 1, claimDays: 7, ipCheck: true });
  assert.equal(referralSettings({ REFERRAL_FRIENDS: "nope" }).friends, 3);
});

test("a PC only ever counts once, and only when Clip Engine is new on it", async () => {
  const env = fakeEnv();
  const me = await inviter(env);
  const other = await inviter(env, { machine: pc(2), ip: "198.51.100.99", name: "Kai" });

  assert.equal((await friend(env, me.code, 1)).counted, true);
  // Again from the same PC, or with the app's data wiped (same machine id), or a new machine id with the same app id.
  assert.equal((await friend(env, me.code, 1)).counted, true, "asking again just repeats the answer");
  const wiped = await (await activate(env, { pc: pc(101), device: "app-reinstalled-99", code: me.code }, "192.0.2.50")).json();
  assert.equal(wiped.counted, true);
  const spoofed = await (await activate(env, { pc: "machine-spoofed-1", device: "app-friend-1", code: me.code }, "192.0.2.51")).json();
  assert.equal(spoofed.counted, true, "the same install, not a new one");
  assert.equal((await status(env, me)).friends, 1);

  // The same PC can't count for a second person.
  const second = await friend(env, other.code, 1);
  assert.equal(second.counted, false);
  assert.match(second.reason, /another friend's invite/);

  // Clip Engine was already on the PC (the app says it isn't a first start).
  const old = await (await activate(env, { pc: pc(300), device: "app-old-300", fresh: false, code: me.code }, "192.0.2.80")).json();
  assert.equal(old.counted, false);
  assert.match(old.reason, /already on this one/);

  // A PC first seen without a code can add one later, but only within the claim window.
  assert.equal((await (await activate(env, { pc: pc(400), device: "app-new-400" }, "192.0.2.90")).json()).counted, false);
  assert.equal((await friend(env, me.code, 300, "192.0.2.90")).counted, true);
  assert.equal((await status(env, me)).friends, 2);
});

test("your own PC and your own network don't count", async () => {
  const env = fakeEnv();
  const me = await inviter(env, { ip: "198.51.100.7" });

  const own = await (await activate(env, { pc: pc(1), device: "app-machine-0001", code: me.code }, "192.0.2.1")).json();
  assert.equal(own.counted, false);
  assert.match(own.reason, /your own invite code/);
  // Leaving it free means a real friend's code still works from that PC... but it isn't new, so nothing else does.

  // A second PC at home: a new machine, but the inviter's address.
  const home = await friend(env, me.code, 1, "198.51.100.7");
  assert.equal(home.counted, false);
  assert.match(home.reason, /same network/);

  // An address the inviter used later (checking their friends from somewhere else) is theirs too.
  await status(env, me, "198.51.100.8");
  assert.equal((await friend(env, me.code, 2, "198.51.100.8")).counted, false);

  // Two friends behind one address: the first counts.
  assert.equal((await friend(env, me.code, 3, "192.0.2.200")).counted, true);
  const sibling = await friend(env, me.code, 4, "192.0.2.200");
  assert.equal(sibling.counted, false);
  assert.match(sibling.reason, /One PC per network/);

  const mine = await status(env, me);
  assert.equal(mine.friends, 1);
  assert.equal(mine.notCounted, 3);

  // With the address check off, only the PC checks apply.
  const relaxed = fakeEnv({ REFERRAL_IP_CHECK: "false" });
  const host = await inviter(relaxed, { ip: "198.51.100.7" });
  assert.equal((await friend(relaxed, host.code, 1, "198.51.100.7")).counted, true);
  assert.equal((await friend(relaxed, host.code, 2, "198.51.100.7")).counted, true);
});

test("opening an invite link credits the app installed from that network, without typing the code", async () => {
  const env = fakeEnv();
  const me = await inviter(env);
  const code = me.code.replace("-", "");

  const page = await call(env, `/r/${code}`, { ip: "192.0.2.33" });
  assert.equal(page.status, 200);
  const text = await page.text();
  assert.match(text, /Enterro invited you to Clip Engine/);
  assert.match(text, /Clip_Engine-Setup\.exe/);
  assert.match(text, new RegExp(me.code));

  // Discord's preview of the link isn't someone opening it.
  await call(env, `/r/${code}`, { ip: "192.0.2.34", headers: { "user-agent": "Mozilla/5.0 (compatible; Discordbot/2.0)" } });
  assert.equal((await (await activate(env, { pc: pc(34), device: "app-34" }, "192.0.2.34")).json()).counted, false);

  const joined = await (await activate(env, { pc: pc(33), device: "app-33" }, "192.0.2.33")).json();
  assert.equal(joined.counted, true);
  assert.equal(joined.inviter, "Enterro");
  assert.equal((await status(env, me)).friends, 1);

  assert.equal((await call(env, "/r/ZZZZZZZZ")).status, 404);
  assert.equal((await call(env, "/r/nope")).status, 404);
});

test("invite codes are checked, and only the inviter sees their friends", async () => {
  const env = fakeEnv();
  const me = await inviter(env);
  assert.equal((await call(env, `/v1/referrals/${me.code}`, { token: "wrong" })).status, 403);
  assert.equal((await call(env, `/v1/referrals/${me.code}`)).status, 403);
  assert.equal((await activate(env, { pc: pc(9), device: "app-9", code: "hello" }, "192.0.2.9")).status, 400);
  assert.equal((await activate(env, { pc: pc(9), device: "app-9", code: "ZZZZ-ZZZZ" }, "192.0.2.9")).status, 404);
  assert.equal((await activate(env, { pc: "x", device: "y" }, "192.0.2.9")).status, 400);
  assert.equal((await call(env, "/v1/referrals", { method: "POST", body: {} })).status, 400);

  // Asking again from the same PC keeps the code and gives a new secret; the old one stops working.
  const again = await inviter(env);
  assert.equal(again.code, me.code);
  assert.notEqual(again.secret, me.secret);
  assert.equal((await call(env, `/v1/referrals/${me.code}`, { token: me.secret })).status, 403);
  assert.equal((await call(env, `/v1/referrals/${me.code}`, { token: again.secret })).status, 200);

  assert.equal(normalizeReferral("https://x.test/r/abcd-efgh"), "ABCDEFGH");
  assert.equal(normalizeReferral("ab0d i234"), "AB0D1234");
  assert.equal(normalizeReferral("ABCDEFGHU"), null);
});
