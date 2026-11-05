import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { checkLicense, subscriptionRenewal } from "../src/plans.js";
import { fakeEnv, request } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);
const DAY = 24 * 60 * 60 * 1000;
const iso = (ms) => new Date(ms).toISOString();

// Polar: license keys, and each customer's subscriptions.
function fakePolar(keys, subscriptions) {
  const polar = { subscriptionCalls: 0, down: false };
  polar.fetch = async (url, init = {}) => {
    if (url.includes("/v1/customer-portal/license-keys/validate")) {
      const key = keys[JSON.parse(init.body).key];
      if (!key) return new Response(JSON.stringify({ detail: "Not found" }), { status: 404 });
      return new Response(JSON.stringify({ id: "lk_1", display_key: "****-0001", expires_at: null, ...key }));
    }
    const u = new URL(url);
    assert.equal(u.pathname, "/v1/subscriptions/");
    assert.equal(init.headers.authorization, "Bearer polar_oat_test");
    assert.equal(u.searchParams.get("active"), "true");
    polar.subscriptionCalls++;
    if (polar.down) return new Response("{}", { status: 403 });
    const items = subscriptions.filter((s) => s.customer_id === u.searchParams.get("customer_id"));
    return new Response(JSON.stringify({ items, pagination: { max_page: 1 } }));
  };
  return polar;
}

const POLAR = { POLAR_ORGANIZATION_ID: "org_1", POLAR_PRO_BENEFIT_ID: "ben_pro", POLAR_ULTRA_BENEFIT_ID: "ben_ultra" };
const KEYS = { "PRO-KEY-0001": { status: "granted", benefit_id: "ben_pro", customer_id: "cus_1" } };

test("a subscription's next renewal goes to the app, from Polar", async () => {
  const soon = Date.now() + 2 * DAY;
  const polar = fakePolar(KEYS, [
    { customer_id: "cus_1", status: "active", current_period_end: iso(soon), cancel_at_period_end: false },
    { customer_id: "cus_2", status: "active", current_period_end: iso(Date.now() + DAY), cancel_at_period_end: false },
  ]);
  const env = fakeEnv({ ...POLAR, POLAR_ACCESS_TOKEN: "polar_oat_test", POLAR_FETCH: polar.fetch });
  const check = await checkLicense(env, "PRO-KEY-0001");
  assert.deepEqual(check.renewal, { renews: Date.parse(iso(soon)), ending: false });

  const reply = await (await call(env, "/v1/license", { method: "POST", body: { key: "PRO-KEY-0001" } })).json();
  assert.equal(reply.renews, Date.parse(iso(soon)));
  assert.equal(reply.ending, false);
  assert.equal(reply.email, undefined, "nothing about the customer comes back");
});

test("a cancelled subscription says when it ends", async () => {
  const end = Date.now() + 5 * DAY;
  const polar = fakePolar(KEYS, [
    { customer_id: "cus_1", status: "active", current_period_end: iso(end), cancel_at_period_end: true, ends_at: iso(end) },
  ]);
  const env = fakeEnv({ ...POLAR, POLAR_ACCESS_TOKEN: "polar_oat_test", POLAR_FETCH: polar.fetch });
  assert.deepEqual(await subscriptionRenewal(env, "cus_1"), { renews: Date.parse(iso(end)), ending: true });
});

test("without a token, or when Polar won't say, there's no renewal date and the key still works", async () => {
  const polar = fakePolar(KEYS, []);
  const noToken = fakeEnv({ ...POLAR, POLAR_FETCH: polar.fetch });
  const check = await checkLicense(noToken, "PRO-KEY-0001");
  assert.equal(check.ok, true);
  assert.equal(check.renewal, null);
  assert.equal(polar.subscriptionCalls, 0);

  polar.down = true;
  const env = fakeEnv({ ...POLAR, POLAR_ACCESS_TOKEN: "polar_oat_test", POLAR_FETCH: polar.fetch });
  const reply = await (await call(env, "/v1/license", { method: "POST", body: { key: "PRO-KEY-0001" } })).json();
  assert.equal(reply.plan, "pro");
  assert.equal(reply.renews, null);
  assert.equal(reply.ending, false);
});
