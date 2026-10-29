#include "detect/Ocr.h"

#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Storage.Streams.h>

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

double OcrScale(UINT width, UINT height, UINT shrink, ReadQuality quality) {
  return clip::OcrScale(width, height, shrink, quality, OcrEngine::MaxImageDimension());
}

namespace {

SoftwareBitmap ToBitmap(const OcrImage& image) {
  const UINT bytes = image.width * image.height * 4;
  winrt::Windows::Storage::Streams::Buffer buffer(bytes);
  buffer.Length(bytes);
  uint8_t* out = buffer.data();
  for (const uint8_t v : image.gray) {
    out[0] = out[1] = out[2] = v;
    out[3] = 255;
    out += 4;
  }
  return SoftwareBitmap::CreateCopyFromBuffer(buffer, BitmapPixelFormat::Bgra8, static_cast<int32_t>(image.width),
                                              static_cast<int32_t>(image.height), BitmapAlphaMode::Premultiplied);
}

}  // namespace

std::vector<std::vector<WordBox>> ReadWords(const OcrEngine& ocr, const std::vector<uint8_t>& bgra, UINT width,
                                            UINT height, int threshold, UINT shrink, ReadQuality quality) {
  const UINT maxDimension = OcrEngine::MaxImageDimension();
  const double scale = clip::OcrScale(width, height, shrink, quality, maxDimension);
  std::vector<std::vector<WordBox>> reads;
  for (int pass = 0; pass < ReadPasses(quality); ++pass) {
    const OcrImage image = PrepareForOcr(bgra.data(), width, height, threshold, shrink, quality, pass, maxDimension);
    const auto result = ocr.RecognizeAsync(ToBitmap(image)).get();
    auto& words = reads.emplace_back();
    for (const auto& line : result.Lines()) {
      for (const auto& word : line.Words()) {
        const auto box = word.BoundingRect();
        words.push_back({winrt::to_string(word.Text()), box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale});
      }
    }
  }
  return reads;
}

}  // namespace clip
