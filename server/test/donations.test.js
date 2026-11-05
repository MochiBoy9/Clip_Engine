import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { checkLicense, donationPerks } from "../src/plans.js";
import { fakeEnv, request } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);
const GB = 1024 * 1024 * 1024;

// Polar: the license key API for the keys in `keys`, and the orders API for the orders in `orders`.
function fakePolar(keys, orders) {
  const polar = { orderCalls: 0, ordersDown: false };
  polar.fetch = async (url, init = {}) => {
    if (url.includes("/v1/customer-portal/license-keys/validate")) {
      const body = JSON.parse(init.body);
      const key = keys[body.key];
      if (!key) return new Response(JSON.stringify({ detail: "Not found" }), { status: 404 });
      return new Response(JSON.stringify({ id: `lk_${body.key}`, display_key: `****-${body.key.slice(-4)}`, expires_at: null, ...key }));
    }
    const u = new URL(url);
    if (u.pathname === "/v1/subscriptions/") return new Response(JSON.stringify({ items: [], pagination: { max_page: 1 } }));
    assert.equal(u.pathname, "/v1/orders/");
    polar.orderCalls++;
    if (polar.ordersDown) return new Response("{}", { status: 502 });
    assert.equal(init.headers.authorization, "Bearer polar_oat_test");
    assert.equal(u.searchParams.get("product_id"), "prod_donate");
    const customer = u.searchParams.get("customer_id");
    const items = orders.filter((o) => o.customer_id === customer);
    return new Response(JSON.stringify({ items, pagination: { total_count: items.length, max_page: 1 } }));
  };
  return polar;
}

const POLAR = {
  POLAR_ORGANIZATION_ID: "org_1", POLAR_PRO_BENEFIT_ID: "ben_pro", POLAR_ULTRA_BENEFIT_ID: "ben_ultra",
  POLAR_DONATION_PRODUCT_ID: "prod_donate", POLAR_DONATION_BENEFIT_ID: "ben_donate", POLAR_ACCESS_TOKEN: "polar_oat_test",
};

const order = (customer, cents, extra = {}) => ({
  customer_id: customer, product_id: "prod_donate", status: "paid", paid: true, net_amount: cents, refunded_amount: 0, ...extra,
});

function donationEnv(keys, orders, vars = {}) {
  const polar = fakePolar(keys, orders);
  return { env: fakeEnv({ ...POLAR, POLAR_FETCH: polar.fetch, ...vars }), polar };
}

const credits = async (env) => (await call(env, "/v1/credits")).json();

test("a donation of any amount makes a Supporter with a little more cloud storage", async () => {
  const { env } = donationEnv(
    { "GIFT-KEY-0001": { status: "granted", benefit_id: "ben_donate", customer_id: "cus_1" } },
    [order("cus_1", 300)],
  );
  const check = await checkLicense(env, "GIFT-KEY-0001");
  assert.equal(check.ok, true);
  assert.equal(check.plan, "free");
  assert.equal(check.role, "supporter");
  assert.equal(check.features.extraMb, 3 * 1024, "1 GB for each dollar");
  assert.equal(check.expires, null, "a donation doesn't run out");

  const info = await (await call(env, "/v1/license", { method: "POST", body: { key: "GIFT-KEY-0001", credit: "Mira" } })).json();
  assert.equal(info.plan, "free");
  assert.equal(info.roleLabel, "Supporter");
  assert.equal(info.supporter, true);
  assert.equal(info.donated, 300);
  assert.equal(info.extraStorage, 3 * GB);
  assert.equal(info.storage, 5 * GB);
  assert.equal(info.credits, false, "under $5: no credits yet");
  assert.deepEqual((await credits(env)).supporter, []);
});

test("from $5 in all a supporter's name goes in the credits, and a refund below it takes it off", async () => {
  const orders = [order("cus_1", 300), order("cus_1", 200), order("cus_1", 900, { status: "pending", paid: false })];
  const { env } = donationEnv({ "GIFT-KEY-0001": { status: "granted", benefit_id: "ben_donate", customer_id: "cus_1" } }, orders);
  const info = await (await call(env, "/v1/license", { method: "POST", body: { key: "GIFT-KEY-0001", credit: " Mira " } })).json();
  assert.equal(info.donated, 500, "two paid donations; the pending one doesn't count yet");
  assert.equal(info.credits, true);
  assert.deepEqual((await credits(env)).supporter, ["Mira"]);

  orders[1].refunded_amount = 200;
  orders[1].status = "refunded";
  orders[1].paid = true;
  const after = await (await call(env, "/v1/license", { method: "POST", body: { key: "GIFT-KEY-0001", credit: "Mira" } })).json();
  assert.equal(after.donated, 300);
  assert.equal(after.credits, false);
  assert.deepEqual((await credits(env)).supporter, []);
});

test("the storage bonus stops at its cap, and the amounts can be changed", async () => {
  assert.equal(donationPerks({}, 10_000).extraMb, 25 * 1024, "$100 is still 25 GB");
  assert.equal(donationPerks({}, 50).extraMb, 1024, "at least 1 GB");
  assert.equal(donationPerks({}, null).credits, false, "unknown amount: no credits");
  const env = { DONATION_GB_PER_DOLLAR: "2", DONATION_MAX_GB: "10", DONATION_CREDITS_FROM: "10" };
  assert.equal(donationPerks(env, 300).extraMb, 6 * 1024);
  assert.equal(donationPerks(env, 9_900).extraMb, 10 * 1024);
  assert.equal(donationPerks(env, 900).credits, false);
  assert.equal(donationPerks(env, 1_000).credits, true);
  assert.equal(donationPerks({ DONATION_MAX_GB: "0" }, 1_000).extraMb, 0, "the storage bonus can be turned off");
});

test("a subscriber who donated keeps their plan and badge and gets the bonus on top", async () => {
  const { env } = donationEnv(
    {
      "PRO-KEY-0001": { status: "granted", benefit_id: "ben_pro", customer_id: "cus_2" },
      "PRO-KEY-0002": { status: "granted", benefit_id: "ben_pro", customer_id: "cus_3" },
    },
    [order("cus_2", 1_000)],
  );
  const info = await (await call(env, "/v1/license", { method: "POST", body: { key: "PRO-KEY-0001", credit: "Kai" } })).json();
  assert.equal(info.plan, "pro");
  assert.equal(info.role, null, "Pro stays the badge");
  assert.equal(info.supporter, true);
  assert.equal(info.donated, 1_000);
  assert.equal(info.storage, (102_400 / 1024 + 10) * GB);
  assert.deepEqual((await credits(env)).pro, ["Kai"], "listed under their plan");

  const plain = await (await call(env, "/v1/license", { method: "POST", body: { key: "PRO-KEY-0002" } })).json();
  assert.equal(plain.supporter, false);
  assert.equal(plain.donated, null);
  assert.equal(plain.extraStorage, 0);
});

test("a donation key with nothing paid is refused, and Polar being down still lets a donor in", async () => {
  const orders = [];
  const { env, polar } = donationEnv({ "GIFT-KEY-0001": { status: "granted", benefit_id: "ben_donate", customer_id: "cus_1" } }, orders);
  const refused = await call(env, "/v1/license", { method: "POST", body: { key: "GIFT-KEY-0001" } });
  assert.equal(refused.status, 403);
  assert.match((await refused.json()).error, /hasn't gone through/);

  orders.push(order("cus_1", 2_000));
  await call(env, "/v1/license", { method: "POST", body: { key: "GIFT-KEY-0001", credit: "Mira" } });
  assert.deepEqual((await credits(env)).supporter, ["Mira"]);
  polar.ordersDown = true;
  const down = await (await call(env, "/v1/license", { method: "POST", body: { key: "GIFT-KEY-0001", credit: "Mira" } })).json();
  assert.equal(down.supporter, true);
  assert.equal(down.donated, null);
  assert.equal(down.extraStorage, GB, "the smallest thank-you until Polar answers");
  assert.deepEqual((await credits(env)).supporter, ["Mira"], "a name isn't taken off because Polar didn't answer");
});

test("without an access token a donation key still works, without the credits", async () => {
  const { env, polar } = donationEnv(
    { "GIFT-KEY-0001": { status: "granted", benefit_id: "ben_donate", customer_id: "cus_1" } },
    [order("cus_1", 2_000)],
    { POLAR_ACCESS_TOKEN: "" },
  );
  const info = await (await call(env, "/v1/license", { method: "POST", body: { key: "GIFT-KEY-0001", credit: "Mira" } })).json();
  assert.equal(info.roleLabel, "Supporter");
  assert.equal(info.credits, false);
  assert.equal(polar.orderCalls, 0);
});

test("a supporter's name stays in the credits after their app stops checking in", async () => {
  const { env } = donationEnv(
    {
      "GIFT-KEY-0001": { status: "granted", benefit_id: "ben_donate", customer_id: "cus_1" },
      "PRO-KEY-0001": { status: "granted", benefit_id: "ben_pro", customer_id: "cus_2" },
    },
    [order("cus_1", 500)],
  );
  await call(env, "/v1/license", { method: "POST", body: { key: "GIFT-KEY-0001", credit: "Mira" } });
  await call(env, "/v1/license", { method: "POST", body: { key: "PRO-KEY-0001", credit: "Kai" } });
  const handles = env.HANDLES.instances.get("handles");
  handles.sql.exec("UPDATE credits SET seen = ?", Date.now() - 60 * 24 * 3600_000);
  const list = await credits(env);
  assert.deepEqual(list.supporter, ["Mira"]);
  assert.deepEqual(list.pro, [], "a subscription that stopped checking in goes");
});

test("a donation key on a cloud account adds its storage and shows Supporter", async () => {
  const { env } = donationEnv(
    { "GIFT-KEY-0001": { status: "granted", benefit_id: "ben_donate", customer_id: "cus_1" } },
    [order("cus_1", 800)],
  );
  const made = await (await call(env, "/v1/accounts", { method: "POST", body: { name: "Mira", device: "device-0000" } })).json();
  const base = `/v1/accounts/${made.account.id}`;
  const claimed = await (await call(env, `${base}/plan`, { method: "POST", token: made.token, body: { key: "GIFT-KEY-0001" } })).json();
  assert.equal(claimed.plan, "free");
  assert.equal(claimed.roleLabel, "Supporter");
  assert.equal(claimed.storage.limit, 10 * GB, "2 GB free and 8 GB from $8");
});

test("the plans list says what donations give", async () => {
  const { env } = donationEnv({}, []);
  const plans = await (await call(env, "/v1/plans")).json();
  assert.deepEqual(plans.donations, { open: true, gbPerDollar: 1, maxGb: 25, creditsFrom: 5 });
  const closed = await (await call(fakeEnv({}), "/v1/plans")).json();
  assert.equal(closed.donations.open, false);
});
