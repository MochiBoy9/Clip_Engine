#pragma once

#include "audio/PcmMixer.h"

#include <atomic>
#include <cstdint>
#include <functional>
#include <thread>

namespace clip {

// Takes mixed audio out of a PcmMixer every 10 ms, `latency` behind real time so every input has
// delivered its part, and hands it on with its timestamp. Runs on its own thread, scheduled like
// audio capture, so the stream it makes never stutters.
class MixClock {
 public:
  using Sink = std::function<void(const int16_t* pcm, uint32_t frames, int64_t pts)>;

  MixClock(PcmMixer& mixer, int64_t latency, Sink sink);
  ~MixClock();

  MixClock(const MixClock&) = delete;
  MixClock& operator=(const MixClock&) = delete;

 private:
  void Run();

  PcmMixer& mixer_;
  const int64_t latency_;
  Sink sink_;
  std::atomic<bool> stop_{false};
  std::thread thread_;
};

}  // namespace clip
