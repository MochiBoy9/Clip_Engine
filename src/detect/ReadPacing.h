#pragma once

#include "detect/Scoreboard.h"

#include <algorithm>
#include <cstdint>

namespace clip {

// When a game's scoreboard is worth reading. It covers most of the screen, so reading it is the
// heaviest thing detection does, and most of a match it isn't up: it's read while its key is
// held (Tab, as most games use), and for a while after a match ends, when games show it by
// themselves. While it's held it's read every kHeldMs, so two reads agree (ScoreboardTracker)
// within a second of Tab going down.
class ScoreboardGate {
 public:
  static constexpr int64_t kAfterMatchMs = 30000;
  static constexpr int kHeldMs = 500;

  void MatchEnded(int64_t nowMs) { endedAt_ = nowMs; }

  bool Wanted(const ScoreboardRule& board, bool keyDown, int64_t nowMs) const {
    if (board.read == ScoreboardRead::Always) return true;
    if (nowMs - endedAt_ >= 0 && nowMs - endedAt_ < kAfterMatchMs) return true;
    return board.read == ScoreboardRead::WhileHeld && keyDown;
  }

  // How often to read it now: often while held, at its own pace otherwise.
  static int IntervalMs(const ScoreboardRule& board, bool keyDown) {
    return keyDown && board.read == ScoreboardRead::WhileHeld ? kHeldMs : board.sampleMs;
  }

 private:
  int64_t endedAt_ = INT64_MIN / 2;
};

// Quick popups and killfeeds are looked for half as often after calmMs with nothing seen (in
// menus, walking about, waiting to respawn), and as often as ever again the moment anything is:
// a kill, a death, a sighting a rule made, a sound. Killfeed lines stay up for seconds and
// popups for a second or more, so the first one after a quiet spell is still caught, a look
// later at most.
class CalmPacer {
 public:
  explicit CalmPacer(int calmMs) : calmMs_(calmMs) {}

  // Something was seen: a moment, or a sighting that may become one. Moments can be counted a
  // little after they're seen, so an earlier time doesn't move it back.
  void Saw(int64_t nowMs) { lastSeenMs_ = std::max(lastSeenMs_, nowMs); }

  bool Calm(int64_t nowMs) const { return calmMs_ > 0 && nowMs - lastSeenMs_ >= calmMs_; }

  // How often to look at a region that asks for every intervalMs. Banners and scoreboards,
  // looked at every second or less often, keep their pace.
  int IntervalMs(int intervalMs, int64_t nowMs) const {
    return Calm(nowMs) && intervalMs < 1000 ? intervalMs * 2 : intervalMs;
  }

 private:
  int calmMs_;
  int64_t lastSeenMs_ = 0;
};

}  // namespace clip
