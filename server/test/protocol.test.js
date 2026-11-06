import assert from "node:assert/strict";
import { test } from "node:test";
import { parseClientMessage, RateLimit } from "../src/protocol.js";

const now = 1_762_142_400_000;
const parse = (m) => parseClientMessage(JSON.stringify(m), now);

test("moments are checked before the squad sees them", () => {
  assert.deepEqual(parse({ t: "moment", ref: "r1", type: "kill", at: now - 500, game: " Valorant ", text: "ENTERRO \u0007 X" }),
    { t: "moment", ref: "r1", type: "kill", at: now - 500, game: "Valorant", text: "ENTERRO   X" });
  assert.equal(parse({ t: "moment", type: "teabag", at: now, game: "Valorant" }), null);
  assert.equal(parse({ t: "moment", type: "kill", at: now - 10 * 60_000, game: "Valorant" }), null, "clock too far off");
  assert.equal(parse({ t: "moment", type: "kill", at: now, game: "" }), null);
  assert.equal(parse({ t: "moment", type: "kill", at: now, game: "x".repeat(200), text: "y".repeat(500) }).text.length, 200);
});

test("presence, views, wants and junk", () => {
  assert.deepEqual(parse({ t: "presence", game: "Apex Legends", match: now - 1000 }), { t: "presence", game: "Apex Legends", match: now - 1000 });
  assert.deepEqual(parse({ t: "presence", game: "", match: now }), { t: "presence", game: "", match: null });
  assert.deepEqual(parse({ t: "view", moment: "mABC", utc_start: now, seconds: 15.257 }), { t: "view", moment: "mABC", utc_start: now, seconds: 15.26 });
  assert.equal(parse({ t: "view", moment: "../x", utc_start: now, seconds: 15 }), null);
  assert.equal(parse({ t: "view", moment: "m1", utc_start: now, seconds: 0 }), null);
  assert.deepEqual(parse({ t: "want", moment: "m1", member: "pABCDEFGHJK" }), { t: "want", moment: "m1", member: "pABCDEFGHJK" });
  assert.equal(parse({ t: "want", moment: "m1", member: "someone" }), null);
  assert.equal(parseClientMessage("{nope", now), null);
  assert.equal(parseClientMessage("x".repeat(5000), now), null);
  assert.equal(parse({ t: "launch-missiles" }), null);
  assert.equal(parse(null), null);
});

test("rate limit", () => {
  const limit = new RateLimit(2);
  assert.ok(limit.allow("a", 0));
  assert.ok(limit.allow("a", 1));
  assert.ok(!limit.allow("a", 2));
  assert.ok(limit.allow("b", 2));
  assert.ok(limit.allow("a", 60_001));
});
