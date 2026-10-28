#pragma once

#include "core/Win.h"
#include "detect/TextMatcher.h"

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.Ocr.h>

#include <cstdint>
#include <vector>

namespace clip {

// English when it's installed, otherwise the user's languages. Null when Windows has no
// text recognition at all.
winrt::Windows::Media::Ocr::OcrEngine CreateOcrEngine();

// How much bigger (or smaller) a region is made before it's read. Small text reads better a
// little larger; shrink is 2 to read a big region of a very large screen at half size, which
// is as sharp as the same text on a 1080p screen and a quarter of the work.
double OcrScale(UINT width, UINT height, UINT shrink);

// Game HUD text is bright on a busy background. Text recognition does best with dark text
// on a light background, so keep each pixel's brightest channel and invert it. With a
// threshold, pixels at least that bright become black and everything else white.
winrt::Windows::Graphics::Imaging::SoftwareBitmap PrepareForOcr(const std::vector<uint8_t>& bgra, UINT width, UINT height,
                                                                int threshold, UINT shrink = 1);

// Reads a region's pixels the way detection does: the words, with their boxes in the
// region's own pixels, in reading order.
std::vector<WordBox> ReadWords(const winrt::Windows::Media::Ocr::OcrEngine& ocr, const std::vector<uint8_t>& bgra,
                               UINT width, UINT height, int threshold, UINT shrink = 1);

}  // namespace clip
