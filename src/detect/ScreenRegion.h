#pragma once

#include <string_view>

namespace clip {

// Which part of the screen a region keeps its place next to when the screen is wider or
// narrower than the one it was made for, or the game's HUD is set bigger or smaller.
enum class Anchor {
  Auto,    // decided by where the region is: see PlaceRegion
  Start,   // the left edge, or across the screen the top
  Center,  // the middle
  End,     // the right edge, or the bottom
};

// The shape most HUDs are laid out for, and that the shipped rules were made on.
inline constexpr double kDefaultAspect = 16.0 / 9.0;

// Part of the screen, as fractions of its width and height so one rule fits every resolution.
struct ScreenRegion {
  double left = 0;
  double top = 0;
  double width = 1;
  double height = 1;
  Anchor anchorX = Anchor::Auto;
  Anchor anchorY = Anchor::Auto;
  double aspect = 0;  // width / height of the screen it was made for; 0 means 16:9

  bool operator==(const ScreenRegion&) const = default;
};

// Where a region falls on a screen of this many pixels, with the game's HUD at hudScale (1 is
// 100%), as fractions of that screen.
//
// Games lay their HUD out for a 16:9 area and stretch it to the screen by keeping each part
// next to its edge or the middle: on an ultrawide the killfeed stays in the top-right corner
// and the kill banner in the middle, the same size as on a 16:9 screen of that height. On a
// 16:10 or 4:3 screen the same happens top to bottom. A bigger HUD grows away from the same
// edge or middle. Anchor::Auto takes the edge a region is next to: its middle in the first
// third of the screen keeps to the start, in the last third to the end, otherwise the middle.
// On a screen the shape the region was made for, at 100%, it stays exactly where it was.
ScreenRegion PlaceRegion(const ScreenRegion& region, double screenWidth, double screenHeight, double hudScale = 1);

// The height of screen that an icon cut at image_screen pixels is drawn at, scaled the same
// way: the height of the region's own 16:9 (or other) area on this screen, times hudScale.
double HudHeight(const ScreenRegion& region, double screenWidth, double screenHeight, double hudScale = 1);

// "top-right", "left", "bottom", "center", "auto" and the like, for anchor in detection.ini.
// An axis the text doesn't mention stays Auto. False when it isn't one of those.
bool ParseAnchor(std::string_view text, Anchor& x, Anchor& y);

// "16:9", "21:9", "32:9", "4:3" or a plain ratio like 2.39, for aspect in detection.ini.
bool ParseAspect(std::string_view text, double& aspect);

}  // namespace clip
