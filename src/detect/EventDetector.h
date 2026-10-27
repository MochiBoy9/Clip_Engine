#pragma once

#include "capture/MonitorCapture.h"
#include "detect/DetectionRules.h"

#include <atomic>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

namespace clip {

// Reads the parts of the captured screen named by the game's rules a few times a second
// with Windows' built-in text recognition, and reports kills, deaths and other moments.
// It copies only those small regions off the GPU, without waiting on it, and runs at
// low priority so it never competes with the game.
class EventDetector {
 public:
  using Sink = std::function<void(const GameEvent&)>;

  EventDetector(std::shared_ptr<GpuDevice> gpu, const MonitorCapture& capture, std::vector<DetectionRule> rules,
                int sampleMs, bool debug, Sink sink);
  ~EventDetector();

  EventDetector(const EventDetector&) = delete;
  EventDetector& operator=(const EventDetector&) = delete;

  // False when Windows has no text recognition for any installed language.
  static bool IsAvailable();

 private:
  struct Region;
  void Run();

  std::shared_ptr<GpuDevice> gpu_;
  const MonitorCapture& capture_;
  std::vector<DetectionRule> rules_;
  int sampleMs_;
  bool debug_;
  Sink sink_;

  std::vector<std::unique_ptr<Region>> regions_;

  std::mutex mutex_;
  std::condition_variable wake_;
  bool stop_ = false;
  std::thread thread_;
};

}  // namespace clip
