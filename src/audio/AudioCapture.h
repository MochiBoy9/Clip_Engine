#pragma once

#include "core/Win.h"

#include <atomic>
#include <cstdint>
#include <functional>
#include <thread>

namespace clip {

// Captures 48 kHz stereo 16-bit PCM from the default speakers (what the game plays) or the
// default microphone with WASAPI. Windows converts the device's own format for us.
class AudioCapture {
 public:
  enum class Source { GameAudio, Microphone };
  using Sink = std::function<void(const int16_t* pcm, uint32_t frames, int64_t pts)>;

  static constexpr uint32_t kSampleRate = 48000;
  static constexpr uint32_t kChannels = 2;

  AudioCapture(Source source, Sink sink);
  ~AudioCapture();

  AudioCapture(const AudioCapture&) = delete;
  AudioCapture& operator=(const AudioCapture&) = delete;

 private:
  void Run();

  Source source_;
  Sink sink_;
  std::atomic<bool> stop_{false};
  std::thread thread_;
};

}  // namespace clip
