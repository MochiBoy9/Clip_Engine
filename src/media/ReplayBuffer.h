#pragma once

#include "media/MediaUtil.h"

#include <array>
#include <deque>
#include <memory>
#include <mutex>
#include <optional>

namespace clip {

// Rolling window of the most recent encoded video and audio, kept compressed in memory so a
// clip can be cut from the past at any moment without re-encoding.
class ReplayBuffer {
 public:
  using PacketPtr = std::shared_ptr<const EncodedPacket>;

  struct Clip {
    std::array<winrt::com_ptr<IMFMediaType>, kStreamCount> formats;
    std::vector<PacketPtr> packets;  // all streams, in timestamp order
    int64_t start = 0;               // timestamp of the first video keyframe
    int64_t end = 0;
  };

  explicit ReplayBuffer(int64_t maxDuration) : maxDuration_(maxDuration) {}

  // Called once per stream by its encoder, before or with its first packet.
  void SetFormat(int stream, IMFMediaType* type);
  void Push(EncodedPacket&& packet);

  // Packets covering [start, end]. Video starts on the last keyframe at or before start, so
  // a clip may begin up to one keyframe interval early.
  std::optional<Clip> Extract(int64_t start, int64_t end) const;

  size_t Bytes() const;

 private:
  mutable std::mutex mutex_;
  const int64_t maxDuration_;
  std::array<std::deque<PacketPtr>, kStreamCount> streams_;
  std::array<winrt::com_ptr<IMFMediaType>, kStreamCount> formats_;
  size_t bytes_ = 0;
};

}  // namespace clip
