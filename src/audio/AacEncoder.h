#pragma once

#include "media/ReplayBuffer.h"

#include <mftransform.h>

#include <cstdint>

namespace clip {

// Encodes one 48 kHz stereo PCM stream to 192 kbps AAC with the encoder built into Windows.
// Timestamps are derived from the sample count, so small capture jitter never reaches the
// file; real gaps are filled with silence and overlaps are trimmed.
class AacEncoder {
 public:
  AacEncoder(int stream, ReplayBuffer& buffer);
  ~AacEncoder();

  AacEncoder(const AacEncoder&) = delete;
  AacEncoder& operator=(const AacEncoder&) = delete;

  // Called from a single capture thread.
  void Push(const int16_t* pcm, uint32_t frames, int64_t pts);

 private:
  void Encode(const int16_t* pcm, uint32_t frames);
  void DrainOutput();

  int stream_;
  ReplayBuffer& buffer_;
  winrt::com_ptr<IMFTransform> transform_;
  DWORD outputSize_ = 0;

  bool started_ = false;
  int64_t startPts_ = 0;
  uint64_t framesEncoded_ = 0;
};

}  // namespace clip
