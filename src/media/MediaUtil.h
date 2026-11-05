#pragma once

#include "core/Win.h"

#include <mfapi.h>
#include <mfidl.h>

#include <cstdint>
#include <span>
#include <vector>

namespace clip {

// Stream numbers inside the replay buffer, in the order their tracks are written to clips. When
// more than one audio source is recorded, the first audio track is all of them mixed (what every
// player plays) and each source follows on a track of its own, for editing; with one source, its
// track is the only one. Clips from before 0.15 have game audio and then the mic.
enum StreamId : int {
  kVideoStream = 0,
  kMixAudioStream = 1,
  kGameAudioStream = 2,
  kMicAudioStream = 3,
  kDiscordAudioStream = 4,
  kAppsAudioStream = 5,
  kStreamCount = 6,
};

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
