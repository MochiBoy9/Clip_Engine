// Tests for the parts of event detection that don't need Windows: text matching, rule
// parsing and turning successive screen reads into events.

#include "app/AutoClipPlanner.h"
#include "detect/DetectionRules.h"
#include "detect/TextMatcher.h"
#include "media/ClipMetadata.h"

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
  TestTracker();
  TestPlanner();
  TestMetadata();
  if (argc > 1) TestShippedRulesParse(argv[1]);
  if (argc > 2) TestMetadataBox(argv[2]);
  if (g_failures == 0) std::printf("All detection tests passed\n");
  return g_failures == 0 ? 0 : 1;
}
