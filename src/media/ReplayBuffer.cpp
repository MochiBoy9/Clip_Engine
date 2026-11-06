#include "media/ReplayBuffer.h"

#include <algorithm>

namespace clip {

void ReplayBuffer::SetFormat(int stream, IMFMediaType* type) {
  auto copy = CopyMediaType(type);
  std::lock_guard lock(mutex_);
  formats_[stream] = std::move(copy);
}

void ReplayBuffer::Push(EncodedPacket&& packet) {
  const int stream = packet.stream;
  auto ptr = std::make_shared<const EncodedPacket>(std::move(packet));

  std::lock_guard lock(mutex_);
  auto& queue = streams_[stream];
  bytes_ += ptr->data.size();
  if (tap_) tap_(ptr);
  queue.push_back(std::move(ptr));

  const int64_t oldest = queue.back()->pts - maxDuration_;
  auto popFront = [&] {
    bytes_ -= queue.front()->data.size();
    queue.pop_front();
  };
  while (!queue.empty() && queue.front()->pts < oldest) popFront();
  // Video can only be decoded from a keyframe, so never keep a partial group of pictures.
  if (stream == kVideoStream) {
    while (!queue.empty() && !queue.front()->keyframe) popFront();
  }

  // Over the memory limit: drop the oldest group of pictures, and the audio from before what's
  // left of the video, until it fits. The newest group always stays.
  auto& video = streams_[kVideoStream];
  while (maxBytes_ > 0 && bytes_ > maxBytes_ && video.size() > 1) {
    auto next = std::find_if(video.begin() + 1, video.end(), [](const PacketPtr& p) { return p->keyframe; });
    if (next == video.end()) break;
    for (auto it = video.begin(); it != next; ++it) bytes_ -= (*it)->data.size();
    video.erase(video.begin(), next);
    const int64_t from = video.front()->pts;
    for (int s = 0; s < kStreamCount; ++s) {
      if (s == kVideoStream) continue;
      auto& audio = streams_[s];
      while (!audio.empty() && audio.front()->pts < from) {
        bytes_ -= audio.front()->data.size();
        audio.pop_front();
      }
    }
  }
}

void ReplayBuffer::SetMaxBytes(size_t maxBytes) {
  std::lock_guard lock(mutex_);
  maxBytes_ = maxBytes;
}

int64_t ReplayBuffer::HeldDuration() const {
  std::lock_guard lock(mutex_);
  const auto& video = streams_[kVideoStream];
  return video.empty() ? 0 : video.back()->pts - video.front()->pts;
}

std::optional<ReplayBuffer::Clip> ReplayBuffer::Extract(int64_t start, int64_t end) const {
  std::lock_guard lock(mutex_);
  const auto& video = streams_[kVideoStream];
  if (!formats_[kVideoStream] || video.empty()) return std::nullopt;

  // Last keyframe at or before start, or the first one we have.
  auto first = video.end();
  for (auto it = video.begin(); it != video.end() && (*it)->pts <= start; ++it) {
    if ((*it)->keyframe) first = it;
  }
  if (first == video.end()) {
    first = std::find_if(video.begin(), video.end(), [](const PacketPtr& p) { return p->keyframe; });
    if (first == video.end() || (*first)->pts > end) return std::nullopt;
  }

  Clip clip;
  clip.start = (*first)->pts;
  clip.end = end;
  for (int s = 0; s < kStreamCount; ++s) {
    if (!formats_[s]) continue;
    clip.formats[s] = formats_[s];
    auto it = s == kVideoStream ? first : streams_[s].begin();
    for (; it != streams_[s].end() && (*it)->pts <= end; ++it) {
      if ((*it)->pts >= clip.start) clip.packets.push_back(*it);
    }
  }
  std::ranges::stable_sort(clip.packets, {}, [](const PacketPtr& p) { return p->pts; });
  return clip;
}

std::optional<ReplayBuffer::Clip> ReplayBuffer::StartTap(Tap tap) {
  std::lock_guard lock(mutex_);
  const auto& video = streams_[kVideoStream];
  if (!formats_[kVideoStream]) return std::nullopt;
  const auto key = std::find_if(video.rbegin(), video.rend(), [](const PacketPtr& p) { return p->keyframe; });
  if (key == video.rend()) return std::nullopt;

  Clip clip;
  clip.start = (*key)->pts;
  clip.end = video.back()->pts;
  for (int s = 0; s < kStreamCount; ++s) {
    if (!formats_[s]) continue;
    clip.formats[s] = formats_[s];
    for (const auto& p : streams_[s]) {
      if (p->pts >= clip.start) clip.packets.push_back(p);
    }
  }
  std::ranges::stable_sort(clip.packets, {}, [](const PacketPtr& p) { return p->pts; });
  tap_ = std::move(tap);
  return clip;
}

void ReplayBuffer::StopTap() {
  std::lock_guard lock(mutex_);
  tap_ = nullptr;
}

size_t ReplayBuffer::Bytes() const {
  std::lock_guard lock(mutex_);
  return bytes_;
}

}  // namespace clip
