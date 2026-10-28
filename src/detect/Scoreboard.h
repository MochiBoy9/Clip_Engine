#pragma once

#include "detect/ScreenRegion.h"
#include "detect/TextMatcher.h"

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace clip {

// A player's totals in the current match as the game shows them, like ("kills", 12),
// ("deaths", 3), ("assists", 4), in a fixed order. Stat names are lower-case ids; the common
// ones are kills, deaths, assists, revives and score.
using Stats = std::vector<std::pair<std::string, int>>;

// The value of one stat, or nothing when it isn't there.
std::optional<int> StatValue(const Stats& stats, std::string_view name);

// "kills 12, deaths 3, assists 4".
std::string DescribeStats(const Stats& stats);

// One reading of the player's totals: from the game's own API, or read off its scoreboard.
struct Scoreboard {
  int64_t time = 0;     // engine clock, like GameEvent::time
  std::string source;   // "api" or "screen"
  Stats stats;
};

// [<game>.scoreboard] in detection.ini: where the game's scoreboard (usually held on Tab, or
// the end-of-match summary) shows the player's totals. Each stat is found under its column
// header on the row with the player's name, so it works whatever order the columns come in and
// whatever other columns the game has. One rule can list several headers for a stat
// ("kills = K | KILLS | E | ELIMS").
struct ScoreboardRule {
  std::string name;  // the section name, for the log
  std::string game;
  // The middle of the screen by default, where most games draw their scoreboard.
  ScreenRegion region{0.05, 0.05, 0.9, 0.9};
  int threshold = 0;
  int sampleMs = 1000;  // the board is big, so it's read less often than a killfeed
  std::vector<std::pair<std::string, std::vector<std::string>>> columns;  // stat, its header words
};

// Reads the player's row of a scoreboard from the words text recognition found in the rule's
// region. Returns the stats it could place, or nothing when the board isn't on screen (no row
// of headers) or the player's name isn't on it.
std::optional<Stats> ReadScoreboard(const ScoreboardRule& rule, const std::vector<WordBox>& words,
                                    const std::vector<std::string>& playerNames);

// Text recognition misreads a digit now and then, so a scoreboard reading only counts once two
// reads in a row agree, and only when it differs from the last one that counted.
class ScoreboardTracker {
 public:
  std::optional<Stats> Update(const std::optional<Stats>& read);

 private:
  std::optional<Stats> pending_;
  std::optional<Stats> reported_;
};

}  // namespace clip
