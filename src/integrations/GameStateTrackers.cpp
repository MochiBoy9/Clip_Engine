#include "integrations/GameStateTrackers.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <map>
#include <optional>
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
    board_.clear();
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

  Counters now;
  now.kills = player["match_stats"]["kills"].Int();
  now.assists = player["match_stats"]["assists"].Int();
  now.deaths = player["match_stats"]["deaths"].Int();
  now.mvps = player["match_stats"]["mvps"].Int();
  now.roundKills = player["state"]["round_kills"].Int();
  now.roundKillHs = player["state"]["round_killhs"].Int();
  board_ = {{"kills", now.kills},
            {"deaths", now.deaths},
            {"assists", now.assists},
            {"mvps", now.mvps},
            {"score", player["match_stats"]["score"].Int()}};

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
    board_.clear();
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
  board_ = {{"kills", kills},
            {"deaths", deaths},
            {"assists", assists},
            {"last_hits", player["last_hits"].Int()},
            {"denies", player["denies"].Int()}};

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

  for (const auto& p : data["allPlayers"].Items()) {
    if (!IsMe(p["riotId"].Str()) && !IsMe(p["riotIdGameName"].Str()) && !IsMe(p["summonerName"].Str())) continue;
    const Json& scores = p["scores"];
    board_ = {{"kills", scores["kills"].Int()},
              {"deaths", scores["deaths"].Int()},
              {"assists", scores["assists"].Int()},
              {"creep_score", scores["creepScore"].Int()},
              {"ward_score", static_cast<int>(scores["wardScore"].Number())}};
    break;
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
    for (const auto& p : data["Players"].Items()) {
      if (me_.empty() || p["Name"].Str() != me_) continue;
      board_ = {{"goals", p["Goals"].Int()},   {"assists", p["Assists"].Int()}, {"saves", p["Saves"].Int()},
                {"shots", p["Shots"].Int()},   {"demos", p["Demos"].Int()},     {"score", p["Score"].Int()}};
    }
  } else if (name == "MatchInitialized") {
    board_.clear();
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

// ---------------------------------------------------------------------- Team Fortress 2

namespace {

// With con_timestamp 1 every line starts "11/04/2026 - 18:41:17: ", and server-style logs
// put "L " before that.
std::string_view StripTimestamp(std::string_view line) {
  std::string_view rest = line.starts_with("L ") ? line.substr(2) : line;
  if (rest.size() >= 23 && std::isdigit(static_cast<unsigned char>(rest[0])) && rest[2] == '/' && rest[5] == '/' &&
      rest.substr(10, 3) == " - " && rest.substr(21, 2) == ": ") {
    return rest.substr(23);
  }
  return line;
}

std::string_view TrimLine(std::string_view s) {
  while (!s.empty() && (s.back() == '\r' || s.back() == '\n' || s.back() == ' ')) s.remove_suffix(1);
  while (!s.empty() && s.front() == ' ') s.remove_prefix(1);
  return s;
}

// The weapon as the console names it ("tf_projectile_rocket", "sniperrifle"), tidied up.
std::string WeaponName(std::string_view weapon) {
  std::string name(weapon);
  for (std::string_view prefix : {"tf_projectile_", "tf_weapon_", "obj_"}) {
    if (name.starts_with(prefix)) name = name.substr(prefix.size());
  }
  std::ranges::replace(name, '_', ' ');
  return name;
}

}  // namespace

Tf2ConsoleTracker::Tf2ConsoleTracker(std::vector<std::string> names) {
  for (auto& name : names) {
    if (!name.empty()) names_.push_back(Lower(std::move(name)));
  }
}

bool Tf2ConsoleTracker::IsMe(std::string_view name) const {
  return !name.empty() && std::ranges::find(names_, Lower(std::string(name))) != names_.end();
}

std::vector<ApiEvent> Tf2ConsoleTracker::Update(std::string_view raw) {
  std::vector<ApiEvent> events;
  const std::string_view line = TrimLine(StripTimestamp(TrimLine(raw)));
  if (line.empty()) return events;

  // Clip Engine's line in autoexec.cfg runs "name", which prints the player's name:
  // "name" = "Mochi" ( def. "unnamed" )
  if (line.starts_with("\"name\" = \"")) {
    const auto rest = line.substr(10);
    const auto end = rest.find('"');
    if (end != std::string_view::npos && end > 0 && rest.substr(0, end) != "unnamed") {
      std::string name = Lower(std::string(rest.substr(0, end)));
      if (std::ranges::find(names_, name) == names_.end()) names_.push_back(std::move(name));
    }
    return events;
  }
  // A new server: the totals start again.
  if (line.starts_with("Connected to ")) {
    kills_ = deaths_ = 0;
    board_.clear();
    return events;
  }
  // Chat is printed as "<name> :  <message>", and a message could look like a kill.
  if (names_.empty() || line.find(" :  ") != std::string_view::npos) return events;

  if (line.ends_with(" suicided.")) {
    if (IsMe(line.substr(0, line.size() - 10))) {
      ++deaths_;
      events.push_back({EventType::Death, "Suicide"});
    }
  } else if (line.ends_with(".") || line.ends_with(". (crit)")) {
    // "<killer> killed <victim> with <weapon>." Names may hold any text, " killed " and " with "
    // included, so the player's own name is looked for at either end first.
    const bool crit = line.ends_with(" (crit)");
    const std::string_view body = line.substr(0, line.size() - (crit ? 8 : 1));
    const auto with = body.rfind(" with ");
    if (with == std::string_view::npos) return events;
    const std::string_view weapon = body.substr(with + 6);
    const std::string_view who = body.substr(0, with);
    const std::string weaponText = WeaponName(weapon) + (crit ? " (crit)" : "");
    bool counted = false;
    for (const auto& name : names_) {
      if (who.size() > name.size() + 8 && Lower(std::string(who.substr(0, name.size()))) == name &&
          who.substr(name.size(), 8) == " killed ") {
        const std::string_view victim = who.substr(name.size() + 8);
        if (IsMe(victim)) break;  // killed themselves with their own rocket: a suicide, printed as such too
        ++kills_;
        events.push_back({EventType::Kill, std::format("Killed {} with {}", victim, weaponText)});
        counted = true;
        break;
      }
      if (who.size() > name.size() + 8 && Lower(std::string(who.substr(who.size() - name.size()))) == name &&
          who.substr(who.size() - name.size() - 8, 8) == " killed ") {
        const std::string_view killer = who.substr(0, who.size() - name.size() - 8);
        ++deaths_;
        events.push_back({EventType::Death, std::format("Killed by {} with {}", killer, weaponText)});
        counted = true;
        break;
      }
    }
    if (!counted) return events;
  } else {
    return events;
  }
  board_ = {{"kills", kills_}, {"deaths", deaths_}};
  return events;
}

// ---------------------------------------------------------------------- Hunt: Showdown 1896

namespace {

// Every <Attr name="..." value="..."/> in the file, in order.
std::vector<std::pair<std::string, std::string>> ReadAttributes(std::string_view xml) {
  std::vector<std::pair<std::string, std::string>> attributes;
  auto quoted = [&](size_t from, std::string_view key, size_t end) -> std::optional<std::string> {
    const auto at = xml.find(key, from);
    if (at == std::string_view::npos || at > end) return std::nullopt;
    const auto open = at + key.size();
    const auto close = xml.find('"', open);
    if (close == std::string_view::npos || close > end) return std::nullopt;
    return std::string(xml.substr(open, close - open));
  };
  for (size_t at = xml.find("<Attr"); at != std::string_view::npos; at = xml.find("<Attr", at + 5)) {
    const auto end = xml.find('>', at);
    if (end == std::string_view::npos) break;
    auto name = quoted(at, "name=\"", end);
    auto value = quoted(at, "value=\"", end);
    if (name && value) attributes.emplace_back(std::move(*name), std::move(*value));
  }
  return attributes;
}

bool EndsWith(std::string_view s, std::string_view end) { return s.size() >= end.size() && s.substr(s.size() - end.size()) == end; }

int ToInt(const std::string& s) {
  int value = 0;
  const auto [ptr, ec] = std::from_chars(s.data(), s.data() + s.size(), value);
  return ec == std::errc() ? value : 0;
}

}  // namespace

std::vector<JournalNote> HuntMissionTracker::Update(std::string_view xml) {
  const auto attributes = ReadAttributes(xml);
  std::map<std::string, std::string> bag;
  std::string fingerprint;
  for (const auto& [name, value] : attributes) {
    if (!name.starts_with("MissionBag") && !name.starts_with("MissionAccolade")) continue;
    bag[name] = value;
    fingerprint += name + '=' + value + ';';
  }
  if (fingerprint.empty() || fingerprint == last_) return {};
  last_ = fingerprint;
  if (std::exchange(first_, false)) return {};

  auto get = [&](const std::string& name) {
    const auto it = bag.find(name);
    return it == bag.end() ? std::string() : it->second;
  };
  auto isTrue = [&](const std::string& name) { return Lower(get(name)) == "true"; };

  // Hunters downed or killed by the player, over every team in the match.
  int hunters = 0;
  for (const auto& [name, value] : bag) {
    if (name.starts_with("MissionBagPlayer_") && (EndsWith(name, "_killedbyme") || EndsWith(name, "_downedbyme"))) {
      hunters += ToInt(value);
    }
  }

  // The accolades: what each one counts, how many, and the XP it gave.
  int monsters = 0, bosses = 0, bounties = 0, xp = 0;
  bool anyAccolade = false;
  for (const auto& [name, value] : bag) {
    if (!name.starts_with("MissionAccoladeEntry_") || !EndsWith(name, "_category")) continue;
    const std::string prefix = name.substr(0, name.size() - std::string_view("_category").size());
    const std::string category = Lower(value);
    const int hits = std::max(1, ToInt(get(prefix + "_hits")));
    anyAccolade = true;
    xp += ToInt(get(prefix + "_xp"));
    if (category.find("boss") != std::string::npos) bosses += hits;
    else if (category.find("monster") != std::string::npos) monsters += hits;
    if (category.find("bounty") != std::string::npos && category.find("extract") != std::string::npos) bounties += hits;
  }
  // Older files list the same things as MissionBagEntry_<n>_category with an _amount.
  if (!anyAccolade) {
    for (const auto& [name, value] : bag) {
      if (!name.starts_with("MissionBagEntry_") || !EndsWith(name, "_category")) continue;
      const std::string prefix = name.substr(0, name.size() - std::string_view("_category").size());
      const std::string category = Lower(value);
      const int amount = std::max(1, ToInt(get(prefix + "_amount")));
      if (category.find("boss") != std::string::npos) bosses += amount;
      else if (category.find("monster") != std::string::npos) monsters += amount;
      if (category.find("bounty") != std::string::npos && category.find("extract") != std::string::npos) bounties += amount;
    }
  }

  std::vector<JournalNote> notes;
  auto add = [&](std::string note, std::string value, int amount, std::string text) {
    notes.push_back({std::move(note), std::move(value), amount, 0, std::move(text)});
  };
  const std::string mode = isTrue("MissionBagIsQuickPlay") ? "Quickplay" : "Bounty Hunt";
  add("deploy", mode, 0, "Match summary in attributes.xml");
  if (monsters > 0) add("enemy", "Monster", monsters, std::format("{} monsters killed", monsters));
  if (bosses > 0) add("enemy", "Boss", bosses, std::format("{} bosses banished", bosses));
  if (hunters > 0) add("player_kill", "Hunters", hunters, std::format("{} hunters downed or killed", hunters));
  if (bounties > 0) add("objective", "Bounty extracted", bounties, std::format("{} bounty tokens extracted", bounties));
  if (xp > 0) add("xp", "", xp, std::format("{} XP", xp));
  if (isTrue("MissionBagIsHunterDead")) add("died", "", 0, "Hunter died");
  else add("extracted", "", 0, "Hunter extracted");
  return notes;
}

}  // namespace clip
