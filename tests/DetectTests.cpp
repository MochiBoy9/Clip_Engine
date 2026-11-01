// Tests for the parts of event detection that don't need Windows: text matching, rule
// parsing and turning successive screen reads into events.

#include "app/AutoClipPlanner.h"
#include "detect/DetectionRules.h"
#include "detect/EventFuser.h"
#include "detect/SoundMatch.h"
#include "detect/SoundWatch.h"
#include "detect/ImageMatch.h"
#include "detect/OcrImage.h"
#include "detect/TextMatcher.h"
#include "media/ClipMetadata.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
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

// The Finals' killfeed: "[SMEDC] CAPN_OSPUZE > [KEAYS] Eclipz", a squad tag before each name.
void TestFinalsKillfeed(const char* path) {
  std::ifstream in(path);
  const auto rules = ParseDetectionRules(in).ForGame("the_finals", {"CAPN_OSPUZE"});
  auto named = [&](const char* name) -> const DetectionRule& {
    return *std::ranges::find_if(rules, [&](const DetectionRule& r) { return r.name == name; });
  };
  const DetectionRule& kill = named("the_finals.kill.killfeed");
  const DetectionRule& death = named("the_finals.death.killfeed");
  CHECK(kill.skipTag && kill.mergeMs > 0 && kill.priority > named("the_finals.kill.score_line").priority);
  auto count = [](const DetectionRule& r, std::initializer_list<const char*> lines) { return CountMatches(r, Rows(lines)); };

  CHECK(count(kill, {"SMEDC CAPN_OSPUZE > MrBean", "KEAYS Eclipz > SUNBO Kloudd"}) == 1);
  CHECK(count(kill, {"[SMEDC] CAPN_OSPUZE I > KEAYS Eclipz"}) == 1);
  CHECK(count(kill, {"CAPN_OSPUZE > KEAYS Eclipz"}) == 1);
  CHECK(count(death, {"SMEDC CAPN_OSPUZE > MrBean"}) == 0);
  CHECK(count(kill, {"KEAYS Eclipz > SMEDC CAPN_OSPUZE"}) == 0);
  CHECK(count(death, {"KEAYS Eclipz > SMEDC CAPN_OSPUZE"}) == 1);
  // Someone without a tag killing the player isn't the player's kill.
  CHECK(count(kill, {"Bob CAPN_OSPUZE"}) == 0);
  // A teammate's kill.
  CHECK(count(kill, {"4JEFF Jaadoo > KEAYS Eclipz"}) == 0);
  // Without a name, the killfeed rules aren't used and the score line counts alone.
  std::ifstream again(path);
  const auto nameless = ParseDetectionRules(again).ForGame("the_finals");
  CHECK(std::ranges::none_of(nameless, [](const DetectionRule& r) { return r.name == "the_finals.kill.killfeed"; }));
}

// Detection Studio tests a killfeed rule with the player's name filled in, as the engine runs it.
void TestSetPhrases() {
  DetectionRule rule;
  rule.position = LinePosition::Start;
  rule.skipTag = true;
  SetPhrases(rule, "{player}", "", {"CAPN_OSPUZE"});
  CHECK(rule.playerWords.size() == 1 && rule.words.size() == 1);
  CHECK(CountMatches(rule, Rows({"SMEDC CAPN_OSPUZE > MrBean"})) == 1);
  CHECK(CountMatches(rule, Rows({"KEAYS Eclipz > SMEDC CAPN_OSPUZE"})) == 0);
  // Without a name there's nothing to look for, and it's kept to fill in later.
  DetectionRule nameless;
  SetPhrases(nameless, "{player} | ELIMINATED", "BY", {});
  CHECK(nameless.words.size() == 1 && nameless.playerWords.size() == 1 && nameless.exclude.size() == 1);
}

DetectionRule FusedRule(const char* name, int priority, int mergeMs = 4000) {
  DetectionRule rule;
  rule.name = name;
  rule.event = EventType::Kill;
  rule.priority = priority;
  rule.mergeMs = mergeMs;
  return rule;
}

constexpr int64_t kMs = 10'000;  // 100 ns units

void TestFuser() {
  const DetectionRule feed = FusedRule("feed", 2);
  const DetectionRule score = FusedRule("score", 1);
  std::vector<GameEvent> events;
  std::vector<std::string> notes;
  EventFuser fuser([&](const GameEvent& e) { events.push_back(e); }, [&](const std::string& n) { notes.push_back(n); });

  // One kill from a recorded match: the killfeed line, then the score line read three times
  // as it faded, which used to be three kills.
  fuser.Add(feed, 1, 15'500 * kMs, "SMEDC CAPN OSPUZE KEAYS ECLIPZ", Witness{"", "", 0.7, 0.03, 0.3, 0.2});
  fuser.Add(score, 1, 15'990 * kMs, "ELIMINATION 250");
  fuser.Flush(16'000 * kMs);
  CHECK(events.empty());  // still waiting for every rule to have a look
  fuser.Flush(16'600 * kMs);
  CHECK(events.size() == 1);
  CHECK(events[0].time == 15'500 * kMs);
  CHECK(events[0].witnesses.size() == 2);
  CHECK(events[0].witnesses[0].rule == "feed" && events[0].witnesses[0].counted);
  CHECK(events[0].witnesses[0].left == 0.7);
  CHECK(events[0].witnesses[1].rule == "score" && !events[0].witnesses[1].counted);
  CHECK(std::abs(events[0].witnesses[1].offset - 0.49) < 0.001);
  fuser.Add(score, 1, 17'280 * kMs, "ELIMIYATJON 250");
  fuser.Add(score, 1, 19'370 * kMs, "KEAYS IMINATION 250");
  fuser.Flush(20'000 * kMs);
  CHECK(events.size() == 1);
  CHECK(notes.size() == 2);

  // Long after: the killfeed was misread, so the score line counts it alone.
  fuser.Add(score, 1, 60'000 * kMs, "ELIMINATION 250");
  fuser.Flush(61'000 * kMs);
  CHECK(events.size() == 2);
  CHECK(events[1].witnesses.size() == 1 && events[1].witnesses[0].counted);

  // A double kill: two killfeed lines a second apart and one score line.
  fuser.Add(feed, 1, 100'000 * kMs, "A");
  fuser.Add(score, 1, 100'200 * kMs, "ELIMINATION 250");
  fuser.Flush(101'000 * kMs);
  fuser.Add(feed, 1, 101'100 * kMs, "B");
  fuser.Flush(101'200 * kMs);
  CHECK(events.size() == 4);
  CHECK(events[3].text == "B");

  // Two killfeed lines in one read count twice once it settles.
  fuser.Add(feed, 2, 200'000 * kMs, "A B");
  fuser.FlushAll();
  CHECK(events.size() == 6);

  // A rule without merge_ms counts at once.
  DetectionRule plain = FusedRule("plain", 1, 0);
  fuser.Add(plain, 1, 300'000 * kMs, "KILL");
  CHECK(events.size() == 7);
  CHECK(events[6].witnesses.size() == 1 && events[6].witnesses[0].rule == "plain");

  // Deaths are their own groups.
  DetectionRule death = FusedRule("death", 1);
  death.event = EventType::Death;
  fuser.Add(feed, 1, 400'000 * kMs, "A");
  fuser.Add(death, 1, 400'100 * kMs, "ELIMINATED BY");
  fuser.FlushAll();
  CHECK(events.size() == 9);
}

// The Finals' elimination chime, as shipped, heard in noise and not heard in noise alone.
void TestSounds(const char* iniPath) {
  std::ifstream in(iniPath);
  const auto rules = ParseDetectionRules(in).ForGame("the_finals");
  auto sound = std::ranges::find_if(rules, [](const DetectionRule& r) { return r.ListensForSound(); });
  CHECK(sound != rules.end());
  if (sound == rules.end()) return;
  CHECK(sound->confirmOnly && sound->event == EventType::Kill);
  std::vector<DetectionRule> listening{*sound};
  const auto folder = std::filesystem::path(iniPath).parent_path();
  for (auto& file : listening[0].sounds) file = (folder / file).string();
  const auto chime = LoadSoundCut(listening[0].sounds[0]);
  CHECK(chime.has_value() && chime->size() > 12000);
  if (!chime) return;

  // Four seconds of game noise at 48 kHz stereo, with the chime at 2 s in one of them.
  auto play = [&](bool withChime) {
    std::vector<int16_t> pcm(48000 * 4 * 2);
    uint32_t seed = 12345;
    for (auto& s : pcm) {
      seed = seed * 1664525 + 1013904223;
      s = static_cast<int16_t>(static_cast<int>(seed >> 16) % 1200 - 600);
    }
    if (withChime) {
      for (size_t i = 0; i < chime->size(); ++i) {
        for (int k = 0; k < 3; ++k) {
          const size_t frame = 96000 + i * 3 + k;
          for (int c = 0; c < 2; ++c) {
            pcm[frame * 2 + c] = static_cast<int16_t>(std::clamp<int>(pcm[frame * 2 + c] + static_cast<int>((*chime)[i] * 32767), -32768, 32767));
          }
        }
      }
    }
    std::vector<double> heard;
    SoundWatch watch(listening, [](const std::string& w) { std::printf("%s\n", w.c_str()); });
    CHECK(!watch.Empty());
    // As WASAPI hands it over, 10 ms at a time.
    for (size_t at = 0; at < pcm.size() / 2; at += 480) {
      watch.Push(pcm.data() + at * 2, 480, 48000, 2, static_cast<int64_t>(at) * 10'000'000 / 48000,
                 [&](const DetectionRule&, int count, int64_t time, const std::string&) {
                   for (int i = 0; i < count; ++i) heard.push_back(time / 1e7);
                 });
    }
    return heard;
  };
  const auto heard = play(true);
  CHECK(heard.size() == 1);
  // The cut starts 0.3 s before the chime, and so does the stretch that matched it.
  if (heard.size() == 1) CHECK(std::abs(heard[0] - 2.3) < 0.1);
  CHECK(play(false).empty());
}

void TestSoundCutRoundTrip(const char* scratch) {
  std::vector<float> samples(1600);
  for (size_t i = 0; i < samples.size(); ++i) samples[i] = static_cast<float>(std::sin(i * 0.1) * 0.5);
  const auto path = std::filesystem::path(scratch) / "cut.wav";
  CHECK(SaveSoundCut(path, samples));
  const auto back = LoadSoundCut(path);
  CHECK(back && back->size() == samples.size());
  if (back) CHECK(std::abs((*back)[100] - samples[100]) < 0.001);
  std::string error;
  CHECK(!LoadSoundCut(std::filesystem::path(scratch) / "missing.wav", &error) && !error.empty());
}

void TestConfirmOnly() {
  DetectionRule chime = FusedRule("chime", 1);
  chime.confirmOnly = true;
  const DetectionRule score = FusedRule("score", 1);
  std::vector<GameEvent> events;
  EventFuser fuser([&](const GameEvent& e) { events.push_back(e); });
  // Heard with nothing seen: not a kill.
  fuser.Add(chime, 1, 0, "sound");
  fuser.Flush(20'000 * kMs);
  CHECK(events.empty());
  // Heard, then seen: one kill, with the sound as a witness.
  fuser.Add(chime, 1, 30'000 * kMs, "sound");
  fuser.Add(score, 1, 30'300 * kMs, "ELIMINATION 250");
  fuser.Flush(31'500 * kMs);
  CHECK(events.size() == 1);
  if (events.size() == 1) {
    CHECK(events[0].time == 30'300 * kMs);
    CHECK(events[0].witnesses.size() == 2 && events[0].witnesses[1].rule == "chime");
  }
  // Seen only after the group settled on the sound alone: still one kill, with both.
  fuser.Add(chime, 1, 60'000 * kMs, "sound");
  fuser.Flush(61'200 * kMs);
  fuser.Add(score, 1, 61'500 * kMs, "ELIMINATION 250");
  CHECK(events.size() == 2);
  if (events.size() == 2) CHECK(events[1].witnesses.size() == 2);
}

// What text recognition gets wrong about keywords and names, and how a killfeed row is read.
void TestKeywordsAndNames() {
  // A keyword run into the next word still counts, but an exclude word stuck to it cancels it.
  DetectionRule kill;
  kill.event = EventType::Kill;
  SetPhrases(kill, "ELIMINATED", "BY | YOU WERE", {});
  CHECK(CountMatches(kill, Rows({"ELIMINATED XXSNIPER"})) == 1);
  CHECK(CountMatches(kill, Rows({"ELIMINATEDXXSNIPER"})) == 1);
  CHECK(CountMatches(kill, Rows({"ELIMINATED BY BOB"})) == 0);
  CHECK(CountMatches(kill, Rows({"ELIMINATEDBY BOB"})) == 0);
  CHECK(FindMatches(kill, Rows({"ELIMINATEDXXSNIPER"})) == std::vector<std::string>{"XXSNIPER"});
  // Another form of a keyword is another word.
  CHECK(!WordsMatch("DEFEATED", "DEFEAT"));
  CHECK(!WordsMatch("ASSISTED", "ASSIST"));
  CHECK(!WordsMatch("REVIVED", "REVIVE"));
  CHECK(WordsMatch("REVIVED", "REVIVED"));
  CHECK(WordsMatch("DEFEAT", "DEFEAT"));
  CHECK(RunOnLength("ELIMINATEDBY", "ELIMINATED") == 10);
  CHECK(RunOnLength("ELIMINATEDX", "ELIMINATED") == 0);  // a stray letter is a misread, not a run-on
  CHECK(RunOnLength("DEFEATED", "DEFEAT") == 0);
  DetectionRule end;
  end.event = EventType::MatchEnd;
  SetPhrases(end, "VICTORY | DEFEAT", "", {});
  CHECK(CountMatches(end, Rows({"ENEMY DEFEATED"})) == 0);
  CHECK(CountMatches(end, Rows({"DEFEAT"})) == 1);

  // Names: another player whose name starts with the player's isn't them.
  CHECK(NameMatches("ENTERRO", "ENTERRO"));
  CHECK(NameMatches("ENTERR0", "ENTERRO"));
  CHECK(NameMatches("ENTEPRO", "ENTERRO"));  // a misread letter
  CHECK(!NameMatches("ENTERROX", "ENTERRO"));
  CHECK(!NameMatches("ENTERROXX", "ENTERRO"));
  CHECK(!NameMatches("ENTERROR2", "ENTERRO"));
  // A four-letter name may have one look-alike letter misread, and only that.
  CHECK(NameMatches("IUKE", "LUKE"));
  CHECK(NameMatches("LUKE", "LUKE"));
  CHECK(!NameMatches("IUKF", "LUKE"));
  CHECK(!NameMatches("DUKE", "LUKE"));
  CHECK(!NameMatches("KAI", "KAL"));

  // Accented names read the same as they're typed.
  CHECK(Tokenize("Jos\xC3\xA9 \xC5\x81ukasz \xC3\x86on") == (std::vector<std::string>{"JOSE", "LUKASZ", "AEON"}));
  CHECK(Tokenize("A\xC3\x97" "B \xE2\x86\x92 C") == (std::vector<std::string>{"A", "B", "C"}));

  // A killfeed row with only the player's name is their death, not a kill as well.
  DetectionRule start;
  start.event = EventType::Kill;
  start.position = LinePosition::Start;
  SetPhrases(start, "{player}", "", {"Enterro"});
  DetectionRule finish;
  finish.event = EventType::Death;
  finish.position = LinePosition::End;
  SetPhrases(finish, "{player}", "", {"Enterro"});
  CHECK(CountMatches(start, Rows({"Enterro"})) == 0);
  CHECK(CountMatches(start, Rows({"Enterro X"})) == 0);  // an icon after the name
  CHECK(CountMatches(finish, Rows({"Enterro"})) == 1);
  CHECK(CountMatches(start, Rows({"Enterro Bob"})) == 1);
  CHECK(CountMatches(start, Rows({"Enterror Bob"})) == 0);
  // A rule with words around the name isn't held to that.
  DetectionRule suicide;
  suicide.event = EventType::Death;
  suicide.position = LinePosition::Start;
  SetPhrases(suicide, "{player} COMMITTED SUICIDE", "", {"Enterro"});
  CHECK(CountMatches(suicide, Rows({"Enterro committed suicide"})) == 1);

  // An assister listed between the killer and the player killed.
  DetectionRule assist;
  assist.event = EventType::Assist;
  assist.position = LinePosition::Middle;
  SetPhrases(assist, "{player}", "", {"Enterro"});
  CHECK(CountMatches(assist, Rows({"Killer + Enterro  Victim"})) == 1);
  CHECK(CountMatches(assist, Rows({"Enterro Victim"})) == 0);
  CHECK(CountMatches(assist, Rows({"Killer Enterro"})) == 0);
  CHECK(CountMatches(assist, Rows({"Killer + Enterro X"})) == 0);
}

// A sound is recognized only once it has played out, after the killfeed showed the same kill;
// it still backs that kill up.
void TestLateSound() {
  DetectionRule chime = FusedRule("chime", 1);
  chime.confirmOnly = true;
  const DetectionRule feed = FusedRule("feed", 2);
  std::vector<GameEvent> events;
  std::vector<std::string> notes;
  EventFuser fuser([&](const GameEvent& e) { events.push_back(e); }, [&](const std::string& n) { notes.push_back(n); });
  fuser.SetHoldMs(EventFuser::kSoundHoldMs);
  fuser.Add(feed, 1, 10'200 * kMs, "ME BOB");
  fuser.Flush(10'500 * kMs);
  fuser.Add(chime, 1, 10'000 * kMs, "sound: 0.61 alike");  // began before the killfeed showed it
  fuser.Flush(11'500 * kMs);
  CHECK(events.empty());  // still waiting for a sound
  fuser.Flush(12'300 * kMs);
  CHECK(events.size() == 1);
  if (events.size() == 1) {
    CHECK(events[0].time == 10'200 * kMs);
    CHECK(events[0].witnesses.size() == 2 && events[0].witnesses[1].rule == "chime");
    CHECK(std::abs(events[0].witnesses[1].offset + 0.2) < 0.001);
  }
  CHECK(notes.empty());
  // Two kills far apart: the late sound joins the one it's nearest.
  fuser.Add(feed, 1, 30'000 * kMs, "ME ANN");
  fuser.Add(feed, 1, 36'000 * kMs, "ME CAT");
  fuser.Add(chime, 1, 35'800 * kMs, "sound");
  fuser.Flush(40'000 * kMs);
  CHECK(events.size() == 3);
  if (events.size() == 3) CHECK(events[1].witnesses.size() == 1 && events[2].witnesses.size() == 2);
}

void TestForgetMs() {
  DetectionRule rule = FusedRule("score", 1);
  rule.words = ParsePhrases("ELIMINATION {number}");
  rule.cooldownMs = 0;
  RuleTracker plain(rule);
  DetectionRule longer = rule;
  longer.forgetMs = 3000;
  RuleTracker patient(longer);
  // The score line lost for a second and a half as it fades, then read again.
  for (RuleTracker* t : {&plain, &patient}) CHECK(t->Update(Rows({"ELIMINATION + 250"}), 0) == 1);
  CHECK(plain.Update(Rows({"ELIMINATION + 250"}), 1500) == 1);
  CHECK(patient.Update(Rows({"ELIMINATION + 250"}), 1500) == 0);
  CHECK(patient.Update(Rows({}), 4600) == 0);
  CHECK(patient.Update(Rows({"ELIMINATION + 250"}), 4800) == 1);
}

void TestWitnessRoundTrip() {
  ClipMetadata m;
  m.game = "The Finals";
  m.trigger = "auto";
  m.seconds = 20;
  Witness counted{"the_finals.kill.killfeed", "SMEDC CAPN OSPUZE \"MrBean\"", 0.7, 0.03, 0.3, 0.2, 0, true};
  Witness agreed{"the_finals.kill", "ELIMINATION 250", 0.3, 0.52, 0.4, 0.24, 0.49, false};
  m.markers.push_back({EventType::Kill, 15.5, "SMEDC CAPN OSPUZE", {counted, agreed}});
  m.markers.push_back({EventType::Death, 18, "ELIMINATED BY", {}});
  const std::string json = ToJson(m);
  CHECK(json.find("\"found_by\"") != std::string::npos);
  const auto back = ParseMetadataJson(json);
  CHECK(back.has_value());
  if (!back) return;
  CHECK(back->markers.size() == 2);
  CHECK(back->markers[0].witnesses.size() == 2);
  CHECK(back->markers[0].witnesses[0].text == counted.text);
  CHECK(back->markers[0].witnesses[0].counted && !back->markers[0].witnesses[1].counted);
  CHECK(std::abs(back->markers[0].witnesses[1].offset - 0.49) < 0.001);
  CHECK(std::abs(back->markers[0].witnesses[1].top - 0.52) < 0.0001);
  CHECK(back->markers[1].witnesses.empty());
  // Trimming keeps them.
  CHECK(Trimmed(*back, 10, 20).markers[0].witnesses.size() == 2);
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

// The user's detection.ini replaces only the games it has rules for.
void TestMergeRules() {
  auto shipped = Parse(R"(
[detection]
sample_ms = 250

[the_finals.kill]
region = 0.3, 0.5, 0.4, 0.1
words = ELIMINATION {number}

[the_finals.scoreboard]
kills = ELIMINATIONS

[valorant.kill]
words = {player}
position = start

[valorant.match_end]
words = VICTORY
)");
  CHECK(shipped.editedGames.contains("the_finals"));
  CHECK(shipped.editedGames.contains("valorant"));

  auto user = Parse(R"(
[detection]
sample_ms = 200

[game.my_game]
name = My Game
exe = MyGame.exe

[valorant.kill]
words = KILLED

[the_finals.edited]

[my_game.kill]
words = KO
)");
  CHECK(user.warnings.empty());
  CHECK(user.editedGames.contains("the_finals"));
  CHECK(user.editedGames.contains("valorant"));
  CHECK(user.sampleMsSet);

  const auto merged = MergeDetectionRules(std::move(shipped), std::move(user));
  CHECK(merged.sampleMs == 200);
  // The Finals: every rule removed in Studio, but its scoreboard stays.
  CHECK(merged.ForGame("the_finals").empty());
  CHECK(merged.ScoreboardsForGame("the_finals").size() == 1);
  // Valorant: the user's one rule instead of both shipped ones.
  const auto valorant = merged.ForGame("valorant", {"Me"});
  CHECK(valorant.size() == 1);
  CHECK(valorant.size() == 1 && valorant[0].words.size() == 1 && valorant[0].words[0].words[0] == "KILLED");
  CHECK(valorant.size() == 1 && valorant[0].sampleMs == 200);  // follows the winning [detection] sample_ms
  CHECK(merged.ForGame("my_game").size() == 1);
  CHECK(merged.games.size() == 1);

  // A game the user's file doesn't mention keeps the shipped rules.
  auto again = Parse("[the_finals.kill]\nwords = ELIMINATION {number}\n\n[valorant.match_end]\nwords = VICTORY\n");
  const auto untouched = MergeDetectionRules(std::move(again), Parse("[overwatch.kill]\nwords = ELIMINATED\n"));
  CHECK(untouched.ForGame("the_finals").size() == 1);
  CHECK(untouched.ForGame("valorant").size() == 1);
  CHECK(untouched.ForGame("overwatch").size() == 1);
  CHECK(untouched.sampleMs == 250);
}

// With the region read several ways, each rule goes by the read that suits it.
void TestChooseRead() {
  DetectionRule rule;
  rule.words = {MakePhrase("ELIMINATED")};
  rule.exclude = {MakePhrase("BY")};
  // One read missed the word; go by the one that has it.
  CHECK(ChooseRead(rule, {Rows({"ELMNTD BOB"}), Rows({"ELIMINATED BOB"})}) == 1);
  // Ties go to the first.
  CHECK(ChooseRead(rule, {Rows({"ELIMINATED BOB"}), Rows({"ELIMINATED BOB"})}) == 0);
  // A read that shows "BY" wins over one that lost it.
  CHECK(ChooseRead(rule, {Rows({"ELIMINATED BY BOB"}), Rows({"ELIMINATED BOB"})}) == 0);
  CHECK(ChooseRead(rule, {Rows({"ELIMINATED BOB"}), Rows({"ELIMINATED BY BOB"})}) == 1);
  CHECK(ChooseRead(rule, {Rows({"ELIMINATED BOB"})}) == 0);
}

std::vector<uint8_t> Bgra(uint32_t width, uint32_t height, uint8_t background) {
  std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4, background);
  for (size_t i = 3; i < pixels.size(); i += 4) pixels[i] = 255;
  return pixels;
}

void Paint(std::vector<uint8_t>& pixels, uint32_t width, uint32_t x0, uint32_t y0, uint32_t x1, uint32_t y1, uint8_t r,
           uint8_t g, uint8_t b) {
  for (uint32_t y = y0; y < y1; ++y) {
    for (uint32_t x = x0; x < x1; ++x) {
      uint8_t* p = pixels.data() + (static_cast<size_t>(y) * width + x) * 4;
      p[0] = b, p[1] = g, p[2] = r;
    }
  }
}

void TestReadQuality() {
  CHECK(ParseReadQuality("High") == ReadQuality::High);
  CHECK(ParseReadQuality("ultra") == ReadQuality::Ultra);
  CHECK(!ParseReadQuality("best"));
  CHECK(ToString(ReadQuality::Low) == "low");
  CHECK(ReadPasses(ReadQuality::Low) == 1 && ReadPasses(ReadQuality::Medium) == 1);
  CHECK(ReadPasses(ReadQuality::High) == 2 && ReadPasses(ReadQuality::Ultra) == 3);
  CHECK(ScaledSampleMs(250, ReadQuality::Low) == 250);
  CHECK(ScaledSampleMs(250, ReadQuality::Ultra) == 125);
  CHECK(ScaledSampleMs(150, ReadQuality::Ultra) == 100);
  CHECK(ShrinksBanners(ReadQuality::Low) && !ShrinksBanners(ReadQuality::High));

  // Low reads short regions twice as big, as before; higher qualities enlarge more of them.
  CHECK(OcrScale(400, 60, 1, ReadQuality::Low, 2600) == 2);
  CHECK(OcrScale(400, 150, 1, ReadQuality::Low, 2600) == 1);
  CHECK(OcrScale(400, 150, 1, ReadQuality::Medium, 2600) == 2);
  CHECK(OcrScale(400, 60, 1, ReadQuality::High, 2600) == 3);
  CHECK(OcrScale(1000, 60, 1, ReadQuality::Ultra, 2600) == 2);  // three times would be too wide
  CHECK(OcrScale(1200, 900, 2, ReadQuality::Ultra, 2600) == 0.5);
  CHECK(OcrScale(6000, 900, 1, ReadQuality::Low, 2600) == 1.0 / 3);  // shrunk to fit

  // Bright text on a dark background comes out dark on white.
  auto pixels = Bgra(40, 20, 30);
  Paint(pixels, 40, 10, 5, 30, 15, 250, 40, 40);  // red text
  const auto low = PrepareForOcr(pixels.data(), 40, 20, 0, 1, ReadQuality::Low, 0, 2600);
  CHECK(low.width == 80 && low.height == 40);
  CHECK(low.gray[0] == 255 - 30);
  CHECK(low.gray[20 * 80 + 40] == 255 - 250);
  // Medium stretches the brightness, so the background turns white and the text black.
  const auto medium = PrepareForOcr(pixels.data(), 40, 20, 0, 1, ReadQuality::Medium, 0, 2600);
  CHECK(medium.gray[0] == 255);
  CHECK(medium.gray[20 * 80 + 40] == 0);
  // and enlarges smoothly: the edge of the text is in between.
  CHECK(medium.gray[20 * 80 + 20] > 0 && medium.gray[20 * 80 + 20] < 255);
  // The cut pass makes it black and white.
  const auto cut = PrepareForOcr(pixels.data(), 40, 20, 0, 1, ReadQuality::High, 1, 2600);
  CHECK(cut.width == 120);  // High reads it three times as big
  CHECK(std::ranges::all_of(cut.gray, [](uint8_t v) { return v == 0 || v == 255; }));
  CHECK(cut.gray[0] == 255 && cut.gray[30 * 120 + 60] == 0);

  // Dark text on a bright banner: the cut takes the rarer side as the text.
  auto banner = Bgra(40, 20, 230);
  Paint(banner, 40, 12, 6, 28, 14, 20, 20, 20);
  const auto dark = PrepareForOcr(banner.data(), 40, 20, 0, 1, ReadQuality::High, 1, 2600);
  CHECK(dark.gray[0] == 255 && dark.gray[30 * 120 + 60] == 0);

  // With a threshold, pass 0 keeps only what's bright enough, pass 2 a little less bright too.
  auto dim = Bgra(40, 20, 30);
  Paint(dim, 40, 10, 5, 30, 15, 170, 170, 170);
  CHECK(PrepareForOcr(dim.data(), 40, 20, 200, 1, ReadQuality::Ultra, 0, 2600).gray[30 * 120 + 60] == 255);
  CHECK(PrepareForOcr(dim.data(), 40, 20, 200, 1, ReadQuality::Ultra, 2, 2600).gray[30 * 120 + 60] == 0);

  // A flat region isn't stretched into specks.
  const auto flat = Bgra(40, 20, 120);
  CHECK(PrepareForOcr(flat.data(), 40, 20, 0, 1, ReadQuality::Medium, 0, 2600).gray[0] == 255 - 120);

  std::array<uint32_t, 256> histogram{};
  histogram[20] = 100;
  histogram[220] = 30;
  const int t = OtsuThreshold(histogram);
  CHECK(t > 20 && t <= 220);
}

bool Near(double a, double b, double within = 1e-6) { return std::abs(a - b) <= within; }

void TestPlaceRegion() {
  const ScreenRegion feed{0.65, 0.125, 0.33, 0.045};
  // On the shape it was made for, at 100%, nothing moves.
  CHECK(Near(PlaceRegion(feed, 2560, 1440).left, 0.65) && Near(PlaceRegion(feed, 1920, 1080).width, 0.33));
  CHECK(Near(PlaceRegion(feed, 2560, 1440).top, 0.125) && Near(PlaceRegion(feed, 2560, 1440).height, 0.045));

  // An ultrawide: the killfeed keeps to the right edge, as many pixels in and as wide as on 2560x1440.
  const ScreenRegion wide = PlaceRegion(feed, 3440, 1440);
  CHECK(wide.anchorX == Anchor::End && wide.anchorY == Anchor::Start);
  CHECK(Near((1 - wide.left - wide.width) * 3440, 0.02 * 2560, 1e-6));
  CHECK(Near(wide.width * 3440, 0.33 * 2560, 1e-6));
  CHECK(Near(wide.top, 0.125) && Near(wide.height, 0.045));

  // The kill banner stays in the middle, the same size.
  const ScreenRegion banner = PlaceRegion({0.42, 0.72, 0.16, 0.16}, 3440, 1440);
  CHECK(Near(banner.left + banner.width / 2, 0.5) && Near(banner.width * 3440, 0.16 * 2560, 1e-6));
  CHECK(Near(banner.top, 0.72));
  // Something on the left keeps to the left edge.
  const ScreenRegion left = PlaceRegion({0.02, 0.8, 0.2, 0.1}, 5120, 1440);
  CHECK(Near(left.left * 5120, 0.02 * 2560, 1e-6) && Near(left.width * 5120, 0.2 * 2560, 1e-6));

  // A 16:10 screen: the HUD's 16:9 area is 1920x1080 in the middle, so the top keeps to the top
  // and the bottom to the bottom, each as far in as on a 1080p screen.
  const ScreenRegion tall = PlaceRegion(feed, 1920, 1200);
  CHECK(Near(tall.top * 1200, 0.125 * 1080, 1e-6) && Near(tall.left, 0.65));
  const ScreenRegion low = PlaceRegion({0.4, 0.8, 0.2, 0.1}, 1920, 1200);
  CHECK(Near((1 - low.top - low.height) * 1200, 0.1 * 1080, 1e-6));

  // A smaller HUD shrinks toward the same corner.
  const ScreenRegion small = PlaceRegion(feed, 1920, 1080, 0.8);
  CHECK(Near(small.left + small.width, 1 - 0.02 * 0.8) && Near(small.width, 0.33 * 0.8));
  CHECK(Near(small.top, 0.125 * 0.8) && Near(small.height, 0.045 * 0.8));
  // A bigger one never runs off the screen.
  const ScreenRegion big = PlaceRegion({0.7, 0.0, 0.3, 0.1}, 1920, 1080, 1.5);
  CHECK(big.left >= 0 && Near(big.left + big.width, 1) && Near(big.width, 0.45));

  // An anchor given in the file wins over where the region is.
  ScreenRegion pinned{0.1, 0.4, 0.2, 0.2};
  pinned.anchorX = Anchor::Center;
  const ScreenRegion centered = PlaceRegion(pinned, 3440, 1440);
  CHECK(Near(centered.left + centered.width / 2, 0.5 + (0.2 - 0.5) * 2560 / 3440));

  // A region made on an ultrawide stays put there. On a 16:9 screen that HUD fits the width, so
  // the region keeps across but moves toward the top.
  ScreenRegion madeWide{0.8, 0.1, 0.15, 0.05};
  CHECK(ParseAspect("3440:1440", madeWide.aspect));
  CHECK(Near(PlaceRegion(madeWide, 3440, 1440).left, 0.8) && Near(PlaceRegion(madeWide, 3440, 1440).top, 0.1));
  const ScreenRegion onNarrow = PlaceRegion(madeWide, 2560, 1440);
  CHECK(Near(onNarrow.left, 0.8) && Near(onNarrow.top * 1440, 0.1 * 2560 * 1440 / 3440, 1e-6));

  CHECK(Near(HudHeight(feed, 1920, 1200), 1080));
  CHECK(Near(HudHeight(feed, 3440, 1440), 1440));
  CHECK(Near(HudHeight(feed, 3440, 1440, 0.9), 1296));

  Anchor x = Anchor::Auto, y = Anchor::Auto;
  CHECK(ParseAnchor("top-right", x, y) && x == Anchor::End && y == Anchor::Start);
  CHECK(ParseAnchor("Center", x, y) && x == Anchor::Center && y == Anchor::Center);
  CHECK(ParseAnchor("bottom center", x, y) && x == Anchor::Center && y == Anchor::End);
  CHECK(ParseAnchor("left", x, y) && x == Anchor::Start && y == Anchor::Auto);
  CHECK(ParseAnchor("auto", x, y) && x == Anchor::Auto && y == Anchor::Auto);
  CHECK(!ParseAnchor("upper", x, y) && !ParseAnchor("left right", x, y) && !ParseAnchor("", x, y));
  double aspect = 0;
  CHECK(ParseAspect("21:9", aspect) && Near(aspect, 21.0 / 9));
  CHECK(ParseAspect("1.6", aspect) && Near(aspect, 1.6));
  CHECK(!ParseAspect("wide", aspect) && !ParseAspect("16:0", aspect) && !ParseAspect("20", aspect));

  // Both keys are read from detection.ini, before or after the region.
  std::istringstream in(
      "[the_finals.kill]\nanchor = top-right\naspect = 21:9\nregion = 0.7, 0.1, 0.25, 0.05\nwords = ELIMINATED\n"
      "[the_finals.scoreboard]\nregion = 0.1, 0.1, 0.8, 0.8\nanchor = center\nkills = K\n"
      "[the_finals.death]\nregion = 0.4, 0.4, 0.2, 0.2\nanchor = sideways\nwords = DEAD\n");
  const auto rules = ParseDetectionRules(in);
  CHECK(rules.rules.size() == 2);
  CHECK(rules.rules[0].region.anchorX == Anchor::End && rules.rules[0].region.anchorY == Anchor::Start);
  CHECK(Near(rules.rules[0].region.aspect, 21.0 / 9) && Near(rules.rules[0].region.left, 0.7));
  CHECK(rules.scoreboards.size() == 1 && rules.scoreboards[0].region.anchorX == Anchor::Center);
  CHECK(rules.rules[1].region.anchorX == Anchor::Auto);
  CHECK(rules.warnings.size() == 1);
}

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
  TestMergeRules();
  TestChooseRead();
  TestReadQuality();
  TestPlaceRegion();
  TestFuser();
  TestForgetMs();
  TestWitnessRoundTrip();
  TestConfirmOnly();
  TestLateSound();
  TestKeywordsAndNames();
  TestSetPhrases();
  if (argc > 1) TestShippedRulesParse(argv[1]);
  if (argc > 1) TestSounds(argv[1]);
  if (argc > 2) TestSoundCutRoundTrip(argv[2]);
  if (argc > 1) TestFinalsRules(argv[1]);
  if (argc > 1) TestFinalsKillfeed(argv[1]);
  if (argc > 2) TestMetadataBox(argv[2]);
  if (g_failures == 0) std::printf("All detection tests passed\n");
  return g_failures == 0 ? 0 : 1;
}
