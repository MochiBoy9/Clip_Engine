import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { normalizeHandle } from "../src/handles.js";
import { cleanMarkers } from "../src/library.js";
import { clipPage, escapeHtml, profilePage, SITE_URL, summarize } from "../src/pages.js";
import { fakeEnv, request, signUpBody } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);

async function account(env, name = "Enterro", device = "device-0000") {
  const r = await call(env, "/v1/accounts", { method: "POST", body: signUpBody(name, device) });
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
  assert.equal(pro.lines, 2160);
  assert.equal(pro.maxClip, 1024 * 1024 * 1024);
  assert.equal((await call(env, `${me.base}/clips/${clip.id}/video`, { method: "PUT", token: me.token, body: new Uint8Array(700 * 1024) })).status, 200);
});

test("every plan records 4K, but 4K and big clips go to the cloud with Pro or Ultra", async () => {
  const MB = 1024 * 1024;
  const env = fakeEnv({ ADMIN_TOKEN: "operator-secret", UPLOAD_PART_BYTES: String(5 * MB) });
  const me = await account(env);
  const add = async (body) => (await call(env, `${me.base}/clips`, { method: "POST", token: me.token, body: { seconds: 10, ...body } })).json();
  const put = (clip, bytes, headers) =>
    call(env, `${me.base}/clips/${clip.id}/video`, { method: "PUT", token: me.token, body: new Uint8Array(bytes), headers });
  const start = (clip, size) => call(env, `${me.base}/clips/${clip.id}/video/parts`, { method: "POST", token: me.token, body: { size } });

  const free = await (await call(env, `${me.base}/account`, { token: me.token })).json();
  assert.equal(free.lines, 1080);
  assert.equal(free.maxClip, 100 * MB);
  assert.equal(free.maxSeconds, 40);

  // Free: clips up to 40 seconds; Pro up to 10 minutes and Ultra 30.
  const long = await call(env, `${me.base}/clips`, { method: "POST", token: me.token, body: { seconds: 45, width: 1920, height: 1080 } });
  assert.equal(long.status, 403);
  assert.match((await long.json()).error, /up to 40 seconds in the cloud\. Trim this one, or Pro and Ultra take longer ones/);
  assert.equal((await put(await add({ seconds: 40.4, width: 1920, height: 1080 }), 1000)).status, 200);

  // Free: a 1080p clip goes up; a 4K one doesn't, and nor does one over 100 MB.
  assert.equal((await put(await add({ width: 1920, height: 1080 }), 1000)).status, 200);
  const uhd = await add({ width: 3840, height: 2160, localId: "uhd.mp4|5000" });
  const refused = await put(uhd, 1000);
  assert.equal(refused.status, 403);
  assert.match((await refused.json()).error, /Free plan puts clips in the cloud at up to 1080p\. Pro and Ultra take 4K/);
  const big = await start(await add({ width: 1920, height: 1080 }), 150 * MB);
  assert.equal(big.status, 413);
  assert.match((await big.json()).error, /up to 100 MB in the cloud\. Pro and Ultra take bigger ones/);
  // The app sends the same clip again as a 1080p copy: the clip takes the copy's size, and it goes up.
  const again = await add({ width: 1920, height: 1080, localId: "uhd.mp4|5000" });
  assert.equal(again.id, uhd.id);
  assert.equal(again.height, 1080);
  assert.equal((await put(again, 1000)).status, 200);

  // Pro: 4K, longer clips, and clips over 100 MB in parts.
  await call(env, "/v1/admin/plan", { method: "POST", token: "operator-secret", body: { account: me.id, plan: "pro" } });
  const tenMinutes = await add({ seconds: 600, width: 3840, height: 2160 });
  assert.equal((await put(tenMinutes, 1000)).status, 200);
  assert.equal((await call(env, `${me.base}/clips`, { method: "POST", token: me.token, body: { seconds: 900 } })).status, 403);
  await call(env, "/v1/admin/plan", { method: "POST", token: "operator-secret", body: { account: me.id, plan: "ultra" } });
  assert.equal((await put(await add({ seconds: 1800 }), 1000)).status, 200);
  const hour = await call(env, `${me.base}/clips`, { method: "POST", token: me.token, body: { seconds: 3600 } });
  assert.match((await hour.json()).error, /Ultra plan takes clips of up to 30 minutes in the cloud\. Trim this one to upload it/);
  await call(env, "/v1/admin/plan", { method: "POST", token: "operator-secret", body: { account: me.id, plan: "pro" } });
  assert.equal((await put(await add({ width: 3840, height: 2160 }), 1000)).status, 200);
  const whole = await put(await add({ width: 3840, height: 2160 }), 1000, { "content-length": String(150 * MB) });
  assert.equal(whole.status, 413, "a Worker can't take a body over 100 MB in one go");
  assert.match((await whole.json()).error, /go up in parts/);
  assert.equal((await start(await add({ width: 3840, height: 2160 }), 2 * 1024 * MB)).status, 413, "over Pro's 1 GB");

  const clip = await add({ width: 3840, height: 2160 });
  const size = 12 * MB + 123;
  const plan = await (await start(clip, size)).json();
  assert.deepEqual(plan, { partBytes: 5 * MB, parts: 3 });
  const bytes = new Uint8Array(size).map((_, i) => i % 251);
  const part = (n, body) => call(env, `${me.base}/clips/${clip.id}/video/parts/${n}`, { method: "PUT", token: me.token, body });
  assert.equal((await part(1, bytes.slice(0, 1000))).status, 400, "every part but the last is partBytes");
  assert.equal((await part(4, bytes.slice(0, 123))).status, 400);
  assert.equal((await part(3, bytes.slice(10 * MB))).status, 200);
  assert.equal((await part(1, bytes.slice(0, 5 * MB))).status, 200);
  const early = await call(env, `${me.base}/clips/${clip.id}/video/parts/done`, { method: "POST", token: me.token });
  assert.equal(early.status, 409);
  assert.equal((await part(2, bytes.slice(5 * MB, 10 * MB))).status, 200);
  const done = await call(env, `${me.base}/clips/${clip.id}/video/parts/done`, { method: "POST", token: me.token });
  assert.equal(done.status, 200);
  const finished = await done.json();
  assert.equal(finished.uploaded, true);
  assert.equal(finished.size, size);
  assert.deepEqual(env.CLIPS.objects.get(`library/${me.id}/${clip.id}.mp4`).bytes, bytes);
  assert.equal(env.CLIPS.uploads.size, 0);

  // Removing a clip part way through drops its parts.
  const dropped = await add({ width: 3840, height: 2160 });
  assert.equal((await start(dropped, 12 * MB)).status, 200);
  assert.equal(env.CLIPS.uploads.size, 1);
  assert.equal((await call(env, `${me.base}/clips/${dropped.id}`, { method: "DELETE", token: me.token })).status, 200);
  assert.equal(env.CLIPS.uploads.size, 0);

  // Back on Free, a 4K clip that's part way up isn't finished.
  const late = await add({ width: 3840, height: 2160 });
  await start(late, 6 * MB);
  await call(env, "/v1/admin/plan", { method: "POST", token: "operator-secret", body: { account: me.id, plan: "free" } });
  const tooLate = await call(env, `${me.base}/clips/${late.id}/video/parts/done`, { method: "POST", token: me.token });
  assert.ok([403, 409].includes(tooLate.status));
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
  const from = (ip) => ({ method: "POST", body: signUpBody("Spam"), headers: { "cf-connecting-ip": ip } });
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

test("a shared clip's Discord preview says it was made with Clip Engine and where to get it", async () => {
  const env = fakeEnv();
  const me = await account(env);
  const clip = await uploadClip(env, me, { title: "Ace" });

  const page = await (await call(env, `/c/${clip.code}`)).text();
  const description = page.match(/<meta property="og:description" content="([^"]*)"/)[1];
  assert.match(description, /^Valorant · 1 kill · 0:15\. Clipped automatically with Clip Engine\. Free for Windows: mochiboy9\.github\.io\/Clip_Engine$/);
  assert.match(page, new RegExp(`<link rel="alternate" type="application/json\\+oembed" href="https://squads\\.test/c/${clip.code}/oembed"`));
  assert.match(page, /class="get-btn" href="[^"]+#download">Download free</, "a download button under the video");

  const oembed = await call(env, `/c/${clip.code}/oembed`);
  assert.equal(oembed.status, 200);
  const body = await oembed.json();
  assert.equal(body.provider_name, "Clip Engine");
  assert.equal(body.provider_url, SITE_URL);
  assert.equal(body.author_url, SITE_URL);
  assert.match(body.author_name, /^Enterro · Clipped with Clip Engine, free for Windows$/);
  assert.equal(body.title, "Ace");
  assert.equal(body.width, 1920);

  assert.equal((await call(env, "/c/ABCDEFGH123456/oembed")).status, 404);
});

test("a link-preview fetch isn't counted as a view, and bad addresses are 404s, not errors", async () => {
  const env = fakeEnv();
  const me = await account(env);
  const clip = await uploadClip(env, me);
  await call(env, `/c/${clip.code}`, { headers: { "user-agent": "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)" } });
  assert.equal((await (await call(env, `/c/${clip.code}.json`)).json()).views, 0);
  await call(env, `/c/${clip.code}`, { headers: { "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" } });
  assert.equal((await (await call(env, `/c/${clip.code}.json`)).json()).views, 1);

  assert.equal((await call(env, "/u/%E0%A4%A")).status, 404);
  assert.equal((await call(env, "/s/%E0%A4%A")).status, 404);
});
