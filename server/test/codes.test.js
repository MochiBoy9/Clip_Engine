import assert from "node:assert/strict";
import { test } from "node:test";
import { formatInvite, isMomentId, isSquadId, newMomentId, normalizeInvite, randomCode, sameSecret } from "../src/codes.js";

test("invite codes survive how people type them", () => {
  const id = randomCode(8);
  const secret = randomCode(12);
  const code = formatInvite(id, secret);
  assert.match(code, /^[0-9A-Z]{4}(-[0-9A-Z]{4}){4}$/);
  assert.deepEqual(normalizeInvite(code), { squadId: id, secret });
  assert.deepEqual(normalizeInvite(` ${code.toLowerCase().replace(/-/g, " ")} `), { squadId: id, secret });
  assert.deepEqual(normalizeInvite(`clipengine://squad/join/${code}`), { squadId: id, secret });
  assert.deepEqual(normalizeInvite(`https://clipengine.app/squad#${code}`), { squadId: id, secret });
  // O for 0 and I or L for 1, as read off a screenshot.
  assert.deepEqual(normalizeInvite("OOOO-IIII-LLLL-0000-1111"), { squadId: "00001111", secret: "111100001111" });
  assert.equal(normalizeInvite("too-short"), null);
  assert.equal(normalizeInvite("UUUU-UUUU-UUUU-UUUU-UUUU"), null);  // U isn't in the alphabet
  assert.equal(normalizeInvite(42), null);
});

test("squad ids and moment ids", () => {
  assert.ok(isSquadId(randomCode(8)));
  assert.ok(!isSquadId("abc"));
  assert.ok(!isSquadId("UUUUUUUU"));
  const a = newMomentId(1_762_142_400_000);
  const b = newMomentId(1_762_142_400_001);
  assert.ok(isMomentId(a) && a.length <= 64);
  assert.ok(a.slice(0, 10) < b.slice(0, 10), "moment ids sort by time");
});

test("secrets compare whole", () => {
  assert.ok(sameSecret("ABC", "ABC"));
  assert.ok(!sameSecret("ABC", "ABD"));
  assert.ok(!sameSecret("ABC", "ABCD"));
  assert.ok(!sameSecret(undefined, "A"));
});
