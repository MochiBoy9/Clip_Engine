#include "integrations/GameStateTrackers.h"

#include <algorithm>
#include <cctype>
#include <format>

namespace clip {
namespace {

// Repeats of one counter going up, capped so a bad packet can't flood the clip list.
constexpr int kMaxRepeats = 5;

void Repeat(std::vector<ApiEvent>& out, int times, EventType type, const std::string& text) {
  for (int i = 0; i < std::min(times, kMaxRepeats); ++i) out.push_back({type, text});
}

std::string Lower(std::string s) {
  std::ranges::transform(s, s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  return s;
}

std::string Upper(std::string s) {
  std::ranges::transform(s, s.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
  return s;
}

// "weapon_ak47" -> "AK47", "npc_dota_hero_axe" -> "AXE".
std::string StripPrefix(const std::string& name, std::string_view prefix) {
  return Upper(name.starts_with(prefix) ? name.substr(prefix.size()) : name);
}

}  // namespace

// ---------------------------------------------------------------------- Counter-Strike 2

std::vector<ApiEvent> Cs2Tracker::Update(const Json& state) {
  std::vector<ApiEvent> events;
  const Json& map = state["map"];
  if (map.IsNull()) {
    // Back in the menus.
    map_.clear();
    phase_.clear();
    haveStats_ = false;
    return events;
  }

  const std::string mapName = map["name"].Str();
  if (mapName != map_) {
    map_ = mapName;
    phase_.clear();
    haveStats_ = false;
  }

  const Json& player = state["player"];
  // While dead, CS2 reports whoever is being spectated. Only the local player's numbers count.
  const bool self = !player.IsNull() && player["steamid"].Str() == state["provider"]["steamid"].Str();
  if (self && player.Has("team")) team_ = player["team"].Str();

  const std::string phase = map["phase"].Str();
  if (phase != phase_) {
    const bool firstLook = phase_.empty();
    if (phase == "live" && (phase_ == "warmup" || (firstLook && map["round"].Int() == 0))) {
      events.push_back({EventType::MatchStart, std::format("Match started on {}", map_)});
    } else if (phase == "gameover" && !firstLook) {
      const int ct = map["team_ct"]["score"].Int();
      const int t = map["team_t"]["score"].Int();
      const int mine = team_ == "CT" ? ct : t;
      const int theirs = team_ == "CT" ? t : ct;
      const char* result = team_.empty() ? "Match over" : mine > theirs ? "Victory" : mine < theirs ? "Defeat" : "Draw";
      events.push_back({EventType::MatchEnd, team_.empty() ? std::format("{} {}-{}", result, ct, t)
                                                            : std::format("{} {}-{}", result, mine, theirs)});
    }
    phase_ = phase;
  }

  if (!self || !player.Has("match_stats")) return events;

  Stats now;
  now.kills = player["match_stats"]["kills"].Int();
  now.assists = player["match_stats"]["assists"].Int();
  now.deaths = player["match_stats"]["deaths"].Int();
  now.mvps = player["match_stats"]["mvps"].Int();
  now.roundKills = player["state"]["round_kills"].Int();
  now.roundKillHs = player["state"]["round_killhs"].Int();

  // Counters going down means a new match on the same map; start counting again.
  const bool comparable = haveStats_ && now.kills >= last_.kills && now.deaths >= last_.deaths;
  if (comparable && phase != "warmup") {
    std::string weapon;
    for (const auto& [slot, w] : player["weapons"].Members()) {
      if (w["state"].Str() == "active") weapon = StripPrefix(w["name"].Str(), "weapon_");
    }
    const bool headshot = now.roundKillHs > last_.roundKillHs;
    std::string killText = headshot ? "Headshot kill" : "Kill";
    if (!weapon.empty()) killText += " with " + weapon;
    Repeat(events, now.kills - last_.kills, EventType::Kill, killText);
    Repeat(events, now.assists - last_.assists, EventType::Assist, "Assist");
    Repeat(events, now.deaths - last_.deaths, EventType::Death, "Died");
    if (now.roundKills >= 3 && now.roundKills > last_.roundKills) {
      events.push_back({EventType::Highlight, now.roundKills >= 5 ? "ACE" : std::format("{}K round", now.roundKills)});
    }
    if (now.mvps > last_.mvps) events.push_back({EventType::Highlight, "Round MVP"});
  }
  last_ = now;
  haveStats_ = true;
  return events;
}

// ---------------------------------------------------------------------- Dota 2

std::vector<ApiEvent> Dota2Tracker::Update(const Json& state, double nowSeconds) {
  std::vector<ApiEvent> events;
  const Json& map = state["map"];
  if (map.IsNull()) {
    match_.clear();
    gameState_.clear();
    haveStats_ = false;
    return events;
  }

  const std::string match = map["matchid"].Str();
  if (match != match_) {
    match_ = match;
    gameState_.clear();
    haveStats_ = false;
    multiKill_ = 0;
  }

  const Json& player = state["player"];
  const std::string gameState = map["game_state"].Str();
  if (gameState != gameState_) {
    if (!gameState_.empty() && gameState == "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS") {
      events.push_back({EventType::MatchStart, "The horn sounded"});
    } else if (!gameState_.empty() && gameState == "DOTA_GAMERULES_STATE_POST_GAME") {
      const std::string winner = map["win_team"].Str();
      const std::string team = player["team_name"].Str();
      const char* result = winner.empty() || winner == "none" || team.empty() ? "Match over"
                           : winner == team                                    ? "Victory"
                                                                               : "Defeat";
      events.push_back({EventType::MatchEnd, result});
    }
    gameState_ = gameState;
  }

  // Spectators get every player at once instead of their own; nothing to count there.
  if (!player.Has("kills")) return events;
  const int kills = player["kills"].Int();
  const int deaths = player["deaths"].Int();
  const int assists = player["assists"].Int();

  if (haveStats_ && kills >= kills_ && deaths >= deaths_) {
    const std::string hero = StripPrefix(state["hero"]["name"].Str(), "npc_dota_hero_");
    for (int i = 0; i < std::min(kills - kills_, kMaxRepeats); ++i) {
      events.push_back({EventType::Kill, hero.empty() ? "Kill" : "Kill as " + hero});
      // Dota counts a multi-kill while each kill comes within 18 seconds of the last.
      multiKill_ = nowSeconds - lastKillAt_ <= 18 ? multiKill_ + 1 : 1;
      lastKillAt_ = nowSeconds;
      static constexpr const char* kNames[] = {"", "", "Double Kill", "Triple Kill", "Ultra Kill", "Rampage"};
      if (multiKill_ >= 2) events.push_back({EventType::Highlight, kNames[std::min(multiKill_, 5)]});
    }
    Repeat(events, deaths - deaths_, EventType::Death, "Died");
    Repeat(events, assists - assists_, EventType::Assist, "Assist");
  }
  kills_ = kills;
  deaths_ = deaths;
  assists_ = assists;
  haveStats_ = true;
  return events;
}

// ---------------------------------------------------------------------- League of Legends

bool LeagueTracker::IsMe(const std::string& name) const {
  if (name.empty()) return false;
  const std::string lower = Lower(name);
  if (names_.contains(lower)) return true;
  // Events sometimes carry "Name" where the player list has "Name#TAG", or the other way round.
  const auto hash = lower.find('#');
  return hash != std::string::npos && names_.contains(lower.substr(0, hash));
}

std::vector<ApiEvent> LeagueTracker::Update(const Json& data) {
  std::vector<ApiEvent> events;
  const double gameTime = data["gameData"]["gameTime"].Number();
  const auto& list = data["events"]["Events"].Items();

  // A new game: the clock went back, or the event ids started over.
  int maxId = -1;
  for (const auto& e : list) maxId = std::max(maxId, e["EventID"].Int(-1));
  if (started_ && (gameTime + 5 < lastGameTime_ || maxId < lastEventId_)) {
    started_ = false;
    lastEventId_ = -1;
  }
  lastGameTime_ = gameTime;

  names_.clear();
  const Json& active = data["activePlayer"];
  for (const char* key : {"riotId", "riotIdGameName", "summonerName"}) {
    std::string name = Lower(active[key].Str());
    if (name.empty()) continue;
    if (const auto hash = name.find('#'); hash != std::string::npos) names_.insert(name.substr(0, hash));
    names_.insert(std::move(name));
  }

  auto withAssist = [&](const Json& e) {
    return std::ranges::any_of(e["Assisters"].Items(), [&](const Json& a) { return IsMe(a.Str()); });
  };

  for (const auto& e : list) {
    const int id = e["EventID"].Int(-1);
    if (id <= lastEventId_) continue;
    const double at = e["EventTime"].Number();
    // Joining a game that's already going: don't clip what happened before we were watching.
    if (!started_ && at < gameTime - 5) continue;
    const double ago = std::clamp(gameTime - at, 0.0, 20.0);
    const std::string name = e["EventName"].Str();

    auto add = [&](EventType type, std::string text) { events.push_back({type, std::move(text), ago}); };

    if (name == "GameStart") {
      add(EventType::MatchStart, "Game started");
    } else if (name == "ChampionKill") {
      const std::string killer = e["KillerName"].Str();
      const std::string victim = e["VictimName"].Str();
      if (IsMe(killer)) add(EventType::Kill, "Killed " + victim);
      else if (IsMe(victim)) add(EventType::Death, "Killed by " + killer);
      else if (withAssist(e)) add(EventType::Assist, "Assist on " + victim);
    } else if (name == "Multikill" && IsMe(e["KillerName"].Str())) {
      static constexpr const char* kNames[] = {"", "", "Double Kill", "Triple Kill", "Quadra Kill", "Penta Kill"};
      add(EventType::Highlight, kNames[std::clamp(e["KillStreak"].Int(), 2, 5)]);
    } else if (name == "Ace" && IsMe(e["Acer"].Str())) {
      add(EventType::Highlight, "Ace");
    } else if (name == "FirstBlood" && IsMe(e["Recipient"].Str())) {
      add(EventType::Highlight, "First Blood");
    } else if (name == "DragonKill" || name == "BaronKill" || name == "HeraldKill" || name == "HordeKill" ||
               name == "AtakhanKill") {
      if (IsMe(e["KillerName"].Str()) || withAssist(e)) {
        std::string monster = name.substr(0, name.size() - 4);
        if (name == "DragonKill" && !e["DragonType"].Str().empty()) monster = e["DragonType"].Str() + " Dragon";
        if (name == "HordeKill") monster = "Voidgrubs";
        add(EventType::Highlight, e["Stolen"].Bool() ? monster + " steal" : monster + " taken");
      }
    } else if (name == "GameEnd") {
      const std::string result = e["Result"].Str();
      add(EventType::MatchEnd, result == "Win" ? "Victory" : result == "Lose" ? "Defeat" : "Game over");
    }
  }
  lastEventId_ = std::max(lastEventId_, maxId);
  started_ = true;
  return events;
}

// ---------------------------------------------------------------------- Rocket League

std::vector<ApiEvent> RocketLeagueTracker::Update(const Json& message) {
  std::vector<ApiEvent> events;
  const std::string name = message["Event"].Str();
  // Data comes as an object, but some builds have sent it as a JSON string.
  std::optional<Json> parsed;
  if (message["Data"].IsString()) parsed = Json::Parse(message["Data"].Str());
  const Json& data = parsed ? *parsed : message["Data"];

  if (name == "UpdateState") {
    // The car the camera follows is the local player's, except during goal replays.
    const Json& game = data["Game"];
    if (game["bHasTarget"].Bool() && !game["bReplay"].Bool() && !game["Target"]["Name"].Str().empty()) {
      me_ = game["Target"]["Name"].Str();
      myTeam_ = game["Target"]["TeamNum"].Int(-1);
    }
  } else if (name == "MatchInitialized") {
    events.push_back({EventType::MatchStart, "Kickoff"});
  } else if (name == "GoalScored") {
    if (!me_.empty() && data["Scorer"]["Name"].Str() == me_) events.push_back({EventType::Highlight, "Goal"});
    if (!me_.empty() && data["Assister"]["Name"].Str() == me_) events.push_back({EventType::Assist, "Assist"});
  } else if (name == "StatfeedEvent" && !me_.empty()) {
    const std::string kind = Lower(data["EventName"].Str());
    const std::string label = data["Type"].Str().empty() ? data["EventName"].Str() : data["Type"].Str();
    const std::string main = data["MainTarget"]["Name"].Str();
    const std::string other = data["SecondaryTarget"]["Name"].Str();
    if (kind.find("demolish") != std::string::npos) {
      if (main == me_) events.push_back({EventType::Kill, "Demolished " + other});
      else if (other == me_) events.push_back({EventType::Death, "Demolished by " + main});
    } else if (main == me_ && (kind.find("save") != std::string::npos || kind.find("hattrick") != std::string::npos)) {
      events.push_back({EventType::Highlight, label});
    }
  } else if (name == "MatchEnded") {
    const int winner = data["WinnerTeamNum"].Int(-1);
    events.push_back({EventType::MatchEnd, myTeam_ < 0 || winner < 0 ? "Match over" : winner == myTeam_ ? "Victory" : "Defeat"});
  }
  return events;
}

}  // namespace clip
