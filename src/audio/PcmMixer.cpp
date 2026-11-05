#include "audio/PcmMixer.h"

#include <algorithm>
#include <cstdlib>

namespace clip {
namespace {

constexpr int64_t kHnsPerSecond = 10'000'000;
// Drift under 10 ms is ordinary capture jitter and is ignored.
constexpr int64_t kToleranceFrames = PcmMixer::kRate / 100;
// After a gap longer than this, an input starts over where its new audio is instead of filling the
// gap with silence.
constexpr int64_t kMaxGapFrames = PcmMixer::kRate * 2;

int16_t Saturate(float v) { return static_cast<int16_t>(std::clamp(v, -32768.0f, 32767.0f)); }

}  // namespace

int64_t PcmMixer::HnsToFrames(int64_t hns) {
  // To the nearest frame, the same way either side of zero.
  const int64_t scaled = hns * kRate + kHnsPerSecond / 2;
  return scaled >= 0 ? scaled / kHnsPerSecond : -((-scaled + kHnsPerSecond - 1) / kHnsPerSecond);
}

int64_t PcmMixer::FramesToHns(int64_t frames) { return frames * kHnsPerSecond / kRate; }

void ApplyGain(int16_t* pcm, uint32_t frames, uint32_t channels, float from, float to) {
  if (from == 1 && to == 1) return;
  if (from == 0 && to == 0) {
    std::fill(pcm, pcm + static_cast<size_t>(frames) * channels, int16_t{0});
    return;
  }
  const float step = frames > 0 ? (to - from) / static_cast<float>(frames) : 0;
  float gain = from;
  for (uint32_t f = 0; f < frames; ++f, gain += step) {
    for (uint32_t c = 0; c < channels; ++c) {
      int16_t& s = pcm[static_cast<size_t>(f) * channels + c];
      s = Saturate(static_cast<float>(s) * gain);
    }
  }
}

void PcmMixer::Push(int index, const int16_t* pcm, uint32_t frames, int64_t pts, float gain) {
  if (frames == 0) return;
  std::lock_guard lock(mutex_);
  Input& in = inputs_[index];
  int64_t at = HnsToFrames(pts);
  // Ordinary jitter: carry on right where the input left off.
  if (in.started && std::llabs(at - in.next) <= kToleranceFrames) at = in.next;

  if (in.samples.empty() || at - in.next > kMaxGapFrames) {
    in.samples.clear();
    in.begin = in.next = std::max(at, mixedUntil_);
  } else if (at > in.next) {
    // A real gap: silence until the new audio.
    in.samples.insert(in.samples.end(), static_cast<size_t>(at - in.next) * kChannels, int16_t{0});
    in.next = at;
  }
  in.started = true;

  // Leave out what overlaps audio already buffered, or a stretch already mixed.
  const int64_t skip = std::clamp<int64_t>(in.next - at, 0, frames);
  const float from = in.gain;
  const float step = (gain - from) / static_cast<float>(frames);
  for (int64_t f = skip; f < frames; ++f) {
    const float g = from + step * static_cast<float>(f);
    for (uint32_t c = 0; c < kChannels; ++c) {
      in.samples.push_back(Saturate(static_cast<float>(pcm[static_cast<size_t>(f) * kChannels + c]) * g));
    }
  }
  in.next = std::max(in.next, at + frames);
  in.gain = gain;
}

void PcmMixer::Remove(int input) {
  std::lock_guard lock(mutex_);
  inputs_.erase(input);
}

size_t PcmMixer::Inputs() const {
  std::lock_guard lock(mutex_);
  return inputs_.size();
}

void PcmMixer::Mix(int64_t from, uint32_t frames, std::vector<int16_t>& out) {
  std::lock_guard lock(mutex_);
  const int64_t end = from + frames;
  sum_.assign(static_cast<size_t>(frames) * kChannels, 0);
  for (auto& [index, in] : inputs_) {
    // Drop what's before this stretch: it arrived too late to be mixed.
    if (in.begin < from) {
      const int64_t drop = std::min(from, in.next) - in.begin;
      in.samples.erase(in.samples.begin(), in.samples.begin() + static_cast<ptrdiff_t>(drop * kChannels));
      in.begin += drop;
      if (in.samples.empty()) in.begin = in.next = std::max(in.next, from);
    }
    const int64_t first = std::max(in.begin, from);
    const int64_t last = std::min(in.next, end);
    if (last <= first) continue;
    auto it = in.samples.begin() + static_cast<ptrdiff_t>((first - in.begin) * kChannels);
    int32_t* target = sum_.data() + (first - from) * kChannels;
    for (int64_t i = 0; i < (last - first) * static_cast<int64_t>(kChannels); ++i) target[i] += *it++;
    in.samples.erase(in.samples.begin(), in.samples.begin() + static_cast<ptrdiff_t>((last - in.begin) * kChannels));
    in.begin = last;
    if (in.samples.empty()) in.next = std::max(in.next, last);
  }
  mixedUntil_ = std::max(mixedUntil_, end);
  out.resize(sum_.size());
  for (size_t i = 0; i < sum_.size(); ++i) out[i] = static_cast<int16_t>(std::clamp(sum_[i], -32768, 32767));
}

}  // namespace clip
