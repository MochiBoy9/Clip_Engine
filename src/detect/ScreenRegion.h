#pragma once

namespace clip {

// Part of the screen, as fractions of its width and height so one rule fits every resolution.
struct ScreenRegion {
  double left = 0;
  double top = 0;
  double width = 1;
  double height = 1;

  bool operator==(const ScreenRegion&) const = default;
};

}  // namespace clip
