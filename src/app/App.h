#pragma once

#include "app/RecordingSession.h"
#include "core/Config.h"
#include "game/GameDetector.h"

#include <filesystem>
#include <future>
#include <memory>
#include <vector>

namespace clip {

// Watches for supported games, records while one is running, and saves clips on the hotkey.
class App {
 public:
  explicit App(Config config);
  int Run();

 private:
  void Poll();
  void StopSession();
  void SaveClip();
  std::filesystem::path ClipPath() const;

  Config config_;
  GameDetector detector_;
  std::unique_ptr<RecordingSession> session_;
  int64_t retryAt_ = 0;
  std::string lastGame_;
  std::vector<std::future<void>> saves_;
};

}  // namespace clip
