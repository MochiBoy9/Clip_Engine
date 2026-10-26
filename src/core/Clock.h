#pragma once

#include "core/Win.h"

#include <cstdint>

namespace clip {

// Every timestamp in the engine is QueryPerformanceCounter time in 100 ns units. That is the
// unit Media Foundation uses, and both Windows.Graphics.Capture frames and WASAPI packets
// report QPC time in it, so video, game audio and mic audio all share one clock.
constexpr int64_t kHnsPerSecond = 10'000'000;
constexpr int64_t kHnsPerMs = 10'000;

inline int64_t QpcFrequency() {
  static const int64_t frequency = [] {
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    return f.QuadPart;
  }();
  return frequency;
}

inline int64_t NowHns() {
  LARGE_INTEGER now;
  QueryPerformanceCounter(&now);
  const int64_t f = QpcFrequency();
  return (now.QuadPart / f) * kHnsPerSecond + (now.QuadPart % f) * kHnsPerSecond / f;
}

}  // namespace clip
