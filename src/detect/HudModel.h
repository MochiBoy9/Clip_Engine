#pragma once

#include "detect/GameEvent.h"

#include <array>
#include <cstdint>
#include <istream>
#include <mutex>
#include <optional>
#include <ostream>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace clip {

// What a line of HUD text means, as the universal HUD reader sees it: one of the moments, a
// menu or lobby (while one is up, nothing in that part of the screen counts), or nothing.
enum class HudLabel { None, Menu, Kill, Assist, Death, Revive, MatchStart, MatchEnd, Highlight };

inline constexpr int kHudLabelCount = 9;

std::string_view HudLabelName(HudLabel label);
std::optional<HudLabel> ParseHudLabel(std::string_view name);
std::optional<EventType> HudLabelEvent(HudLabel label);
HudLabel HudLabelFor(EventType event);

// One line of text and what it means, to learn from. Text is as read off the screen, or in
// the built-in examples with {P} for the player's own name, {N} for anyone else's and # for a
// number. zone is the part of the screen it was in (see UniversalZones), or empty for anywhere.
struct HudExample {
  HudLabel label = HudLabel::None;
  std::string zone;
  std::string text;
  std::string game;  // the game it was taught in, or empty for every game
};

// What the model makes of one line.
struct HudGuess {
  HudLabel label = HudLabel::None;
  double confidence = 0;  // 0-1, how sure it is of the label
};

// A small learned model of what HUD text means, for reading the HUD of a game nobody wrote
// rules for. It's a naive Bayes classifier over the words on a line, pairs of words, and where
// on the line the player's own name sits, so "YOU > ENEMY" in a killfeed is a kill and
// "ENEMY > YOU" a death, in any game. Words it doesn't know (other players' names) count as
// "a name", and OCR's misreads of words it does know still count as those words.
//
// It starts from built-in examples covering the words games commonly use, and learns from the
// player's corrections in Check detection (hud_learned.txt), the game's own corrections
// counting most. Training is counting words, so it takes a millisecond, and a line is
// classified in microseconds: it adds nothing measurable to reading the screen.
class HudModel {
 public:
  // Learned examples count this many times as much as built-in ones, those from the game being
  // played even more, so a few corrections outweigh the general examples.
  static constexpr double kLearnedWeight = 3;
  static constexpr double kSameGameWeight = 6;
  // How sure it must be before a line counts.
  static constexpr double kMinConfidence = 0.8;

  // Trains on the built-in examples, then on learned ones; those from `game` count most.
  HudModel(const std::vector<HudExample>& learned = {}, const std::string& game = {},
           std::vector<std::string> playerNames = {});

  HudGuess Classify(const std::vector<std::string>& tokens, std::string_view zone = {}) const;
  std::array<double, kHudLabelCount> Probabilities(const std::vector<std::string>& tokens, std::string_view zone = {}) const;

  // Each row of one read of a region: the label of every row it's sure of, or None. While a
  // menu row shows, every other row is None.
  std::vector<HudGuess> ClassifyRows(const std::vector<std::vector<std::string>>& rows, std::string_view zone) const;

  // The line as the model sees it: {P} for the player's name, {N} for a name, # for a number,
  // and its words as the model knows them. For tests and the log.
  std::vector<std::string> Normalize(const std::vector<std::string>& tokens) const;

  const std::vector<std::string>& PlayerNames() const { return playerNames_; }
  size_t ExampleCount() const { return examples_; }

 private:
  void Train(const std::vector<HudExample>& examples, const std::vector<double>& weights);
  std::vector<std::string> Features(const std::vector<std::string>& normalized, std::string_view zone) const;
  std::string KnownWord(const std::string& token) const;

  std::vector<std::vector<std::string>> nameTokens_;  // each player name, tokenized
  std::vector<std::string> playerNames_;
  std::unordered_set<std::string> vocabulary_;
  std::vector<std::string> fuzzyWords_;  // the vocabulary's longer words, for misread tokens
  std::unordered_map<std::string, std::array<double, kHudLabelCount>> counts_;
  std::array<double, kHudLabelCount> totals_{};
  std::array<double, kHudLabelCount> classExamples_{};
  size_t examples_ = 0;

  // The last few answers: every rule on a region asks about the same rows, once for each way
  // the region was read (ChooseRead) and again for the read it goes by.
  struct Answer {
    std::vector<std::vector<std::string>> rows;
    std::string zone;
    std::vector<HudGuess> guesses;
  };
  static constexpr size_t kRemembered = 8;
  mutable std::mutex lastMutex_;
  mutable std::vector<Answer> last_;
  mutable size_t nextAnswer_ = 0;
};

// The built-in examples the model starts from.
std::vector<HudExample> SeedHudExamples();

// hud_learned.txt: one example per line, "<game>\t<label>\t<zone>\t<text>", with * for every
// game. Lines that don't parse are skipped.
std::vector<HudExample> ReadHudExamples(std::istream& in);
void WriteHudExample(std::ostream& out, const HudExample& example);

// --- Teaching the model from the player's marks of what really happened in a clip ---

// One read of one zone: its rows of tokens, at a time in the clip.
struct ZoneRead {
  int64_t ms = 0;
  std::string zone;
  std::vector<std::vector<std::string>> rows;
};

struct HudMark {
  EventType event = EventType::Kill;
  int64_t ms = 0;
};

// "kill@12.3;death@40.15" as --learn takes the marks.
std::vector<HudMark> ParseHudMarks(std::string_view text);

// What to learn from a clip read zone by zone through its length, given the player's marks of
// every moment in it (as Check detection has them). For each marked moment the model would
// miss, the line that appeared around it (and wasn't on screen a few seconds before) that
// most looks like that kind of moment is taught as one. Each line the model counts with no
// mark of that kind near it is taught as nothing. Each line is taught once.
std::vector<HudExample> TeachFromMarks(const HudModel& model, const std::vector<ZoneRead>& reads,
                                       const std::vector<HudMark>& marks, const std::string& game,
                                       double toleranceSeconds = 2.0);

}  // namespace clip
