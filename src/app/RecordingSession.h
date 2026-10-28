#pragma once

#include "audio/AacEncoder.h"
#include "audio/AudioCapture.h"
#include "capture/MonitorCapture.h"
#include "core/Config.h"
#include "detect/EventDetector.h"
#include "media/ReplayBuffer.h"
#include "video/VideoEncoder.h"

#include <atomic>
#include <deque>
#include <memory>
#include <string>
#include <thread>

namespace clip {

// Everything that runs while one game (or the desktop) is being recorded: monitor capture,
// a frame pacer feeding the hardware encoder, game and mic audio, and the replay buffer.
class RecordingSession {
 public:
  // rules: what to look for on screen in this game; empty means no event detection.
  // scoreboards: where the game shows the player's totals, found by playerNames.
  RecordingSession(const Config& config, HMONITOR monitor, std::string label, std::vector<DetectionRule> rules,
                   std::vector<ScoreboardRule> scoreboards, std::vector<std::string> playerNames,
                   int detectionSampleMs);
  ~RecordingSession();

  RecordingSession(const RecordingSession&) = delete;
  RecordingSession& operator=(const RecordingSession&) = delete;

  const std::string& Label() const { return label_; }
  HMONITOR Monitor() const { return monitor_; }

  // False when the session can't continue (resolution changed, encoder failed) and should be
  // recreated.
  bool Healthy() const;

  const ReplayBuffer& Buffer() const { return buffer_; }
  ReplayBuffer& Buffer() { return buffer_; }

  // Adds a moment found some other way than reading the screen (the game's own API).
  void AddEvent(const GameEvent& event) { OnEvent(event); }

  // Events detected since the last call.
  std::vector<GameEvent> TakeNewEvents();
  // Scoreboard readings since the last call.
  std::vector<Scoreboard> TakeScoreboards();
  // Every detected event still in the replay buffer between start and end.
  std::vector<GameEvent> EventsBetween(int64_t start, int64_t end) const;

 private:
  void PaceFrames();
  void OnEvent(const GameEvent& event);
  void StartAudio(AudioCapture::Source source, int stream, std::unique_ptr<AacEncoder>& encoder,
                  std::unique_ptr<AudioCapture>& capture);

  std::string label_;
  HMONITOR monitor_;
  UINT fps_ = 60;

  // Declared in dependency order; the destructor tears down in reverse.
  std::shared_ptr<GpuDevice> gpu_;
  ReplayBuffer buffer_;
  std::unique_ptr<MonitorCapture> capture_;
  std::unique_ptr<VideoEncoder> video_;
  std::unique_ptr<AacEncoder> gameAudioEncoder_;
  std::unique_ptr<AacEncoder> micAudioEncoder_;
  std::unique_ptr<AudioCapture> gameAudio_;
  std::unique_ptr<AudioCapture> micAudio_;
  std::unique_ptr<EventDetector> detector_;

  int64_t bufferDuration_;
  mutable std::mutex eventsMutex_;
  std::deque<GameEvent> history_;
  std::vector<GameEvent> newEvents_;
  std::vector<Scoreboard> newScoreboards_;

  std::atomic<bool> stop_{false};
  std::thread pacer_;
};

}  // namespace clip
