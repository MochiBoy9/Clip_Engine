import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { normalizeHandle } from "../src/handles.js";
import { cleanMarkers } from "../src/library.js";
import { clipPage, escapeHtml, profilePage, SITE_URL, summarize } from "../src/pages.js";
import { fakeEnv, request } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);

async function account(env, name = "Enterro", device = "device-0000") {
  const r = await call(env, "/v1/accounts", { method: "POST", body: { name, device } });
  assert.equal(r.status, 200);
  const b = await r.json();
  return { token: b.token, id: b.account.id, base: `/v1/accounts/${b.account.id}` };
}

async function uploadClip(env, me, { visibility = "unlisted", bytes = 1000, title = "", localId } = {}) {
  const clip = await (await call(env, `${me.base}/clips`, {
    method: "POST", token: me.token,
    body: { title, game: "Valorant", seconds: 15, recorded: 1_762_142_400_000, width: 1920, height: 1080, visibility, localId,
            markers: [{ type: "kill", time: 10, text: "ENTERRO > X" }, { type: "teabag", time: 2 }, { type: "death", time: 99 }] },
  })).json();
  const up = await call(env, `${me.base}/clips/${clip.id}/video`, { method: "PUT", token: me.token, body: new Uint8Array(bytes).fill(7) });
  assert.equal(up.status, 200, await up.clone().text());
  return clip;
}

test("an account, a clip and its short link", async () => {
  const env = fakeEnv();
  const me = await account(env);
  const clip = await uploadClip(env, me);
  assert.equal(clip.code.length, 14);
  assert.deepEqual(clip.markers, [{ type: "kill", time: 10, text: "ENTERRO > X" }], "unknown moments and ones past the end are dropped");

  const page = await call(env, `/c/${clip.code.toLowerCase()}`);
  assert.equal(page.status, 200);
  const text = await page.text();
  assert.match(text, /<video id="v"/);
  assert.match(text, /og:video" content="https:\/\/squads\.test\/c\/[0-9A-Z]{14}\/video"/);
  assert.match(text, /class="mark" style="left:66\.67%/);
  assert.match(text, /1 kill/);

  const video = await call(env, `/c/${clip.code}/video`, { headers: { range: "bytes=0-99" } });
  assert.equal(video.status, 206);
  assert.equal((await video.arrayBuffer()).byteLength, 100);
  const info = await (await call(env, `/c/${clip.code}.json`)).json();
  assert.equal(info.views, 1, "opening the page counts a view");
  assert.equal(info.ownerName, "Enterro");

  // Private: the link stops working, the owner still sees it.
  await call(env, `${me.base}/clips/${clip.id}`, { method: "PATCH", token: me.token, body: { visibility: "private" } });
  assert.equal((await call(env, `/c/${clip.code}`)).status, 404);
  assert.equal((await call(env, `/c/${clip.code}/video`)).status, 404);
  assert.equal((await call(env, `${me.base}/clips/${clip.id}/video`, { token: me.token })).status, 200);

  // Someone else's token, or none, gets nothing from the account.
  const other = await account(env, "Kai", "device-0001");
  assert.equal((await call(env, `${me.base}/clips`, { token: other.token })).status, 401);
  assert.equal((await call(env, `${me.base}/clips`)).status, 401);
  // A made-up code.
  assert.equal((await call(env, "/c/ZZZZZZZZZZZZZZ")).status, 404);
  assert.equal((await call(env, "/c/short")).status, 404);
});

test("the same clip uploaded twice is one clip", async () => {
  const env = fakeEnv();
  const me = await account(env);
  const a = await uploadClip(env, me, { localId: "Valorant_2026-11-03_21-04-05.mp4:123" });
  const again = await (await call(env, `${me.base}/clips`, { method: "POST", token: me.token, body: { seconds: 15, localId: "Valorant_2026-11-03_21-04-05.mp4:123" } })).json();
  assert.equal(again.id, a.id);
  const list = await (await call(env, `${me.base}/clips`, { token: me.token })).json();
  assert.equal(list.clips.length, 1);
  assert.equal(list.account.storage.used, 1000);
});

test("profiles show public clips, featured first", async () => {
  const env = fakeEnv();
  const me = await account(env);
  const bad = await call(env, `${me.base}/account`, { method: "PATCH", token: me.token, body: { handle: "x" } });
  assert.equal(bad.status, 400);
  const taken = await call(env, `${me.base}/account`, { method: "PATCH", token: me.token, body: { handle: "admin" } });
  assert.equal(taken.status, 409);
  const set = await (await call(env, `${me.base}/account`, { method: "PATCH", token: me.token, body: { handle: "@Enterro" } })).json();
  assert.equal(set.handle, "enterro");

  const other = await account(env, "Kai", "device-0001");
  const clash = await call(env, `${other.base}/account`, { method: "PATCH", token: other.token, body: { handle: "ENTERRO" } });
  assert.equal(clash.status, 409);

  await uploadClip(env, me, { visibility: "unlisted", title: "Hidden" });
  const older = await uploadClip(env, me, { visibility: "public", title: "Older ace" });
  await uploadClip(env, me, { visibility: "public", title: "Newer" });
  await call(env, `${me.base}/clips/${older.id}`, { method: "PATCH", token: me.token, body: { featured: true } });

  const profile = await (await call(env, "/u/enterro.json")).json();
  assert.deepEqual(profile.clips.map((c) => c.title), ["Older ace", "Newer"]);
  const page = await (await call(env, "/u/Enterro")).text();
  assert.match(page, /Older ace/);
  assert.doesNotMatch(page, /Hidden/);
  assert.equal((await call(env, "/u/nobody")).status, 404);

  // Taking a new name gives the old one up.
  await call(env, `${me.base}/account`, { method: "PATCH", token: me.token, body: { handle: "enterro2" } });
  assert.equal((await call(env, "/u/enterro")).status, 404);
  assert.equal((await call(env, `${other.base}/account`, { method: "PATCH", token: other.token, body: { handle: "enterro" } })).status, 200);
});

test("plans set how much a library keeps, and only the operator sets them", async () => {
  const env = fakeEnv({ FREE_STORAGE_MB: "1", ADMIN_TOKEN: "operator-secret" });
  const me = await account(env);
  await uploadClip(env, me, { bytes: 700 * 1024 });
  const clip = await (await call(env, `${me.base}/clips`, { method: "POST", token: me.token, body: { seconds: 10 } })).json();
  const full = await call(env, `${me.base}/clips/${clip.id}/video`, { method: "PUT", token: me.token, body: new Uint8Array(700 * 1024) });
  assert.equal(full.status, 507);
  assert.match((await full.json()).error, /Free cloud library is full/);

  assert.equal((await call(env, "/v1/admin/plan", { method: "POST", token: "wrong", body: { account: me.id, plan: "pro" } })).status, 403);
  // Not around the Worker either.
  assert.equal((await call(env, `${me.base}/admin/plan`, { method: "POST", token: me.token, body: { plan: "pro" } })).status, 404);
  const pro = await (await call(env, "/v1/admin/plan", { method: "POST", token: "operator-secret", body: { account: me.id, plan: "pro" } })).json();
  assert.equal(pro.plan, "pro");
  assert.equal(pro.lines, 1440);
  assert.equal((await call(env, `${me.base}/clips/${clip.id}/video`, { method: "PUT", token: me.token, body: new Uint8Array(700 * 1024) })).status, 200);
});

test("signing a second PC in with a one-time code", async () => {
  const env = fakeEnv();
  const me = await account(env);
  const { code } = await (await call(env, `${me.base}/link`, { method: "POST", token: me.token })).json();
  assert.match(code, /^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{2}$/);
  const wrong = await call(env, "/v1/accounts/signin", { method: "POST", body: { code: code.slice(0, -1) + (code.endsWith("0") ? "1" : "0"), device: "device-0002" } });
  assert.equal(wrong.status, 403);
  const ok = await call(env, "/v1/accounts/signin", { method: "POST", body: { code: code.toLowerCase(), device: "device-0002" } });
  assert.equal(ok.status, 200);
  const second = await ok.json();
  assert.equal(second.account.id, me.id);
  assert.equal((await call(env, `${me.base}/clips`, { token: second.token })).status, 200);
  const reused = await call(env, "/v1/accounts/signin", { method: "POST", body: { code, device: "device-0003" } });
  assert.equal(reused.status, 403, "a code works once");
});

test("pages escape what players typed", () => {
  const page = clipPage({ code: "ABCDEFGH123456", title: '<script>alert("x")</script>', game: "A&B", seconds: 10, markers: [{ type: "kill", time: 5, text: '"><img>' }], ownerName: "<b>" }, "https://x");
  assert.doesNotMatch(page, /<script>alert/);
  assert.doesNotMatch(page, /"><img>/);
  assert.match(page, /A&amp;B/);
  const profile = profilePage({ name: "<i>", handle: "a_b", clips: [] }, "https://x");
  assert.match(profile, /&lt;i&gt;/);
  assert.equal(escapeHtml(`'`), "&#39;");
  assert.equal(summarize([{ type: "kill" }, { type: "kill" }, { type: "match_end" }]), "2 kills, 1 match end");
  assert.equal(normalizeHandle(" @Mochi.Boy_9 "), "mochi.boy_9");
  assert.equal(normalizeHandle(".dot"), null);
  assert.equal(cleanMarkers("nope", 10).length, 0);
});

test("a sign-in code stops working after a few wrong tries", async () => {
  const env = fakeEnv();
  const me = await account(env);
  const { code } = await (await call(env, `${me.base}/link`, { method: "POST", token: me.token })).json();
  const wrong = code.slice(0, -1) + (code.endsWith("0") ? "1" : "0");
  for (let i = 0; i < 5; i++) {
    assert.equal((await call(env, "/v1/accounts/signin", { method: "POST", body: { code: wrong, device: "device-9999" } })).status, 403);
  }
  assert.equal((await call(env, "/v1/accounts/signin", { method: "POST", body: { code, device: "device-9999" } })).status, 403,
    "the right code no longer works either");
});

test("each address gets a limited number of sign-ups, code tries and changes a minute", async () => {
  const counts = new Map();
  const limiter = (limit) => ({ async limit({ key }) { counts.set(key, (counts.get(key) ?? 0) + 1); return { success: counts.get(key) <= limit }; } });
  const env = fakeEnv({ AUTH_LIMIT: limiter(3), WRITE_LIMIT: limiter(100) });
  const from = (ip) => ({ method: "POST", body: { name: "Spam", device: "device-0000" }, headers: { "cf-connecting-ip": ip } });
  for (let i = 0; i < 3; i++) assert.equal((await call(env, "/v1/accounts", from("203.0.113.7"))).status, 200);
  const blocked = await call(env, "/v1/accounts", from("203.0.113.7"));
  assert.equal(blocked.status, 429);
  assert.equal(blocked.headers.get("retry-after"), "60");
  assert.equal((await call(env, "/v1/accounts", from("198.51.100.2"))).status, 200, "other addresses aren't affected");
  assert.equal((await call(env, "/v1/health", { headers: { "cf-connecting-ip": "203.0.113.7" } })).status, 200, "reads aren't limited");
});

test("a cloud clip carries a quiet Clip Engine mark that leads to the download", () => {
  const page = clipPage({ code: "ABCDEFGH123456", title: "Ace", game: "Valorant", seconds: 10, markers: [] }, "https://x");
  const mark = page.match(/<div class="player"[^>]*>(<a class="watermark"[^>]*>)/);
  assert.ok(mark, "the mark sits inside the player, over the video");
  assert.match(mark[1], new RegExp(`href="${SITE_URL}"`));
  assert.match(page, /\.watermark\{[^}]*opacity:\.55/, "semi-transparent until hovered");
  assert.match(page, /<meta property="og:site_name" content="Clip Engine">/);
  assert.match(page, /Download Clip Engine/);
});
