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
  const UINT outWidth = std::min(width * scale, maxDimension);
  const UINT outHeight = std::min(height * scale, maxDimension);

  winrt::Windows::Storage::Streams::Buffer buffer(outWidth * outHeight * 4);
  buffer.Length(outWidth * outHeight * 4);
  uint8_t* out = buffer.data();
  for (UINT y = 0; y < outHeight; ++y) {
    const uint8_t* row = bgra.data() + static_cast<size_t>(y / scale) * width * 4;
    for (UINT x = 0; x < outWidth; ++x) {
      const uint8_t* p = row + static_cast<size_t>(x / scale) * 4;
      const uint8_t bright = std::max({p[0], p[1], p[2]});
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
