#pragma once

#include "core/Win.h"
#include "detect/OcrImage.h"
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

// OcrScale (detect/OcrImage.h) for the largest picture Windows text recognition takes.
double OcrScale(UINT width, UINT height, UINT shrink, ReadQuality quality);

// Reads a region's pixels the way detection does: the words, with their boxes in the
// region's own pixels, in reading order. One read for each of the quality's ReadPasses.
std::vector<std::vector<WordBox>> ReadWords(const winrt::Windows::Media::Ocr::OcrEngine& ocr,
                                            const std::vector<uint8_t>& bgra, UINT width, UINT height, int threshold,
                                            UINT shrink, ReadQuality quality);

}  // namespace clip
