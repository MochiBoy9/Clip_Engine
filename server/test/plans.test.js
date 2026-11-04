import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { checkLicense, planName } from "../src/plans.js";
import { fakeEnv, request } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);

// Polar's license key API, answering for the keys in `keys` and counting the calls.
function fakePolar(keys) {
  const polar = { calls: 0, down: false };
  polar.fetch = async (url, init) => {
    polar.calls++;
    if (polar.down) throw new Error("offline");
    assert.match(url, /\/v1\/customer-portal\/license-keys\/validate$/);
    const body = JSON.parse(init.body);
    assert.equal(body.organization_id, "org_1");
    const key = keys[body.key];
    if (!key) return new Response(JSON.stringify({ detail: "Not found" }), { status: 404 });
    return new Response(JSON.stringify({ id: `lk_${body.key}`, display_key: `****-${body.key.slice(-4)}`, expires_at: null, ...key }));
  };
  return polar;
}

const POLAR = { POLAR_ORGANIZATION_ID: "org_1", POLAR_PRO_BENEFIT_ID: "ben_pro", POLAR_ULTRA_BENEFIT_ID: "ben_ultra" };

function planEnv(keys) {
  const polar = fakePolar(keys);
  return { env: fakeEnv({ ...POLAR, POLAR_FETCH: polar.fetch }), polar };
}

async function account(env, name = "Enterro", device = "device-0000") {
  const b = await (await call(env, "/v1/accounts", { method: "POST", body: { name, device } })).json();
  return { token: b.token, id: b.account.id, base: `/v1/accounts/${b.account.id}` };
}

test("a license key says which plan it pays for", async () => {
  const { env } = planEnv({
    "PRO-KEY-0001": { status: "granted", benefit_id: "ben_pro" },
    "ULTRA-KEY-0001": { status: "granted", benefit_id: "ben_ultra" },
    "OLD-KEY-0001": { status: "revoked", benefit_id: "ben_pro" },
    "OTHER-KEY-0001": { status: "granted", benefit_id: "ben_someone_else" },
    "GONE-KEY-0001": { status: "granted", benefit_id: "ben_pro", expires_at: "2020-01-01T00:00:00Z" },
  });
  assert.equal((await checkLicense(env, " PRO-KEY-0001 ")).plan, "pro");
  assert.equal((await checkLicense(env, "ULTRA-KEY-0001")).plan, "ultra");
  for (const key of ["OLD-KEY-0001", "OTHER-KEY-0001", "GONE-KEY-0001", "MISSING-0001", "x"]) {
    const check = await checkLicense(env, key);
    assert.equal(check.ok, false, key);
    assert.equal(check.rejected, true, key);
  }
  assert.match((await checkLicense(env, "OLD-KEY-0001")).error, /revoked/);
  assert.equal((await checkLicense({}, "PRO-KEY-0001")).rejected, false, "not open until Polar is set up");
  assert.equal(planName("plus"), "pro", "accounts from before Pro and Ultra");

  const pro = await call(env, "/v1/license", { method: "POST", body: { key: "PRO-KEY-0001" } });
  assert.equal(pro.status, 200);
  const info = await pro.json();
  assert.equal(info.plan, "pro");
  assert.equal(info.lines, 1440);
  assert.equal(info.storage, 102_400 * 1024 * 1024);
  assert.equal((await call(env, "/v1/license", { method: "POST", body: { key: "OLD-KEY-0001" } })).status, 403);
  const plans = await (await call(env, "/v1/plans")).json();
  assert.deepEqual(plans.plans.map((p) => [p.plan, p.price, p.lines]), [["free", 0, 1080], ["pro", 2.99, 1440], ["ultra", 9.99, 2160]]);
  assert.equal(plans.open, true);
});

test("subscribers can have their name in the credits", async () => {
  const { env } = planEnv({
    "PRO-KEY-0001": { status: "granted", benefit_id: "ben_pro" },
    "ULTRA-KEY-0001": { status: "granted", benefit_id: "ben_ultra" },
  });
  await call(env, "/v1/license", { method: "POST", body: { key: "PRO-KEY-0001", credit: "  Kai   the  Clipper " } });
  await call(env, "/v1/license", { method: "POST", body: { key: "ULTRA-KEY-0001", credit: "Mira" } });
  await call(env, "/v1/license", { method: "POST", body: { key: "ULTRA-KEY-0001", credit: "Mira" } });
  assert.deepEqual(await (await call(env, "/v1/credits")).json(), { ultra: ["Mira"], pro: ["Kai the Clipper"] });
  // Checking without credit leaves it; credit "" takes it off.
  await call(env, "/v1/license", { method: "POST", body: { key: "PRO-KEY-0001" } });
  await call(env, "/v1/license", { method: "POST", body: { key: "ULTRA-KEY-0001", credit: "" } });
  assert.deepEqual(await (await call(env, "/v1/credits")).json(), { ultra: [], pro: ["Kai the Clipper"] });
});

test("a subscription on a cloud account sets its storage, and goes back to Free when it ends", async () => {
  const keys = { "ULTRA-KEY-0001": { status: "granted", benefit_id: "ben_ultra" } };
  const { env, polar } = planEnv(keys);
  const me = await account(env);
  const other = await account(env, "Kai", "device-0001");

  const claimed = await call(env, `${me.base}/plan`, { method: "POST", token: me.token, body: { key: "ULTRA-KEY-0001" } });
  assert.equal(claimed.status, 200, await claimed.clone().text());
  const a = await claimed.json();
  assert.equal(a.plan, "ultra");
  assert.equal(a.storage.limit, 256_000 * 1024 * 1024);
  assert.equal(a.planKey, "****-0001");

  const twice = await call(env, `${other.base}/plan`, { method: "POST", token: other.token, body: { key: "ULTRA-KEY-0001" } });
  assert.equal(twice.status, 409, "one subscription, one account");
  assert.equal((await call(env, `${me.base}/plan`, { method: "POST", token: me.token, body: { key: "NOPE-KEY-0001" } })).status, 403);

  // Twelve hours on, Polar says the subscription ended.
  const library = env.LIBRARIES.instances.get(me.id);
  const calls = polar.calls;
  await call(env, `${me.base}/account`, { token: me.token });
  assert.equal(polar.calls, calls, "not checked again so soon");
  library.setMeta("plan_checked", String(Date.now() - 13 * 3600_000));
  keys["ULTRA-KEY-0001"] = { status: "revoked", benefit_id: "ben_ultra" };
  const after = await (await call(env, `${me.base}/account`, { token: me.token })).json();
  assert.equal(after.plan, "free");
  assert.equal(after.planKey, null);

  // A key put back on works, and Polar being down keeps it for now.
  keys["ULTRA-KEY-0001"] = { status: "granted", benefit_id: "ben_ultra" };
  await call(env, `${me.base}/plan`, { method: "POST", token: me.token, body: { key: "ULTRA-KEY-0001" } });
  library.setMeta("plan_checked", String(Date.now() - 13 * 3600_000));
  polar.down = true;
  assert.equal((await (await call(env, `${me.base}/account`, { token: me.token })).json()).plan, "ultra");
  library.setMeta("plan_checked", String(Date.now() - 13 * 3600_000));
  library.setMeta("plan_ok", String(Date.now() - 15 * 24 * 3600_000));
  assert.equal((await (await call(env, `${me.base}/account`, { token: me.token })).json()).plan, "free", "two weeks without Polar");
  polar.down = false;

  // Taking it off frees it for another account.
  await call(env, `${me.base}/plan`, { method: "DELETE", token: me.token });
  assert.equal((await call(env, `${other.base}/plan`, { method: "POST", token: other.token, body: { key: "ULTRA-KEY-0001" } })).status, 200);
});
