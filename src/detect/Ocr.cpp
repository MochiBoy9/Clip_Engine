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

SoftwareBitmap PrepareForOcr(const std::vector<uint8_t>& bgra, UINT width, UINT height, int threshold) {
  const UINT maxDimension = OcrEngine::MaxImageDimension();
  UINT scale = height < 120 ? 2 : 1;  // small text reads better a little larger
  if (width * scale > maxDimension || height * scale > maxDimension) scale = 1;
  // A big region (a whole scoreboard on a 4K screen) is shrunk to fit, keeping the brightest
  // pixel of each block so thin bright text survives.
  const UINT shrink = std::max<UINT>(1, (std::max(width, height) + maxDimension - 1) / maxDimension);
  const UINT outWidth = std::min(width * scale / shrink, maxDimension);
  const UINT outHeight = std::min(height * scale / shrink, maxDimension);

  auto brightness = [&](UINT x, UINT y) {
    const uint8_t* p = bgra.data() + (static_cast<size_t>(y) * width + x) * 4;
    return std::max({p[0], p[1], p[2]});
  };

  winrt::Windows::Storage::Streams::Buffer buffer(outWidth * outHeight * 4);
  buffer.Length(outWidth * outHeight * 4);
  uint8_t* out = buffer.data();
  for (UINT y = 0; y < outHeight; ++y) {
    for (UINT x = 0; x < outWidth; ++x) {
      uint8_t bright = 0;
      if (shrink == 1) {
        bright = brightness(x / scale, y / scale);
      } else {
        for (UINT dy = 0; dy < shrink; ++dy) {
          for (UINT dx = 0; dx < shrink; ++dx) {
            bright = std::max(bright, brightness(std::min(x * shrink + dx, width - 1), std::min(y * shrink + dy, height - 1)));
          }
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

}  // namespace clip
