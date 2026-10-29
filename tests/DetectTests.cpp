// Tests for the parts of event detection that don't need Windows: text matching, rule
// parsing and turning successive screen reads into events.

#include "app/AutoClipPlanner.h"
#include "detect/DetectionRules.h"
#include "detect/ImageMatch.h"
#include "detect/TextMatcher.h"
#include "media/ClipMetadata.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>

namespace {

int g_failures = 0;

void Check(bool ok, const char* expression, int line) {
  if (ok) return;
  ++g_failures;
  std::printf("FAILED line %d: %s\n", line, expression);
}

#define CHECK(x) Check((x), #x, __LINE__)

using namespace clip;

std::vector<std::vector<std::string>> Rows(std::initializer_list<const char*> lines);

void TestTokenize() {
  const auto tokens = Tokenize("  Eliminated: xX_Sn1per_Xx!! ");
  CHECK(tokens.size() == 4);
  CHECK(tokens[0] == "ELIMINATED");
  CHECK(tokens[1] == "XX");
  CHECK(tokens[2] == "SN1PER");
}

void TestWordsMatch() {
  CHECK(WordsMatch("ELIMINATED", "ELIMINATED"));
  CHECK(WordsMatch("EL1MINATED", "ELIMINATED"));   // 1 read in place of I
  CHECK(WordsMatch("ELIMINATD", "ELIMINATED"));    // dropped letter
  CHECK(WordsMatch("ELIMINATEDXXSNIPER", "ELIMINATED"));  // ran into the next word
  CHECK(WordsMatch("ASS1ST", "ASSIST"));
  CHECK(WordsMatch("K1LL", "KILL"));
  CHECK(!WordsMatch("KILLED", "KILL"));  // short words must match exactly
  CHECK(!WordsMatch("SKILL", "KILL"));
  CHECK(!WordsMatch("ELEVATED", "ELIMINATED"));
  CHECK(!WordsMatch("", "KILL"));
  // Plurals are scoreboard headings, not the moment itself.
  CHECK(!WordsMatch("REVIVES", "REVIVE"));
  CHECK(!WordsMatch("ELIMINATIONS", "ELIMINATION"));
  CHECK(!WordsMatch("EUMINATION5", "ELIMINATION"));  // misread, still plural
  CHECK(!WordsMatch("ELIMINATIONS", "ELIMINATED"));
  CHECK(!WordsMatch("ASSISTS", "ASSIST"));
  CHECK(WordsMatch("ASSISS", "ASSIST"));  // a misread last letter isn't a plural
  CHECK(WordsMatch("KILLS", "KILLS"));
  // {number}
  CHECK(WordsMatch("250", kNumberWord));
  CHECK(WordsMatch("25O", kNumberWord));
  CHECK(!WordsMatch("15M", kNumberWord));  // a distance
  CHECK(!WordsMatch("FANS", kNumberWord));
  CHECK(!WordsMatch("O", kNumberWord));
}

void TestCountPhrase() {
  const auto tokens = Tokenize("ELIMINATED Bob  ELIMINATED Ann  ELIMINATED BY Zed");
  CHECK(CountPhrase(tokens, MakePhrase("eliminated")) == 3);
  CHECK(CountPhrase(tokens, MakePhrase("Eliminated by")) == 1);
  CHECK(CountPhrase(tokens, MakePhrase("victory")) == 0);
  CHECK(CountPhrase(Tokenize("PLAY OF THE GAME"), MakePhrase("play of the game")) == 1);
}

DetectionRules Parse(const std::string& text) {
  std::istringstream in(text);
  return ParseDetectionRules(in);
}

void TestParse() {
  const auto rules = Parse(R"(
[detection]
sample_ms = 300

[overwatch.kill]
region = 0.3, 0.55, 0.4, 0.2   # center
words = ELIMINATED | final blow
exclude = BY
cooldown_ms = 500
threshold = 180

[overwatch.death.killcam]
region = 0, 0, 1, 1
words = KILLCAM

[overwatch.nonsense]
words = X

[overwatch.assist]
region = 0.9, 0.9, 0.5, 0.5
)");
  CHECK(rules.sampleMs == 300);
  CHECK(rules.rules.size() == 2);
  const auto& kill = rules.rules[0];
  CHECK(kill.game == "overwatch");
  CHECK(kill.event == EventType::Kill);
  CHECK(kill.region == (ScreenRegion{0.3, 0.55, 0.4, 0.2}));
  CHECK(kill.words.size() == 2);
  CHECK(kill.words[1].words.size() == 2);
  CHECK(kill.exclude.size() == 1);
  CHECK(kill.cooldownMs == 500);
  CHECK(kill.threshold == 180);
  CHECK(rules.rules[1].event == EventType::Death);
  CHECK(rules.rules[1].name == "overwatch.death.killcam");
  // The unknown event, the region off the screen and the rule without words are all reported.
  CHECK(rules.warnings.size() == 3);
  CHECK(rules.ForGame("overwatch").size() == 2);
  CHECK(rules.ForGame("the_finals").empty());
}

void TestNumberPhrase() {
  const auto phrase = MakePhrase("Elimination {NUMBER}");
  CHECK(phrase.words.size() == 2 && phrase.words[0] == "ELIMINATION" && phrase.words[1] == kNumberWord);
  CHECK(HasNumber(phrase));
  CHECK(!HasNumber(MakePhrase("ELIMINATION")));

  DetectionRule rule;
  rule.words = {phrase};
  CHECK(CountMatches(rule, Rows({"ELIMINATION 250"})) == 1);
  CHECK(CountMatches(rule, Rows({"ELIMINATION | + 250"})) == 1);  // the bar read as a letter
  CHECK(CountMatches(rule, Rows({"ELIMINATION I + 250"})) == 1);
  CHECK(CountMatches(rule, Rows({"ELIMINATION", "250"})) == 0);  // the number belongs to another row
  CHECK(CountMatches(rule, Rows({"ELIMINATION FANS 250"})) == 0);
  CHECK(CountMatches(rule, Rows({"ELIMINATION 250", "ELIMINATION 250"})) == 2);

  rule.position = LinePosition::Start;
  CHECK(CountMatches(rule, Rows({"ELIMINATION 250"})) == 1);
  CHECK(CountMatches(rule, Rows({"ELIMINATED BOB"})) == 0);
}

void TestCustomGames() {
  const auto rules = Parse(R"(
[game.valorant]
name = VALORANT
exe = VALORANT-Win64-Shipping.exe | VALORANT.exe

[valorant.kill]
region = 0.7, 0.05, 0.3, 0.2
words = ELIMINATED
exclude = BY

[game.half]
name = Missing exe

[game.bad id!]
name = X
exe = x.exe
)");
  CHECK(rules.games.size() == 1);
  if (!rules.games.empty()) {
    CHECK(rules.games[0].id == "valorant");
    CHECK(rules.games[0].name == "VALORANT");
    CHECK(rules.games[0].executables.size() == 2);
    CHECK(rules.games[0].executables.size() == 2 && rules.games[0].executables[0] == "VALORANT-Win64-Shipping.exe");
  }
  CHECK(rules.ForGame("valorant").size() == 1);
  CHECK(rules.warnings.size() == 2);  // the game without an exe, and the bad id

  const auto& kill = rules.rules[0];
  CHECK(CountMatches(kill, Tokenize("ELIMINATED Jett  ELIMINATED Sage")) == 2);
  CHECK(CountMatches(kill, Tokenize("ELIMINATED BY Reyna")) == 0);
}

void TestShippedRulesParse(const char* path) {
  std::ifstream in(path);
  CHECK(in.good());
  const auto rules = ParseDetectionRules(in);
  for (const auto& w : rules.warnings) std::printf("detection.ini %s\n", w.c_str());
  CHECK(rules.warnings.empty());
  for (const char* game : {"the_finals", "overwatch", "battlefield_6"}) {
    const auto forGame = rules.ForGame(game);
    bool kill = false, death = false;
    for (const auto& r : forGame) {
      kill |= r.event == EventType::Kill;
      death |= r.event == EventType::Death;
    }
    CHECK(kill && death);
  }
}

// What The Finals really puts on screen, as text recognition read it in recorded matches.
void TestFinalsRules(const char* path) {
  std::ifstream in(path);
  const auto rules = ParseDetectionRules(in).ForGame("the_finals");
  auto rule = [&](EventType event) -> const DetectionRule& {
    return *std::ranges::find_if(rules, [&](const DetectionRule& r) { return r.event == event; });
  };
  const DetectionRule& kill = rule(EventType::Kill);
  const DetectionRule& assist = rule(EventType::Assist);
  const DetectionRule& revive = rule(EventType::Revive);
  auto count = [](const DetectionRule& r, std::initializer_list<const char*> lines) { return CountMatches(r, Rows(lines)); };

  // A kill: the headline and its score line are one kill, not two.
  CHECK(count(kill, {"ELIMINATED TTPS SUSHICUBBY", "ELIMINATION 250 | + 202"}) == 1);
  CHECK(count(kill, {"fliMINATfD", "ELIMINATION + 250"}) == 1);
  CHECK(count(kill, {"ELIMINATED DOVAHKIIN2299", "SPECIAL DELIVERY + 10 + 20 FANS"}) == 0);  // the score line isn't up yet
  CHECK(count(kill, {"fliMINATfD SA5SVRfWAR", "ELIMINATION 250 + 131 + 40 FANS", "FLOW STATE"}) == 1);
  CHECK(count(revive, {"ELIMINATED TTPS SUSHICUBBY", "ELIMINATION 250 | + 202"}) == 0);

  // A downed teammate's marker and the prompt to revive them aren't revives; the score line is.
  CHECK(count(revive, {"REVIVE"}) == 0);
  CHECK(count(revive, {"15m REVIVE"}) == 0);
  CHECK(count(revive, {"REVIVE 14m"}) == 0);
  CHECK(count(revive, {"37m", "REVIVE", "ForemostFace"}) == 0);
  CHECK(count(revive, {"E REVIVE"}) == 0);
  CHECK(count(revive, {"REVIVE PVP THCORPSE", "REVIVE + 200"}) == 1);

  CHECK(count(assist, {"ASSIST BINIO", "ASSIST + 100"}) == 1);
  CHECK(count(assist, {"PICK UP 19m", "ASSIST"}) == 0);

  // The end-of-match summary lists every stat by name.
  for (const auto* r : {&kill, &assist, &revive}) {
    CHECK(count(*r, {"WINS 217", "ELIMINATIONS 3,237", "REVIVES 712", "Eliminations 12", "Assists 8", "Revives 1"}) == 0);
  }

  // One score line up for three seconds, read with what's around it changing, is one kill.
  RuleTracker kills(kill);
  CHECK(kills.Update(Rows({"ELIMINATED DOVAHKllN2299", "ELIMINATION + 250 + 20 FANS"}), 0) == 1);
  CHECK(kills.Update(Rows({"IMIN EL IMIN KIN,'299", "ELIMINATION 250 + 20 FANS"}), 250) == 0);
  CHECK(kills.Update(Rows({"fillWINATfD DOVAHKllN229", "ELIMINATIO + 250"}), 500) == 0);
  CHECK(kills.Update(Rows({"ELIMINATED", "ELIMINATION 250"}), 1500) == 0);
  CHECK(kills.Update(Rows({"ELIMINATED"}), 2500) == 0);
  CHECK(kills.Update(Rows({}), 3000) == 0);
  CHECK(kills.Update(Rows({}), 4500) == 0);
  // The next kill.
  CHECK(kills.Update(Rows({"fliMINATED nssmnvnn", "FLOW STATE 40 + 40 FANS"}), 5000) == 0);
  CHECK(kills.Update(Rows({"fillWINATfD SA5SVRfWAR", "ELIMINATION + 250", "FLOW STATE"}), 5250) == 1);
  CHECK(kills.Update(Rows({"fillWINATfD SA5SVRfWAR", "ELIMINATION + 250"}), 5500) == 0);
}

void TestTracker() {
  DetectionRule rule;
  rule.words = {MakePhrase("ELIMINATED")};
  rule.exclude = {MakePhrase("BY")};
  rule.cooldownMs = 0;
  RuleTracker tracker(rule);

  CHECK(tracker.Update(Tokenize("nothing here"), 0) == 0);
  CHECK(tracker.Update(Tokenize("ELIMINATED Bob"), 250) == 1);
  CHECK(tracker.Update(Tokenize("ELIMINATED Bob"), 500) == 0);   // still the same message
  CHECK(tracker.Update(Tokenize("ELIMINATED Bob ELIMINATED Ann"), 750) == 1);  // a second kill
  CHECK(tracker.Update(Tokenize("ELIMINATED Ann"), 1000) == 0);  // one read missing a line
  CHECK(tracker.Update(Tokenize("ELIMINATED Bob ELIMINATED Ann"), 1250) == 0);  // flicker isn't a kill
  CHECK(tracker.Update(Tokenize(""), 1500) == 0);
  CHECK(tracker.Update(Tokenize(""), 1750) == 0);                // now it's really gone
  CHECK(tracker.Update(Tokenize("ELIMINATED Cat"), 2000) == 1);
  CHECK(tracker.Update(Tokenize("ELIMINATED BY Zed"), 2250) == 0);  // excluded: that's a death

  DetectionRule slow = rule;
  slow.cooldownMs = 5000;
  RuleTracker cooled(slow);
  CHECK(cooled.Update(Tokenize("ELIMINATED A"), 0) == 1);
  CHECK(cooled.Update(Tokenize(""), 100) == 0);
  CHECK(cooled.Update(Tokenize(""), 200) == 0);
  CHECK(cooled.Update(Tokenize("ELIMINATED A"), 300) == 0);  // inside the cooldown
  CHECK(cooled.Update(Tokenize(""), 400) == 0);
  CHECK(cooled.Update(Tokenize(""), 500) == 0);
  CHECK(cooled.Update(Tokenize("ELIMINATED A"), 6000) == 1);
}

// One kill stays in the killfeed for seconds while the screen is read several times a second.
// It must count once, however the reads come back.
void TestTrackerCountsEachKillOnce() {
  DetectionRule rule;
  rule.words = {MakePhrase("ELIMINATED")};
  rule.position = LinePosition::Start;
  rule.cooldownMs = 0;
  RuleTracker tracker(rule);
  auto rows = [](std::initializer_list<const char*> lines) {
    std::vector<std::vector<std::string>> result;
    for (const char* line : lines) result.push_back(Tokenize(line));
    return result;
  };

  CHECK(tracker.Update(rows({"ELIMINATED VEX"}), 0) == 1);
  // OCR loses the line for several reads, then finds it again: still the same kill.
  CHECK(tracker.Update(rows({}), 250) == 0);
  CHECK(tracker.Update(rows({}), 500) == 0);
  CHECK(tracker.Update(rows({}), 750) == 0);
  CHECK(tracker.Update(rows({"ELIMINATED SHADOW"}), 1000) == 1);
  CHECK(tracker.Update(rows({"ELIMINATED SHAD0W", "ELIMINATED VEX"}), 1250) == 0);  // misread name
  CHECK(tracker.Update(rows({"ELIMINATED SHADDW"}), 1400) == 0);
  // The feed scrolls: the old line leaves as a new one arrives in the same read.
  CHECK(tracker.Update(rows({"ELIMINATED MIRA"}), 1500) == 1);
  CHECK(tracker.Update(rows({"ELIMINATED MIRA", "ELIMINATED VEX"}), 1750) == 0);  // VEX flickers back
  // The same player killed again while the first line still shows: two lines, two kills.
  CHECK(tracker.Update(rows({"ELIMINATED MIRA", "ELIMINATED MIRA"}), 2000) == 1);
  CHECK(tracker.Update(rows({"ELIMINATED MIRA"}), 2250) == 0);
  // Long after the lines are gone, killing that player again counts.
  CHECK(tracker.Update(rows({}), 3000) == 0);
  CHECK(tracker.Update(rows({"ELIMINATED VEX"}), 9000) == 1);

  // Text read as one run: the names after each keyword tell the kills apart.
  DetectionRule run;
  run.words = {MakePhrase("ELIMINATED")};
  run.cooldownMs = 0;
  RuleTracker anywhere(run);
  CHECK(anywhere.Update(Tokenize("ENTERRO ELIMINATED VEX"), 0) == 1);
  CHECK(anywhere.Update(Tokenize("ENTERRO ELIMINATED MIRA ENTERRO ELIMINATED VEX"), 250) == 1);
  CHECK(anywhere.Update(Tokenize("ENTERRO ELIMINATED MIRA"), 500) == 0);
  CHECK(anywhere.Update(Tokenize("ENTERRO ELIMINATED VEX ENTERRO ELIMINATED MIRA"), 750) == 0);
}

std::vector<std::vector<std::string>> Rows(std::initializer_list<const char*> lines) {
  std::vector<std::vector<std::string>> result;
  for (const char* line : lines) result.push_back(Tokenize(line));
  return result;
}

// Exclude cancels the match it belongs to, not every match in the region.
void TestExcludeOnlyNearby() {
  DetectionRule rule;
  rule.words = {MakePhrase("ELIMINATED")};
  rule.exclude = {MakePhrase("BY"), MakePhrase("YOU WERE")};
  CHECK(CountMatches(rule, Rows({"ELIMINATED BOB", "DEFUSED BY ANN"})) == 1);  // another popup's "BY"
  CHECK(CountMatches(rule, Rows({"YOU WERE ELIMINATED"})) == 0);
  CHECK(CountMatches(rule, Rows({"ELIMINATED", "BY ZED"})) == 0);  // the death banner, wrapped
  CHECK(CountMatches(rule, Rows({"ZED", "ELIMINATED BY"})) == 0);
  CHECK(CountMatches(rule, Rows({"ELIMINATED BOB", "YOU WERE ELIMINATED"})) == 1);

  // Multi-kill medals hold the kill word too; excluding them leaves the kill popup alone.
  DetectionRule kill;
  kill.words = {MakePhrase("KILL")};
  kill.exclude = {MakePhrase("DOUBLE KILL")};
  CHECK(CountMatches(kill, Rows({"KILL 100", "DOUBLE KILL 50"})) == 1);
}

// Numbers change while a message stays up, and other rows aren't part of it.
void TestMessageNames() {
  DetectionRule respawn;
  respawn.event = EventType::Death;
  respawn.words = {MakePhrase("RESPAWN IN")};
  respawn.cooldownMs = 0;
  RuleTracker death(respawn);
  CHECK(death.Update(Rows({"RESPAWN IN 10"}), 0) == 1);
  CHECK(death.Update(Rows({"RESPAWN IN 9"}), 250) == 0);
  CHECK(death.Update(Rows({"RESPAWN IN 8"}), 500) == 0);

  DetectionRule rule;
  rule.words = {MakePhrase("ELIMINATED")};
  rule.cooldownMs = 0;
  RuleTracker kills(rule);
  CHECK(kills.Update(Rows({"VEX ELIMINATED", "ROUND TIMER"}), 0) == 1);
  CHECK(kills.Update(Rows({"VEX ELIMINATED", "SPIKE PLANTED"}), 250) == 0);  // the next row changed, not the kill
  CHECK(kills.Update(Rows({"VEX ELIMINATED"}), 500) == 0);
  CHECK(kills.Update(Rows({"ELIMINATED +100"}), 5000) == 1);
  CHECK(kills.Update(Rows({"ELIMINATED +125"}), 5250) == 0);  // the score ticked up, same popup

  // Popups without names count again once they've been gone a moment.
  CHECK(kills.Update(Rows({}), 5500) == 0);
  CHECK(kills.Update(Rows({}), 6000) == 0);
  CHECK(kills.Update(Rows({}), 6500) == 0);
  CHECK(kills.Update(Rows({"ELIMINATED +100"}), 6750) == 1);
}

// A quick double kill inside a rule's cooldown is still two kills; a death screen read twice is one death.
void TestCooldownKeepsKills() {
  DetectionRule rule;
  rule.event = EventType::Kill;
  rule.words = {MakePhrase("ELIMINATED")};
  rule.cooldownMs = 1000;
  RuleTracker kills(rule);
  CHECK(kills.Update(Rows({"ELIMINATED ANN"}), 0) == 1);
  CHECK(kills.Update(Rows({"ELIMINATED ANN", "ELIMINATED BOB"}), 250) == 0);  // held for now
  CHECK(kills.Update(Rows({"ELIMINATED ANN", "ELIMINATED BOB"}), 500) == 0);
  CHECK(kills.Update(Rows({"ELIMINATED BOB"}), 1000) == 1);  // counted once the cooldown is over
  CHECK(kills.Update(Rows({"ELIMINATED BOB"}), 2000) == 0);

  DetectionRule screen = rule;
  screen.event = EventType::Death;
  screen.words = {MakePhrase("ELIMINATED BY")};
  screen.cooldownMs = 8000;
  RuleTracker deaths(screen);
  CHECK(deaths.Update(Rows({"ELIMINATED BY ZED"}), 0) == 1);
  CHECK(deaths.Update(Rows({"ELIMINATED BY ZOE"}), 250) == 0);  // misread as another name
  CHECK(deaths.Update(Rows({}), 9000) == 0);
}

void TestImageRulesParse() {
  const auto rules = Parse(R"(
[detection]
sample_ms = 200

[valorant.kill.banner]
region = 0.40, 0.70, 0.20, 0.20
image = icons/valorant_kill.png
image_match = 0.85
image_screen = 1440

[valorant.match_end]
words = VICTORY

[valorant.death]
words = KILLED BY
sample_ms = 300

[valorant.highlight]
image_match = 2

[valorant.assist]
words = ASSIST
image = assist.png
)");
  CHECK(rules.rules.size() == 4);
  CHECK(rules.warnings.size() == 3);  // the bad image_match, then nothing to look for, then both
  if (rules.rules.size() == 4) {
    CHECK(rules.rules[3].LooksForImage() && !rules.rules[3].ReadsText());  // one or the other, so a kill counts once
    CHECK(rules.rules[0].LooksForImage() && !rules.rules[0].ReadsText());
    CHECK(rules.rules[0].image == "icons/valorant_kill.png");
    CHECK(rules.rules[0].imageMatch == 0.85);
    CHECK(rules.rules[0].imageScreen == 1440);
    CHECK(rules.rules[0].sampleMs == 200);
    CHECK(rules.rules[1].sampleMs == 1000);  // banners are checked less often
    CHECK(rules.rules[2].sampleMs == 300);
  }
}

// Draws a ring with a dark middle, like a kill icon, into a BGRA buffer.
void DrawRing(std::vector<uint8_t>& bgra, int width, int cx, int cy, double radius) {
  const int height = static_cast<int>(bgra.size() / 4 / width);
  for (int y = 0; y < height; ++y) {
    for (int x = 0; x < width; ++x) {
      const double d = std::hypot(x - cx, y - cy);
      uint8_t* p = &bgra[(static_cast<size_t>(y) * width + x) * 4];
      if (d > radius * 0.6 && d < radius) p[0] = 40, p[1] = 40, p[2] = 230, p[3] = 255;  // red
      else if (d <= radius * 0.6) p[0] = p[1] = p[2] = 10, p[3] = 255;
    }
  }
}

std::vector<uint8_t> Noise(int width, int height, unsigned seed) {
  std::vector<uint8_t> bgra(static_cast<size_t>(width) * height * 4);
  for (size_t i = 0; i < bgra.size(); i += 4) {
    seed = seed * 1103515245 + 12345;
    const uint8_t v = static_cast<uint8_t>(60 + (seed >> 16) % 80);
    bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
    bgra[i + 3] = 255;
  }
  return bgra;
}

void TestImageMatch() {
  // The icon as cut from a 1080p screenshot: a ring on a transparent background.
  std::vector<uint8_t> icon(40 * 40 * 4, 0);
  DrawRing(icon, 40, 20, 20, 18);
  const GrayImage picture = ToGray(icon.data(), 40, 40, 40 * 4, true);
  CHECK(!picture.mask.empty());

  const ImageMatcher same(picture, 1080, 1080);
  CHECK(same.Valid());
  auto screen = Noise(300, 160, 1);
  CHECK(same.BestMatch(ToGray(screen.data(), 300, 160, 300 * 4, false)) < 0.5);
  DrawRing(screen, 300, 211, 77, 18);
  const double found = same.BestMatch(ToGray(screen.data(), 300, 160, 300 * 4, false));
  CHECK(found > 0.85);

  // The same icon on a 1440p screen shows a third bigger.
  const ImageMatcher bigger(picture, 1080, 1440);
  auto large = Noise(400, 210, 2);
  DrawRing(large, 400, 150, 120, 24);
  CHECK(bigger.BestMatch(ToGray(large.data(), 400, 210, 400 * 4, false)) > 0.8);
  CHECK(same.BestMatch(ToGray(large.data(), 400, 210, 400 * 4, false)) < bigger.BestMatch(ToGray(large.data(), 400, 210, 400 * 4, false)));

  // A flat picture would match any flat patch, so it's refused.
  std::vector<uint8_t> flat(20 * 20 * 4, 200);
  CHECK(!ImageMatcher(ToGray(flat.data(), 20, 20, 20 * 4, false), 0, 1080).Valid());
}

void TestPlanner() {
  AutoClipPlanner planner(10, 5, 40);
  planner.Add(100);  // wants 90..105
  planner.Add(103);  // overlaps: 90..108
  planner.Add(150);  // separate: 140..155
  CHECK(planner.TakeReady(107).empty());
  auto ready = planner.TakeReady(110);
  CHECK(ready.size() == 1 && ready[0].start == 90 && ready[0].end == 108);
  planner.Add(160);  // 140..165
  planner.Add(172);  // 140..177, 37 long: still one clip
  planner.Add(180);  // would make 140..185, 45 long: continues in a new clip from 177
  const auto all = planner.TakeAll();
  CHECK(all.size() == 2);
  CHECK(all[0].start == 140 && all[0].end == 177);
  CHECK(all[1].start == 177 && all[1].end == 185);
  CHECK(planner.Empty());
}

void TestMetadata() {
  ClipMetadata m;
  m.game = "The \"Finals\"";
  m.trigger = "auto";
  m.seconds = 15.5;
  m.markers = {{EventType::Kill, 10.0, "ELIMINATED A"}, {EventType::Kill, 11.25, "ELIMINATED B"},
               {EventType::Revive, 12.0, "REVIVED"}};
  CHECK(Summarize(m.markers) == "2 kills, 1 revive");
  const auto json = ToJson(m);
  CHECK(json.find(R"("game": "The \"Finals\"")") != std::string::npos);
  CHECK(json.find(R"({ "type": "kill", "time": 11.25, "text": "ELIMINATED B" })") != std::string::npos);
  CHECK(json.find(R"("title": "2 kills, 1 revive")") != std::string::npos);
  CHECK(ToJson(ClipMetadata{}).find(R"("events": [])") != std::string::npos);
}

void TestMetadataRoundTrip() {
  ClipMetadata m;
  m.game = "Battlefield 6";
  m.trigger = "hotkey";
  m.seconds = 30;
  m.session = "2026-10-27T20:41:05";
  m.sessionSeconds = 812.5;
  m.match = 3;
  m.markers = {{EventType::Kill, 4.5, "KILLED \"X\"\n"}, {EventType::MatchEnd, 29, "VICTORY"}};
  const auto json = ToJson(m);
  CHECK(json.find(R"("match": 3)") != std::string::npos);

  const auto back = ParseMetadataJson(json);
  CHECK(back.has_value());
  if (back) {
    CHECK(back->game == "Battlefield 6");
    CHECK(back->trigger == "hotkey");
    CHECK(back->session == m.session);
    CHECK(back->sessionSeconds == 812.5);
    CHECK(back->match == 3);
    CHECK(back->markers.size() == 2);
    CHECK(back->markers.size() == 2 && back->markers[0].text == "KILLED \"X\"\n");
    CHECK(back->markers.size() == 2 && back->markers[1].type == EventType::MatchEnd);
  }

  // What the desktop app writes: compact, with fields this side doesn't know.
  const auto app = ParseMetadataJson(
      R"({"version":1,"game":"Overwatch","trigger":"auto","seconds":15.2,"title":"1 kill","favorite":true,)"
      R"("tags":["a",{"b":null}],"events":[{"type":"kill","time":3.25,"text":"\u00c9LIMIN\u00c9"},{"type":"nope","time":1,"text":""}]})");
  CHECK(app.has_value());
  if (app) {
    CHECK(app->game == "Overwatch");
    CHECK(app->sessionSeconds < 0);
    CHECK(app->markers.size() == 2);
    CHECK(app->markers.size() == 2 && app->markers[0].text == "\xC3\x89LIMIN\xC3\x89");
    CHECK(app->markers.size() == 2 && app->markers[1].type == EventType::Highlight);
  }
  CHECK(!ParseMetadataJson("{\"game\": ").has_value());
  CHECK(!ParseMetadataJson("not json").has_value());
}

void TestTrimmed() {
  ClipMetadata m;
  m.seconds = 30;
  m.sessionSeconds = 100;
  m.markers = {{EventType::Kill, 4, "A"}, {EventType::Kill, 12, "B"}, {EventType::Death, 20, "C"}};
  const auto t = Trimmed(m, 10, 18);
  CHECK(t.seconds == 8);
  CHECK(t.sessionSeconds == 110);
  CHECK(t.markers.size() == 1);
  CHECK(t.markers.size() == 1 && t.markers[0].seconds == 2 && t.markers[0].text == "B");

  ClipMetadata old;  // a clip from before sessions were recorded keeps "unknown"
  CHECK(Trimmed(old, 5, 6).sessionSeconds < 0);
}

void TestMetadataBox(const char* scratch) {
  const std::string path = std::string(scratch) + "/metadata_test.mp4";
  {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    // A minimal 'ftyp' box and an empty 'mdat', like the start of a real file.
    const char ftyp[] = {0, 0, 0, 16, 'f', 't', 'y', 'p', 'i', 's', 'o', 'm', 0, 0, 2, 0};
    const char mdat[] = {0, 0, 0, 8, 'm', 'd', 'a', 't'};
    out.write(ftyp, sizeof(ftyp));
    out.write(mdat, sizeof(mdat));
  }
  CHECK(!ReadMetadataBox(path).has_value());
  CHECK(AppendMetadataBox(path, R"({"game":"Overwatch"})"));
  const auto json = ReadMetadataBox(path);
  CHECK(json.has_value() && *json == R"({"game":"Overwatch"})");
  std::remove(path.c_str());
}

}  // namespace

int main(int argc, char** argv) {
  TestTokenize();
  TestWordsMatch();
  TestCountPhrase();
  TestParse();
  TestCustomGames();
  TestTracker();
  TestTrackerCountsEachKillOnce();
  TestExcludeOnlyNearby();
  TestMessageNames();
  TestCooldownKeepsKills();
  TestImageRulesParse();
  TestImageMatch();
  TestPlanner();
  TestMetadata();
  TestMetadataRoundTrip();
  TestTrimmed();
  TestNumberPhrase();
  if (argc > 1) TestShippedRulesParse(argv[1]);
  if (argc > 1) TestFinalsRules(argv[1]);
  if (argc > 2) TestMetadataBox(argv[2]);
  if (g_failures == 0) std::printf("All detection tests passed\n");
  return g_failures == 0 ? 0 : 1;
}
