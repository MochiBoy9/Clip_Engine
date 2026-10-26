#pragma once

#include "audio/AacEncoder.h"
#include "audio/AudioCapture.h"
#include "capture/MonitorCapture.h"
#include "core/Config.h"
#include "media/ReplayBuffer.h"
#include "video/VideoEncoder.h"

#include <atomic>
#include <memory>
#include <string>
#include <thread>

namespace clip {

// Everything that runs while one game (or the desktop) is being recorded: monitor capture,
// a frame pacer feeding the hardware encoder, game and mic audio, and the replay buffer.
class RecordingSession {
 public:
  RecordingSession(const Config& config, HMONITOR monitor, std::string label);
  ~RecordingSession();

  RecordingSession(const RecordingSession&) = delete;
  RecordingSession& operator=(const RecordingSession&) = delete;

  const std::string& Label() const { return label_; }
  HMONITOR Monitor() const { return monitor_; }

  // False when the session can't continue (resolution changed, encoder failed) and should be
  // recreated.
  bool Healthy() const;

  const ReplayBuffer& Buffer() const { return buffer_; }

 private:
  void PaceFrames();
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

  std::atomic<bool> stop_{false};
  std::thread pacer_;
};

}  // namespace clip
