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

size_t ReplayBuffer::Bytes() const {
  std::lock_guard lock(mutex_);
  return bytes_;
}

}  // namespace clip
