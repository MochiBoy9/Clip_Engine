#include "detect/RuleTester.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "detect/ImageMatch.h"
#include "detect/Ocr.h"

#include <mferror.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wincodec.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <cwctype>

namespace clip {
namespace {

bool IsImage(const std::filesystem::path& path) {
  std::wstring ext = path.extension().wstring();
  std::ranges::transform(ext, ext.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
  return ext == L".png" || ext == L".jpg" || ext == L".jpeg" || ext == L".bmp";
}

winrt::com_ptr<IWICImagingFactory> Wic() {
  winrt::com_ptr<IWICImagingFactory> factory;
  winrt::check_hresult(
      CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.put())));
  return factory;
}

}  // namespace

Picture LoadPicture(const std::filesystem::path& path) {
  const auto wic = Wic();
  winrt::com_ptr<IWICBitmapDecoder> decoder;
  winrt::check_hresult(wic->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand,
                                                      decoder.put()));
  winrt::com_ptr<IWICBitmapFrameDecode> source;
  winrt::check_hresult(decoder->GetFrame(0, source.put()));
  winrt::com_ptr<IWICFormatConverter> converter;
  winrt::check_hresult(wic->CreateFormatConverter(converter.put()));
  winrt::check_hresult(converter->Initialize(source.get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr,
                                             0, WICBitmapPaletteTypeCustom));
  Picture frame;
  winrt::check_hresult(converter->GetSize(&frame.width, &frame.height));
  frame.bgra.resize(static_cast<size_t>(frame.width) * frame.height * 4);
  winrt::check_hresult(converter->CopyPixels(nullptr, frame.width * 4, static_cast<UINT>(frame.bgra.size()), frame.bgra.data()));
  return frame;
}

namespace {

// Decodes the frame showing at `seconds` (or the last one, past the end). length gets the
// video's length in seconds, when it's known.
Picture LoadVideoFrame(const std::filesystem::path& path, double seconds, double* videoLength = nullptr) {
  winrt::com_ptr<IMFAttributes> attributes;
  winrt::check_hresult(MFCreateAttributes(attributes.put(), 1));
  winrt::check_hresult(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE));
  winrt::com_ptr<IMFSourceReader> reader;
  winrt::check_hresult(MFCreateSourceReaderFromURL(path.c_str(), attributes.get(), reader.put()));

  if (videoLength) {
    PROPVARIANT duration;
    PropVariantInit(&duration);
    if (SUCCEEDED(reader->GetPresentationAttribute(static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION,
                                                   &duration)) &&
        duration.vt == VT_UI8) {
      *videoLength = static_cast<double>(duration.uhVal.QuadPart) / kHnsPerSecond;
    }
    PropVariantClear(&duration);
  }

  const auto video = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
  winrt::check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
  winrt::check_hresult(reader->SetStreamSelection(video, TRUE));
  {
    winrt::com_ptr<IMFMediaType> type;
    winrt::check_hresult(MFCreateMediaType(type.put()));
    winrt::check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    winrt::check_hresult(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32));
    winrt::check_hresult(reader->SetCurrentMediaType(video, nullptr, type.get()));
  }
  winrt::com_ptr<IMFMediaType> current;
  winrt::check_hresult(reader->GetCurrentMediaType(video, current.put()));
  Picture frame;
  winrt::check_hresult(MFGetAttributeSize(current.get(), MF_MT_FRAME_SIZE, &frame.width, &frame.height));
  const auto stride = static_cast<INT32>(MFGetAttributeUINT32(current.get(), MF_MT_DEFAULT_STRIDE, frame.width * 4));

  const auto target = static_cast<int64_t>(std::llround(std::max(0.0, seconds) * kHnsPerSecond));
  PROPVARIANT position;
  PropVariantInit(&position);
  position.vt = VT_I8;
  position.hVal.QuadPart = target;
  winrt::check_hresult(reader->SetCurrentPosition(GUID_NULL, position));

  // Seeking lands on the keyframe before the target; decode forward to it.
  winrt::com_ptr<IMFSample> chosen;
  while (true) {
    DWORD index = 0, flags = 0;
    LONGLONG time = 0;
    winrt::com_ptr<IMFSample> sample;
    winrt::check_hresult(reader->ReadSample(video, 0, &index, &flags, &time, sample.put()));
    if (sample) chosen = sample;
    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) || (sample && time + kHnsPerSecond / 120 >= target)) break;
  }
  if (!chosen) throw winrt::hresult_error(MF_E_INVALIDREQUEST, L"There's no video at that time");

  winrt::com_ptr<IMFMediaBuffer> buffer;
  winrt::check_hresult(chosen->ConvertToContiguousBuffer(buffer.put()));
  BYTE* data = nullptr;
  DWORD length = 0;
  winrt::check_hresult(buffer->Lock(&data, nullptr, &length));
  const size_t rowBytes = static_cast<size_t>(frame.width) * 4;
  const size_t pitch = static_cast<size_t>(std::abs(stride));
  frame.bgra.resize(rowBytes * frame.height);
  for (UINT y = 0; y < frame.height && (y + 1) * pitch <= length; ++y) {
    // A negative stride means the rows are stored bottom-up.
    const UINT sourceRow = stride < 0 ? frame.height - 1 - y : y;
    std::memcpy(frame.bgra.data() + y * rowBytes, data + sourceRow * pitch, rowBytes);
  }
  buffer->Unlock();
  for (size_t i = 3; i < frame.bgra.size(); i += 4) frame.bgra[i] = 255;  // RGB32 leaves alpha undefined
  return frame;
}

Picture LoadFrame(const std::filesystem::path& path, double seconds, double* videoLength = nullptr) {
  return IsImage(path) ? LoadPicture(path) : LoadVideoFrame(path, seconds, videoLength);
}

void SavePng(const Picture& frame, const std::filesystem::path& path) {
  const auto wic = Wic();
  winrt::com_ptr<IWICStream> stream;
  winrt::check_hresult(wic->CreateStream(stream.put()));
  winrt::check_hresult(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE));
  winrt::com_ptr<IWICBitmapEncoder> encoder;
  winrt::check_hresult(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, encoder.put()));
  winrt::check_hresult(encoder->Initialize(stream.get(), WICBitmapEncoderNoCache));
  winrt::com_ptr<IWICBitmapFrameEncode> out;
  winrt::check_hresult(encoder->CreateNewFrame(out.put(), nullptr));
  winrt::check_hresult(out->Initialize(nullptr));
  winrt::check_hresult(out->SetSize(frame.width, frame.height));
  WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
  winrt::check_hresult(out->SetPixelFormat(&format));
  winrt::check_hresult(out->WritePixels(frame.height, frame.width * 4, static_cast<UINT>(frame.bgra.size()),
                                        const_cast<BYTE*>(frame.bgra.data())));
  winrt::check_hresult(out->Commit());
  winrt::check_hresult(encoder->Commit());
}

}  // namespace

int RunFrameCommand(const std::filesystem::path& input, const std::filesystem::path& output, double seconds) {
  try {
    double length = 0;
    const Picture frame = LoadFrame(input, seconds, &length);
    if (output.has_parent_path()) std::filesystem::create_directories(output.parent_path());
    SavePng(frame, output);
    log::Info("Frame {}x{} saved to {}", frame.width, frame.height, winrt::to_string(output.wstring()));
    if (length > 0) log::Info("Video length: {:.2f}", length);
    return 0;
  } catch (const winrt::hresult_error& e) {
    log::Error("Frame failed: {}", Describe(e));
  } catch (const std::exception& e) {
    log::Error("Frame failed: {}", e.what());
  }
  return 1;
}

int RunReadCommand(const std::filesystem::path& input, double seconds, const DetectionRule& rule, ReadQuality quality) {
  try {
    const auto ocr = CreateOcrEngine();
    if (!ocr) {
      log::Error("Read failed: Windows text recognition isn't installed. Add English under Settings > Time & language > Language.");
      return 1;
    }
    const Picture frame = LoadFrame(input, seconds);
    const auto left = static_cast<UINT>(std::floor(rule.region.left * frame.width));
    const auto top = static_cast<UINT>(std::floor(rule.region.top * frame.height));
    const auto right = std::min(frame.width, static_cast<UINT>(std::ceil((rule.region.left + rule.region.width) * frame.width)));
    const auto bottom = std::min(frame.height, static_cast<UINT>(std::ceil((rule.region.top + rule.region.height) * frame.height)));
    if (right < left + 8 || bottom < top + 8) {
      log::Error("Read failed: the box is too small to read");
      return 1;
    }

    // The same pixels detection copies off the screen.
    const UINT width = right - left;
    const UINT height = bottom - top;
    std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
    for (UINT y = 0; y < height; ++y) {
      std::memcpy(pixels.data() + static_cast<size_t>(y) * width * 4,
                  frame.bgra.data() + (static_cast<size_t>(top + y) * frame.width + left) * 4, static_cast<size_t>(width) * 4);
    }
    // Big banners on a very large screen are read at half size at Low, as detection reads them.
    const bool banner = rule.position == LinePosition::Anywhere && rule.event != EventType::Kill &&
                        rule.event != EventType::Assist && rule.event != EventType::Revive;
    const UINT shrink = ShrinksBanners(quality) && banner && frame.height >= 2000 && height >= 480 ? 2 : 1;
    const auto reads = ReadWords(ocr, pixels, width, height, rule.threshold, shrink, quality);
    std::vector<std::vector<std::vector<std::string>>> rows;
    for (const auto& words : reads) rows.push_back(GroupIntoRows(words));
    const size_t chosen = rule.ReadsText() ? ChooseRead(rule, rows) : 0;
    std::string text;
    for (const auto& word : reads[chosen]) text += (text.empty() ? "" : " ") + word.text;
    log::Info("Read: {}", text);
    int matches = rule.ReadsText() ? CountMatches(rule, rows[chosen]) : 0;

    if (rule.LooksForImage()) {
      const Picture icon = LoadPicture(std::filesystem::path(winrt::to_hstring(rule.image).c_str()));
      const ImageMatcher matcher(ToGray(icon.bgra.data(), static_cast<int>(icon.width), static_cast<int>(icon.height),
                                        icon.width * 4, true),
                                 rule.imageScreen, static_cast<int>(frame.height));
      if (!matcher.Valid()) {
        log::Error("Read failed: the image is one flat color, so it would match anything flat");
        return 1;
      }
      const double alike = matcher.BestMatch(
          ToGray(pixels.data(), static_cast<int>(width), static_cast<int>(height), static_cast<size_t>(width) * 4, false));
      log::Info("Image: {:.2f}", alike);
      if (alike >= rule.imageMatch) ++matches;
    }
    log::Info("Matches: {}", matches);
    return 0;
  } catch (const winrt::hresult_error& e) {
    log::Error("Read failed: {}", Describe(e));
  } catch (const std::exception& e) {
    log::Error("Read failed: {}", e.what());
  }
  return 1;
}

}  // namespace clip
