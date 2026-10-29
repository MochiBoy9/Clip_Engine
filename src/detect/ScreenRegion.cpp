#include "detect/ScreenRegion.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <string>
#include <vector>

namespace clip {
namespace {

// The 16:9 (or other) area the HUD is laid out in, in pixels: the screen's full height on a
// wider screen, its full width on a narrower one.
void DesignArea(const ScreenRegion& region, double width, double height, double& designW, double& designH) {
  const double aspect = region.aspect > 0 ? region.aspect : kDefaultAspect;
  if (width / height >= aspect) {
    designH = height;
    designW = height * aspect;
  } else {
    designW = width;
    designH = width / aspect;
  }
}

Anchor Resolve(Anchor anchor, double start, double size) {
  if (anchor != Anchor::Auto) return anchor;
  const double middle = start + size / 2;
  if (middle < 1.0 / 3) return Anchor::Start;
  if (middle > 2.0 / 3) return Anchor::End;
  return Anchor::Center;
}

// One axis: start and size as fractions of the design area, k the design area's length (times
// the HUD scale) over the screen's.
void PlaceAxis(Anchor anchor, double k, double& start, double& size) {
  const double placedSize = std::min(size * k, 1.0);
  double placed = start * k;
  if (anchor == Anchor::End) placed = 1 - (1 - (start + size)) * k - placedSize;
  if (anchor == Anchor::Center) placed = 0.5 + (start + size / 2 - 0.5) * k - placedSize / 2;
  size = placedSize;
  start = std::clamp(placed, 0.0, 1 - placedSize);
}

std::string Lower(std::string_view s) {
  std::string out(s);
  std::ranges::transform(out, out.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  return out;
}

bool ParseNumber(std::string_view text, double& value) {
  while (!text.empty() && text.front() == ' ') text.remove_prefix(1);
  while (!text.empty() && text.back() == ' ') text.remove_suffix(1);
  if (text.empty()) return false;
  const auto [ptr, ec] = std::from_chars(text.data(), text.data() + text.size(), value);
  return ec == std::errc() && ptr == text.data() + text.size();
}

}  // namespace

ScreenRegion PlaceRegion(const ScreenRegion& region, double screenWidth, double screenHeight, double hudScale) {
  ScreenRegion placed = region;
  if (screenWidth <= 0 || screenHeight <= 0) return placed;
  hudScale = std::clamp(hudScale, 0.25, 4.0);
  double designW = 0, designH = 0;
  DesignArea(region, screenWidth, screenHeight, designW, designH);
  placed.anchorX = Resolve(region.anchorX, region.left, region.width);
  placed.anchorY = Resolve(region.anchorY, region.top, region.height);
  PlaceAxis(placed.anchorX, designW * hudScale / screenWidth, placed.left, placed.width);
  PlaceAxis(placed.anchorY, designH * hudScale / screenHeight, placed.top, placed.height);
  placed.aspect = screenWidth / screenHeight;
  return placed;
}

double HudHeight(const ScreenRegion& region, double screenWidth, double screenHeight, double hudScale) {
  if (screenWidth <= 0 || screenHeight <= 0) return screenHeight;
  double designW = 0, designH = 0;
  DesignArea(region, screenWidth, screenHeight, designW, designH);
  return designH * std::clamp(hudScale, 0.25, 4.0);
}

bool ParseAnchor(std::string_view text, Anchor& x, Anchor& y) {
  std::vector<std::string> words;
  std::string word;
  for (const char c : Lower(text) + " ") {
    if (std::isalpha(static_cast<unsigned char>(c))) {
      word += c;
    } else if (!word.empty()) {
      words.push_back(std::move(word));
      word.clear();
    }
  }
  if (words.empty()) return false;
  Anchor ax = Anchor::Auto, ay = Anchor::Auto;
  bool setX = false, setY = false;
  int middles = 0;
  for (const auto& w : words) {
    if (w == "auto") continue;
    if (w == "left" || w == "right") {
      if (setX) return false;
      ax = w == "left" ? Anchor::Start : Anchor::End;
      setX = true;
    } else if (w == "top" || w == "bottom") {
      if (setY) return false;
      ay = w == "top" ? Anchor::Start : Anchor::End;
      setY = true;
    } else if (w == "center" || w == "centre" || w == "middle") {
      ++middles;
    } else {
      return false;
    }
  }
  // "center" is whichever axis the rest doesn't name, or both.
  if (middles > 0) {
    if (!setX && !setY) {
      ax = ay = Anchor::Center;
    } else if (!setX) {
      ax = Anchor::Center;
    } else if (!setY) {
      ay = Anchor::Center;
    } else {
      return false;
    }
  }
  x = ax;
  y = ay;
  return true;
}

bool ParseAspect(std::string_view text, double& aspect) {
  double value = 0;
  if (const auto colon = text.find(':'); colon != std::string_view::npos) {
    double w = 0, h = 0;
    if (!ParseNumber(text.substr(0, colon), w) || !ParseNumber(text.substr(colon + 1), h) || w <= 0 || h <= 0) return false;
    value = w / h;
  } else if (!ParseNumber(text, value)) {
    return false;
  }
  if (value < 0.5 || value > 6) return false;
  aspect = value;
  return true;
}

}  // namespace clip
