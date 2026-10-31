#pragma once

#include "detect/DetectionRules.h"
#include "detect/SoundMatch.h"

#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace clip {

// Listens to the game's audio for the rules that have a sound, as EventDetector reads the
// screen for the rest. Sounds are told apart only by going away, like a score line.
class SoundWatch {
 public:
  // The rule heard `count` new moments, the first starting at `time` (the audio's clock).
  using Found = std::function<void(const DetectionRule& rule, int count, int64_t time, const std::string& text)>;

  // The rules that listen, with their sounds at full paths. Those whose sounds can't be read
  // are skipped, saying why through warn. The rules must outlive the watch.
  SoundWatch(const std::vector<DetectionRule>& rules, const std::function<void(const std::string&)>& warn);
  ~SoundWatch();

  bool Empty() const { return watches_.empty(); }

  // Audio as it's played: interleaved 16-bit samples at any rate, the first frame at pts
  // (100 ns units).
  void Push(const int16_t* pcm, size_t frames, int rate, int channels, int64_t pts, const Found& found);

 private:
  struct Watch;
  std::vector<std::unique_ptr<Watch>> watches_;
  std::unique_ptr<SoundResampler> resampler_;
  int rate_ = 0;
  int channels_ = 0;
  SoundFeatures features_;
  std::vector<float> samples_;
  int64_t taken_ = 0;  // samples at kSoundRate so far
};

}  // namespace clip
