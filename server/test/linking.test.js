import assert from "node:assert/strict";
import { test } from "node:test";
import { groups, linkedMembers, playingTogether } from "../src/linking.js";

const t0 = 1_762_142_400_000;

test("squadmates in the same game are linked", () => {
  assert.ok(playingTogether({ member: "a", game: "Valorant", match: null }, { member: "b", game: "valorant", match: null }));
  assert.ok(!playingTogether({ member: "a", game: "Valorant" }, { member: "b", game: "Overwatch" }));
  assert.ok(!playingTogether({ member: "a", game: "" }, { member: "b", game: "" }));
});

test("match starts have to agree when both apps saw one", () => {
  const a = { member: "a", game: "The Finals", match: t0 };
  assert.ok(playingTogether(a, { member: "b", game: "The Finals", match: t0 + 30_000 }));
  assert.ok(!playingTogether(a, { member: "b", game: "The Finals", match: t0 + 120_000 }), "a different lobby");
  assert.ok(playingTogether(a, { member: "b", game: "The Finals", match: null }), "a game or app that didn't see the start");
});

test("unlinking someone keeps them apart either way round", () => {
  const a = { member: "a", game: "Apex Legends", match: null, unlinked: ["b"] };
  const b = { member: "b", game: "Apex Legends", match: null };
  assert.ok(!playingTogether(a, b));
  assert.ok(!playingTogether(b, a));
});

test("linked members and groups", () => {
  const presences = [
    { member: "a", game: "Valorant", match: t0 },
    { member: "b", game: "Valorant", match: t0 + 10_000 },
    { member: "b", game: "Valorant", match: t0 + 10_000 },  // the app open twice
    { member: "c", game: "Valorant", match: t0 + 600_000 },
    { member: "d", game: "Overwatch", match: null },
    { member: "e", game: "", match: null },
  ];
  assert.deepEqual(linkedMembers(presences[0], presences), ["b"]);
  assert.deepEqual(linkedMembers(presences[4], presences), []);
  assert.deepEqual(groups(presences), [{ game: "Valorant", members: ["a", "b"] }]);
});
