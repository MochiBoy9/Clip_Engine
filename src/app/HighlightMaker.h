#pragma once

#include "detect/GameEvent.h"

#include <algorithm>
#include <cstdint>
#include <format>
#include <string>
#include <vector>

namespace clip {

// Highlights made from the kills already found, for every game: multi-kills (kills each within
// kChainGap of the one before) and kill streaks (every fifth kill without dying). Games that
// announce their own multi-kills (Call of Duty's DOUBLE KILL, Halo's medals) keep theirs: a
// multi-kill the game called out itself isn't made again.
//
// A multi-kill is only known once it's over, kChainGap after its last kill, so it's handed back
// by Poll then, timed at its last kill. A streak is known at once.
class HighlightMaker {
 public:
  static constexpr int64_t kSecond = 10'000'000;
  static constexpr int64_t kChainGap = 6 * kSecond;
  static constexpr int kStreakStep = 5;
  // The rule a made highlight is put down to, as the player shows why a moment was found.
  static constexpr const char* kMultiKillRule = "clip_engine.highlight.multi_kill";
  static constexpr const char* kStreakRule = "clip_engine.highlight.streak";

  // A moment found; returns any highlight it makes right away (a streak).
  std::vector<GameEvent> OnEvent(const GameEvent& event) {
    std::vector<GameEvent> made;
    switch (event.type) {
      case EventType::Kill:
        if (chain_ > 0 && event.time - chainLast_ > kChainGap) Close(made);
        if (chain_ == 0) chainFirst_ = chainLast_ = event.time;
        ++chain_;
        chainLast_ = std::max(chainLast_, event.time);
        if (++streak_ % kStreakStep == 0) {
          made.push_back(Made(kStreakRule, event.time, std::format("{} kill streak", streak_),
                              std::format("{} kills without dying", streak_)));
        }
        break;
      case EventType::Death:
        streak_ = 0;
        break;
      case EventType::Highlight:
        if (!IsMade(event)) lastGameHighlight_ = event.time;
        break;
      default:
        break;
    }
    return made;
  }

  // The multi-kills that are over by now.
  std::vector<GameEvent> Poll(int64_t now) {
    std::vector<GameEvent> made;
    if (chain_ > 0 && now - chainLast_ > kChainGap) Close(made);
    return made;
  }

  // A new match: the multi-kill going on is handed back, and the streak starts over.
  std::vector<GameEvent> Reset() {
    std::vector<GameEvent> made;
    if (chain_ > 0) Close(made);
    streak_ = 0;
    return made;
  }

  static bool IsMade(const GameEvent& event) {
    return !event.witnesses.empty() && event.witnesses.front().rule.starts_with("clip_engine.");
  }

 private:
  static GameEvent Made(const char* rule, int64_t time, std::string text, std::string why) {
    GameEvent event;
    event.type = EventType::Highlight;
    event.time = time;
    event.text = std::move(text);
    Witness witness;
    witness.rule = rule;
    witness.text = std::move(why);
    event.witnesses.push_back(std::move(witness));
    return event;
  }

  void Close(std::vector<GameEvent>& made) {
    const int kills = chain_;
    chain_ = 0;
    if (kills < 2) return;
    // The game called this one out itself.
    if (lastGameHighlight_ >= chainFirst_ - kSecond && lastGameHighlight_ <= chainLast_ + kChainGap) return;
    static constexpr const char* kNames[] = {"", "", "Double kill", "Triple kill", "Quad kill", "Penta kill"};
    std::string name = kills <= 5 ? kNames[kills] : std::format("{} kill multi-kill", kills);
    made.push_back(Made(kMultiKillRule, chainLast_, std::move(name),
                        std::format("{} kills in {:.1f} s", kills,
                                    static_cast<double>(chainLast_ - chainFirst_) / kSecond)));
  }

  int chain_ = 0;
  int64_t chainFirst_ = 0;
  int64_t chainLast_ = 0;
  int streak_ = 0;
  int64_t lastGameHighlight_ = INT64_MIN / 2;
};

}  // namespace clip
