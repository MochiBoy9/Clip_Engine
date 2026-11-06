import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { normalizeGrant } from "../src/plans.js";
import { fakeEnv, request, signUpBody } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);
const ADMIN = "operator-secret";
const DAY = 24 * 3600_000;
const NO_CREDITS = { ultra: [], pro: [], supporter: [], exclusive: [], creator: [], special: [] };

const make = async (env, body) => (await call(env, "/v1/admin/grants", { method: "POST", token: ADMIN, body })).json();
const list = async (env) => (await (await call(env, "/v1/admin/grants", { token: ADMIN })).json()).grants;
const patch = (env, code, body) => call(env, `/v1/admin/grants/${encodeURIComponent(code)}`, { method: "PATCH", token: ADMIN, body });
const license = (env, key, device = "pc-one") => call(env, "/v1/license", { method: "POST", body: { key, device } });

async function account(env, name = "Enterro", device = "device-0000") {
  const b = await (await call(env, "/v1/accounts", { method: "POST", body: signUpBody(name, device) })).json();
  return { token: b.token, id: b.account.id, base: `/v1/accounts/${b.account.id}` };
}

test("only the operator's token reaches the key maker", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  for (const token of [undefined, "wrong", `${ADMIN}x`]) {
    assert.equal((await call(env, "/v1/admin/grants", { token })).status, 403);
    assert.equal((await call(env, "/v1/admin/grants", { method: "POST", token, body: { plan: "ultra" } })).status, 403);
  }
  assert.equal((await call(fakeEnv(), "/v1/admin/grants", { token: "" })).status, 403, "no token set means no one");
  assert.equal((await call(env, "/admin")).status, 404, "the key maker isn't on the service");
  const preflight = await call(env, "/v1/admin/grants", { method: "OPTIONS" });
  assert.equal(preflight.status, 204);
  assert.match(preflight.headers.get("access-control-allow-headers"), /authorization/);
  assert.equal((await call(env, "/v1/admin/grants", { token: ADMIN })).headers.get("access-control-allow-origin"), "*");
  // The directory's own routes can't be reached from outside.
  assert.equal((await call(env, "/grant/list", { method: "POST", body: {} })).status, 404);
});

test("special keys give a plan and features for their time, from first use", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const { grants } = await make(env, { count: 3, plan: "ultra", days: 30, extraStorageGb: 50, themes: ["chrome", "arc"], lines: 1440, maxDevices: 2, note: "for Kai" });
  assert.equal(grants.length, 3);
  assert.equal(new Set(grants.map((g) => g.code)).size, 3);
  const key = grants[0].code;
  assert.match(key, /^CEK-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$/);
  assert.equal(grants[0].status, "unused");
  assert.equal(grants[0].expires, null, "the clock starts when it's used");

  const first = await license(env, key.toLowerCase().replace(/-/g, " "));
  assert.equal(first.status, 200);
  const info = await first.json();
  assert.equal(info.plan, "ultra");
  assert.equal(info.grant, true);
  assert.equal(info.lines, 1440);
  assert.deepEqual(info.themes, ["chrome", "arc"]);
  assert.equal(info.extraStorage, 50 * 1024 * 1024 * 1024);
  assert.equal(info.storage, (256_000 + 50 * 1024) * 1024 * 1024);
  assert.ok(info.expires > Date.now() + 29 * DAY && info.expires <= Date.now() + 30 * DAY);

  // Two PCs, then no more until the operator frees them.
  assert.equal((await license(env, key, "pc-one")).status, 200);
  assert.equal((await license(env, key, "pc-two")).status, 200);
  const third = await license(env, key, "pc-three");
  assert.equal(third.status, 403);
  assert.match((await third.json()).error, /2 PCs/);
  assert.equal((await patch(env, key, { resetDevices: true })).status, 200);
  assert.equal((await license(env, key, "pc-three")).status, 200);

  const row = (await list(env)).find((g) => g.code === key);
  assert.equal(row.status, "active");
  assert.equal(row.devices, 1);
  assert.equal(row.note, "for Kai");

  // Turned off, then on again; changed to Pro with no end.
  assert.equal((await patch(env, key, { revoked: true })).status, 200);
  assert.equal((await license(env, key)).status, 403);
  const changed = await (await patch(env, key, { revoked: false, plan: "pro", days: null, themes: null, lines: null })).json();
  assert.equal(changed.status, "active");
  assert.equal(changed.expires, null);
  const pro = await (await license(env, key)).json();
  assert.equal(pro.plan, "pro");
  assert.equal(pro.lines, 2160, "Pro puts clips in the cloud at up to 4K");
  assert.equal(pro.themes, null);
  assert.equal(pro.expires, null);

  assert.equal((await license(env, "CEK-0000-0000-0000")).status, 403, "made-up keys are refused");
  assert.equal((await patch(env, key, { plan: "mega" })).status, 400);
  assert.equal((await patch(env, key, { lines: 1234 })).status, 400);
  assert.equal((await patch(env, key, { themes: ["neon"] })).status, 400);
  assert.equal((await patch(env, "CEK-0000-0000-0000", { note: "x" })).status, 404);
});

test("a special key runs out after its days, and restarting its time brings it back", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const [{ code }] = (await make(env, { plan: "pro", days: 7 })).grants;
  const realNow = Date.now;
  try {
    assert.equal((await license(env, code)).status, 200);
    Date.now = () => realNow() + 8 * DAY;
    const late = await license(env, code);
    assert.equal(late.status, 403);
    assert.match((await late.json()).error, /run out/);
    assert.equal((await list(env))[0].status, "expired");
    await patch(env, code, { restart: true });
    assert.equal((await license(env, code)).status, 200);
  } finally {
    Date.now = realNow;
  }
});

test("a Free special key gives only its extras, and stays out of the credits", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const [{ code }] = (await make(env, { plan: "free", days: null, extraStorageGb: 10, lines: 1440 })).grants;
  const info = await (await call(env, "/v1/license", { method: "POST", body: { key: code, device: "pc", credit: "Nope" } })).json();
  assert.equal(info.plan, "free");
  assert.equal(info.lines, 1440);
  assert.equal(info.storage, (2048 + 10 * 1024) * 1024 * 1024);
  assert.deepEqual(await (await call(env, "/v1/credits")).json(), NO_CREDITS);
});

test("a special key on a cloud account sets its storage and resolution, on one account at a time", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const [{ code }] = (await make(env, { plan: "pro", days: 365, extraStorageGb: 20, lines: 2160 })).grants;
  const a = await account(env);
  const put = await call(env, `${a.base}/plan`, { method: "POST", token: a.token, body: { key: code } });
  assert.equal(put.status, 200);
  const acct = await put.json();
  assert.equal(acct.plan, "pro");
  assert.equal(acct.lines, 2160);
  assert.equal(acct.storage.limit, (102_400 + 20 * 1024) * 1024 * 1024);

  const b = await account(env, "Mira", "device-1111");
  assert.equal((await call(env, `${b.base}/plan`, { method: "POST", token: b.token, body: { key: code } })).status, 409);

  const off = await (await call(env, `${a.base}/plan`, { method: "DELETE", token: a.token })).json();
  assert.equal(off.plan, "free");
  assert.equal(off.lines, 1080);
  assert.equal(off.storage.limit, 2048 * 1024 * 1024);
});

test("special keys read the way people type them", () => {
  assert.equal(normalizeGrant("CEK-ABCD-EFGH-JKMN"), "ABCDEFGHJKMN");
  assert.equal(normalizeGrant(" cek abcd efgh jkmn "), "ABCDEFGHJKMN");
  assert.equal(normalizeGrant("CEK-0O1I-L000-0000"), "001110000000");
  assert.equal(normalizeGrant("CEK-ABCD-EFGH"), null);
  assert.equal(normalizeGrant("PRO-KEY-0001"), null);
});

test("a special key's name stays out of the credits unless the operator lets it in", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const [{ code }] = (await make(env, { plan: "ultra", days: null })).grants;
  const info = await (await call(env, "/v1/license", { method: "POST", body: { key: code, device: "pc", credit: "Free Rider" } })).json();
  assert.equal(info.plan, "ultra");
  assert.deepEqual(await (await call(env, "/v1/credits")).json(), NO_CREDITS);
});

test("roles, perks, credits and a reason go with a special key", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const [made] = (await make(env, {
    plan: "ultra", role: "creator", days: null, perks: ["contact", "bugs", "contact"], credits: true,
    reason: "Streams Clip Engine every week", showReason: true, note: "kai's channel",
  })).grants;
  assert.equal(made.role, "creator");
  assert.equal(made.roleLabel, "Content Creator");
  assert.deepEqual(made.perks, ["contact", "bugs"]);
  assert.equal(made.credits, true);
  assert.equal(made.reason, "Streams Clip Engine every week");

  const info = await (await call(env, "/v1/license", { method: "POST", body: { key: made.code, device: "pc", credit: "Kai" } })).json();
  assert.equal(info.plan, "ultra");
  assert.equal(info.role, "creator");
  assert.equal(info.roleLabel, "Content Creator");
  assert.deepEqual(info.perks, ["contact", "bugs"]);
  assert.equal(info.credits, true);
  assert.equal(info.reason, "Streams Clip Engine every week");
  assert.deepEqual((await (await call(env, "/v1/credits")).json()).creator, ["Kai"]);

  // The reason can stay private, and taking the key out of the credits takes the name off.
  await patch(env, made.code, { showReason: false, credits: false });
  const hidden = await (await call(env, "/v1/license", { method: "POST", body: { key: made.code, device: "pc", credit: "Kai" } })).json();
  assert.equal(hidden.reason, "");
  assert.equal(hidden.credits, false);
  assert.deepEqual(await (await call(env, "/v1/credits")).json(), NO_CREDITS);

  // A Special or Exclusive key on a cloud account shows its title there.
  const [special] = (await make(env, { plan: "pro", role: "exclusive", days: 30 })).grants;
  const a = await account(env);
  const acct = await (await call(env, `${a.base}/plan`, { method: "POST", token: a.token, body: { key: special.code } })).json();
  assert.equal(acct.plan, "pro");
  assert.equal(acct.roleLabel, "Exclusive");

  assert.equal((await patch(env, made.code, { role: "vip" })).status, 400);
  assert.equal((await patch(env, made.code, { perks: ["free-money"] })).status, 400);
  assert.equal((await patch(env, made.code, { role: null })).status, 200);
  assert.equal((await list(env)).find((g) => g.code === made.code).role, null);
});

test("a special key can last hours or minutes, or end on a set date", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const realNow = Date.now;
  try {
    const [hours] = (await make(env, { plan: "pro", minutes: 90 })).grants;
    assert.equal(hours.minutes, 90);
    assert.equal(hours.days, null);
    const first = await (await license(env, hours.code)).json();
    assert.ok(first.expires > Date.now() + 89 * 60_000 && first.expires <= Date.now() + 90 * 60_000);

    const end = Date.now() + 3 * DAY;
    const [dated] = (await make(env, { plan: "ultra", days: null, endsAt: end })).grants;
    assert.equal(dated.expires, end, "a set end shows before the key is used");
    assert.equal((await (await license(env, dated.code)).json()).expires, end);

    // Both: whichever comes first.
    const [both] = (await make(env, { plan: "pro", days: 30, endsAt: end })).grants;
    assert.equal((await (await license(env, both.code)).json()).expires, end);

    Date.now = () => realNow() + 2 * 3600_000;
    assert.equal((await license(env, hours.code)).status, 403, "an hour and a half is up");
    assert.equal((await license(env, dated.code)).status, 200);
    Date.now = () => realNow() + 4 * DAY;
    const late = await license(env, dated.code);
    assert.equal(late.status, 403);
    assert.match((await late.json()).error, /run out/);
    const [unused] = (await make(env, { plan: "pro", endsAt: realNow() + DAY })).grants;
    assert.equal(unused.status, "expired", "a key whose date passed before it was used has run out too");
  } finally {
    Date.now = realNow;
  }
  assert.equal((await make(env, { plan: "pro", minutes: -5 })).error !== undefined, true);
  assert.equal((await make(env, { plan: "pro", endsAt: "tomorrow" })).error !== undefined, true);
});

test("a key with a direct line or bug reports reaches the operator's inbox, and gets answers", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const [line] = (await make(env, { plan: "pro", role: "special", perks: ["contact"], note: "Mira" })).grants;
  const [plain] = (await make(env, { plan: "pro" })).grants;
  const send = (key, body, device = "pc-one") => call(env, "/v1/contact", { method: "POST", body: { key, device, ...body } });
  const mine = async (key, device = "pc-one") => call(env, "/v1/contact/list", { method: "POST", body: { key, device } });

  assert.equal((await send(line.code, { kind: "message", text: "hi" })).status, 403, "the key has to be on that PC first");
  await license(env, line.code);
  await license(env, plain.code);

  const sent = await send(line.code, { kind: "message", text: "  Love the app! Can squads be bigger?  ", name: "Mira" });
  assert.equal(sent.status, 200);
  assert.equal((await sent.json()).messages[0].text, "Love the app! Can squads be bigger?");
  assert.equal((await send(line.code, { kind: "bug", text: "crash" })).status, 403, "bug reports are their own perk");
  assert.equal((await send(plain.code, { kind: "message", text: "hi" })).status, 403);
  assert.equal((await send(line.code, { kind: "message", text: "   " })).status, 400);
  assert.equal((await send(line.code, { kind: "message", text: "from elsewhere" }, "pc-two")).status, 403);
  assert.equal((await send("some-polar-key", { kind: "message", text: "hi" })).status, 403);

  // The operator sees it with who sent it, answers, and marks it done.
  for (const token of [undefined, "wrong"]) assert.equal((await call(env, "/v1/admin/messages", { token })).status, 403);
  const inbox = await (await call(env, "/v1/admin/messages", { token: ADMIN })).json();
  assert.equal(inbox.messages.length, 1);
  const [message] = inbox.messages;
  assert.equal(message.code, line.code);
  assert.equal(message.kind, "message");
  assert.equal(message.name, "Mira");
  assert.equal(message.roleLabel, "Special");
  assert.equal(message.note, "Mira");
  assert.equal((await list(env)).find((g) => g.code === line.code).openMessages, 1);
  const answer = await call(env, `/v1/admin/messages/${message.id}`, { method: "PATCH", token: ADMIN, body: { reply: "Yes, soon!", done: true } });
  assert.equal(answer.status, 200);
  assert.equal(answer.headers.get("access-control-allow-origin"), "*");
  assert.equal((await call(env, "/v1/admin/messages/999", { method: "PATCH", token: ADMIN, body: { done: true } })).status, 404);

  const back = await (await mine(line.code)).json();
  assert.equal(back.messages[0].reply, "Yes, soon!");
  assert.ok(back.messages[0].replied > 0);
  assert.deepEqual(back.perks, ["contact"]);
  assert.equal((await list(env)).find((g) => g.code === line.code).openMessages, 0);

  // Bug reports, and a daily limit.
  await patch(env, line.code, { perks: ["contact", "bugs"] });
  assert.equal((await send(line.code, { kind: "bug", text: "The clip list flickers" })).status, 200);
  for (let i = 0; i < 18; i++) await send(line.code, { kind: "message", text: `note ${i}` });
  assert.equal((await send(line.code, { kind: "message", text: "one too many" })).status, 429);

  // A turned-off key can't send.
  await patch(env, line.code, { revoked: true });
  assert.equal((await send(line.code, { kind: "bug", text: "hello?" })).status, 403);
});

test("keys made before minutes keep their days", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const [{ code }] = (await make(env, { plan: "pro", days: 7 })).grants;
  const row = (await list(env)).find((g) => g.code === code);
  assert.equal(row.days, 7);
  assert.equal(row.minutes, 7 * 1440);
});
