import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { normalizeCode, readCookie } from "../src/hub.js";
import { WEB_SESSION_MS } from "../src/library.js";
import { badgeFor } from "../src/plans.js";
import { fakeEnv, request, signUpBody } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);
const ADMIN = "operator-secret";
const HUB = { "x-hub": "1" };

async function account(env, name = "Enterro", device = "device-0000") {
  const b = await (await call(env, "/v1/accounts", { method: "POST", body: signUpBody(name, device) })).json();
  return { token: b.token, id: b.account.id, base: `/v1/accounts/${b.account.id}` };
}

async function clip(env, me, { visibility = "public", game = "Valorant", title = "", kills = 1, thumb = false } = {}) {
  const c = await (await call(env, `${me.base}/clips`, {
    method: "POST", token: me.token,
    body: { title, game, seconds: 20, visibility, markers: Array.from({ length: kills }, (_, i) => ({ type: "kill", time: i + 1 })) },
  })).json();
  if (thumb) await call(env, `${me.base}/clips/${c.id}/thumb`, { method: "PUT", token: me.token, body: new Uint8Array(10) });
  await call(env, `${me.base}/clips/${c.id}/video`, { method: "PUT", token: me.token, body: new Uint8Array(100) });
  return c;
}

const feed = async (env, query = "") => (await call(env, `/hub/api/feed${query}`)).json();
const setHandle = (env, me, handle) => call(env, `${me.base}/account`, { method: "PATCH", token: me.token, body: { handle } });

// Signs in on the hub with a code made in the app, and answers the cookie header to send after.
async function hubSignIn(env, me) {
  const { code } = await (await call(env, `${me.base}/link`, { method: "POST", token: me.token, body: {} })).json();
  const r = await call(env, "/hub/api/signin", { method: "POST", headers: HUB, body: { code } });
  assert.equal(r.status, 200, await r.clone().text());
  const cookies = r.headers.getSetCookie();
  const session = cookies.find((c) => c.startsWith("ce_hub="));
  assert.match(session, /HttpOnly; Secure; SameSite=Lax/);
  return { cookie: session.split(";")[0], cookies };
}

test("public clips are in the hub, and only while they're public and uploaded", async () => {
  const env = fakeEnv();
  const me = await account(env);
  await setHandle(env, me, "enterro");
  const shown = await clip(env, me, { title: "Ace on Ascent", kills: 5 });
  await clip(env, me, { visibility: "unlisted", title: "Just for friends" });
  const draft = await (await call(env, `${me.base}/clips`, { method: "POST", token: me.token, body: { game: "Valorant", seconds: 5, visibility: "public" } })).json();

  let page = await feed(env);
  assert.deepEqual(page.clips.map((c) => c.code), [shown.code], "unlisted and not-yet-uploaded clips stay out");
  assert.deepEqual(page.clips[0].moments, { kill: 5 });
  assert.deepEqual(page.clips[0].owner, { name: "Enterro", handle: "enterro", badge: null });
  assert.match(page.html, /Ace on Ascent/);
  assert.match(page.html, /5 Kills/);

  // Opening the clip counts for the hub too.
  await call(env, `/c/${shown.code}`);
  assert.equal((await feed(env)).clips[0].views, 1);

  // Made private: gone at once. Public again: back. Removed: gone.
  await call(env, `${me.base}/clips/${shown.id}`, { method: "PATCH", token: me.token, body: { visibility: "private" } });
  assert.equal((await feed(env)).clips.length, 0);
  await call(env, `${me.base}/clips/${shown.id}`, { method: "PATCH", token: me.token, body: { visibility: "public" } });
  assert.equal((await feed(env)).clips.length, 1);
  await call(env, `${me.base}/clips/${shown.id}`, { method: "DELETE", token: me.token });
  assert.equal((await feed(env)).clips.length, 0);
  assert.ok(draft.id);

  // A new name follows the clips into the hub.
  await clip(env, me, { title: "Clutch" });
  await setHandle(env, me, "enterro_gg");
  assert.equal((await feed(env)).clips[0].owner.handle, "enterro_gg");
});

test("clips made public before the hub existed join it the next time their owner is around", async () => {
  const env = fakeEnv();
  const me = await account(env);
  await setHandle(env, me, "enterro");
  const c = await clip(env, me);
  // As if it had been made public before the hub: not in it, and the account not yet looked at.
  await env.HANDLES.get("handles").fetch(new Request("https://h/feed/remove", { method: "POST", body: JSON.stringify({ code: c.code }) }));
  env.LIBRARIES.instances.get(me.id).sql.exec("DELETE FROM meta WHERE key = 'feed_v'");
  assert.equal((await feed(env)).clips.length, 0);
  await call(env, "/u/enterro");
  assert.equal((await feed(env)).clips.length, 1);
});

test("the hub sorts, filters by game, searches and pages", async () => {
  const env = fakeEnv();
  const kai = await account(env, "Kai", "device-0001");
  const me = await account(env, "Enterro");
  await setHandle(env, kai, "kai");
  for (let i = 0; i < 30; i++) await clip(env, me, { title: `Round ${i}`, game: i % 3 === 0 ? "Apex Legends" : "Valorant" });
  const popular = await clip(env, kai, { title: "100% the best", game: "Fortnite" });
  for (let i = 0; i < 3; i++) await call(env, `/c/${popular.code}`);

  const first = await feed(env);
  assert.equal(first.clips.length, 24);
  assert.equal(first.clips[0].code, popular.code, "newest first");
  const second = await feed(env, `?cursor=${first.next}`);
  assert.equal(second.clips.length, 7);
  assert.equal(second.next, null);
  assert.equal(new Set([...first.clips, ...second.clips].map((c) => c.code)).size, 31, "no clip twice");

  assert.equal((await feed(env, "?sort=top")).clips[0].code, popular.code);
  assert.equal((await feed(env, "?game=apex%20legends")).clips.length, 10);
  assert.equal((await feed(env, "?q=%40kai")).clips.length, 1, "by username");
  assert.equal((await feed(env, "?q=100%25")).clips.length, 1, "% is a letter, not a wildcard");
  assert.equal((await feed(env, "?q=round%2012")).clips.length, 1);

  const { games } = await (await call(env, "/hub/api/games")).json();
  assert.deepEqual(games[0], { game: "Valorant", clips: 20 });

  // The page itself works without scripts: the first cards, the game chips, and a link for more.
  const html = await (await call(env, "/hub?game=Valorant")).text();
  assert.match(html, /Clips from everyone/);
  assert.match(html, /aria-current="page">Valorant<b>20<\/b>/);
  assert.equal((html.match(/class="card"/g) ?? []).length, 20);
  assert.doesNotMatch(html, /id="more"/);
  assert.match(await (await call(env, "/hub")).text(), /id="more"/);
});

test("badges show for Pro, Ultra and roles, and their owners can hide them", async () => {
  assert.equal(badgeFor("free", null), null);
  assert.deepEqual(badgeFor("pro", null), { kind: "pro", label: "Pro" });
  assert.deepEqual(badgeFor("pro", "creator"), { kind: "creator", label: "Content Creator" });

  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const me = await account(env);
  await setHandle(env, me, "enterro");
  const c = await clip(env, me);
  const [grant] = (await (await call(env, "/v1/admin/grants", { method: "POST", token: ADMIN, body: { plan: "ultra", role: "creator", days: 30 } })).json()).grants;
  const acct = await (await call(env, `${me.base}/plan`, { method: "POST", token: me.token, body: { key: grant.code } })).json();
  assert.deepEqual(acct.badge, { kind: "creator", label: "Content Creator" });
  assert.equal(acct.hideBadge, false);

  assert.deepEqual((await feed(env)).clips[0].owner.badge, { kind: "creator", label: "Content Creator" });
  assert.match(await (await call(env, "/u/enterro")).text(), /class="badge creator"/);
  assert.match(await (await call(env, `/c/${c.code}`)).text(), /class="badge creator"/);

  const hidden = await (await call(env, `${me.base}/account`, { method: "PATCH", token: me.token, body: { hideBadge: true } })).json();
  assert.equal(hidden.hideBadge, true);
  assert.deepEqual(hidden.badge, { kind: "creator", label: "Content Creator" }, "the account still knows it has one");
  assert.equal((await feed(env)).clips[0].owner.badge, null);
  assert.equal((await (await call(env, "/u/enterro.json")).json()).badge, null);
  assert.doesNotMatch(await (await call(env, `/c/${c.code}`)).text(), /class="badge/);
  // New public clips come in without it too.
  await clip(env, me, { title: "Another" });
  assert.ok((await feed(env)).clips.every((x) => x.owner.badge === null));

  await call(env, `${me.base}/account`, { method: "PATCH", token: me.token, body: { hideBadge: false } });
  assert.ok((await feed(env)).clips.every((x) => x.owner.badge?.kind === "creator"));

  // Back to Free: no badge to show.
  await call(env, `${me.base}/plan`, { method: "DELETE", token: me.token });
  assert.ok((await feed(env)).clips.every((x) => x.owner.badge === null));
});

test("signing in on the hub with a code from the app", async () => {
  const env = fakeEnv();
  const me = await account(env);
  await setHandle(env, me, "enterro");
  const c = await clip(env, me, { visibility: "private", thumb: true });

  assert.deepEqual(normalizeCode("k7qm-2x9d-pa4r-ht"), { account: "K7QM2X9D", secret: "PA4RHT" });
  assert.equal(normalizeCode("nope"), null);
  assert.equal(readCookie(new Request("https://x", { headers: { cookie: "a=1; ce_hub=AB.cd" } }), "ce_hub"), "AB.cd");

  // Signed out, the hub page sends you to sign in; the feed works for anyone.
  const away = await call(env, "/hub/me");
  assert.equal(away.status, 303);
  assert.match(away.headers.get("location"), /\/hub\/signin\?next=me$/);
  assert.equal((await call(env, "/hub")).status, 200);
  assert.equal((await call(env, "/hub/api/me")).status, 401);

  const { cookie, cookies } = await hubSignIn(env, me);
  assert.ok(cookies.some((x) => x.startsWith("ce_hub_who=%40enterro;")), "the bar's name, readable by the page");
  const headers = { cookie, ...HUB };
  const mine = await (await call(env, "/hub/api/me", { headers })).json();
  assert.equal(mine.account.handle, "enterro");
  assert.equal(mine.clips.length, 1);
  assert.equal((await call(env, `/hub/api/clips/${c.id}/thumb`, { headers })).status, 200, "your own private thumbnails");
  assert.equal((await call(env, "/hub/me", { headers })).status, 200);

  // Changes need the x-hub header and this origin, so another site can't make them with the cookie.
  const made = (body, h = headers) => call(env, `/hub/api/clips/${c.id}`, { method: "PATCH", headers: h, body });
  assert.equal((await made({ visibility: "public" }, { cookie })).status, 403);
  assert.equal((await made({ visibility: "public" }, { ...headers, origin: "https://evil.test" })).status, 403);
  assert.equal((await made({ visibility: "public" }, { ...headers, origin: "https://squads.test" })).status, 200);
  assert.equal((await feed(env)).clips.length, 1);

  const renamed = await call(env, "/hub/api/me", { method: "PATCH", headers, body: { name: "Enterro GG" } });
  assert.equal((await renamed.json()).name, "Enterro GG");

  // A used code doesn't work twice; a made-up one doesn't work at all.
  const { code } = await (await call(env, `${me.base}/link`, { method: "POST", token: me.token, body: {} })).json();
  assert.equal((await call(env, "/hub/api/signin", { method: "POST", headers: HUB, body: { code } })).status, 200);
  assert.equal((await call(env, "/hub/api/signin", { method: "POST", headers: HUB, body: { code } })).status, 403);
  assert.equal((await call(env, "/hub/api/signin", { method: "POST", headers: HUB, body: { code: "ZZZZ-ZZZZ-ZZZZ-ZZ" } })).status, 403);

  // Signing out ends that session on the account, not just in the browser.
  const out = await call(env, "/hub/api/signout", { method: "POST", headers, body: {} });
  assert.ok(out.headers.getSetCookie().some((x) => x.startsWith("ce_hub=;") && x.includes("Max-Age=0")));
  assert.equal((await call(env, "/hub/api/me", { headers })).status, 401);
  // The app's own sign-in is untouched.
  assert.equal((await call(env, `${me.base}/account`, { token: me.token })).status, 200);
});

test("a hub sign-in runs out after 30 days; the app's doesn't", async () => {
  const env = fakeEnv();
  const me = await account(env);
  const { cookie } = await hubSignIn(env, me);
  const now = Date.now;
  try {
    Date.now = () => now() + WEB_SESSION_MS + 60_000;
    assert.equal((await call(env, "/hub/api/me", { headers: { cookie } })).status, 401);
    assert.equal((await call(env, `${me.base}/account`, { token: me.token })).status, 200);
  } finally {
    Date.now = now;
  }
});

test("reporting a clip, and the operator hiding it", async () => {
  const env = fakeEnv({ ADMIN_TOKEN: ADMIN });
  const me = await account(env);
  const c = await clip(env, me, { title: "<script>alert(1)</script>" });
  const report = (body, headers = HUB) => call(env, "/hub/api/report", { method: "POST", headers, body });

  assert.match(await (await call(env, `/c/${c.code}`)).text(), /Report this clip/);
  assert.equal((await report({ code: c.code, reason: "cheating" }, {})).status, 403, "only from the hub's own pages");
  assert.equal((await report({ code: c.code, reason: "cheating", text: "wallhack" })).status, 200);
  assert.equal((await report({ code: "AAAAAAAAAAAAAA" })).status, 404);

  assert.equal((await call(env, "/v1/admin/reports")).status, 403);
  const { clips } = await (await call(env, "/v1/admin/reports", { token: ADMIN })).json();
  assert.equal(clips[0].code, c.code);
  assert.equal(clips[0].reports, 1);
  assert.equal(clips[0].list[0].text, "wallhack");

  const html = await (await call(env, "/hub")).text();
  assert.doesNotMatch(html, /<script>alert/);
  assert.match(html, /&lt;script&gt;alert/);

  await call(env, "/v1/admin/hide", { method: "POST", token: ADMIN, body: { code: c.code } });
  assert.equal((await feed(env)).clips.length, 0);
  assert.equal((await call(env, `/c/${c.code}`)).status, 200, "its link still plays for whoever has it");
  await call(env, "/v1/admin/hide", { method: "POST", token: ADMIN, body: { code: c.code, hidden: false, clearReports: true } });
  assert.equal((await feed(env)).clips.length, 1);
});

test("the service's address in a browser opens the hub", async () => {
  const env = fakeEnv();
  const r = await call(env, "/", { headers: { accept: "text/html" } });
  assert.equal(r.status, 302);
  assert.match(r.headers.get("location"), /\/hub$/);
  assert.equal((await (await call(env, "/")).json()).ok, true);
  const page = await call(env, "/hub/signin");
  assert.match(page.headers.get("content-security-policy"), /frame-ancestors 'none'/);
  assert.match(await page.text(), /autocomplete="one-time-code"/);
});
