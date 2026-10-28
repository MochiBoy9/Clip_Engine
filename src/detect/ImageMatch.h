#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace clip {

// Finding an icon in part of the screen, for HUDs that show a kill as a picture rather than
// words (a skull, a kill banner, a hit marker). The icon comes from a screenshot of the game,
// so this learns from one example, and it runs on the CPU in a millisecond or two.

// A grayscale picture: each pixel the brightest of its color channels, as text recognition
// gets it, so it holds up whatever color the game tints the icon.
struct GrayImage {
  int width = 0;
  int height = 0;
  std::vector<float> pixels;
  std::vector<uint8_t> mask;  // which pixels count; empty when all of them do
};

// stride is bytes per row. With useAlpha, pixels less than half opaque don't count, so an
// icon cut out with a transparent background matches over any background.
GrayImage ToGray(const uint8_t* bgra, int width, int height, size_t stride, bool useAlpha);

// Averages (or repeats) pixels to make the image the given size.
GrayImage Resize(const GrayImage& image, int width, int height);

// An icon, ready to be looked for in a screen of some height.
class ImageMatcher {
 public:
  // pictureScreenHeight is the height of the screen the icon was cut from, or 0 for the same
  // screen it's looked for in.
  ImageMatcher(const GrayImage& picture, int pictureScreenHeight, int screenHeight);

  // False for an icon that's one flat color, which would match any flat patch of screen.
  bool Valid() const { return fine_.norm > 0; }

  // How alike the region is to the icon where they're most alike: 1 the same, 0 unrelated.
  double BestMatch(const GrayImage& region) const;

 private:
  // Icons are compared this many pixels across at most, which is enough to tell a skull from
  // a square, and searched for first at half that.
  static constexpr int kSearchSize = 32;

  // The icon at one size, less its mean, ready to be compared by normalized cross-correlation
  // over the pixels that count, so a brighter or dimmer screen doesn't matter, only the shape.
  struct Level {
    GrayImage icon;
    std::vector<float> weight;  // 1 for pixels that count, 0 for the rest
    double norm = 0;
    int count = 0;  // pixels that count
    void Prepare(GrayImage image);
    double Score(const GrayImage& region, int x, int y) const;
  };

  double scale_ = 1;  // screen pixels to search pixels
  Level fine_;
  Level coarse_;      // half the size; unused for small icons
  bool useCoarse_ = false;
};

}  // namespace clip
