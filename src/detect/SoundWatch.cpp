#include "detect/SoundWatch.h"

#include <algorithm>
#include <format>

namespace clip {

struct SoundWatch::Watch {
  Watch(const DetectionRule& r, const SoundTemplate& sound) : rule(r), matcher(sound), tracker(r), length(sound.Length()) {}
  const DetectionRule& rule;
  SoundMatcher matcher;
  RuleTracker tracker;
  size_t length;
  double best = 0;  // the closest it came while heard
};

SoundWatch::SoundWatch(const std::vector<DetectionRule>& rules, const std::function<void(const std::string&)>& warn) {
  for (const auto& rule : rules) {
    if (!rule.ListensForSound()) continue;
    std::vector<std::vector<float>> cuts;
    for (const auto& file : rule.sounds) {
      std::string error;
      auto cut = LoadSoundCut(std::filesystem::path(std::u8string(file.begin(), file.end())), &error);
      if (cut) cuts.push_back(std::move(*cut));
      else if (warn) warn(std::format("Detection rule {}: couldn't use {}, as {}", rule.name, file, error));
    }
    const auto sound = SoundTemplate::FromCuts(cuts);
    if (!sound) {
      if (warn) warn(std::format("Detection rule {}: its sounds are too short to listen for; skipping it", rule.name));
      continue;
    }
    watches_.push_back(std::make_unique<Watch>(rule, *sound));
  }
}

SoundWatch::~SoundWatch() = default;

void SoundWatch::Push(const int16_t* pcm, size_t frames, int rate, int channels, int64_t pts, const Found& found) {
  if (watches_.empty() || frames == 0) return;
  if (!resampler_ || rate != rate_ || channels != channels_) {
    resampler_ = std::make_unique<SoundResampler>(rate, channels);
    rate_ = rate;
    channels_ = channels;
  }
  // Where this audio starts, at kSoundRate, to tell when each sound began.
  const int64_t anchorSample = taken_;
  samples_.clear();
  resampler_->Push(pcm, frames, samples_);
  taken_ += static_cast<int64_t>(samples_.size());

  const int64_t firstLook = features_.Looks();
  const auto rows = features_.Push(samples_.data(), samples_.size());
  for (size_t r = 0; r < rows.size(); ++r) {
    const int64_t look = firstLook + static_cast<int64_t>(r);
    for (auto& watch : watches_) {
      const double alike = watch->matcher.Push(rows[r]);
      const bool heard = alike >= watch->rule.soundMatch;
      // The sound began where the stretch that matched it did.
      const int64_t start = (look - static_cast<int64_t>(watch->length) + 1) * kSoundHop;
      const int64_t time = pts + (start - anchorSample) * 10'000'000 / kSoundRate;
      if (heard) watch->best = std::max(watch->best, alike);
      const int fresh = watch->tracker.UpdateMatches(heard ? std::vector<std::string>{""} : std::vector<std::string>{},
                                                     time / 10'000);
      if (fresh > 0) found(watch->rule, fresh, time, std::format("sound: {:.2f} alike", watch->best));
      if (!heard) watch->best = 0;
    }
  }
}

}  // namespace clip
