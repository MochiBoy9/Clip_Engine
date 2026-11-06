#pragma once

#include "core/Win.h"

#include <cstdint>

#include "squad/SquadClips.h"

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

// The engine clock and the wall clock read together, for turning a moment's time into the
// Unix time a squadmate's app can line its own clip up with, and back.
inline ClockPair NowClocks() {
  FILETIME ft;
  GetSystemTimePreciseAsFileTime(&ft);
  const int64_t hns = NowHns();
  const int64_t sinceUnix = ((static_cast<int64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime) - 116'444'736'000'000'000;
  return {hns, sinceUnix / 10'000};
}

}  // namespace clip
