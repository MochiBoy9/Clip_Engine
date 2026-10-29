#pragma once

#include "capture/MonitorCapture.h"
#include "detect/DetectionRules.h"
#include "detect/OcrImage.h"

#include <winrt/Windows.Media.Ocr.h>

#include <atomic>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

namespace clip {

// Reads the parts of the captured screen named by the game's rules a few times a second
// with Windows' built-in text recognition, or looks there for an icon, and reports kills,
// deaths and other moments, and the player's totals whenever the game's scoreboard is up. It copies only those small regions off the GPU, without waiting
// on it, and runs at low priority so it never competes with the game. To keep that work
// small, a region inside another is read as part of it when that's cheaper, a region that
// hasn't changed since it was last read isn't read again, and banners that stay up for
// seconds are looked at less often than kill popups.
class EventDetector {
 public:
  using Sink = std::function<void(const GameEvent&)>;
  using ScoreboardSink = std::function<void(const Scoreboard&)>;

  // playerNames: for finding the player's row on a scoreboard. quality: how hard to work at
  // reading (see ReadQuality). hudScale: the size the game's HUD is set to, 1 for 100%; with
  // the screen's shape, it moves each rule's region to where the HUD draws it (PlaceRegion).
  EventDetector(std::shared_ptr<GpuDevice> gpu, const MonitorCapture& capture, std::vector<DetectionRule> rules,
                std::vector<ScoreboardRule> scoreboards, std::vector<std::string> playerNames, int sampleMs,
                ReadQuality quality, double hudScale, bool debug, Sink sink, ScoreboardSink scoreboardSink);
  ~EventDetector();

  EventDetector(const EventDetector&) = delete;
  EventDetector& operator=(const EventDetector&) = delete;

  // False when Windows has no text recognition for any installed language.
  static bool IsAvailable();

  // Reads the screen only while a window of this process is in front, so another window
  // over the game (this app's clip list, a browser) is never taken for the game's HUD.
  // 0, the default, reads it all the time.
  void SetGameProcess(DWORD processId) { gameProcess_ = processId; }

 private:
  struct Region;
  void Run();
  void Process(Region& region, int64_t time, const winrt::Windows::Media::Ocr::OcrEngine& ocr);

  std::shared_ptr<GpuDevice> gpu_;
  const MonitorCapture& capture_;
  std::vector<DetectionRule> rules_;
  std::vector<ScoreboardRule> scoreboards_;
  std::vector<std::string> playerNames_;
  int sampleMs_;
  ReadQuality quality_;
  bool debug_;
  Sink sink_;
  ScoreboardSink scoreboardSink_;

  std::vector<std::unique_ptr<Region>> regions_;
  std::atomic<DWORD> gameProcess_{0};

  std::mutex mutex_;
  std::condition_variable wake_;
  bool stop_ = false;
  std::thread thread_;
};

}  // namespace clip
