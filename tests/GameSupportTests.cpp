// Tests for the games added in 0.4: the trackers that read CS2, Dota 2, League of Legends and
// Rocket League's own APIs, the stream readers behind them, killfeed rules, and the shipped
// rules for every game that's read off the screen.

#include "app/Timeline.h"
#include "core/Json.h"
#include "detect/DetectionRules.h"
#include "detect/Scoreboard.h"
#include "detect/TextMatcher.h"
#include "integrations/DiscordActivity.h"
#include "integrations/GameStateTrackers.h"
#include "integrations/StreamReaders.h"

#include <cstdio>
#include <format>
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

Json J(const std::string& text) {
  auto parsed = Json::Parse(text);
  CHECK(parsed.has_value());
  return parsed ? *parsed : Json{};
}

int CountType(const std::vector<ApiEvent>& events, EventType type) {
  int n = 0;
  for (const auto& e : events) n += e.type == type;
  return n;
}

void TestJson() {
  const auto j = J(R"({"a": 1, "b": [true, null, "x\"yé"], "c": {"d": -2.5e1}, "s": "7", "t": "True"})");
  CHECK(j["a"].Int() == 1);
  CHECK(j["b"].Items().size() == 3);
  CHECK(j["b"][0].Bool());
  CHECK(j["b"][1].IsNull());
  CHECK(j["b"][2].Str() == "x\"y\xC3\xA9");
  CHECK(j["c"]["d"].Number() == -25);
  CHECK(j["s"].Int() == 7);  // numbers sent as strings
  CHECK(j["t"].Bool());
  CHECK(j["missing"]["deeper"].IsNull());
  CHECK(j["missing"].Int(42) == 42);
  CHECK(j["a"].Str() == "1");
  CHECK(!Json::Parse("{\"a\": }").has_value());
  CHECK(!Json::Parse("[1, 2").has_value());
  CHECK(!Json::Parse("{} trailing").has_value());
  CHECK(J("\"\\ud83d\\ude00\"").Str() == "\xF0\x9F\x98\x80");
}

std::string Cs2(const std::string& phase, int round, const std::string& observed, int kills, int assists, int deaths,
                int mvps, int roundKills, int roundHs, const std::string& extra = "") {
  return std::format(R"({{"provider": {{"steamid": "765"}},
    "map": {{"name": "de_mirage", "phase": "{}", "round": {}, "team_ct": {{"score": 13}}, "team_t": {{"score": 9}}}},
    "player": {{"steamid": "{}", "team": "CT",
      "state": {{"round_kills": {}, "round_killhs": {}}},
      "match_stats": {{"kills": {}, "assists": {}, "deaths": {}, "mvps": {}}},
      "weapons": {{"weapon_0": {{"name": "weapon_knife", "state": "holstered"}},
                   "weapon_1": {{"name": "weapon_ak47", "state": "active"}}}}}}{}}})",
                     phase, round, observed, roundKills, roundHs, kills, assists, deaths, mvps, extra);
}

void TestCs2() {
  Cs2Tracker cs;
  CHECK(cs.Update(J(Cs2("warmup", 0, "765", 0, 0, 0, 0, 0, 0))).empty());
  // Warmup kills don't count.
  CHECK(cs.Update(J(Cs2("warmup", 0, "765", 3, 0, 0, 0, 3, 0))).empty());
  auto e = cs.Update(J(Cs2("live", 0, "765", 0, 0, 0, 0, 0, 0)));
  CHECK(e.size() == 1 && e[0].type == EventType::MatchStart);

  e = cs.Update(J(Cs2("live", 1, "765", 1, 0, 0, 0, 1, 1)));
  CHECK(e.size() == 1 && e[0].type == EventType::Kill && e[0].text == "Headshot kill with AK47");
  e = cs.Update(J(Cs2("live", 1, "765", 3, 1, 0, 0, 3, 1)));
  CHECK(CountType(e, EventType::Kill) == 2);
  CHECK(CountType(e, EventType::Assist) == 1);
  CHECK(CountType(e, EventType::Highlight) == 1 && e.back().text == "3K round");

  // Dead and spectating a teammate: their numbers aren't ours.
  CHECK(cs.Update(J(Cs2("live", 1, "999", 20, 5, 9, 4, 2, 0))).empty());
  e = cs.Update(J(Cs2("live", 2, "765", 3, 1, 1, 1, 0, 0)));
  CHECK(CountType(e, EventType::Death) == 1);
  CHECK(CountType(e, EventType::Highlight) == 1 && e.back().text == "Round MVP");

  e = cs.Update(J(Cs2("gameover", 22, "765", 3, 1, 1, 1, 0, 0)));
  CHECK(e.size() == 1 && e[0].type == EventType::MatchEnd && e[0].text == "Victory 13-9");

  // Back to the menu, then a match joined part way through: no start, and old totals aren't kills.
  CHECK(cs.Update(J(R"({"provider": {"steamid": "765"}})")).empty());
  CHECK(cs.Update(J(Cs2("live", 7, "765", 9, 2, 4, 1, 0, 0))).empty());
  CHECK(cs.Update(J(Cs2("live", 7, "765", 10, 2, 4, 1, 1, 0))).size() == 1);
}

std::string Dota(const std::string& state, int kills, int deaths, int assists, const std::string& win = "none") {
  return std::format(R"({{"map": {{"matchid": "77", "game_state": "{}", "win_team": "{}"}},
    "player": {{"team_name": "radiant", "kills": {}, "deaths": {}, "assists": {}}},
    "hero": {{"name": "npc_dota_hero_axe"}}}})",
                     state, win, kills, deaths, assists);
}

void TestDota2() {
  Dota2Tracker dota;
  CHECK(dota.Update(J(Dota("DOTA_GAMERULES_STATE_PRE_GAME", 0, 0, 0)), 0).empty());
  auto e = dota.Update(J(Dota("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", 0, 0, 0)), 90);
  CHECK(e.size() == 1 && e[0].type == EventType::MatchStart);
  e = dota.Update(J(Dota("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", 1, 0, 0)), 300);
  CHECK(e.size() == 1 && e[0].type == EventType::Kill && e[0].text == "Kill as AXE");
  e = dota.Update(J(Dota("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", 2, 0, 1)), 310);
  CHECK(CountType(e, EventType::Kill) == 1);
  CHECK(CountType(e, EventType::Highlight) == 1);  // within 18 s: a double kill
  CHECK(CountType(e, EventType::Assist) == 1);
  e = dota.Update(J(Dota("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", 3, 1, 1)), 400);
  CHECK(CountType(e, EventType::Highlight) == 0);  // too late for a multi-kill
  CHECK(CountType(e, EventType::Death) == 1);
  e = dota.Update(J(Dota("DOTA_GAMERULES_STATE_POST_GAME", 3, 1, 1, "radiant")), 2000);
  CHECK(e.size() == 1 && e[0].type == EventType::MatchEnd && e[0].text == "Victory");
}

std::string League(double gameTime, const std::string& events) {
  return std::format(R"({{"activePlayer": {{"riotId": "Mochi#NA1", "riotIdGameName": "Mochi", "summonerName": "Mochi#NA1"}},
    "gameData": {{"gameTime": {}}}, "events": {{"Events": [{}]}}}})",
                     gameTime, events);
}

void TestLeague() {
  LeagueTracker lol;
  // Joined late: the events from before aren't clipped.
  const std::string early = R"({"EventID": 0, "EventName": "GameStart", "EventTime": 0.03},
                               {"EventID": 1, "EventName": "ChampionKill", "EventTime": 300, "KillerName": "Mochi", "VictimName": "Bob", "Assisters": []})";
  CHECK(lol.Update(J(League(600, early))).empty());

  const std::string more = early + R"(,
    {"EventID": 2, "EventName": "ChampionKill", "EventTime": 601, "KillerName": "Mochi", "VictimName": "Ann", "Assisters": []},
    {"EventID": 3, "EventName": "Multikill", "EventTime": 601, "KillerName": "Mochi", "KillStreak": 2},
    {"EventID": 4, "EventName": "ChampionKill", "EventTime": 602, "KillerName": "Zed", "VictimName": "Cat", "Assisters": ["mochi"]},
    {"EventID": 5, "EventName": "ChampionKill", "EventTime": 603, "KillerName": "Zed", "VictimName": "Mochi#NA1", "Assisters": []},
    {"EventID": 6, "EventName": "BaronKill", "EventTime": 603, "KillerName": "Mochi", "Stolen": "True", "Assisters": []},
    {"EventID": 7, "EventName": "DragonKill", "EventTime": 603, "KillerName": "Zed", "DragonType": "Fire", "Stolen": "False", "Assisters": []})";
  auto e = lol.Update(J(League(604, more)));
  CHECK(e.size() == 5);
  CHECK(e.size() == 5 && e[0].type == EventType::Kill && e[0].text == "Killed Ann" && e[0].secondsAgo == 3);
  CHECK(e.size() == 5 && e[1].type == EventType::Highlight && e[1].text == "Double Kill");
  CHECK(e.size() == 5 && e[2].type == EventType::Assist);
  CHECK(e.size() == 5 && e[3].type == EventType::Death && e[3].text == "Killed by Zed");
  CHECK(e.size() == 5 && e[4].text == "Baron steal");
  CHECK(lol.Update(J(League(605, more))).empty());  // nothing new

  e = lol.Update(J(League(900, more + R"(, {"EventID": 8, "EventName": "GameEnd", "EventTime": 900, "Result": "Win"})")));
  CHECK(e.size() == 1 && e[0].type == EventType::MatchEnd && e[0].text == "Victory");

  // The next game starts over at id 0.
  e = lol.Update(J(League(1, R"({"EventID": 0, "EventName": "GameStart", "EventTime": 0.03})")));
  CHECK(e.size() == 1 && e[0].type == EventType::MatchStart);
}

void TestRocketLeague() {
  RocketLeagueTracker rl;
  CHECK(rl.Update(J(R"({"Event": "MatchInitialized", "Data": {"MatchGuid": "x"}})")).size() == 1);
  // Before we know which car is ours, nothing is credited to us.
  CHECK(rl.Update(J(R"({"Event": "GoalScored", "Data": {"Scorer": {"Name": "Mochi", "TeamNum": 0}}})")).empty());
  rl.Update(J(R"({"Event": "UpdateState", "Data": {"Game": {"bHasTarget": true, "bReplay": false,
                  "Target": {"Name": "Mochi", "TeamNum": 0}}}})"));
  // During a goal replay the camera follows someone else; that isn't us.
  rl.Update(J(R"({"Event": "UpdateState", "Data": {"Game": {"bHasTarget": true, "bReplay": true,
                  "Target": {"Name": "Other", "TeamNum": 1}}}})"));
  auto e = rl.Update(J(R"({"Event": "GoalScored", "Data": {"Scorer": {"Name": "Mochi"}, "Assister": {"Name": "Pal"}}})"));
  CHECK(e.size() == 1 && e[0].type == EventType::Highlight && e[0].text == "Goal");
  e = rl.Update(J(R"({"Event": "GoalScored", "Data": {"Scorer": {"Name": "Pal"}, "Assister": {"Name": "Mochi"}}})"));
  CHECK(e.size() == 1 && e[0].type == EventType::Assist);
  // Data sent as a string.
  e = rl.Update(J(R"({"Event": "StatfeedEvent", "Data": "{\"EventName\": \"Demolish\", \"Type\": \"Demolition\", \"MainTarget\": {\"Name\": \"Mochi\"}, \"SecondaryTarget\": {\"Name\": \"Foe\"}}"})"));
  CHECK(e.size() == 1 && e[0].type == EventType::Kill && e[0].text == "Demolished Foe");
  e = rl.Update(J(R"({"Event": "StatfeedEvent", "Data": {"EventName": "Demolish", "MainTarget": {"Name": "Foe"}, "SecondaryTarget": {"Name": "Mochi"}}})"));
  CHECK(e.size() == 1 && e[0].type == EventType::Death);
  e = rl.Update(J(R"({"Event": "StatfeedEvent", "Data": {"EventName": "EpicSave", "Type": "Epic Save", "MainTarget": {"Name": "Mochi"}}})"));
  CHECK(e.size() == 1 && e[0].text == "Epic Save");
  CHECK(rl.Update(J(R"({"Event": "StatfeedEvent", "Data": {"EventName": "Shot", "MainTarget": {"Name": "Mochi"}}})")).empty());
  e = rl.Update(J(R"({"Event": "MatchEnded", "Data": {"WinnerTeamNum": 1}})"));
  CHECK(e.size() == 1 && e[0].type == EventType::MatchEnd && e[0].text == "Defeat");
}

void TestJsonObjectStream() {
  JsonObjectStream stream;
  stream.Feed(R"({"a": "}{"}{"b")");
  auto first = stream.Next();
  CHECK(first.has_value() && *first == R"({"a": "}{"})");
  CHECK(!stream.Next().has_value());
  stream.Feed(R"(: {"c": 1}}
{"d": "\"}"})");
  auto second = stream.Next();
  CHECK(second.has_value() && *second == R"({"b": {"c": 1}})");
  auto third = stream.Next();
  CHECK(third.has_value() && *third == R"({"d": "\"}"})");
  CHECK(!stream.Next().has_value());
}

void TestWebSocketReader() {
  WebSocketReader reader;
  // "Hel" + "lo" as a fragmented text message, then a ping and a close.
  const std::string frames = std::string("\x01\x03Hel", 5) + std::string("\x80\x02lo", 4) +
                             std::string("\x89\x01!", 3) + std::string("\x88\x00", 2);
  reader.Feed(frames.substr(0, 3));
  CHECK(!reader.Next().has_value());  // not a whole frame yet
  reader.Feed(frames.substr(3));
  auto text = reader.Next();
  CHECK(text && text->kind == WebSocketReader::Kind::Text && text->payload == "Hello");
  auto ping = reader.Next();
  CHECK(ping && ping->kind == WebSocketReader::Kind::Ping && ping->payload == "!");
  auto close = reader.Next();
  CHECK(close && close->kind == WebSocketReader::Kind::Close);

  // A 200-byte message uses the 16-bit length.
  WebSocketReader big;
  big.Feed(std::string("\x81\x7E\x00\xC8", 4) + std::string(200, 'x'));
  auto message = big.Next();
  CHECK(message && message->payload.size() == 200);

  // Our pong is masked, and masking it again gives the payload back.
  const auto pong = WebSocketReader::ClientFrame(0xA, "abc", 0x01020304);
  CHECK(pong.size() == 2 + 4 + 3);
  CHECK(static_cast<unsigned char>(pong[0]) == 0x8A && static_cast<unsigned char>(pong[1]) == 0x83);
  CHECK((pong[6] ^ 0x01) == 'a' && (pong[8] ^ 0x03) == 'c');
}

DetectionRules Parse(const std::string& text) {
  std::istringstream in(text);
  return ParseDetectionRules(in);
}

void TestRows() {
  // One killfeed line read as two pieces either side of the weapon icon, plus a second line.
  const auto rows = GroupIntoRows({{"Victim1", 300, 12, 80, 18},
                                   {"xX_Mochi", 100, 10, 90, 20},
                                   {"Enemy", 110, 42, 60, 18},
                                   {"Mochi", 280, 41, 60, 20}});
  CHECK(rows.size() == 2);
  CHECK(rows.size() == 2 && rows[0] == (std::vector<std::string>{"XX", "MOCHI", "VICTIM1"}));
  CHECK(rows.size() == 2 && rows[1] == (std::vector<std::string>{"ENEMY", "MOCHI"}));

  CHECK(PhraseAtStart(Tokenize("Mochi Bob"), MakePhrase("Mochi")));
  CHECK(PhraseAtStart(Tokenize("@ Mochi Bob"), MakePhrase("Mochi")));          // symbols aren't tokens at all
  CHECK(PhraseAtStart(Tokenize("Q Mochi Bob"), MakePhrase("Mochi")));          // an icon read as a letter
  CHECK(!PhraseAtStart(Tokenize("Bob Mochi"), MakePhrase("Mochi")));
  CHECK(!PhraseAtStart(Tokenize("Bobby Mochi"), MakePhrase("Mochi")));  // only a short stray token is skipped
  CHECK(PhraseAtEnd(Tokenize("Bob Mochi"), MakePhrase("Mochi")));
  CHECK(PhraseAtEnd(Tokenize("Bob Mochi ii"), MakePhrase("Mochi")));
  CHECK(!PhraseAtEnd(Tokenize("Mochi Bob"), MakePhrase("Mochi")));
}

void TestKillfeedRules() {
  const auto rules = Parse(R"(
[valorant.kill]
region = 0.66, 0.07, 0.33, 0.26
words = {player}
position = start
cooldown_ms = 0

[valorant.death]
region = 0.66, 0.07, 0.33, 0.26
words = {Player}
position = end

[valorant.match_end]
words = VICTORY

[valorant.highlight]
words = ACE
position = middle
)");
  CHECK(rules.rules.size() == 4);
  CHECK(rules.warnings.size() == 1);  // the bad position
  CHECK(rules.rules[0].NeedsPlayer() && rules.rules[0].position == LinePosition::Start);
  CHECK(rules.rules[1].NeedsPlayer());

  // Without a name the killfeed rules can't work; they're left out and reported.
  std::vector<std::string> needing;
  CHECK(rules.ForGame("valorant", {}, &needing).size() == 2);
  CHECK(needing.size() == 2);

  const auto filled = rules.ForGame("valorant", {"xX_Mochi", "Mochi Alt"});
  CHECK(filled.size() == 4);
  CHECK(filled.size() == 4 && filled[0].words.size() == 2 && filled[0].words[0].words.size() == 2);

  RuleTracker kills(filled[0]);
  RuleTracker deaths(filled[1]);
  const std::vector<std::vector<std::string>> one = {Tokenize("xX_Mochi Bob"), Tokenize("Pal Foe")};
  const std::vector<std::vector<std::string>> two = {Tokenize("xX_Mochi Bob"), Tokenize("Pal Foe"), Tokenize("xX Mochi Cat")};
  const std::vector<std::vector<std::string>> died = {Tokenize("Pal Foe"), Tokenize("Zed xX Mochi")};
  CHECK(kills.Update(one, 0) == 1);
  CHECK(deaths.Update(one, 0) == 0);
  CHECK(kills.Update(one, 250) == 0);  // the same line still showing
  CHECK(kills.Update(two, 500) == 1);  // a second kill
  CHECK(kills.Update(died, 750) == 0);
  CHECK(deaths.Update(died, 750) == 1);
}

void TestShippedRules(const char* path) {
  std::ifstream in(path);
  CHECK(in.good());
  const auto rules = ParseDetectionRules(in);
  for (const auto& w : rules.warnings) std::printf("detection.ini %s\n", w.c_str());
  CHECK(rules.warnings.empty());
  for (const char* game : {"valorant", "fortnite", "apex_legends", "pubg", "marvel_rivals", "rainbow_six_siege",
                           "call_of_duty", "arc_raiders", "delta_force", "deadlock", "halo_infinite", "destiny_2",
                           "team_fortress_2"}) {
    bool kill = false, death = false, end = false;
    for (const auto& r : rules.ForGame(game, {"Mochi"})) {
      kill |= r.event == EventType::Kill;
      death |= r.event == EventType::Death;
      end |= r.event == EventType::MatchEnd;
    }
    if (!(kill && death && end)) std::printf("%s is missing a kill, death or match end rule\n", game);
    CHECK(kill && death && end);
  }
  // Tarkov shows no kills, but its deaths and raid ends are found.
  CHECK(rules.ForGame("escape_from_tarkov").size() == 2);
  // Every scoreboard rule reads kills, and only games that have a scoreboard get one.
  for (const auto& board : rules.scoreboards) {
    CHECK(board.columns.size() >= 2 && board.columns[0].first == "kills");
  }
  // Games read through their own APIs have no screen rules.
  for (const char* game : {"cs2", "dota2", "league_of_legends", "rocket_league"}) CHECK(rules.ForGame(game).empty());
}


void TestTrackerBoards() {
  Cs2Tracker cs;
  cs.Update(J(Cs2("live", 3, "765", 7, 2, 4, 1, 0, 0)));
  CHECK(StatValue(cs.Board(), "kills") == 7);
  CHECK(StatValue(cs.Board(), "deaths") == 4);
  CHECK(StatValue(cs.Board(), "assists") == 2);
  // Spectating someone else leaves the player's own totals alone.
  cs.Update(J(Cs2("live", 3, "999", 20, 5, 9, 4, 0, 0)));
  CHECK(StatValue(cs.Board(), "kills") == 7);
  cs.Update(J(R"({"provider": {"steamid": "765"}})"));
  CHECK(cs.Board().empty());

  Dota2Tracker dota;
  dota.Update(J(Dota("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", 5, 2, 9)), 0);
  CHECK(StatValue(dota.Board(), "kills") == 5 && StatValue(dota.Board(), "assists") == 9);

  LeagueTracker league;
  league.Update(J(R"({"gameData": {"gameTime": 600}, "activePlayer": {"riotId": "Mochi#EUW"},
    "allPlayers": [{"riotId": "Other#NA", "scores": {"kills": 9}},
                   {"riotId": "Mochi#EUW", "scores": {"kills": 4, "deaths": 1, "assists": 7, "creepScore": 88, "wardScore": 12.6}}],
    "events": {"Events": []}})"));
  CHECK(StatValue(league.Board(), "kills") == 4);
  CHECK(StatValue(league.Board(), "creep_score") == 88);

  RocketLeagueTracker rl;
  rl.Update(J(R"({"Event": "UpdateState", "Data": {"Game": {"bHasTarget": true, "bReplay": false,
    "Target": {"Name": "Mochi", "TeamNum": 0}},
    "Players": [{"Name": "Mochi", "Goals": 2, "Assists": 1, "Saves": 3, "Shots": 5, "Demos": 1, "Score": 420}]}})"));
  CHECK(StatValue(rl.Board(), "goals") == 2 && StatValue(rl.Board(), "score") == 420);
}

WordBox W(const std::string& text, double x, double y, double width = 20, double height = 16) {
  return {text, x, y, width, height};
}

void TestScoreboardRules() {
  std::istringstream ini(R"(
[valorant.scoreboard]
region = 0.1, 0.1, 0.8, 0.8
kills = K | KILLS
deaths = D
assists = A
score = ACS
sample_ms = 1500

[valorant.scoreboard.empty]
region = 0.1, 0.1, 0.8, 0.8
)");
  const auto rules = ParseDetectionRules(ini);
  CHECK(rules.scoreboards.size() == 1);
  CHECK(rules.warnings.size() == 1);  // the one without columns
  const auto boards = rules.ScoreboardsForGame("valorant");
  CHECK(boards.size() == 1 && boards[0].columns.size() == 4 && boards[0].sampleMs == 1500);
  CHECK(boards.size() == 1 && boards[0].region.left == 0.1);
  CHECK(rules.ScoreboardsForGame("fortnite").empty());
}

void TestReadScoreboard() {
  ScoreboardRule rule;
  rule.columns = {{"kills", {"K"}}, {"deaths", {"D"}}, {"assists", {"A"}}, {"score", {"ACS"}}};

  // Headers out of the rule's order, an extra column, a teammate's row above the player's, and
  // numbers a little off-center under their headers.
  const std::vector<WordBox> board = {
      W("MATCH", 10, 5, 60), W("ACS", 300, 40, 40), W("K", 400, 40, 12), W("D", 450, 40, 12), W("A", 500, 40, 12),
      W("ECON", 560, 40, 40),
      W("Teammate", 60, 80, 90), W("250", 302, 80, 36), W("20", 398, 80, 20), W("11", 448, 80, 20), W("3", 502, 80, 10),
      W("Mochi", 60, 120, 60), W("312", 302, 120, 36), W("14", 401, 120, 20), W("6", 452, 120, 10), W("O", 503, 120, 10),
      W("88", 565, 120, 20),
  };
  const auto stats = ReadScoreboard(rule, board, {"Mochi"});
  CHECK(stats.has_value());
  if (stats) {
    CHECK(StatValue(*stats, "kills") == 14);
    CHECK(StatValue(*stats, "deaths") == 6);
    CHECK(StatValue(*stats, "assists") == 0);  // "O" read for 0
    CHECK(StatValue(*stats, "score") == 312);
    CHECK(DescribeStats(*stats) == "kills 14, deaths 6, assists 0, score 312");
  }

  // Not the player's board, or no board at all.
  CHECK(!ReadScoreboard(rule, board, {"Somebody"}).has_value());
  CHECK(!ReadScoreboard(rule, {W("A", 10, 10), W("4", 30, 30)}, {"Mochi"}).has_value());
  // A row of digits isn't a header row ("4" looks like "A").
  CHECK(!ReadScoreboard(rule, {W("Mochi", 10, 10, 60), W("4", 400, 10), W("4", 450, 10)}, {"Mochi"}).has_value());

  ScoreboardTracker tracker;
  const Stats a = {{"kills", 3}};
  const Stats b = {{"kills", 4}};
  CHECK(!tracker.Update(a).has_value());  // once could be a misread
  CHECK(tracker.Update(a) == a);
  CHECK(!tracker.Update(a).has_value());  // already reported
  CHECK(!tracker.Update(b).has_value());
  CHECK(!tracker.Update(std::nullopt).has_value());
  CHECK(!tracker.Update(b).has_value());  // the board closed in between
  CHECK(tracker.Update(b) == b);
}

void TestTimeline() {
  CHECK(TimelineHeader("2026-10-27T20:41:05", "Valorant") ==
        R"({"session": "2026-10-27T20:41:05", "game": "Valorant", "version": 1})");
  CHECK(TimelineEvent(12.345, 1, {EventType::Kill, 0, "Mochi \"ELIMINATED\" Bot"}) ==
        R"({"t": 12.35, "match": 1, "type": "kill", "text": "Mochi \"ELIMINATED\" Bot"})");
  CHECK(TimelineScores(40, 2, {0, "api", {{"kills", 12}, {"deaths", 3}}}) ==
        R"({"t": 40.00, "match": 2, "source": "api", "stats": {"kills": 12, "deaths": 3}})");
  CHECK(TimelineFileName("2026-10-27T20:41:05", "A/B") == std::filesystem::path("2026-10-27T20-41-05 A-B.jsonl"));
  // Every line reads back as JSON.
  CHECK(Json::Parse(TimelineEvent(1, 0, {EventType::Death, 0, "tab\there"})).has_value());
}

void TestDiscordActivity() {
  PresenceState state;
  state.game = "Valorant";
  state.startUnix = 1793000000;
  CHECK(PresenceDetails(state) == "Valorant");
  CHECK(PresenceLine(state) == "Recording with Clip Engine");
  state.kills = 12;
  state.deaths = 1;
  state.clips = 3;
  CHECK(PresenceLine(state) == "12 kills \xC2\xB7 1 death \xC2\xB7 3 clips");
  state.assists = 2;
  state.longRecording = true;
  CHECK(PresenceLine(state) == "Long recording \xC2\xB7 12 kills \xC2\xB7 1 death \xC2\xB7 2 assists \xC2\xB7 3 clips");
  state.away = true;
  CHECK(PresenceLine(state).starts_with("Tabbed out \xC2\xB7 12 kills"));

  // Discord wants 2 to 128 characters in each line.
  state.game = "X";
  CHECK(PresenceDetails(state).size() == 2);
  state.game = std::string(200, 'a');
  CHECK(PresenceDetails(state).size() == 128);

  state.game = "Halo \"Infinite\"";
  const auto activity = J(PresenceActivityJson(state, "0.7.0", true));
  CHECK(activity["details"].Str() == "Halo \"Infinite\"");
  CHECK(activity["status_display_type"].Int() == 2);
  CHECK(activity["timestamps"]["start"].Number() == 1793000000);
  CHECK(activity["assets"]["large_text"].Str() == "Clip Engine 0.7.0");
  CHECK(!J(PresenceActivityJson(state, "0.7.0", false)).Has("status_display_type"));

  const auto command = J(SetActivityCommand(42, PresenceActivityJson(state, "0.7.0", true), "7"));
  CHECK(command["cmd"].Str() == "SET_ACTIVITY");
  CHECK(command["args"]["pid"].Int() == 42);
  CHECK(command["nonce"].Str() == "7");
  CHECK(command["args"]["activity"]["state"].Str() == PresenceLine(state));
  CHECK(J(SetActivityCommand(42, "", "8"))["args"]["activity"].IsNull());

  const std::string frame = DiscordFrame(1, "{}");
  CHECK(frame == std::string("\x01\0\0\0\x02\0\0\0{}", 10));
}

}  // namespace

int main(int argc, char** argv) {
  TestJson();
  TestCs2();
  TestDota2();
  TestLeague();
  TestRocketLeague();
  TestJsonObjectStream();
  TestWebSocketReader();
  TestRows();
  TestKillfeedRules();
  TestTrackerBoards();
  TestScoreboardRules();
  TestReadScoreboard();
  TestTimeline();
  TestDiscordActivity();
  if (argc > 1) TestShippedRules(argv[1]);
  if (g_failures == 0) std::printf("All game support tests passed\n");
  return g_failures == 0 ? 0 : 1;
}
