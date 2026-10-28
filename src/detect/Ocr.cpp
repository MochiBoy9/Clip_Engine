#include "detect/Ocr.h"

#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Storage.Streams.h>

#include <algorithm>

namespace clip {

using winrt::Windows::Graphics::Imaging::BitmapAlphaMode;
using winrt::Windows::Graphics::Imaging::BitmapPixelFormat;
using winrt::Windows::Graphics::Imaging::SoftwareBitmap;
using winrt::Windows::Media::Ocr::OcrEngine;

OcrEngine CreateOcrEngine() {
  const winrt::Windows::Globalization::Language english(L"en-US");
  if (OcrEngine::IsLanguageSupported(english)) return OcrEngine::TryCreateFromLanguage(english);
  return OcrEngine::TryCreateFromUserProfileLanguages();
}

double OcrScale(UINT width, UINT height, UINT shrink) {
  if (shrink > 1) return 1.0 / shrink;
  const UINT maxDimension = OcrEngine::MaxImageDimension();
  return height < 120 && width * 2 <= maxDimension && height * 2 <= maxDimension ? 2.0 : 1.0;
}

SoftwareBitmap PrepareForOcr(const std::vector<uint8_t>& bgra, UINT width, UINT height, int threshold, UINT shrink) {
  const UINT maxDimension = OcrEngine::MaxImageDimension();
  shrink = std::max(shrink, 1u);
  const UINT grow = OcrScale(width, height, shrink) > 1 ? 2 : 1;
  const UINT outWidth = std::min(width * grow / shrink, maxDimension);
  const UINT outHeight = std::min(height * grow / shrink, maxDimension);

  winrt::Windows::Storage::Streams::Buffer buffer(outWidth * outHeight * 4);
  buffer.Length(outWidth * outHeight * 4);
  uint8_t* out = buffer.data();
  auto brightness = [&](UINT x, UINT y) {
    const uint8_t* p = bgra.data() + (static_cast<size_t>(y) * width + x) * 4;
    return std::max({p[0], p[1], p[2]});
  };
  for (UINT y = 0; y < outHeight; ++y) {
    for (UINT x = 0; x < outWidth; ++x) {
      uint8_t bright = 0;
      if (shrink == 1) {
        bright = brightness(x / grow, y / grow);
      } else {
        // Shrinking keeps the brightest pixel of each block, so thin bright strokes survive.
        for (UINT sy = y * shrink; sy < std::min(height, (y + 1) * shrink); ++sy) {
          for (UINT sx = x * shrink; sx < std::min(width, (x + 1) * shrink); ++sx) bright = std::max(bright, brightness(sx, sy));
        }
      }
      const uint8_t v = threshold > 0 ? (bright >= threshold ? 0 : 255) : static_cast<uint8_t>(255 - bright);
      out[0] = out[1] = out[2] = v;
      out[3] = 255;
      out += 4;
    }
  }
  return SoftwareBitmap::CreateCopyFromBuffer(buffer, BitmapPixelFormat::Bgra8, static_cast<int32_t>(outWidth),
                                              static_cast<int32_t>(outHeight), BitmapAlphaMode::Premultiplied);
}

std::vector<WordBox> ReadWords(const OcrEngine& ocr, const std::vector<uint8_t>& bgra, UINT width, UINT height,
                               int threshold, UINT shrink) {
  const double scale = OcrScale(width, height, shrink);
  const auto result = ocr.RecognizeAsync(PrepareForOcr(bgra, width, height, threshold, shrink)).get();
  std::vector<WordBox> words;
  for (const auto& line : result.Lines()) {
    for (const auto& word : line.Words()) {
      const auto box = word.BoundingRect();
      words.push_back({winrt::to_string(word.Text()), box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale});
    }
  }
  return words;
}

}  // namespace clip
