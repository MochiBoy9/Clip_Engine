#pragma once

#include "core/Win.h"

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.Ocr.h>

#include <cstdint>
#include <vector>

namespace clip {

// English when it's installed, otherwise the user's languages. Null when Windows has no
// text recognition at all.
winrt::Windows::Media::Ocr::OcrEngine CreateOcrEngine();

// Game HUD text is bright on a busy background. Text recognition does best with dark text
// on a light background, so keep each pixel's brightest channel and invert it. With a
// threshold, pixels at least that bright become black and everything else white.
winrt::Windows::Graphics::Imaging::SoftwareBitmap PrepareForOcr(const std::vector<uint8_t>& bgra, UINT width, UINT height,
                                                                int threshold);

}  // namespace clip
