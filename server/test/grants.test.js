import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { normalizeGrant } from "../src/plans.js";
import { fakeEnv, request } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);
const ADMIN = "operator-secret";
const DAY = 24 * 3600_000;

const make = async (env, body) => (await call(env, "/v1/admin/grants", { method: "POST", token: ADMIN, body })).json();
const list = async (env) => (await (await call(env, "/v1/admin/grants", { token: ADMIN })).json()).grants;
const patch = (env, code, body) => call(env, `/v1/admin/grants/${encodeURIComponent(code)}`, { method: "PATCH", token: ADMIN, body });
const license = (env, key, device = "pc-one") => call(env, "/v1/license", { method: "POST", body: { key, device } });

async function account(env, name = "Enterro", device = "device-0000") {
  const b = await (await call(env, "/v1/accounts", { method: "POST", body: { name, device } })).json();
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
  assert.equal(pro.lines, 1440);
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
  assert.deepEqual(await (await call(env, "/v1/credits")).json(), { ultra: [], pro: [] });
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
