// Runs squad sync end to end against a running service, the way two desktop apps would:
//   npx wrangler dev        (in one terminal)
//   node test/e2e.mjs [http://127.0.0.1:8787]
// Creates a squad, joins it, links two players in one game, sends a kill, saves views, uploads
// one, reads it back with a range and leaves.

import assert from "node:assert/strict";

const base = process.argv[2] ?? "http://127.0.0.1:8787";

async function call(path, { method = "GET", token, body, headers = {} } = {}) {
  const h = { ...headers };
  if (token) h.authorization = `Bearer ${token}`;
  if (body && !(body instanceof Uint8Array)) h["content-type"] = "application/json";
  const r = await fetch(base + path, { method, headers: h, body: body instanceof Uint8Array ? body : body && JSON.stringify(body) });
  return r;
}

function connect(squadId, token) {
  const url = base.replace(/^http/, "ws") + `/v1/squads/${squadId}/socket`;
  // Node's WebSocket takes headers through undici's own option, as the desktop app's does.
  const ws = new WebSocket(url, { headers: { authorization: `Bearer ${token}` } });
  const inbox = [];
  const waiters = [];
  ws.addEventListener("message", (e) => {
    const m = JSON.parse(e.data);
    const i = waiters.findIndex((w) => w.type === m.t);
    if (i >= 0) waiters.splice(i, 1)[0].resolve(m);
    else inbox.push(m);
  });
  return new Promise((resolve, reject) => {
    ws.addEventListener("error", (e) => reject(new Error("socket failed: " + (e.message ?? ""))));
    ws.addEventListener("open", () =>
      resolve({
        send: (m) => ws.send(JSON.stringify(m)),
        next: (type) => {
          const i = inbox.findIndex((m) => m.t === type);
          if (i >= 0) return Promise.resolve(inbox.splice(i, 1)[0]);
          return new Promise((res, rej) => {
            waiters.push({ type, resolve: res });
            setTimeout(() => rej(new Error(`no ${type} message`)), 5000);
          });
        },
        close: () => ws.close(),
      }));
  });
}

const health = await (await call("/v1/health")).json();
assert.equal(health.ok, true);

const a = await (await call("/v1/squads", { method: "POST", body: { name: "Enterro", device: "e2e-device-a" } })).json();
const squadId = a.squad.id;
const joined = await call("/v1/join", { method: "POST", body: { invite: a.squad.invite, name: "Kai", device: "e2e-device-b" } });
assert.equal(joined.status, 200, await joined.clone().text());
const b = await joined.json();
console.log("squad", squadId, "invite", a.squad.invite);

const wa = await connect(squadId, a.token);
const wb = await connect(squadId, b.token);
wa.send({ t: "hello", app: "e2e" });
wb.send({ t: "hello", app: "e2e" });
const welcome = await wa.next("welcome");
assert.equal(welcome.squad.members.length, 2);

const sent = Date.now();
wa.send({ t: "ping", c: sent });
const pong = await wa.next("pong");
assert.equal(pong.c, sent);
console.log("clock offset estimate", pong.s - (sent + Date.now()) / 2, "ms");

const now = Date.now();
wa.send({ t: "presence", game: "Valorant", match: now - 30_000 });
wb.send({ t: "presence", game: "Valorant", match: now - 25_000 });
await new Promise((r) => setTimeout(r, 300));
wa.send({ t: "moment", ref: "k1", type: "kill", at: now, game: "Valorant", text: "ENTERRO > X" });
const ok = await wa.next("moment-ok");
assert.deepEqual(ok.linked, [b.squad.you]);
const moment = await wb.next("moment");
assert.equal(moment.id, ok.id);
console.log("kill reached the linked squadmate:", moment.id);

wa.send({ t: "view", moment: ok.id, utc_start: now - 10_000, seconds: 15 });
wb.send({ t: "view", moment: ok.id, utc_start: now - 9_500, seconds: 15 });
await wa.next("view");
await new Promise((r) => setTimeout(r, 300));

const video = new Uint8Array(300_000).map((_, i) => i % 251);
const up = await call(`/v1/squads/${squadId}/moments/${ok.id}/views/me/video`, { method: "PUT", token: b.token, body: video });
assert.equal(up.status, 200, await up.clone().text());
const uploaded = await wa.next("uploaded");
assert.equal(uploaded.size, video.length);

const part = await call(`/v1/squads/${squadId}/moments/${ok.id}/views/${b.squad.you}/video`, { token: a.token, headers: { range: "bytes=1000-1999" } });
assert.equal(part.status, 206);
const bytes = new Uint8Array(await part.arrayBuffer());
assert.equal(bytes.length, 1000);
assert.equal(bytes[0], 1000 % 251);

const library = await (await call(`/v1/squads/${squadId}/library`, { token: a.token })).json();
assert.equal(library.moments.length, 1);
assert.equal(library.moments[0].views.length, 2);
console.log("library:", library.moments[0].views.map((v) => `${v.name}${v.uploaded ? " (uploaded)" : ""}`).join(", "));

wa.close();
wb.close();
assert.equal((await call(`/v1/squads/${squadId}/members/me`, { method: "DELETE", token: b.token })).status, 200);
assert.equal((await call(`/v1/squads/${squadId}/members/me`, { method: "DELETE", token: a.token })).status, 200);
assert.equal((await call(`/v1/squads/${squadId}/squad`, { token: a.token })).status, 404);
console.log("end to end: all good");
