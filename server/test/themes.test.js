import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { THEME_IDS, cleanGrant } from "../src/plans.js";

// The key maker can give any theme the app has, and Ultra's colour wheel and adaptive themes on their own.
test("special keys can give every colour theme, the colour wheel and adaptive themes", () => {
  const app = readFileSync(new URL("../../ui/ClipEngine.Desktop/Services/AccentThemes.cs", import.meta.url), "utf8");
  const ids = [...app.matchAll(/new\("([a-z]+)", "[^"]+", Color\.Parse/g)].map((m) => m[1]);
  assert.ok(ids.length >= 16);
  for (const id of ids) assert.ok(THEME_IDS.includes(id), `${id} is missing from THEME_IDS`);
  assert.ok(THEME_IDS.includes("wheel"));
  assert.ok(THEME_IDS.includes("adaptive"));
  assert.equal(new Set(THEME_IDS).size, THEME_IDS.length);
});

test("a special key's list of themes takes the new ones", () => {
  assert.deepEqual(cleanGrant({ themes: ["cobalt", "wheel", "adaptive"] }).grant.themes, ["cobalt", "wheel", "adaptive"]);
  assert.ok(cleanGrant({ themes: ["rainbow"] }).error);
});
