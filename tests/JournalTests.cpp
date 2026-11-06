// Tests for the journal: journal rules in detection.ini, what they note from a read of the
// screen, the tracker that tells new notes from ones already seen, the timeline line, Hunt:
// Showdown 1896's match summary, and the shipped journal rules.

#include "app/Timeline.h"
#include "detect/DetectionRules.h"
#include "detect/Journal.h"
#include "detect/TextMatcher.h"
#include "integrations/GameStateTrackers.h"

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

using Rows = std::vector<std::vector<std::string>>;

Rows R(std::initializer_list<const char*> lines) {
  Rows rows;
  for (const char* line : lines) rows.push_back(Tokenize(line));
  return rows;
}

DetectionRules Parse(const std::string& text) {
  std::istringstream in(text);
  return ParseDetectionRules(in);
}

JournalRule Only(const std::string& text) {
  const auto rules = Parse(text);
  for (const auto& w : rules.warnings) std::printf("warning: %s\n", w.c_str());
  CHECK(rules.warnings.empty());
  CHECK(rules.journals.size() == 1);
  return rules.journals.empty() ? JournalRule{} : rules.journals.front();
}

void TestParsing() {
  const auto rules = Parse(R"(
[arc_raiders.journal.map]
region = 0, 0, 1, 0.4
values = Dam Battlegrounds | The Blue Gate (BLUE GATE, BLUEGATE) | Stella Montis

[arc_raiders.journal.enemy.feed]
region = 0.6, 0.3, 0.4, 0.4
pattern = DESTROYED {value} {xp} XP | DESTROYED {value}
exclude = DAMAGED

[arc_raiders.journal.inventory]
region = 0.05, 0.1, 0.9, 0.8
lines = true
when = INVENTORY
when_region = 0, 0, 1, 0.1
anchor = top

[arc_raiders.journal.nothing]
region = 0, 0, 1, 1

[arc_raiders.journal]
region = 0, 0, 1, 1
words = X

[arc_raiders.journal.bad]
region = 0, 0, 1, 1
pattern = {value} {number}
)");
  CHECK(rules.journals.size() == 3);
  // No words to look for, no note name, and a pattern of nothing but placeholders (which
  // then has nothing to look for either).
  CHECK(rules.warnings.size() == 4);
  // Journal rules don't count as the game's own detection rules, so its shipped ones stay.
  CHECK(!rules.editedGames.contains("arc_raiders"));
  CHECK(rules.ForGame("arc_raiders").empty());

  const auto& map = rules.journals[0];
  CHECK(map.note == "map" && map.game == "arc_raiders");
  CHECK(map.values.size() == 3);
  CHECK(map.sampleMs == 1500);  // a menu
  // Longest first, and words in brackets are what's looked for.
  CHECK(map.values[0].name == "Dam Battlegrounds" || map.values[0].name == "Stella Montis");
  bool blueGate = false;
  for (const auto& v : map.values) blueGate |= v.name == "The Blue Gate" && v.phrases.size() == 2;
  CHECK(blueGate);

  const auto& enemy = rules.journals[1];
  CHECK(enemy.note == "enemy" && enemy.name == "arc_raiders.journal.enemy.feed");
  CHECK(enemy.patterns.size() == 2 && enemy.ReadsFeed());
  CHECK(enemy.sampleMs == 500);  // a feed
  CHECK(enemy.patterns[0].words.size() == 4 && enemy.patterns[0].words[1] == kValueWord && enemy.patterns[0].words[2] == kXpWord);

  const auto& inventory = rules.journals[2];
  CHECK(inventory.lines && inventory.whenRegion.has_value());
  // when_region keeps to the same edge as the region.
  CHECK(inventory.whenRegion && inventory.whenRegion->anchorY == inventory.region.anchorY);

  // The user's journal rules for a game replace the shipped ones; other games keep theirs.
  auto shipped = Parse("[a.journal.map]\nregion = 0,0,1,1\nvalues = One\n[b.journal.map]\nregion = 0,0,1,1\nvalues = Two\n");
  auto user = Parse("[a.journal.died]\nregion = 0,0,1,1\nwords = DEAD\n[a.kill]\nregion = 0,0,1,1\nwords = KILL\n");
  const auto merged = MergeDetectionRules(shipped, user);
  CHECK(merged.JournalsForGame("a").size() == 1 && merged.JournalsForGame("a")[0].note == "died");
  CHECK(merged.JournalsForGame("b").size() == 1);
  // And editing a game's other rules leaves its shipped journal alone.
  auto other = Parse("[b.kill]\nregion = 0,0,1,1\nwords = KILL\n");
  CHECK(MergeDetectionRules(shipped, other).JournalsForGame("b").size() == 1);
}

void TestFindNotes() {
  const auto enemy = Only(R"(
[g.journal.enemy]
region = 0, 0, 1, 1
pattern = DESTROYED {value} {xp} XP | DESTROYED {value} | {value} DESTROYED
exclude = DAMAGED
)");
  auto notes = FindNotes(enemy, R({"+100 XP", "DESTROYED WASP 100 XP", "Damaged Hornet 30 XP", "destroyed arc probe"}));
  CHECK(notes.size() == 2);
  CHECK(notes.size() == 2 && notes[0].value == "Wasp" && notes[0].xp == 100 && notes[0].note == "enemy");
  CHECK(notes.size() == 2 && notes[1].value == "Arc Probe" && notes[1].xp == 0);
  // "1,250" is one number, and an icon read as a letter isn't part of the name.
  notes = FindNotes(enemy, R({"DESTROYED BASTION 1,250 XP", "i ROCKETEER DESTROYED"}));
  CHECK(notes.size() == 2 && notes[0].value == "Bastion" && notes[0].xp == 1250);
  CHECK(notes.size() == 2 && notes[1].value == "Rocketeer");
  // A line that's all icons and numbers has no value.
  CHECK(FindNotes(enemy, R({"DESTROYED 100"})).empty());

  const auto map = Only(R"(
[g.journal.map]
region = 0, 0, 1, 1
values = Dam Battlegrounds (DAM BATTLEGROUNDS, BATTLEGROUNDS) | Buried City | The Blue Gate (BLUE GATE)
)");
  notes = FindNotes(map, R({"DEPLOYING TO", "THE BLUE GATE"}));
  CHECK(notes.size() == 1 && notes[0].value == "The Blue Gate");
  notes = FindNotes(map, R({"BURIEO CITY"}));  // a letter misread
  CHECK(notes.size() == 1 && notes[0].value == "Buried City");

  const auto words = Only("[g.journal.extracted]\nregion = 0,0,1,1\nwords = EXTRACTED\nexclude = FAILED\n");
  CHECK(FindNotes(words, R({"YOU EXTRACTED"})).size() == 1);
  CHECK(FindNotes(words, R({"EXTRACTION FAILED"})).empty());
  CHECK(FindNotes(words, R({"RAID", "EXTRACTED FAILED"})).empty());

  const auto loot = Only("[g.journal.loot]\nregion = 0,0,1,1\npattern = PICKED UP {number} {value} | PICKED UP {value}\nexclude = XP\n");
  notes = FindNotes(loot, R({"Picked up 3 Metal Parts", "+250 XP", "Picked up ARC Alloy"}));
  CHECK(notes.size() == 2 && notes[0].value == "Metal Parts" && notes[0].amount == 3);
  CHECK(notes.size() == 2 && notes[1].value == "Arc Alloy" && notes[1].amount == 0);
  // A pattern with nothing to look for but a number and a value would take any line.
  CHECK(Parse("[g.journal.loot]\nregion = 0,0,1,1\npattern = {number} {value} | PICKED UP {value}\n").warnings.size() == 1);

  const auto inventory = Only(R"(
[g.journal.inventory]
region = 0, 0, 1, 1
lines = true
when = BACKPACK
exclude = SORT
)");
  // A tooltip's description isn't an item.
  notes = FindNotes(inventory, R({"BACKPACK 12/20", "Metal Parts x3", "Bandage 2", "Sort", "3", "a b",
                                  "Can be recycled into metal parts and rubber"}));
  CHECK(notes.size() == 2);
  CHECK(notes.size() == 2 && notes[0].value == "Metal Parts" && notes[0].amount == 3);
  CHECK(notes.size() == 2 && notes[1].value == "Bandage" && notes[1].amount == 2);
}

void TestTracker() {
  const auto enemy = Only("[g.journal.enemy]\nregion = 0,0,1,1\npattern = DESTROYED {value}\n");
  JournalTracker feed(enemy);
  CHECK(feed.Update(R({"DESTROYED WASP"}), 0).size() == 1);
  CHECK(feed.Update(R({"DESTROYED WASP"}), 500).empty());  // still showing
  CHECK(feed.Update(R({}), 1000).empty());                // missed a read
  CHECK(feed.Update(R({"DESTROYED WASP"}), 1500).empty());
  // A second one at once, and a different one.
  CHECK(feed.Update(R({"DESTROYED WASP", "DESTROYED WASP", "DESTROYED HORNET"}), 2000).size() == 2);
  // Gone a while, then another.
  CHECK(feed.Update(R({}), 4000).empty());
  CHECK(feed.Update(R({"DESTROYED WASP"}), 9000).size() == 1);

  // A menu listing every map notes none; the screen showing the one picked notes it, once.
  const auto map = Only("[g.journal.map]\nregion = 0,0,1,1\nvalues = Buried City | Spaceport\n");
  JournalTracker menu(map);
  CHECK(menu.Update(R({"BURIED CITY", "SPACEPORT"}), 0).empty());
  auto notes = menu.Update(R({"SPACEPORT"}), 1500);
  CHECK(notes.size() == 1 && notes[0].value == "Spaceport");
  CHECK(menu.Update(R({"SPACEPORT"}), 3000).empty());
  CHECK(menu.Update(R({"BURIED CITY"}), 4500).size() == 1);
  // After a raid away from the menu, the same map picked again is noted again.
  CHECK(menu.Update(R({"BURIED CITY"}), 6000).empty());
  CHECK(menu.Update(R({"BURIED CITY"}), 20 * 60 * 1000).size() == 1);

  // pick = false notes each of the values shown.
  const auto conditions = Only("[g.journal.condition]\nregion = 0,0,1,1\nvalues = Night Raid | Storm\npick = false\n");
  JournalTracker shown(conditions);
  CHECK(shown.Update(R({"NIGHT RAID", "STORM"}), 0).size() == 2);

  // when without when_region: only while its words are in the same read.
  const auto inventory = Only("[g.journal.inventory]\nregion = 0,0,1,1\nlines = true\nwhen = BACKPACK\n");
  JournalTracker list(inventory);
  CHECK(list.Update(R({"Metal Parts"}), 0).empty());
  CHECK(list.Update(R({"BACKPACK", "Metal Parts", "Bandage"}), 1000).size() == 2);
  CHECK(list.Update(R({"BACKPACK", "Metal Parts", "Bandage", "Battery"}), 2000).size() == 1);
  // With when_region, the gate says.
  const auto gated = Only("[g.journal.inventory]\nregion = 0,0,1,1\nlines = true\nwhen = BACKPACK\nwhen_region = 0,0,1,0.1\n");
  JournalTracker behind(gated);
  CHECK(behind.Update(R({"Metal Parts"}), 0, false).empty());
  CHECK(behind.Update(R({"Metal Parts"}), 1000, true).size() == 1);

  // cooldown_ms: a banner read again soon after isn't another run's end.
  const auto died = Only("[g.journal.died]\nregion = 0,0,1,1\nwords = YOU DIED\ncooldown_ms = 60000\nforget_ms = 1000\n");
  JournalTracker banner(died);
  CHECK(banner.Update(R({"YOU DIED"}), 0).size() == 1);
  CHECK(banner.Update(R({}), 5000).empty());
  CHECK(banner.Update(R({"YOU DIED"}), 10000).empty());
  CHECK(banner.Update(R({}), 65000).empty());
  CHECK(banner.Update(R({"YOU DIED"}), 70000).size() == 1);
}

void TestTimelineNote() {
  JournalNote note{"enemy", "Wasp", 0, 100, "DESTROYED \"WASP\" 100 XP"};
  CHECK(TimelineNote(12.5, 2, note) ==
        R"({"t": 12.50, "match": 2, "note": "enemy", "value": "Wasp", "xp": 100, "text": "DESTROYED \"WASP\" 100 XP"})");
  JournalNote bare{"extracted", "", 0, 0, "EXTRACTED"};
  CHECK(TimelineNote(1, 0, bare) == R"({"t": 1.00, "match": 0, "note": "extracted", "text": "EXTRACTED"})");
  JournalNote loot{"loot", "Metal Parts", 3, 0, "+3 Metal Parts"};
  CHECK(TimelineNote(2, 1, loot) ==
        R"({"t": 2.00, "match": 1, "note": "loot", "value": "Metal Parts", "amount": 3, "text": "+3 Metal Parts"})");
}

std::string HuntXml(bool dead, int killedByMe, int monsters, const char* extra = "") {
  return std::string(R"(<?xml version="1.0"?>
<Attributes Version="37">
 <Attr name="MissionBagIsHunterDead" value=")") + (dead ? "true" : "false") + R"("/>
 <Attr name="MissionBagIsQuickPlay" value="false"/>
 <Attr name="MissionBagPlayer_0_0_killedbyme" value=")" + std::to_string(killedByMe) + R"("/>
 <Attr name="MissionBagPlayer_0_0_downedbyme" value="1"/>
 <Attr name="MissionBagPlayer_1_0_killedme" value="1"/>
 <Attr name="MissionAccoladeEntry_0_category" value="accolade_monsters_killed"/>
 <Attr name="MissionAccoladeEntry_0_hits" value=")" + std::to_string(monsters) + R"("/>
 <Attr name="MissionAccoladeEntry_0_xp" value="400"/>
 <Attr name="MissionAccoladeEntry_1_category" value="accolade_bounty_extracted"/>
 <Attr name="MissionAccoladeEntry_1_hits" value="1"/>
 <Attr name="MissionAccoladeEntry_1_xp" value="1000"/>
 <Attr name="UIColorBlindMode" value="0"/>)" + extra + R"(
</Attributes>)";
}

void TestHunt() {
  HuntMissionTracker hunt;
  // The match already in the file was played before the engine was watching.
  CHECK(hunt.Update(HuntXml(false, 2, 7)).empty());
  CHECK(hunt.Update(HuntXml(false, 2, 7)).empty());
  // A setting changing isn't a new match.
  CHECK(hunt.Update(HuntXml(false, 2, 7, R"(<Attr name="UIFov" value="90"/>)")).empty());

  auto notes = hunt.Update(HuntXml(true, 1, 12));
  auto find = [&](const std::string& name) -> const JournalNote* {
    for (const auto& n : notes) {
      if (n.note == name) return &n;
    }
    return nullptr;
  };
  CHECK(find("deploy") && find("deploy")->value == "Bounty Hunt");
  CHECK(find("enemy") && find("enemy")->amount == 12 && find("enemy")->value == "Monster");
  CHECK(find("player_kill") && find("player_kill")->amount == 2);  // one killed, one downed
  CHECK(find("objective") && find("objective")->value == "Bounty extracted");
  CHECK(find("xp") && find("xp")->amount == 1400);
  CHECK(find("died") && !find("extracted"));
  CHECK(!notes.empty() && notes.back().note == "died");  // how it ended comes last

  notes = hunt.Update(HuntXml(false, 0, 3));
  CHECK(find("extracted") && !find("died") && find("player_kill") && find("player_kill")->amount == 1);
  CHECK(hunt.Update("not xml at all").empty());
}

void TestShipped(const char* path) {
  std::ifstream in(path);
  CHECK(in.good());
  const auto rules = ParseDetectionRules(in);
  for (const auto& w : rules.warnings) std::printf("detection.ini %s\n", w.c_str());
  CHECK(rules.warnings.empty());

  const auto arc = rules.JournalsForGame("arc_raiders");
  auto rule = [&](const std::string& note) -> const JournalRule* {
    for (const auto& j : arc) {
      if (j.note == note) return &j;
    }
    return nullptr;
  };
  for (const char* note : {"map", "queue", "deploy", "enemy", "player_kill", "loot", "inventory", "died", "extracted", "xp"}) {
    if (!rule(note)) std::printf("ARC Raiders has no %s journal rule\n", note);
    CHECK(rule(note) != nullptr);
  }
  if (rule("enemy")) {
    // The XP log names each ARC destroyed and the XP it gave.
    auto notes = FindNotes(*rule("enemy"), R({"DESTROYED WASP +100 XP", "DAMAGED HORNET +30 XP", "DESTROYED A RAIDER"}));
    CHECK(notes.size() == 1 && notes[0].value == "Wasp" && notes[0].xp == 100);
  }
  if (rule("map")) {
    JournalTracker map(*rule("map"));
    CHECK(map.Update(R({"DAM BATTLEGROUNDS", "BURIED CITY", "SPACEPORT", "THE BLUE GATE"}), 0).empty());
    auto notes = map.Update(R({"BURIED CITY", "NIGHT RAID"}), 2000);
    CHECK(notes.size() == 1 && notes[0].value == "Buried City");
  }
  if (rule("extracted") && rule("died")) {
    CHECK(FindNotes(*rule("extracted"), R({"EXTRACTED"})).size() == 1);
    CHECK(FindNotes(*rule("died"), R({"EXTRACTED"})).empty());
    CHECK(FindNotes(*rule("died"), R({"YOU DIED"})).size() == 1);
  }
  if (rule("inventory")) {
    // The backpack is read only while its title shows.
    CHECK(rule("inventory")->lines && rule("inventory")->whenRegion);
  }
  // The results screen's XP, with the bonus only an extraction gets.
  if (rule("xp")) {
    auto notes = FindNotes(*rule("xp"), R({"SCAVENGED 2,450", "DESTROYED ARC 1,100", "SAFE RETURN BONUS 1,500"}));
    CHECK(notes.size() == 3 && notes[0].amount == 2450 && notes[2].amount == 1500);
    // Assists and the total are lines of their own, not the Destroyed ARC line.
    notes = FindNotes(*rule("xp"), R({"DESTROYED ARC (ASSIST) 300", "KNOCKED OUT RAIDER 1,000", "TOTAL XP EARNED 5,200"}));
    CHECK(notes.size() == 3 && notes[0].amount == 300 && notes[1].amount == 1000 && notes[2].amount == 5200);
  }
  if (rule("enemy")) CHECK(FindNotes(*rule("enemy"), R({"DESTROYED ARC (ASSIST) 300"})).empty());
  if (rule("player_kill")) CHECK(FindNotes(*rule("player_kill"), R({"KNOCKED OUT RAIDER +1,000"})).size() == 1);
  // The results screen's Safe Return Bonus says the raid ended in extraction, even when the banner was missed.
  int extractedRules = 0;
  for (const auto& j : arc) {
    if (j.note == "extracted" && !FindNotes(j, R({"SAFE RETURN BONUS 1,500"})).empty()) ++extractedRules;
    if (j.note == "extracted") CHECK(FindNotes(j, R({"TEAMMATE HAS EXTRACTED"})).empty());
  }
  CHECK(extractedRules == 2);

  const auto tarkov = rules.JournalsForGame("escape_from_tarkov");
  int outcomes = 0;
  for (const auto& j : tarkov) {
    if (j.note == "extracted" || j.note == "died" || j.note == "left") outcomes += static_cast<int>(FindNotes(j, R({"KILLED IN ACTION"})).size());
  }
  CHECK(outcomes == 1);
}

}  // namespace

int main(int argc, char** argv) {
  TestParsing();
  TestFindNotes();
  TestTracker();
  TestTimelineNote();
  TestHunt();
  if (argc > 1) TestShipped(argv[1]);
  if (g_failures == 0) std::printf("All journal tests passed\n");
  return g_failures == 0 ? 0 : 1;
}
