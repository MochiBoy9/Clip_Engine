#pragma once

#include <cstddef>
#include <cstdint>
#include <deque>
#include <map>
#include <mutex>
#include <vector>

namespace clip {

// Adds several 48 kHz stereo 16-bit streams together on one timeline. Each input arrives on its
// own thread with its own timestamps (QPC time in 100 ns units, as WASAPI gives them), and Mix()
// takes a stretch of the timeline once every input has had time to deliver it. Like AacEncoder,
// each input is kept continuous: jitter under 10 ms is ignored, real gaps become silence and
// overlaps are dropped. Audio that arrives after its stretch was mixed is dropped too.
// Thread-safe: inputs push from their capture threads while one thread mixes.
class PcmMixer {
 public:
  static constexpr uint32_t kRate = 48000;
  static constexpr uint32_t kChannels = 2;

  static int64_t HnsToFrames(int64_t hns);
  static int64_t FramesToHns(int64_t frames);

  // Adds audio to an input, which starts the first time it's pushed to. Each sample is scaled by
  // gain, moving smoothly from the input's last gain to this one over the packet so a change of
  // volume doesn't click.
  void Push(int input, const int16_t* pcm, uint32_t frames, int64_t pts, float gain = 1);
  // Forgets an input, such as an app that closed.
  void Remove(int input);

  // Mixes `frames` frames from frame number `from` (HnsToFrames of its time) into out, which gets
  // frames * kChannels values, and forgets everything before the end of that stretch.
  void Mix(int64_t from, uint32_t frames, std::vector<int16_t>& out);

  // How many inputs there are, for tests and logs.
  size_t Inputs() const;

 private:
  struct Input {
    std::deque<int16_t> samples;  // interleaved, from frame `begin` up to frame `next`
    int64_t begin = 0;
    int64_t next = 0;
    bool started = false;
    float gain = 1;
  };

  mutable std::mutex mutex_;
  std::map<int, Input> inputs_;
  int64_t mixedUntil_ = INT64_MIN;  // frame number everything before which has been mixed
  std::vector<int32_t> sum_;
};

// Scales PCM by a gain that moves from `from` to `to` across it, saturating at the 16-bit limits.
void ApplyGain(int16_t* pcm, uint32_t frames, uint32_t channels, float from, float to);

}  // namespace clip
