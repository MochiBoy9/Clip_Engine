#pragma once

#include "detect/GameEvent.h"
#include "detect/Scoreboard.h"

#include <algorithm>
#include <cstdint>
#include <optional>
#include <string_view>

namespace clip {

// Tells where one match ends and the next begins, for the records, the Matches page and the
// Discord status, in every game: most games' HUDs say when a match ends but not when the
// next one starts, and a banner that says "a match starts" may show at every round.
//
//   - A match start opens a new match, unless the match going on is still being played: it has
//     had kills or deaths and the last one was less than kStartGap ago (a round's banner), or
//     it has had none since its own start was seen (the same start shown twice).
//   - A match end closes the match. Moments in the kAfterEnd after it are still that match's
//     (the result screen, a replay); the first one after that opens the next match.
//   - A scoreboard whose totals went down is a new match's scoreboard.
//   - In games where something happens every few minutes, a moment after idleGap with nothing
//     at all is in a new match (the last one's end was missed, and the player queued again).
//
// The first match of a recording opens without a start, as match 0, as it always has.
// Times are in 100 ns units. Each call that opens a match returns when it began.
class MatchTracker {
 public:
  static constexpr int64_t kSecond = 10'000'000;
  static constexpr int64_t kStartGap = 120 * kSecond;
  static constexpr int64_t kAfterEnd = 20 * kSecond;

  // idleGap: 0 for games where minutes can pass without a kill or a death (battle royales,
  // extraction games, MOBAs), so a quiet stretch is never taken for a new match.
  explicit MatchTracker(int64_t idleGap = 0) : idleGap_(idleGap) {}

  // The idle gap that suits a game (see above), by its id.
  static int64_t IdleGapFor(std::string_view gameId) {
    constexpr std::string_view kBusy[] = {"valorant",          "overwatch",    "marvel_rivals", "rainbow_six_siege",
                                          "halo_infinite",     "destiny_2",    "team_fortress_2", "the_finals",
                                          "battlefield_6",     "cs2"};
    for (auto id : kBusy) {
      if (id == gameId) return 300 * kSecond;
    }
    return 0;
  }

  std::optional<int64_t> OnEvent(EventType type, int64_t time) {
    std::optional<int64_t> opened;
    switch (type) {
      case EventType::MatchStart: {
        const bool playing = open_ && !ended_ && (combat_ ? time - lastActivity_ < kStartGap : started_);
        if (!playing) opened = Open(time, true);
        break;
      }
      case EventType::MatchEnd:
        if (!open_) open_ = true;
        if (!ended_) {
          ended_ = true;
          endTime_ = time;
        }
        break;
      default:
        if (!open_) {
          open_ = true;
        } else if (ended_ ? time - endTime_ >= kAfterEnd : idleGap_ > 0 && time - lastActivity_ >= idleGap_) {
          opened = Open(time, false);
        }
        if (type == EventType::Kill || type == EventType::Death || type == EventType::Assist ||
            type == EventType::Revive) {
          combat_ = true;
        }
        break;
    }
    lastActivity_ = std::max(lastActivity_, time);
    return opened;
  }

  std::optional<int64_t> OnScores(const Stats& stats, int64_t time) {
    int total = 0;
    for (const char* name : {"kills", "deaths", "assists"}) total += StatValue(stats, name).value_or(0);
    std::optional<int64_t> opened;
    // Totals only go up in a match; two or more fewer (one could be a misread digit), or none
    // at all after some, is the next match. Right after a match end it's the result screen.
    const bool lower = total + 1 < total_ || (total == 0 && total_ > 0);
    if (open_ && total_ >= 0 && lower && (!ended_ || time - endTime_ >= kAfterEnd)) opened = Open(time, false);
    if (!open_) open_ = true;
    total_ = total;
    return opened;
  }

 private:
  int64_t Open(int64_t time, bool started) {
    open_ = true;
    ended_ = false;
    combat_ = false;
    started_ = started;
    total_ = -1;
    lastActivity_ = time;
    return time;
  }

  int64_t idleGap_;
  bool open_ = false;     // a match is going on (the first one, match 0, opens by itself)
  bool ended_ = false;    // and its end has been seen
  bool started_ = false;  // its start was seen
  bool combat_ = false;   // it has had a kill, death, assist or revive
  int64_t endTime_ = 0;
  int64_t lastActivity_ = INT64_MIN / 2;
  int total_ = -1;  // kills + deaths + assists on its last scoreboard, -1 before one
};

}  // namespace clip
