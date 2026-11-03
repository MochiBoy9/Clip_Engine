#pragma once

#include "media/ClipMetadata.h"

#include <algorithm>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// Squad sync: when a squadmate playing the same game gets a kill, the desktop app asks the
// engine for this player's own view of that moment, by wall-clock time, so every teammate's
// clip of one moment lines up. The desktop app does all the networking; the engine only cuts
// clips from its replay buffer. None of this needs Windows, so it is tested on its own.

// The same instant on the engine's clock (QPC, 100 ns units) and on the wall clock (Unix ms).
struct ClockPair {
  int64_t hns = 0;
  int64_t unixMs = 0;
};

// Converts between the two clocks around a pair read at (about) the same time. QPC and the
// wall clock drift apart by well under a millisecond a minute, and the pair is read fresh each
// time, so this stays exact enough to line clips up to the frame.
inline int64_t HnsAtUnixMs(const ClockPair& now, int64_t unixMs) { return now.hns + (unixMs - now.unixMs) * 10'000; }
inline int64_t UnixMsAtHns(const ClockPair& now, int64_t hns) {
  const int64_t delta = hns - now.hns;
  // Round toward the nearest millisecond either side of zero.
  return now.unixMs + (delta >= 0 ? (delta + 5'000) / 10'000 : -((-delta + 5'000) / 10'000));
}

// "squad-save <moment> <start unix ms> <end unix ms> <game name>": save this player's view of a
// squad moment. The moment id names the clip's group in the squad library.
struct SquadSaveRequest {
  std::string moment;
  int64_t startUnixMs = 0;
  int64_t endUnixMs = 0;
  std::string game;  // only saved while this game is being recorded; empty for any game
};

// "squad-presence <playing> <max> <party id> [<join url>]": the squad as the Discord status
// shows it. "squad-presence 0" takes it off.
struct SquadPresence {
  int playing = 0;
  int max = 0;
  std::string partyId;
  std::string joinUrl;  // https only
};

std::optional<SquadPresence> ParseSquadPresence(std::string_view line);

// Moment ids come from the network, so they are held to a safe shape: 1 to 64 letters,
// digits, '-' or '_'.
bool IsSquadMomentId(std::string_view id);

// Reads one command line from the desktop app. Nothing for anything else, a malformed line or
// a window that is backwards or longer than two minutes.
std::optional<SquadSaveRequest> ParseSquadSave(std::string_view line);

// Squad windows waiting to be saved: a teammate's kill usually arrives before the part after
// it has been recorded, so each window waits until its end has passed. Repeats of a moment id
// are dropped, and a window that overlaps one already waiting for the same moment is merged.
class SquadSaveQueue {
 public:
  struct Window {
    std::string moment;
    int64_t start = 0;  // engine time
    int64_t end = 0;
  };

  // False when the moment was already queued or saved.
  bool Add(Window window);

  // Windows whose end has passed by now.
  std::vector<Window> TakeReady(int64_t now);
  std::vector<Window> TakeAll();
  bool Empty() const { return pending_.empty(); }

 private:
  std::vector<Window> pending_;
  std::vector<std::string> seen_;  // the last few moment ids, so a repeat isn't saved twice
};

// One part of a squad highlight: seconds [from, to] of a clip. Given on the command line as
// "<clip>|<from>|<to>" (Windows paths can't hold '|').
struct MontagePart {
  std::string path;  // UTF-8
  double from = 0;
  double to = 0;
};

std::optional<MontagePart> ParseMontagePart(std::string_view spec);

// The highlight's metadata: each part's moments moved to where the part lands in the
// highlight, and its length. parts and their metadata go in order; metadata may be missing.
ClipMetadata MontageMetadata(const std::vector<MontagePart>& parts, const std::vector<std::optional<ClipMetadata>>& metadata);

}  // namespace clip
