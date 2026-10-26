#pragma once

#include "core/Win.h"

#include <mfapi.h>
#include <mfidl.h>

#include <cstdint>
#include <span>
#include <vector>

namespace clip {

// Stream numbers inside the replay buffer and saved clips.
enum StreamId : int { kVideoStream = 0, kGameAudioStream = 1, kMicAudioStream = 2, kStreamCount = 3 };

// One compressed video or audio frame. Timestamps are QPC time in 100 ns units.
struct EncodedPacket {
  int stream = kVideoStream;
  int64_t pts = 0;
  int64_t duration = 0;
  bool keyframe = false;
  std::vector<uint8_t> data;
};

winrt::com_ptr<IMFMediaType> CopyMediaType(IMFMediaType* source);
EncodedPacket PacketFromSample(IMFSample* sample, int stream);
winrt::com_ptr<IMFSample> CreateMemorySample(DWORD capacity);

// Annex B helpers for H.264 and HEVC bitstreams from hardware encoders.
bool ContainsKeyframe(std::span<const uint8_t> annexB, bool hevc);
// SPS/PPS (and VPS for HEVC) with start codes, as MP4 writing needs them.
std::vector<uint8_t> ExtractParameterSets(std::span<const uint8_t> annexB, bool hevc);

}  // namespace clip
