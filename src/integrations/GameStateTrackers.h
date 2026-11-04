#pragma once

#include "core/Json.h"
#include "detect/GameEvent.h"
#include "detect/Scoreboard.h"

#include <set>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// A moment reported by a game's own API.
struct ApiEvent {
  EventType type = EventType::Highlight;
  std::string text;
  double secondsAgo = 0;  // how long before it was reported the moment happened
};

// Each tracker turns what one game reports into moments, and keeps the player's totals for the
// match as the game reports them in Board(). They hold no sockets or threads, so they can be
// tested anywhere; GameIntegration feeds them.

// Counter-Strike 2 Game State Integration. CS2 posts the whole state on every change; kills,
// assists, deaths and MVPs show up as counters going up.
class Cs2Tracker {
 public:
  std::vector<ApiEvent> Update(const Json& state);

  const Stats& Board() const { return board_; }

 private:
  struct Counters {
    int kills = 0, assists = 0, deaths = 0, mvps = 0, roundKills = 0, roundKillHs = 0;
  };
  std::string map_;
  std::string phase_;
  std::string team_;
  bool haveStats_ = false;
  Counters last_;
  Stats board_;
};

// Dota 2 Game State Integration.
class Dota2Tracker {
 public:
  // nowSeconds: any steadily increasing clock, for telling multi-kills apart.
  std::vector<ApiEvent> Update(const Json& state, double nowSeconds);

  const Stats& Board() const { return board_; }

 private:
  std::string match_;
  std::string gameState_;
  bool haveStats_ = false;
  int kills_ = 0, deaths_ = 0, assists_ = 0;
  int multiKill_ = 0;
  double lastKillAt_ = -1000;
  Stats board_;
};

// League of Legends Live Client Data API (/liveclientdata/allgamedata), polled while a game runs.
class LeagueTracker {
 public:
  std::vector<ApiEvent> Update(const Json& allGameData);

  const Stats& Board() const { return board_; }

 private:
  bool IsMe(const std::string& name) const;

  std::set<std::string> names_;  // the active player's names, lower case
  bool started_ = false;
  int lastEventId_ = -1;
  double lastGameTime_ = 0;
  Stats board_;
};

// Rocket League Stats API: one message per call, {"Event": "...", "Data": {...}}.
class RocketLeagueTracker {
 public:
  std::vector<ApiEvent> Update(const Json& message);

  const Stats& Board() const { return board_; }

 private:
  std::string me_;
  int myTeam_ = -1;
  Stats board_;
};

// Team Fortress 2's console log (con_logfile, or -condebug's console.log), one line at a time.
// The game prints every killfeed line to its console as "<killer> killed <victim> with
// <weapon>." with " (crit)" after a critical hit, and "<player> suicided." for a suicide, so
// kills and deaths are exact, whatever the HUD looks like.
class Tf2ConsoleTracker {
 public:
  // names: the player's in-game names (player_names in the settings). The name the game itself
  // prints when the log starts ("name" = "...") is added to them.
  explicit Tf2ConsoleTracker(std::vector<std::string> names = {});

  std::vector<ApiEvent> Update(std::string_view line);

  const Stats& Board() const { return board_; }
  // Whether a name to look for is known, from the settings or the game.
  bool KnowsName() const { return !names_.empty(); }

 private:
  bool IsMe(std::string_view name) const;

  std::vector<std::string> names_;  // lower case
  int kills_ = 0, deaths_ = 0;
  Stats board_;
};

}  // namespace clip
