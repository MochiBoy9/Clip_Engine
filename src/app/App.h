#pragma once

#include "app/AutoClipPlanner.h"
#include "app/RecordingSession.h"
#include "app/Timeline.h"
#include "core/Config.h"
#include "detect/DetectionRules.h"
#include "game/GameDetector.h"
#include "integrations/GameIntegration.h"
#include "media/LongRecorder.h"

#include <filesystem>
#include <future>
#include <memory>
#include <optional>
#include <vector>

namespace clip {

// Watches for supported games, records while one is running, saves clips on the hotkey and
// around the moments the screen reader detects, runs long recordings on their own hotkey, and
// keeps a timeline of every moment and scoreboard for the player's records.
class App {
 public:
  App(Config config, DetectionRules rules);
  int Run();

 private:
  void Poll();
  void StopSession();
  void HandleEvents();
  void SaveReadyAutoClips(bool all);
  void SaveHotkeyClip();
  // Saves [start, end] of the replay buffer. Returns false when there's nothing to save yet.
  bool SaveRange(int64_t start, int64_t end, const char* trigger);
  // A new file name in the game's clip folder; suffix goes before the extension.
  std::filesystem::path ClipPath(std::string_view suffix = {});
  // Starts a long recording, or stops the one running.
  void ToggleLongRecording();
  // Stops the long recording and saves it with every moment it caught.
  void FinishLongRecording(std::string_view why);
  // Stops it early when it can't go on (a write failed, the drive is nearly full).
  void CheckLongRecording();
  // Writes changes to the player's totals, from the game's API or its scoreboard, to the timeline.
  void PumpScoreboards();
  double SessionSeconds(int64_t time) const;
  // Which match of the recording a moment belongs to: the match starts detected by then.
  int MatchAt(int64_t time) const;
  // Starts or stops reading the running game's own API, for games that have one.
  void SyncIntegration(const DetectedGame* game);
  // Hands moments from the game's API to the recording, like the ones read off the screen.
  void PumpIntegrationEvents();

  Config config_;
  DetectionRules rules_;
  GameDetector detector_;
  std::unique_ptr<RecordingSession> session_;
  std::string sessionGameId_;
  // When the current recording started, and the match starts detected in it, for grouping
  // clips by match.
  std::string sessionId_;
  int64_t sessionStart_ = 0;
  std::vector<int64_t> matchStarts_;
  AutoClipPlanner planner_;
  int64_t retryAt_ = 0;
  std::string lastGame_;
  std::vector<std::future<void>> saves_;
  std::string lastStem_;
  int lastStemCount_ = 0;
  std::unique_ptr<GameIntegration> integration_;
  const GameProfile* integrationGame_ = nullptr;
  DWORD integrationProcess_ = 0;

  std::unique_ptr<LongRecorder> longRecording_;
  std::vector<GameEvent> longEvents_;  // the moments it caught, for its markers
  std::unique_ptr<TimelineWriter> timeline_;
};

}  // namespace clip
