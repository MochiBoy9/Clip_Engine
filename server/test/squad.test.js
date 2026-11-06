import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { isMediaPointer, serveMedia } from "../src/media.js";
import { fakeEnv, makeSquad, request } from "./fakes.js";

// What the Worker sends for a squad view's video or thumbnail: the bytes the Durable Object points at.
async function media(squad, bucket, req) {
  const answer = await squad.fetch(req.clone());
  return isMediaPointer(answer) ? serveMedia(req, { CLIPS: bucket }, answer) : answer;
}

async function body(response) {
  return response.json();
}

// A squad with an owner and members, each with a socket that has said hello.
async function squadOf(names, env = {}) {
  const { squad, ctx, bucket } = makeSquad(env);
  const created = await squad.fetch(request("/create", {
    method: "POST", body: { name: names[0], squad: "Night shift", device: "device-0000" }, headers: { "x-squad-id": "ABCD1234" },
  }));
  assert.equal(created.status, 200);
  const first = await body(created);
  const secret = first.squad.invite.replace(/-/g, "").slice(8);
  const people = [{ name: names[0], token: first.token, id: first.squad.you }];
  for (let i = 1; i < names.length; i++) {
    const joined = await squad.fetch(request("/join", { method: "POST", body: { name: names[i], secret, device: `device-000${i}` } }));
    assert.equal(joined.status, 200);
    const b = await body(joined);
    people.push({ name: names[i], token: b.token, id: b.squad.you });
  }
  for (const p of people) {
    await squad.fetch(request("/socket", { token: p.token, headers: { upgrade: "websocket" } }));
    p.ws = squad.lastSocket;
    await squad.webSocketMessage(p.ws, JSON.stringify({ t: "hello", app: "test" }));
  }
  for (const p of people) p.ws.take();
  return { squad, ctx, bucket, people, secret, info: first.squad };
}

const say = (squad, p, message) => squad.webSocketMessage(p.ws, JSON.stringify(message));

test("create, join and the invite code", async () => {
  const { squad, people, info, secret } = await squadOf(["Enterro", "Kai"]);
  assert.equal(info.name, "Night shift");
  assert.match(info.invite, /^ABCD-1234-/);
  const wrong = await squad.fetch(request("/join", { method: "POST", body: { name: "Nope", secret: "000000000000", device: "device-9999" } }));
  assert.equal(wrong.status, 403);
  // The same PC joining again keeps its place and gets a new token.
  const again = await body(await squad.fetch(request("/join", { method: "POST", body: { name: "Kai", secret, device: "device-0001" } })));
  assert.equal(again.squad.you, people[1].id);
  assert.equal(again.squad.members.length, 2);
  const old = await squad.fetch(request("/squad", { token: people[1].token }));
  assert.equal(old.status, 401, "the old token stops working");
  const unauthenticated = await squad.fetch(request("/library"));
  assert.equal(unauthenticated.status, 401);
});

test("a full squad turns new members away, and only the owner rotates the code", async () => {
  const { squad, people, secret } = await squadOf(["A", "B"], { MAX_MEMBERS: "2" });
  const full = await squad.fetch(request("/join", { method: "POST", body: { name: "C", secret, device: "device-0003" } }));
  assert.equal(full.status, 409);
  assert.equal((await squad.fetch(request("/invite/rotate", { method: "POST", token: people[1].token }))).status, 403);
  const rotated = await body(await squad.fetch(request("/invite/rotate", { method: "POST", token: people[0].token })));
  assert.notEqual(rotated.invite.replace(/-/g, "").slice(8), secret);
});

test("presence, linking and a kill reaching only linked squadmates", async () => {
  const t = Date.now();
  const { squad, people } = await squadOf(["Enterro", "Kai", "Remy"]);
  const [me, kai, remy] = people;
  await say(squad, me, { t: "presence", game: "Valorant", match: t - 60_000 });
  await say(squad, kai, { t: "presence", game: "Valorant", match: t - 50_000 });
  await say(squad, remy, { t: "presence", game: "Valorant", match: t - 900_000 });  // another lobby
  const presence = kai.ws.take("presence").pop();
  assert.equal(presence.member, remy.id);
  assert.deepEqual(presence.groups, [{ game: "Valorant", members: [me.id, kai.id] }]);

  await say(squad, me, { t: "moment", ref: "k1", type: "kill", at: t - 200, game: "Valorant", text: "ENTERRO > X" });
  const ok = me.ws.take("moment-ok")[0];
  assert.deepEqual(ok.linked, [kai.id]);
  const got = kai.ws.take("moment");
  assert.equal(got.length, 1);
  assert.equal(got[0].id, ok.id);
  assert.equal(got[0].at, t - 200);
  assert.equal(got[0].byName, "Enterro");
  assert.equal(remy.ws.take("moment").length, 0);

  // Unlinking stops it.
  await say(squad, kai, { t: "unlink", member: me.id });
  await say(squad, me, { t: "moment", ref: "k2", type: "kill", at: t, game: "Valorant", text: "" });
  assert.deepEqual(me.ws.take("moment-ok")[0].linked, []);
});

test("views, the library, uploads with ranges and asking for an upload", async () => {
  const t = Date.now();
  const { squad, people, bucket } = await squadOf(["Enterro", "Kai", "Remy"]);
  const [me, kai, remy] = people;
  for (const p of [me, kai]) await say(squad, p, { t: "presence", game: "The Finals", match: null });
  await say(squad, me, { t: "moment", ref: "k", type: "kill", at: t, game: "The Finals", text: "" });
  const id = me.ws.take("moment-ok")[0].id;

  await say(squad, me, { t: "view", moment: id, utc_start: t - 10_000, seconds: 15 });
  await say(squad, kai, { t: "view", moment: id, utc_start: t - 9_000, seconds: 14.5 });
  await say(squad, remy, { t: "view", moment: id, utc_start: t - 9_000, seconds: 14 });  // wasn't linked: ignored
  assert.equal(remy.ws.take("view").length, 2, "everyone hears about views");

  let library = await body(await squad.fetch(request("/library", { token: remy.token })));
  assert.equal(library.moments.length, 1);
  assert.deepEqual(library.moments[0].views.map((v) => v.name), ["Enterro", "Kai"], "the one who made the play comes first");
  assert.ok(library.moments[0].views.every((v) => !v.uploaded));

  // Remy asks for Kai's view; Kai's app is asked to upload it.
  await say(squad, remy, { t: "want", moment: id, member: kai.id });
  assert.deepEqual(kai.ws.take("upload-request"), [{ t: "upload-request", moment: id, from: remy.id }]);
  assert.equal(remy.ws.take("want-ok")[0].online, true);

  const video = new Uint8Array(1000).map((_, i) => i % 256);
  const forbidden = await squad.fetch(request(`/moments/${id}/views/${me.id}/video`, { method: "PUT", token: kai.token, body: video }));
  assert.equal(forbidden.status, 403);
  const up = await squad.fetch(request(`/moments/${id}/views/me/video`, { method: "PUT", token: kai.token, body: video }));
  assert.equal(up.status, 200);
  assert.ok(bucket.objects.has(`ABCD1234/${id}/${kai.id}.mp4`));
  assert.equal(remy.ws.take("uploaded")[0].member, kai.id);

  const whole = await media(squad, bucket, request(`/moments/${id}/views/${kai.id}/video`, { token: remy.token }));
  assert.equal(whole.status, 200);
  assert.equal((await whole.arrayBuffer()).byteLength, 1000);
  const part = await media(squad, bucket, request(`/moments/${id}/views/${kai.id}/video`, { token: remy.token, headers: { range: "bytes=100-199" } }));
  assert.equal(part.status, 206);
  assert.equal(part.headers.get("content-range"), "bytes 100-199/1000");
  assert.equal(new Uint8Array(await part.arrayBuffer())[0], 100);
  // The browser already has this upload: answered without reading R2.
  const reads = bucket.reads;
  const again = await media(squad, bucket, request(`/moments/${id}/views/${kai.id}/video`, { token: remy.token, headers: { "if-none-match": whole.headers.get("etag") } }));
  assert.equal(again.status, 304);
  assert.equal(bucket.reads, reads);
  // Not uploaded: the Durable Object says so without asking R2.
  assert.equal((await media(squad, bucket, request(`/moments/${id}/views/${me.id}/thumb`, { token: remy.token }))).status, 404);
  assert.equal(bucket.reads, reads);

  library = await body(await squad.fetch(request("/library", { token: remy.token })));
  assert.ok(library.moments[0].views.find((v) => v.member === kai.id).uploaded);
  const missing = await squad.fetch(request(`/moments/${id}/views/${me.id}/video`, { token: remy.token }));
  assert.equal(missing.status, 404);
});

test("a full library makes room by dropping the oldest unstarred uploads", async () => {
  const t = Date.now();
  // 1 MB of room.
  const { squad, people, bucket } = await squadOf(["A", "B"], { SQUAD_STORAGE_MB: "1" });
  const [a] = people;
  await say(squad, a, { t: "presence", game: "Apex Legends", match: null });
  const ids = [];
  for (let i = 0; i < 3; i++) {
    await say(squad, a, { t: "moment", ref: `r${i}`, type: "kill", at: t - 3000 + i * 1000, game: "Apex Legends", text: "" });
    const id = a.ws.take("moment-ok")[0].id;
    ids.push(id);
    await say(squad, a, { t: "view", moment: id, utc_start: t, seconds: 10 });
  }
  await squad.fetch(request(`/moments/${ids[0]}/star`, { method: "POST", token: a.token, body: { star: true } }));
  const half = new Uint8Array(450 * 1024);
  for (const id of ids) {
    const r = await squad.fetch(request(`/moments/${id}/views/me/video`, { method: "PUT", token: a.token, body: half }));
    assert.equal(r.status, 200);
  }
  // The starred first one stays; the second made room for the third.
  assert.ok(bucket.objects.has(`ABCD1234/${ids[0]}/${a.id}.mp4`));
  assert.ok(!bucket.objects.has(`ABCD1234/${ids[1]}/${a.id}.mp4`));
  assert.ok(bucket.objects.has(`ABCD1234/${ids[2]}/${a.id}.mp4`));
  // Still listed, just on its owner's PC now.
  const library = await body(await squad.fetch(request("/library", { token: a.token })));
  assert.equal(library.moments.length, 3);
  assert.equal(library.moments.find((m) => m.id === ids[1]).views[0].uploaded, false);

  const tooBig = await squad.fetch(request(`/moments/${ids[1]}/views/me/video`, {
    method: "PUT", token: a.token, body: new Uint8Array(10), headers: { "content-length": String(200 * 1024 * 1024) },
  }));
  assert.equal(tooBig.status, 413);
});

test("cleanup keeps starred moments and drops old uploads", async () => {
  const t = Date.now();
  const { squad, people, bucket } = await squadOf(["A", "B"], { KEEP_DAYS: "30" });
  const [a] = people;
  await say(squad, a, { t: "presence", game: "Overwatch", match: null });
  await say(squad, a, { t: "moment", ref: "x", type: "kill", at: t, game: "Overwatch", text: "" });
  const kept = a.ws.take("moment-ok")[0].id;
  await say(squad, a, { t: "moment", ref: "y", type: "kill", at: t, game: "Overwatch", text: "" });
  const old = a.ws.take("moment-ok")[0].id;
  await say(squad, a, { t: "moment", ref: "z", type: "kill", at: t, game: "Overwatch", text: "" });
  const nobody = a.ws.take("moment-ok")[0].id;  // no one saved a view of it
  for (const id of [kept, old]) {
    await say(squad, a, { t: "view", moment: id, utc_start: t, seconds: 10 });
    await squad.fetch(request(`/moments/${id}/views/me/video`, { method: "PUT", token: a.token, body: new Uint8Array(10) }));
  }
  await squad.fetch(request(`/moments/${kept}/star`, { method: "POST", token: a.token, body: {} }));
  await squad.cleanup(t + 31 * 24 * 3600_000);
  assert.ok(bucket.objects.has(`ABCD1234/${kept}/${a.id}.mp4`));
  assert.ok(!bucket.objects.has(`ABCD1234/${old}/${a.id}.mp4`));
  const library = await body(await squad.fetch(request("/library", { token: a.token })));
  assert.deepEqual(library.moments.map((m) => m.id), [kept]);
  assert.equal(squad.sql.exec("SELECT COUNT(*) AS n FROM moments WHERE id = ?", nobody).toArray()[0].n, 0);
});

test("leaving takes your clips with you, and the last one out deletes the squad", async () => {
  const t = Date.now();
  const { squad, people, bucket } = await squadOf(["Owner", "Kai"]);
  const [owner, kai] = people;
  for (const p of people) await say(squad, p, { t: "presence", game: "Valorant", match: null });
  await say(squad, owner, { t: "moment", ref: "k", type: "kill", at: t, game: "Valorant", text: "" });
  const id = owner.ws.take("moment-ok")[0].id;
  await say(squad, kai, { t: "view", moment: id, utc_start: t, seconds: 10 });
  await squad.fetch(request(`/moments/${id}/views/me/video`, { method: "PUT", token: kai.token, body: new Uint8Array(10) }));

  assert.equal((await squad.fetch(request(`/members/${owner.id}`, { method: "DELETE", token: kai.token }))).status, 403);
  assert.equal((await squad.fetch(request("/members/me", { method: "DELETE", token: owner.token }))).status, 200);
  assert.ok(owner.ws.closed);
  const left = kai.ws.take("left")[0];
  assert.equal(left.owner, kai.id, "the next member becomes the owner");
  assert.ok(bucket.objects.has(`ABCD1234/${id}/${kai.id}.mp4`));

  assert.equal((await squad.fetch(request("/members/me", { method: "DELETE", token: kai.token }))).status, 200);
  assert.equal(bucket.objects.size, 0);
});

test("the worker routes creates, joins and squad requests", async () => {
  const env = fakeEnv();
  const health = await worker.fetch(request("/v1/health"), env);
  assert.equal((await health.json()).ok, true);

  const created = await (await worker.fetch(request("/v1/squads", { method: "POST", body: { name: "Enterro", device: "device-0000" } }), env)).json();
  assert.equal(created.squad.name, "Enterro's squad");
  const joined = await worker.fetch(request("/v1/join", {
    method: "POST", body: { invite: created.squad.invite.toLowerCase(), name: "Kai", device: "device-0001" },
  }), env);
  assert.equal(joined.status, 200);
  const kai = await joined.json();
  assert.equal(kai.squad.members.length, 2);

  const info = await worker.fetch(request(`/v1/squads/${created.squad.id.toLowerCase()}/squad`, { token: kai.token }), env);
  assert.equal(info.status, 200);
  assert.equal((await worker.fetch(request("/v1/join", { method: "POST", body: { invite: "nope" } }), env)).status, 400);
  const unknown = await worker.fetch(request("/v1/join", {
    method: "POST", body: { invite: "ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ", name: "X", device: "device-0002" },
  }), env);
  assert.equal(unknown.status, 404);
  assert.equal((await worker.fetch(request("/v1/squads/abc/squad"), env)).status, 404);
  // The join page from a Discord status.
  const page = await worker.fetch(request(`/s/${created.squad.invite}`), env);
  assert.equal(page.status, 200);
  const text = await page.text();
  assert.match(text, /Join Enterro&#39;s squad/);
  assert.match(text, /clipengine:\/\/squad\/join\//);
  assert.match(text, /2 of 8 · Enterro, Kai/);
  const wrongCode = created.squad.invite.slice(0, -1) + (created.squad.invite.endsWith("0") ? "1" : "0");
  assert.equal((await worker.fetch(request(`/s/${wrongCode}`), env)).status, 404);
  // The squad's own create and join can't be reached around the Worker.
  const sneaky = await worker.fetch(request(`/v1/squads/${created.squad.id}/join`, { method: "POST", body: { secret: "x" } }), env);
  assert.equal(sneaky.status, 404);
});

test("socket messages from someone who isn't set up are ignored, and clocks answer pings", async () => {
  const { squad, people } = await squadOf(["A"]);
  await say(squad, people[0], { t: "ping", c: 12345 });
  const pong = people[0].ws.take("pong")[0];
  assert.equal(pong.c, 12345);
  assert.ok(Math.abs(pong.s - Date.now()) < 1000);
  await squad.webSocketMessage(people[0].ws, "not json");
  await squad.webSocketClose(people[0].ws);
  assert.ok(people[0].ws.closed);
});
