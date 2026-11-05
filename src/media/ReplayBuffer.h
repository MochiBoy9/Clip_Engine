#pragma once

#include "media/MediaUtil.h"

#include <array>
#include <deque>
#include <functional>
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

  // Also keeps it under this many bytes, dropping the oldest stretch first, so a long buffer at a
  // high bitrate can't take more memory than the PC can spare. 0 = no limit.
  void SetMaxBytes(size_t maxBytes);
  // How much gameplay the buffer holds now, from its first video frame to its last.
  int64_t HeldDuration() const;

  // Called once per stream by its encoder, before or with its first packet.
  void SetFormat(int stream, IMFMediaType* type);
  void Push(EncodedPacket&& packet);

  // Packets covering [start, end]. Video starts on the last keyframe at or before start, so
  // a clip may begin up to one keyframe interval early.
  std::optional<Clip> Extract(int64_t start, int64_t end) const;

  size_t Bytes() const;

  // Also hands every packet pushed from now on to tap, for a recording longer than the buffer.
  // Returns the formats and the buffered packets from the most recent video keyframe on, so the
  // recording opens on a picture right away, or nothing before the first keyframe. tap is called
  // with the buffer locked, from the encoders' threads, so it must be quick. One tap at a time.
  using Tap = std::function<void(const PacketPtr&)>;
  std::optional<Clip> StartTap(Tap tap);
  void StopTap();

 private:
  mutable std::mutex mutex_;
  const int64_t maxDuration_;
  size_t maxBytes_ = 0;
  std::array<std::deque<PacketPtr>, kStreamCount> streams_;
  std::array<winrt::com_ptr<IMFMediaType>, kStreamCount> formats_;
  size_t bytes_ = 0;
  Tap tap_;
};

}  // namespace clip
