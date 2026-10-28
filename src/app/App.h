#pragma once

#include "app/AutoClipPlanner.h"
#include "app/RecordingSession.h"
#include "core/Config.h"
#include "detect/DetectionRules.h"
#include "game/GameDetector.h"

#include <filesystem>
#include <future>
#include <memory>
#include <optional>
#include <vector>

namespace clip {

// Watches for supported games, records while one is running, saves clips on the hotkey and
// around the moments the screen reader detects.
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
  std::filesystem::path ClipPath();

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
};

}  // namespace clip
