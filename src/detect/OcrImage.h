#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string_view>
#include <vector>

namespace clip {

// How hard detection works to read the screen, set with reading_quality in clip_engine.ini.
// Low is how Clip Engine read the screen up to 0.5: one quick read of each region. Each step
// up reads small text bigger and smoother, and High and Ultra read every region more than one
// way and look more often, trading CPU time for fewer missed and misread moments.
enum class ReadQuality { Low, Medium, High, Ultra };

std::optional<ReadQuality> ParseReadQuality(std::string_view text);
std::string_view ToString(ReadQuality quality);

// How often a region is looked at, given how often its rule asks for: High looks a quarter
// more often and Ultra twice as often, so short popups are caught in more frames.
int ScaledSampleMs(int sampleMs, ReadQuality quality);

// How many different ways each region is read (see PrepareForOcr's pass).
int ReadPasses(ReadQuality quality);

// Whether a big banner on a screen taller than 2000 pixels is read at half size, which is
// quicker and usually as good. Only Low does it.
bool ShrinksBanners(ReadQuality quality);

// How much bigger (or smaller) a region is made before it's read. Small text reads better
// larger; shrink is 2 to read a region at half size, and a region too big for text
// recognition (maxDimension on a side) is shrunk to fit.
double OcrScale(uint32_t width, uint32_t height, uint32_t shrink, ReadQuality quality, uint32_t maxDimension);

// A picture ready for text recognition: one brightness per pixel, dark text on white.
struct OcrImage {
  uint32_t width = 0;
  uint32_t height = 0;
  std::vector<uint8_t> gray;
};

// Game HUD text is bright on a busy background, and text recognition does best with dark
// text on a light one, so each pixel's brightest channel is kept and inverted. With a
// threshold, pixels at least that bright become black and everything else white.
//
// From Medium up, small text is enlarged smoothly rather than in blocks, and the region's
// brightness is stretched to the full range so dim or washed-out text stands out. pass picks
// one of the ways of reading it (0 to ReadPasses - 1):
//   0  as above
//   1  without a threshold: cut at the brightness that best splits text from background
//      (Otsu's method), with the rarer side taken as the text, so dark text on a bright
//      banner reads too; with a threshold: the region as is, in case the threshold is too high
//   2  a second cut: without a threshold, halfway between the first cut and the brightest
//      pixels, which drops glow and clutter around bright text; with one, a quarter lower,
//      which keeps thin or dimmer strokes
OcrImage PrepareForOcr(const uint8_t* bgra, uint32_t width, uint32_t height, int threshold, uint32_t shrink,
                       ReadQuality quality, int pass, uint32_t maxDimension);

// The brightness that best splits a histogram in two (Otsu's method): pixels at least this
// bright are one side.
int OtsuThreshold(const std::array<uint32_t, 256>& histogram);

// How a region looked, on a coarse grid, to tell whether it changed since it was last read: in
// each cell, the average brightness and how many pixels are bright enough to be HUD text.
struct RegionLook {
  static constexpr int kGridX = 16;
  static constexpr int kGridY = 8;
  std::array<float, kGridX * kGridY> level{};  // average of each pixel's brightest channel, 0-255
  std::array<int, kGridX * kGridY> bright{};   // pixels at least as bright as HUD text
};

// HUD text is drawn bright, white or a strong color; most scenery isn't.
inline constexpr int kHudBright = 200;
// How many of a cell's pixels must turn bright or stop being so for text to have come or gone:
// a tenth of one small letter. Raw captured frames don't flicker, so text that stays put doesn't
// change them at all.
inline constexpr int kHudChangePixels = 8;

// bgra: width * height pixels. bright: how bright HUD text is at least (the rule's threshold,
// or kHudBright).
RegionLook LookAt(const std::vector<uint8_t>& bgra, uint32_t width, uint32_t height, int bright = kHudBright);

// Whether the region changed enough to be worth reading again. hudOnly: only HUD-bright pixels
// that came or went count, so scenery moving behind a killfeed doesn't make it read again when
// no text came or went (a region checked this way is also read once a second whatever it looks
// like, for text dimmer than that). Otherwise any change in brightness does, as before 0.10:
// video noise and compression move a cell's level by a level or so, and new text far more.
bool RegionChanged(const RegionLook& before, const RegionLook& now, bool hudOnly);

}  // namespace clip
