#include "detect/OcrImage.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <string>

namespace clip {

std::optional<ReadQuality> ParseReadQuality(std::string_view text) {
  std::string lower(text);
  std::ranges::transform(lower, lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  if (lower == "low") return ReadQuality::Low;
  if (lower == "medium") return ReadQuality::Medium;
  if (lower == "high") return ReadQuality::High;
  if (lower == "ultra") return ReadQuality::Ultra;
  return std::nullopt;
}

std::string_view ToString(ReadQuality quality) {
  switch (quality) {
    case ReadQuality::Low: return "low";
    case ReadQuality::Medium: return "medium";
    case ReadQuality::High: return "high";
    case ReadQuality::Ultra: return "ultra";
  }
  return "medium";
}

int ScaledSampleMs(int sampleMs, ReadQuality quality) {
  switch (quality) {
    case ReadQuality::High: return std::max(100, sampleMs * 3 / 4);
    case ReadQuality::Ultra: return std::max(100, sampleMs / 2);
    default: return sampleMs;
  }
}

int ReadPasses(ReadQuality quality) {
  switch (quality) {
    case ReadQuality::High: return 2;
    case ReadQuality::Ultra: return 3;
    default: return 1;
  }
}

bool ShrinksBanners(ReadQuality quality) { return quality == ReadQuality::Low; }

namespace {

uint32_t ShrinkToFit(uint32_t width, uint32_t height, uint32_t shrink, uint32_t maxDimension) {
  return std::max({shrink, 1u, (std::max(width, height) + maxDimension - 1) / maxDimension});
}

// How many times bigger a region of this size is read: text in a short region is small.
uint32_t Grow(uint32_t width, uint32_t height, ReadQuality quality, uint32_t maxDimension) {
  // Regions shorter than these are read three and two times bigger.
  uint32_t belowFor3 = 0, belowFor2 = 120;
  switch (quality) {
    case ReadQuality::Low: break;
    case ReadQuality::Medium: belowFor2 = 200; break;
    case ReadQuality::High: belowFor3 = 120, belowFor2 = 300; break;
    case ReadQuality::Ultra: belowFor3 = 200, belowFor2 = 450; break;
  }
  auto fits = [&](uint32_t grow) { return width * grow <= maxDimension && height * grow <= maxDimension; };
  if (height < belowFor3 && fits(3)) return 3;
  if (height < belowFor2 && fits(2)) return 2;
  return 1;
}

// The brightness at or below which a fraction of the pixels are.
int Percentile(const std::array<uint32_t, 256>& histogram, uint64_t total, double fraction) {
  const auto target = static_cast<uint64_t>(std::ceil(total * fraction));
  uint64_t seen = 0;
  for (int v = 0; v < 256; ++v) {
    seen += histogram[v];
    if (seen >= std::max<uint64_t>(target, 1)) return v;
  }
  return 255;
}

}  // namespace

double OcrScale(uint32_t width, uint32_t height, uint32_t shrink, ReadQuality quality, uint32_t maxDimension) {
  shrink = ShrinkToFit(width, height, shrink, maxDimension);
  if (shrink > 1) return 1.0 / shrink;
  return Grow(width, height, quality, maxDimension);
}

int OtsuThreshold(const std::array<uint32_t, 256>& histogram) {
  uint64_t total = 0;
  double sum = 0;
  for (int v = 0; v < 256; ++v) {
    total += histogram[v];
    sum += static_cast<double>(v) * histogram[v];
  }
  if (total == 0) return 128;
  double sumBelow = 0, best = -1;
  uint64_t below = 0;
  int threshold = 128;
  for (int t = 1; t < 256; ++t) {
    // Pixels darker than t are one side, the rest the other.
    below += histogram[t - 1];
    sumBelow += static_cast<double>(t - 1) * histogram[t - 1];
    const uint64_t above = total - below;
    if (below == 0 || above == 0) continue;
    const double meanBelow = sumBelow / below;
    const double meanAbove = (sum - sumBelow) / above;
    const double between = static_cast<double>(below) * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
    if (between > best) {
      best = between;
      threshold = t;
    }
  }
  return threshold;
}

OcrImage PrepareForOcr(const uint8_t* bgra, uint32_t width, uint32_t height, int threshold, uint32_t shrink,
                       ReadQuality quality, int pass, uint32_t maxDimension) {
  shrink = ShrinkToFit(width, height, shrink, maxDimension);
  const uint32_t grow = shrink > 1 ? 1 : Grow(width, height, quality, maxDimension);

  // Each pixel's brightest channel, at the size it's read before enlarging. Shrinking keeps
  // the brightest pixel of each block, so thin bright strokes survive.
  const uint32_t srcWidth = std::max(1u, width / shrink);
  const uint32_t srcHeight = std::max(1u, height / shrink);
  std::vector<uint8_t> bright(static_cast<size_t>(srcWidth) * srcHeight);
  std::array<uint32_t, 256> histogram{};
  for (uint32_t y = 0; y < srcHeight; ++y) {
    for (uint32_t x = 0; x < srcWidth; ++x) {
      uint8_t b = 0;
      for (uint32_t sy = y * shrink; sy < std::min(height, (y + 1) * shrink); ++sy) {
        for (uint32_t sx = x * shrink; sx < std::min(width, (x + 1) * shrink); ++sx) {
          const uint8_t* p = bgra + (static_cast<size_t>(sy) * width + sx) * 4;
          b = std::max({b, p[0], p[1], p[2]});
        }
      }
      bright[static_cast<size_t>(y) * srcWidth + x] = b;
      ++histogram[b];
    }
  }

  // How each brightness comes out, for this pass.
  const bool careful = quality != ReadQuality::Low;
  const uint64_t total = static_cast<uint64_t>(srcWidth) * srcHeight;
  const int low = Percentile(histogram, total, 0.01);
  const int high = Percentile(histogram, total, 0.99);
  std::array<uint8_t, 256> map{};
  auto invertStretched = [&] {
    // A region that's nearly one brightness has no text to bring out; stretching it would only
    // turn noise into specks.
    const bool stretch = careful && high - low >= 24;
    for (int v = 0; v < 256; ++v) {
      const int s = stretch ? std::clamp((v - low) * 255 / (high - low), 0, 255) : v;
      map[v] = static_cast<uint8_t>(255 - s);
    }
  };
  auto cut = [&](int at, bool brightText) {
    for (int v = 0; v < 256; ++v) map[v] = (brightText ? v >= at : v < at) ? 0 : 255;
  };
  if (threshold > 0) {
    if (pass == 1) invertStretched();
    else cut(pass == 2 ? std::max(1, threshold * 3 / 4) : threshold, true);
  } else if (pass == 0) {
    invertStretched();
  } else {
    const int otsu = OtsuThreshold(histogram);
    uint64_t atOrAbove = 0;
    for (int v = otsu; v < 256; ++v) atOrAbove += histogram[v];
    // Text covers less of a region than what's behind it.
    const bool brightText = atOrAbove * 2 <= total;
    if (pass == 1) cut(otsu, brightText);
    else cut(brightText ? (otsu + high + 1) / 2 : (otsu + low) / 2, brightText);
  }

  OcrImage out;
  out.width = std::min(srcWidth * grow, maxDimension);
  out.height = std::min(srcHeight * grow, maxDimension);
  out.gray.resize(static_cast<size_t>(out.width) * out.height);
  uint8_t* o = out.gray.data();
  for (uint32_t y = 0; y < out.height; ++y) {
    if (grow == 1 || !careful) {
      const uint8_t* row = bright.data() + static_cast<size_t>(y / grow) * srcWidth;
      for (uint32_t x = 0; x < out.width; ++x) *o++ = map[row[x / grow]];
      continue;
    }
    // Enlarging smoothly keeps letters' curves; blocks make an O look like a D.
    const double fy = std::clamp((y + 0.5) / grow - 0.5, 0.0, srcHeight - 1.0);
    const auto y0 = static_cast<uint32_t>(fy);
    const uint32_t y1 = std::min(y0 + 1, srcHeight - 1);
    const double wy = fy - y0;
    const uint8_t* r0 = bright.data() + static_cast<size_t>(y0) * srcWidth;
    const uint8_t* r1 = bright.data() + static_cast<size_t>(y1) * srcWidth;
    for (uint32_t x = 0; x < out.width; ++x) {
      const double fx = std::clamp((x + 0.5) / grow - 0.5, 0.0, srcWidth - 1.0);
      const auto x0 = static_cast<uint32_t>(fx);
      const uint32_t x1 = std::min(x0 + 1, srcWidth - 1);
      const double wx = fx - x0;
      const double top = r0[x0] + (r0[x1] - r0[x0]) * wx;
      const double bottom = r1[x0] + (r1[x1] - r1[x0]) * wx;
      *o++ = map[static_cast<int>(std::lround(top + (bottom - top) * wy))];
    }
  }
  return out;
}

}  // namespace clip
