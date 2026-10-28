#include "detect/ImageMatch.h"

#include <algorithm>
#include <cmath>

namespace clip {

GrayImage ToGray(const uint8_t* bgra, int width, int height, size_t stride, bool useAlpha) {
  GrayImage image;
  image.width = width;
  image.height = height;
  image.pixels.resize(static_cast<size_t>(width) * height);
  if (useAlpha) image.mask.resize(image.pixels.size());
  bool anyHidden = false;
  for (int y = 0; y < height; ++y) {
    const uint8_t* row = bgra + y * stride;
    for (int x = 0; x < width; ++x) {
      const uint8_t* p = row + x * 4;
      const size_t i = static_cast<size_t>(y) * width + x;
      image.pixels[i] = std::max({p[0], p[1], p[2]});
      if (useAlpha) {
        image.mask[i] = p[3] >= 128;
        anyHidden |= p[3] < 128;
      }
    }
  }
  if (!anyHidden) image.mask.clear();
  return image;
}

GrayImage Resize(const GrayImage& image, int width, int height) {
  GrayImage out;
  out.width = std::max(width, 1);
  out.height = std::max(height, 1);
  out.pixels.resize(static_cast<size_t>(out.width) * out.height);
  if (!image.mask.empty()) out.mask.resize(out.pixels.size());
  if (image.width == 0 || image.height == 0) return out;

  const double sx = static_cast<double>(image.width) / out.width;
  const double sy = static_cast<double>(image.height) / out.height;
  for (int oy = 0; oy < out.height; ++oy) {
    const int y0 = static_cast<int>(oy * sy);
    const int y1 = std::clamp(static_cast<int>((oy + 1) * sy), y0 + 1, image.height);
    for (int ox = 0; ox < out.width; ++ox) {
      const int x0 = static_cast<int>(ox * sx);
      const int x1 = std::clamp(static_cast<int>((ox + 1) * sx), x0 + 1, image.width);
      double sum = 0;
      int counted = 0;
      int total = 0;
      for (int y = y0; y < y1; ++y) {
        for (int x = x0; x < x1; ++x) {
          const size_t i = static_cast<size_t>(y) * image.width + x;
          ++total;
          if (!image.mask.empty() && !image.mask[i]) continue;
          sum += image.pixels[i];
          ++counted;
        }
      }
      const size_t o = static_cast<size_t>(oy) * out.width + ox;
      out.pixels[o] = counted > 0 ? static_cast<float>(sum / counted) : 0.0f;
      if (!out.mask.empty()) out.mask[o] = counted * 2 >= total;
    }
  }
  return out;
}

void ImageMatcher::Level::Prepare(GrayImage image) {
  icon = std::move(image);
  norm = 0;
  count = 0;
  if (icon.width < 3 || icon.height < 3) return;
  double sum = 0;
  for (size_t i = 0; i < icon.pixels.size(); ++i) {
    if (!icon.mask.empty() && !icon.mask[i]) continue;
    sum += icon.pixels[i];
    ++count;
  }
  if (count < 9) return;
  const double mean = sum / count;
  double squares = 0;
  for (size_t i = 0; i < icon.pixels.size(); ++i) {
    if (!icon.mask.empty() && !icon.mask[i]) {
      icon.pixels[i] = 0;
      continue;
    }
    icon.pixels[i] = static_cast<float>(icon.pixels[i] - mean);
    squares += icon.pixels[i] * icon.pixels[i];
  }
  // Less than a couple of brightness levels of detail is a flat patch.
  norm = squares / count > 4.0 ? std::sqrt(squares) : 0;
  weight.resize(icon.pixels.size());
  for (size_t i = 0; i < weight.size(); ++i) weight[i] = icon.mask.empty() || icon.mask[i] ? 1.0f : 0.0f;
}

double ImageMatcher::Level::Score(const GrayImage& region, int x, int y) const {
  // Single precision without branches, so the compiler can do several pixels at once.
  float cross = 0, sum = 0, squares = 0;
  for (int iy = 0; iy < icon.height; ++iy) {
    const float* r = region.pixels.data() + static_cast<size_t>(y + iy) * region.width + x;
    const float* t = icon.pixels.data() + static_cast<size_t>(iy) * icon.width;
    const float* w = weight.data() + static_cast<size_t>(iy) * icon.width;
    for (int ix = 0; ix < icon.width; ++ix) {
      const float v = r[ix] * w[ix];
      cross += r[ix] * t[ix];
      sum += v;
      squares += v * r[ix];
    }
  }
  const double variance = static_cast<double>(squares) - static_cast<double>(sum) * sum / count;
  if (variance <= 4.0 * count) return 0;  // a flat patch is like nothing
  return cross / (std::sqrt(variance) * norm);
}

ImageMatcher::ImageMatcher(const GrayImage& picture, int pictureScreenHeight, int screenHeight) {
  if (picture.width < 2 || picture.height < 2) return;
  // The icon as big as it shows on this screen, then both brought down to the search size.
  const double onScreen = pictureScreenHeight > 0 ? static_cast<double>(screenHeight) / pictureScreenHeight : 1.0;
  const double largest = std::max(picture.width, picture.height) * onScreen;
  scale_ = std::min(1.0, kSearchSize / largest);
  GrayImage icon = Resize(picture, static_cast<int>(std::lround(picture.width * onScreen * scale_)),
                          static_cast<int>(std::lround(picture.height * onScreen * scale_)));
  useCoarse_ = std::max(icon.width, icon.height) >= 16;
  if (useCoarse_) coarse_.Prepare(Resize(icon, (icon.width + 1) / 2, (icon.height + 1) / 2));
  fine_.Prepare(std::move(icon));
  if (useCoarse_ && coarse_.norm == 0) useCoarse_ = false;
}

double ImageMatcher::BestMatch(const GrayImage& screen) const {
  if (!Valid()) return 0;
  const GrayImage region = scale_ < 1 ? Resize(screen, static_cast<int>(std::lround(screen.width * scale_)),
                                               static_cast<int>(std::lround(screen.height * scale_)))
                                      : screen;
  const GrayImage& icon = fine_.icon;
  if (region.width < icon.width || region.height < icon.height) return 0;
  const int maxX = region.width - icon.width;
  const int maxY = region.height - icon.height;

  if (!useCoarse_) {
    double best = 0;
    for (int y = 0; y <= maxY; ++y) {
      for (int x = 0; x <= maxX; ++x) best = std::max(best, fine_.Score(region, x, y));
    }
    return best;
  }

  // Look everywhere at half size, then closely around the few best places.
  const GrayImage half = Resize(region, (region.width + 1) / 2, (region.height + 1) / 2);
  struct Candidate {
    double score;
    int x, y;
  };
  constexpr size_t kCandidates = 6;
  std::vector<Candidate> candidates;
  // Every other place at half size is never more than two pixels off at full size.
  for (int y = 0; y + coarse_.icon.height <= half.height; y += 2) {
    for (int x = 0; x + coarse_.icon.width <= half.width; x += 2) {
      const double s = coarse_.Score(half, x, y);
      if (candidates.size() == kCandidates && s <= candidates.back().score) continue;
      // Neighbours of a candidate are the same place; keep the better of them.
      auto near = std::ranges::find_if(candidates, [&](const Candidate& c) { return std::abs(c.x - x) <= 4 && std::abs(c.y - y) <= 4; });
      if (near != candidates.end()) {
        if (s <= near->score) continue;
        *near = {s, x, y};
      } else {
        if (candidates.size() == kCandidates) candidates.pop_back();
        candidates.push_back({s, x, y});
      }
      std::ranges::sort(candidates, [](const Candidate& a, const Candidate& b) { return a.score > b.score; });
    }
  }

  double best = 0;
  for (const auto& c : candidates) {
    for (int y = std::max(0, c.y * 2 - 3); y <= std::min(maxY, c.y * 2 + 3); ++y) {
      for (int x = std::max(0, c.x * 2 - 3); x <= std::min(maxX, c.x * 2 + 3); ++x) best = std::max(best, fine_.Score(region, x, y));
    }
  }
  return best;
}

}  // namespace clip
