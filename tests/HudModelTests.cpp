// Tests for the universal HUD reader: the learned model of what HUD text means, the rules it
// reads the screen with, and teaching it from the player's marks.

#include "detect/DetectionRules.h"
#include "detect/EventFuser.h"
#include "detect/HudModel.h"
#include "detect/TextMatcher.h"
#include "detect/UniversalHud.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>

namespace {

using namespace clip;

int g_failures = 0;

void Check(bool ok, const char* expression, int line) {
  if (ok) return;
  ++g_failures;
  std::printf("FAILED HudModelTests.cpp line %d: %s\n", line, expression);
}

#define CHECK(x) Check((x), #x, __LINE__)

HudLabel Label(const HudModel& model, const char* zone, const char* text) {
  const HudGuess guess = model.Classify(Tokenize(text), zone);
  return guess.confidence >= HudModel::kMinConfidence ? guess.label : HudLabel::None;
}

std::vector<std::vector<std::string>> Rows(std::initializer_list<const char*> lines) {
  std::vector<std::vector<std::string>> rows;
  for (const char* line : lines) rows.push_back(Tokenize(line));
  return rows;
}

void TestLabels() {
  CHECK(HudLabelName(HudLabel::MatchStart) == "match_start");
  CHECK(ParseHudLabel("menu") == HudLabel::Menu);
  CHECK(!ParseHudLabel("round"));
  CHECK(HudLabelEvent(HudLabel::Kill) == EventType::Kill);
  CHECK(!HudLabelEvent(HudLabel::Menu));
  CHECK(!HudLabelEvent(HudLabel::None));
  CHECK(HudLabelFor(EventType::Revive) == HudLabel::Revive);
}

// What the built-in examples alone make of lines from games they were never told about.
void TestSeedModel() {
  const HudModel model({}, "", {"Enterro"});
  using enum HudLabel;
  // Killfeeds: the player first is a kill, last a death, and other players' lines nothing.
  CHECK(Label(model, "feed_right", "ENTERRO SniperBob") == Kill);
  CHECK(Label(model, "feed_right", "SniperBob ENTERRO") == Death);
  CHECK(Label(model, "feed_right", "SniperBob xXKillerXx") == None);
  CHECK(Label(model, "feed_right", "[SMEDC] ENTERRO [KEAYS] Bob") == Kill);
  CHECK(Label(model, "feed_right", "Bob KILLED Alice") == None);
  CHECK(Label(model, "feed_right", "ENTERRO KILLED Alice") == Kill);
  CHECK(Label(model, "feed_left", "ENTERR0 Bob") == Kill);  // OCR's 0 for O in the name
  // Popups and score lines.
  CHECK(Label(model, "center", "ELIMINATED Bob") == Kill);
  CHECK(Label(model, "center", "ELIMINATlON +250") == Kill);  // misread l for I
  CHECK(Label(model, "center", "+100 KILL") == Kill);
  CHECK(Label(model, "center", "ENEMY DOWNED") == Kill);
  CHECK(Label(model, "bottom", "YOU KILLED Bob WITH AKM") == Kill);
  CHECK(Label(model, "center", "ASSIST +100") == Assist);
  CHECK(Label(model, "center", "REVIVE +200") == Revive);
  CHECK(Label(model, "center", "HOLD E TO REVIVE") == None);  // a prompt, not a revive
  CHECK(Label(model, "center", "DOUBLE KILL") == Highlight);
  // Death screens and banners.
  CHECK(Label(model, "banner", "YOU DIED") == Death);
  CHECK(Label(model, "banner", "ELIMINATED BY Bob") == Death);
  CHECK(Label(model, "center", "KILLED BY Bob") == Death);
  CHECK(Label(model, "banner", "RESPAWN IN 5") == Death);
  CHECK(Label(model, "banner", "VICTORY") == MatchEnd);
  CHECK(Label(model, "banner", "ROUND 1") == MatchStart);
  CHECK(Label(model, "top", "ROUND 5") == None);  // only the first round starts a match
  CHECK(Label(model, "banner", "PLAY") == Menu);
  // Names and numbers alone, and the player's own nameplate, mean nothing.
  CHECK(Label(model, "center", "30 120") == None);
  CHECK(Label(model, "center", "Bob") == None);
  CHECK(Label(model, "bottom", "ENTERRO 45") == None);
  CHECK(Label(model, "feed_left", "ENTERRO") == None);
  CHECK(Label(model, "center", "") == None);
}

void TestNormalize() {
  const HudModel model({}, "", {"Enterro#1234", "Big Bob"});
  const auto n = model.Normalize(Tokenize("ENTERRO ELIMINATFD Some Guy 250 x"));
  // The tag's number isn't needed, a misread word is the word, a name in two words is one name,
  // and a stray letter is left out.
  CHECK(n.size() == 4);
  if (n.size() == 4) {
    CHECK(n[0] == "{P}");
    CHECK(n[1] == "ELIMINATED");
    CHECK(n[2] == "{N}");
    CHECK(n[3] == "#");
  }
  const auto two = model.Normalize(Tokenize("BIG BOB KILLED you"));
  CHECK(!two.empty() && two[0] == "{P}");
  // Names that only look like a word aren't taken for it.
  const auto alice = model.Normalize(Tokenize("ALICE"));
  CHECK(alice.size() == 1 && alice[0] == "{N}");
}

void TestLearning() {
  using enum HudLabel;
  // A game that shows a kill as "VAPORIZED <name>": no built-in example says so.
  const HudModel before({}, "neon", {"Enterro"});
  CHECK(Label(before, "center", "VAPORIZED Bob") == None);

  std::vector<HudExample> learned = {
      {HudLabel::Kill, "center", "VAPORIZED Alice", "neon"},
      {HudLabel::Kill, "center", "VAPORIZED Mallory", "neon"},
      {HudLabel::None, "center", "VAPOR TRAIL READY", "neon"},
  };
  const HudModel after(learned, "neon", {"Enterro"});
  CHECK(Label(after, "center", "VAPORIZED Bob") == Kill);
  CHECK(Label(after, "center", "VAPOR1ZED Carol") == Kill);  // and misread
  // One player's name in one example isn't a word to learn.
  const auto n = after.Normalize(Tokenize("Alice"));
  CHECK(n.size() == 1 && n[0] == "{N}");
  CHECK(after.ExampleCount() == before.ExampleCount() + 3);

  // A correction from the game being played outweighs the built-in examples.
  std::vector<HudExample> quirk;
  for (int i = 0; i < 4; ++i) quirk.push_back({HudLabel::None, "center", "ENEMY DOWNED", "pacifist"});
  const HudModel inGame(quirk, "pacifist", {});
  const HudModel elsewhere(quirk, "other", {});
  CHECK(Label(inGame, "center", "ENEMY DOWNED") == None);
  CHECK(Label(HudModel({}, "", {}), "center", "ENEMY DOWNED") == Kill);
  (void)elsewhere;
}

void TestExampleFile(const std::filesystem::path& scratch) {
  std::istringstream in(
      "# comment\n"
      "neon\tkill\tcenter\tVAPORIZED {P}\r\n"
      "*\tdeath\tbanner\tGAME OVER MAN\n"
      "bad line without tabs\n"
      "neon\tnot_a_label\tcenter\tX\n");
  const auto examples = ReadHudExamples(in);
  CHECK(examples.size() == 2);
  if (examples.size() == 2) {
    CHECK(examples[0].game == "neon" && examples[0].label == HudLabel::Kill && examples[0].zone == "center" &&
          examples[0].text == "VAPORIZED {P}");
    CHECK(examples[1].game.empty() && examples[1].label == HudLabel::Death);
  }
  std::ostringstream out;
  WriteHudExample(out, examples.at(1));
  CHECK(out.str() == "*\tdeath\tbanner\tGAME OVER MAN\n");

  const auto file = scratch / "hud_learned_test.txt";
  std::error_code ignored;
  std::filesystem::remove(file, ignored);
  CHECK(AppendHudExamples(file, examples) == 2);
  CHECK(AppendHudExamples(file, examples) == 0);  // already there
  {
    // Closed again before the file is removed, which Windows won't do to an open file.
    std::ifstream back(file);
    CHECK(ReadHudExamples(back).size() == 2);
  }
  // And the model loads it.
  const auto model = LoadHudModel(file, "neon", {});
  CHECK(model->ExampleCount() == HudModel().ExampleCount() + 2);
  std::filesystem::remove(file, ignored);
}

void TestMarks() {
  const auto marks = ParseHudMarks("kill@12.3; death@40.15;bogus@1;revive@x;assist@0");
  CHECK(marks.size() == 3);
  if (marks.size() == 3) {
    CHECK(marks[0].event == EventType::Kill && marks[0].ms == 12300);
    CHECK(marks[1].event == EventType::Death && marks[1].ms == 40150);
    CHECK(marks[2].event == EventType::Assist && marks[2].ms == 0);
  }
}

void TestUniversalRules() {
  auto model = std::make_shared<const HudModel>(std::vector<HudExample>{}, "neon", std::vector<std::string>{"Enterro"});
  const auto rules = UniversalRules("neon", model);
  size_t expected = 0;
  for (const auto& zone : UniversalZones()) expected += zone.events.size();
  CHECK(rules.size() == expected);
  for (const auto& rule : rules) {
    CHECK(rule.classify && rule.hudModel && rule.ReadsText());
    CHECK(rule.name.starts_with("neon.") && rule.name.find(".universal_") != std::string::npos);
    CHECK(rule.mergeMs > 0);
  }

  auto find = [&](const char* name) -> const DetectionRule& {
    for (const auto& rule : rules) {
      if (rule.name == name) return rule;
    }
    std::printf("no rule %s\n", name);
    return rules.front();
  };
  const auto& feedKill = find("neon.kill.universal_feed_right");
  const auto& feedDeath = find("neon.death.universal_feed_right");
  CHECK(feedKill.priority == 2);
  const auto feed = Rows({"Bob Alice", "ENTERRO Mallory", "Carol ENTERRO"});
  CHECK(CountMatches(feedKill, feed) == 1);
  CHECK(CountMatches(feedDeath, feed) == 1);
  const auto keys = FindMatches(feedKill, feed);
  CHECK(keys.size() == 1 && keys[0].find("MALLORY") != std::string::npos);

  // A killfeed line counts once however long it stays up; a new one counts again.
  RuleTracker tracker(feedKill);
  CHECK(tracker.Update(feed, 0) == 1);
  CHECK(tracker.Update(feed, 500) == 0);
  CHECK(tracker.Update(Rows({"ENTERRO Mallory", "ENTERRO Trent"}), 1000) == 1);

  // exclude still cancels the rows it's on.
  DetectionRule excluded = feedKill;
  excluded.exclude = ParsePhrases("MALLORY");
  CHECK(CountMatches(excluded, feed) == 0);

  // While a menu shows, nothing in that part of the screen counts.
  const auto& bannerEnd = find("neon.match_end.universal_banner");
  CHECK(CountMatches(bannerEnd, Rows({"VICTORY"})) == 1);
  CHECK(CountMatches(bannerEnd, Rows({"VICTORY", "PLAY"})) == 0);

  // The model hasn't been given: nothing counts.
  DetectionRule bare = feedKill;
  bare.hudModel.reset();
  CHECK(CountMatches(bare, feed) == 0);
}

void TestUniversalFuses() {
  // One kill shown in the killfeed and as a popup counts once; two in the killfeed count two.
  auto model = std::make_shared<const HudModel>(std::vector<HudExample>{}, "neon", std::vector<std::string>{"Enterro"});
  const auto rules = UniversalRules("neon", model);
  const DetectionRule* feed = nullptr;
  const DetectionRule* center = nullptr;
  for (const auto& rule : rules) {
    if (rule.name == "neon.kill.universal_feed_right") feed = &rule;
    if (rule.name == "neon.kill.universal_center") center = &rule;
  }
  CHECK(feed && center);
  if (!feed || !center) return;
  std::vector<GameEvent> events;
  EventFuser fuser([&](const GameEvent& e) { events.push_back(e); });
  constexpr int64_t kMs = 10000;
  RuleTracker feedTracker(*feed), centerTracker(*center);
  fuser.Add(*feed, feedTracker.Update(Rows({"ENTERRO Bob"}), 0), 0, "ENTERRO Bob");
  fuser.Add(*center, centerTracker.Update(Rows({"ELIMINATED Bob"}), 300), 300 * kMs, "ELIMINATED Bob");
  CHECK(feedTracker.Update(Rows({"ENTERRO Bob"}), 2500) == 0);  // still up
  fuser.Flush(5000 * kMs);
  CHECK(events.size() == 1);
  CHECK(feedTracker.Update(Rows({"ENTERRO Bob"}), 5000) == 0);
  fuser.Add(*feed, feedTracker.Update(Rows({"ENTERRO Bob", "ENTERRO Carol"}), 6000), 6000 * kMs, "");
  fuser.FlushAll();
  CHECK(events.size() == 2);
}

void TestClassifyRulesParse() {
  std::istringstream in(
      "[neon.kill.popup]\n"
      "region = 0.40, 0.60, 0.20, 0.10\n"
      "classify = true\n"
      "[neon.death.feed]\n"
      "region = 0.70, 0.02, 0.28, 0.20\n"
      "classify = yes\n"
      "zone = FEED_RIGHT\n"
      "[neon.kill.bad]\n"
      "region = 0.4, 0.4, 0.1, 0.1\n"
      "classify = sometimes\n");
  auto rules = ParseDetectionRules(in);
  CHECK(rules.rules.size() == 2);  // the bad one has nothing to look for
  CHECK(!rules.warnings.empty());
  auto forGame = rules.ForGame("neon");
  auto model = std::make_shared<const HudModel>();
  AttachHudModel(forGame, model);
  CHECK(forGame.size() == 2);
  if (forGame.size() == 2) {
    CHECK(forGame[0].classify && forGame[0].hudModel == model && forGame[0].hudZone == "center");
    CHECK(forGame[1].hudZone == "feed_right");
  }
  CHECK(NearestZone({0.75, 0.05, 0.2, 0.1}) == "feed_right");
  CHECK(NearestZone({0.02, 0.6, 0.2, 0.1}) == "feed_bottom");
  CHECK(NearestZone({0.4, 0.02, 0.2, 0.06}) == "top");
}

void TestRulesRemoved() {
  std::istringstream shippedText("[neon.kill]\nregion = 0.4, 0.4, 0.2, 0.2\nwords = ZAP\n"
                                 "[other.kill]\nregion = 0.4, 0.4, 0.2, 0.2\nwords = ZAP\n");
  std::istringstream userText("[neon.edited]\n[fresh.edited]\n[other.kill.mine]\nregion = 0.4, 0.4, 0.2, 0.2\nwords = POW\n");
  const auto merged = MergeDetectionRules(ParseDetectionRules(shippedText), ParseDetectionRules(userText));
  CHECK(merged.rulesRemoved.contains("neon"));    // every built-in rule taken away
  CHECK(!merged.rulesRemoved.contains("other"));  // replaced with the user's own
  CHECK(!merged.rulesRemoved.contains("fresh"));  // never had any: the universal reader reads it
}

void TestTeachFromMarks() {
  const HudModel model({}, "neon", {"Enterro"});
  std::vector<ZoneRead> reads;
  auto add = [&](int64_t from, int64_t to, const char* zone, std::initializer_list<const char*> lines) {
    for (int64_t ms = from; ms <= to; ms += 500) reads.push_back({ms, zone, Rows(lines)});
  };
  add(0, 60000, "bottom", {"AMMO 30 120"});                 // always there
  add(10000, 12000, "center", {"VAPORIZED Bob"});           // the game's kill popup, unknown to the model
  add(9000, 13000, "top", {"WAVE 3"});                      // new around then too, but less like a kill
  add(30000, 31000, "center", {"ELIMINATED Carol"});        // the model would count this; nothing happened
  add(45000, 47000, "feed_right", {"ENTERRO Dave"});        // a kill it already gets
  const auto marks = ParseHudMarks("kill@10.2;kill@45.5");
  const auto taught = TeachFromMarks(model, reads, marks, "neon");
  bool vaporized = false, carol = false, dave = false, ammo = false;
  for (const auto& e : taught) {
    CHECK(e.game == "neon");
    if (e.text == "VAPORIZED BOB") vaporized = e.label == HudLabel::Kill && e.zone == "center";
    if (e.text == "ELIMINATED CAROL") carol = e.label == HudLabel::None;
    if (e.text.find("DAVE") != std::string::npos) dave = true;
    if (e.text.find("AMMO") != std::string::npos) ammo = true;
  }
  CHECK(vaporized);
  CHECK(carol);
  CHECK(!dave);   // already caught, nothing to learn
  CHECK(!ammo);   // on screen before the mark too
  // The player's name is kept as {P}, so what's learned works for anyone.
  std::vector<ZoneRead> named = {{20000, "feed_right", Rows({"ENTERRO Bob"})}, {20500, "feed_right", Rows({"ENTERRO Bob"})}};
  const auto wrong = TeachFromMarks(model, named, {}, "neon");
  CHECK(wrong.size() == 1 && wrong[0].text == "{P} BOB" && wrong[0].label == HudLabel::None);
}

}  // namespace

int RunHudModelTests(const std::filesystem::path& scratch) {
  TestLabels();
  TestSeedModel();
  TestNormalize();
  TestLearning();
  TestExampleFile(scratch);
  TestMarks();
  TestUniversalRules();
  TestUniversalFuses();
  TestClassifyRulesParse();
  TestRulesRemoved();
  TestTeachFromMarks();
  return g_failures;
}
