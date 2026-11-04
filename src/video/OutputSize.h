#pragma once

#include <algorithm>
#include <cstdint>

namespace clip {

struct FrameSize {
  uint32_t width = 0;
  uint32_t height = 0;
  bool operator==(const FrameSize&) const = default;
};

// The size a recording is encoded at: the monitor's own size, or smaller when the resolution
// setting asks for less. maxLines is the "p" in 1440p, measured on the short side so a portrait
// monitor works too; 0 means the monitor's size. Never larger than the monitor, keeps its shape,
// and both sides are even, as the encoders need.
inline FrameSize OutputSize(uint32_t width, uint32_t height, int maxLines) {
  const uint32_t shortSide = std::min(width, height);
  if (maxLines <= 0 || shortSide <= static_cast<uint32_t>(maxLines) || shortSide == 0) {
    return {width & ~1u, height & ~1u};
  }
  const double scale = static_cast<double>(maxLines) / shortSide;
  auto even = [scale](uint32_t side) {
    const auto scaled = static_cast<uint32_t>(side * scale + 0.5);
    return std::max<uint32_t>(2, (scaled + 1) & ~1u);
  };
  return {even(width), even(height)};
}

}  // namespace clip
