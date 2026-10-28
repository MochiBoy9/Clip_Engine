#pragma once

#include "detect/GameEvent.h"
#include "detect/TextMatcher.h"

#include <cstdint>
#include <istream>
#include <string>
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

  std::vector<DetectionRule> ForGame(const std::string& game) const;
};

// Reads detection.ini. Sections are named [<game id>.<event>] or [<game id>.<event>.<anything>],
// and [game.<id>] adds a game.
DetectionRules ParseDetectionRules(std::istream& in);

// "A | B" as phrases, as the words and exclude settings are read.
std::vector<Phrase> ParsePhrases(const std::string& value);

// How many times the rule's words appear in what was read, or 0 when an excluded word is there too.
int CountMatches(const DetectionRule& rule, const std::vector<std::string>& tokens);

// Turns what one rule sees in successive screen reads into new events. A message that
// stays on screen for several reads counts once; a second line appearing counts again.
class RuleTracker {
 public:
  explicit RuleTracker(const DetectionRule& rule) : rule_(&rule) {}

  // Returns how many new events this read shows.
  int Update(const std::vector<std::string>& tokens, int64_t nowMs);

  const DetectionRule& Rule() const { return *rule_; }

 private:
  const DetectionRule* rule_;
  int shown_ = 0;
  int lowerReads_ = 0;
  int64_t lastFiredMs_ = INT64_MIN / 2;
};

}  // namespace clip
