#pragma once

#include "detect/GameEvent.h"
#include "detect/TextMatcher.h"

#include <cstdint>
#include <istream>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// Part of the screen, as fractions of its width and height so one rule fits every resolution.
struct ScreenRegion {
  double left = 0;
  double top = 0;
  double width = 1;
  double height = 1;

  bool operator==(const ScreenRegion&) const = default;
};

// Where on a row of text the words must be.
enum class LinePosition {
  Anywhere,  // anywhere in the region (the default)
  Start,     // first on a row: in a killfeed, the player who got the kill
  End,       // last on a row: in a killfeed, the player who died
};

// Stands for the player's own in-game names (player_names in clip_engine.ini) in words and exclude.
inline constexpr std::string_view kPlayerPlaceholder = "{player}";

// One "this text in this part of the screen means this happened" rule from detection.ini.
struct DetectionRule {
  std::string name;  // the section name, for the log
  std::string game;  // GameProfile id
  EventType event = EventType::Highlight;
  ScreenRegion region;
  std::vector<Phrase> words;    // any of these means the event
  std::vector<Phrase> exclude;  // but not while any of these is on screen
  int cooldownMs = 1000;        // ignore repeats closer together than this
  int threshold = 0;            // 1-255 keeps only pixels at least this bright; 0 reads the region as is
  LinePosition position = LinePosition::Anywhere;

  // Phrases as written, kept when they mention {player} so the names can be filled in later.
  std::vector<std::string> playerWords;
  std::vector<std::string> playerExclude;
  bool NeedsPlayer() const { return !playerWords.empty() || !playerExclude.empty(); }
};

// A game added in detection.ini as [game.<id>] with a name and its executables, so games
// Clip Engine doesn't know yet can be recorded and given rules.
struct CustomGame {
  std::string id;
  std::string name;
  std::vector<std::string> executables;  // file names like "VALORANT-Win64-Shipping.exe"
};

struct DetectionRules {
  int sampleMs = 250;  // how often the screen is read
  std::vector<CustomGame> games;
  std::vector<DetectionRule> rules;
  std::vector<std::string> warnings;

  // The game's rules, with {player} filled in from playerNames. Rules that need a name are left
  // out when there are none, and their names added to needingName.
  std::vector<DetectionRule> ForGame(const std::string& game, const std::vector<std::string>& playerNames = {},
                                     std::vector<std::string>* needingName = nullptr) const;
};

// Reads detection.ini. Sections are named [<game id>.<event>] or [<game id>.<event>.<anything>],
// and [game.<id>] adds a game.
DetectionRules ParseDetectionRules(std::istream& in);

// "A | B" as phrases, as the words and exclude settings are read. Phrases mentioning {player} are left out.
std::vector<Phrase> ParsePhrases(const std::string& value);

// How many times the rule's words show in one read of its region, given as rows of tokens.
int CountMatches(const DetectionRule& rule, const std::vector<std::vector<std::string>>& rows);
// The same for one run of tokens, read as a single row.
inline int CountMatches(const DetectionRule& rule, const std::vector<std::string>& tokens) {
  return CountMatches(rule, std::vector<std::vector<std::string>>{tokens});
}

// Turns what one rule sees in successive screen reads into new events. A message that
// stays on screen for several reads counts once; a second line appearing counts again.
class RuleTracker {
 public:
  explicit RuleTracker(const DetectionRule& rule) : rule_(&rule) {}

  // Returns how many new events this read shows.
  int Update(const std::vector<std::vector<std::string>>& rows, int64_t nowMs);
  // The same for a read that is a single row.
  int Update(const std::vector<std::string>& tokens, int64_t nowMs) {
    return Update(std::vector<std::vector<std::string>>{tokens}, nowMs);
  }

  const DetectionRule& Rule() const { return *rule_; }

 private:
  const DetectionRule* rule_;
  int shown_ = 0;
  int lowerReads_ = 0;
  int64_t lastFiredMs_ = INT64_MIN / 2;
};

}  // namespace clip
