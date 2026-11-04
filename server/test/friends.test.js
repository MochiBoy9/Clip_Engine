import assert from "node:assert/strict";
import { test } from "node:test";
import worker from "../src/index.js";
import { normalizeFriendCode } from "../src/library.js";
import { fakeEnv, request } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);

async function account(env, name, handle) {
  const b = await (await call(env, "/v1/accounts", { method: "POST", body: { name, device: `device-${name}0000` } })).json();
  const me = { token: b.token, id: b.account.id, base: `/v1/accounts/${b.account.id}`, code: b.account.friendCode };
  if (handle) await call(env, `${me.base}/account`, { method: "PATCH", token: me.token, body: { handle } });
  return me;
}

const list = async (env, me) => (await call(env, `${me.base}/friends`, { token: me.token })).json();

test("friends by profile name or friend code, accepted both ways", async () => {
  const env = fakeEnv();
  const me = await account(env, "Enterro", "enterro");
  const kai = await account(env, "Kai", "kai");
  const mira = await account(env, "Mira");

  assert.match(me.code, /^[0-9A-Z]{4}-[0-9A-Z]{4}$/);
  assert.equal(normalizeFriendCode(me.code.toLowerCase().replace("-", " ")), me.id);

  // A request by @name shows as sent on one side and received on the other.
  const sent = await (await call(env, `${me.base}/friends`, { method: "POST", token: me.token, body: { handle: "@Kai" } })).json();
  assert.deepEqual(sent.sent.map((f) => f.name), ["Kai"]);
  const theirs = await list(env, kai);
  assert.deepEqual(theirs.received.map((f) => [f.name, f.handle]), [["Enterro", "enterro"]]);
  assert.equal(theirs.friends.length, 0);

  const accepted = await (await call(env, `${kai.base}/friends/${me.id}/accept`, { method: "POST", token: kai.token })).json();
  assert.deepEqual(accepted.friends.map((f) => f.name), ["Enterro"]);
  assert.deepEqual((await list(env, me)).friends.map((f) => [f.name, f.online]), [["Kai", false]]);

  // By friend code, and asking back counts as yes.
  await call(env, `${mira.base}/friends`, { method: "POST", token: mira.token, body: { code: me.code } });
  const back = await (await call(env, `${me.base}/friends`, { method: "POST", token: me.token, body: { code: mira.code } })).json();
  assert.deepEqual(back.friends.map((f) => f.name).sort(), ["Kai", "Mira"]);
  assert.deepEqual((await list(env, mira)).friends.map((f) => f.name), ["Enterro"]);

  // Mistakes.
  assert.equal((await call(env, `${me.base}/friends`, { method: "POST", token: me.token, body: { handle: "nobody_here" } })).status, 404);
  assert.equal((await call(env, `${me.base}/friends`, { method: "POST", token: me.token, body: { code: me.code } })).status, 400);
  assert.equal((await call(env, `${me.base}/friends`, { method: "POST", token: me.token, body: { code: "ZZZZ-ZZZZ" } })).status, 404);
  assert.equal((await call(env, `${me.base}/friends`, { token: kai.token })).status, 401);
  // Other accounts can't be told things straight from outside.
  assert.equal((await call(env, `${kai.base}/peer/removed`, { method: "POST", body: { account: me.id } })).status, 404);

  // Unfriending takes it off both lists.
  await call(env, `${kai.base}/friends/${me.id}`, { method: "DELETE", token: kai.token });
  assert.deepEqual((await list(env, me)).friends.map((f) => f.name), ["Mira"]);
  assert.equal((await list(env, kai)).friends.length, 0);
});

test("friends see who's online, what they're playing and their squad", async () => {
  const env = fakeEnv();
  const me = await account(env, "Enterro", "enterro");
  const kai = await account(env, "Kai", "kai");
  const stranger = await account(env, "Rook", "rook");
  await call(env, `${me.base}/friends`, { method: "POST", token: me.token, body: { handle: "kai" } });
  await call(env, `${kai.base}/friends/${me.id}/accept`, { method: "POST", token: kai.token });

  await call(env, `${kai.base}/presence`, { method: "POST", token: kai.token, body: { game: "The Finals", invite: "n1gh-t5hf-7q4k-2m9x-w3bd" } });
  await call(env, `${stranger.base}/presence`, { method: "POST", token: stranger.token, body: { game: "Valorant" } });
  const [friend] = (await list(env, me)).friends;
  assert.equal(friend.online, true);
  assert.equal(friend.game, "The Finals");
  assert.equal(friend.invite, "N1GHT5HF7Q4K2M9XW3BD");

  await call(env, `${kai.base}/presence`, { method: "DELETE", token: kai.token });
  assert.equal((await list(env, me)).friends[0].online, false);
});
