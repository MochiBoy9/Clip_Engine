#include "detect/RuleTester.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "detect/EventFuser.h"
#include "detect/ImageMatch.h"
#include "detect/SoundWatch.h"
#include "detect/Ocr.h"

#include <mferror.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wincodec.h>

#include <algorithm>
#include <compare>
#include <cmath>
#include <cstring>
#include <cwctype>
#include <format>
#include <map>
#include <memory>
#include <optional>

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

// A video opened for reading its frames as BGRA, in order. length gets its length in seconds,
// when it's known.
struct VideoReader {
  winrt::com_ptr<IMFSourceReader> reader;
  UINT32 width = 0;
  UINT32 height = 0;
  INT32 stride = 0;

  explicit VideoReader(const std::filesystem::path& path, double* length = nullptr) {
    winrt::com_ptr<IMFAttributes> attributes;
    winrt::check_hresult(MFCreateAttributes(attributes.put(), 1));
    winrt::check_hresult(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE));
    winrt::check_hresult(MFCreateSourceReaderFromURL(path.c_str(), attributes.get(), reader.put()));

    if (length) {
      PROPVARIANT duration;
      PropVariantInit(&duration);
      if (SUCCEEDED(reader->GetPresentationAttribute(static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION,
                                                     &duration)) &&
          duration.vt == VT_UI8) {
        *length = static_cast<double>(duration.uhVal.QuadPart) / kHnsPerSecond;
      }
      PropVariantClear(&duration);
    }

    winrt::check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
    winrt::check_hresult(reader->SetStreamSelection(Stream(), TRUE));
    {
      winrt::com_ptr<IMFMediaType> type;
      winrt::check_hresult(MFCreateMediaType(type.put()));
      winrt::check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
      winrt::check_hresult(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32));
      winrt::check_hresult(reader->SetCurrentMediaType(Stream(), nullptr, type.get()));
    }
    winrt::com_ptr<IMFMediaType> current;
    winrt::check_hresult(reader->GetCurrentMediaType(Stream(), current.put()));
    winrt::check_hresult(MFGetAttributeSize(current.get(), MF_MT_FRAME_SIZE, &width, &height));
    stride = static_cast<INT32>(MFGetAttributeUINT32(current.get(), MF_MT_DEFAULT_STRIDE, width * 4));
  }

  static DWORD Stream() { return static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM); }

  void Seek(double seconds) {
    PROPVARIANT position;
    PropVariantInit(&position);
    position.vt = VT_I8;
    position.hVal.QuadPart = static_cast<int64_t>(std::llround(std::max(0.0, seconds) * kHnsPerSecond));
    winrt::check_hresult(reader->SetCurrentPosition(GUID_NULL, position));
  }

  // The next frame and its time, or null at the end of the video.
  winrt::com_ptr<IMFSample> Next(int64_t& time) {
    while (true) {
      DWORD index = 0, flags = 0;
      LONGLONG at = 0;
      winrt::com_ptr<IMFSample> sample;
      winrt::check_hresult(reader->ReadSample(Stream(), 0, &index, &flags, &at, sample.put()));
      if (sample) {
        time = at;
        return sample;
      }
      if (flags & MF_SOURCE_READERF_ENDOFSTREAM) return nullptr;
    }
  }

  Picture ToPicture(IMFSample* sample) const {
    winrt::com_ptr<IMFMediaBuffer> buffer;
    winrt::check_hresult(sample->ConvertToContiguousBuffer(buffer.put()));
    BYTE* data = nullptr;
    DWORD bytes = 0;
    winrt::check_hresult(buffer->Lock(&data, nullptr, &bytes));
    Picture frame;
    frame.width = width;
    frame.height = height;
    const size_t rowBytes = static_cast<size_t>(width) * 4;
    const size_t pitch = static_cast<size_t>(std::abs(stride));
    frame.bgra.resize(rowBytes * height);
    for (UINT y = 0; y < height && (y + 1) * pitch <= bytes; ++y) {
      // A negative stride means the rows are stored bottom-up.
      const UINT sourceRow = stride < 0 ? height - 1 - y : y;
      std::memcpy(frame.bgra.data() + y * rowBytes, data + sourceRow * pitch, rowBytes);
    }
    buffer->Unlock();
    for (size_t i = 3; i < frame.bgra.size(); i += 4) frame.bgra[i] = 255;  // RGB32 leaves alpha undefined
    return frame;
  }
};

// A video's game audio (its first audio track), read alongside its frames for the rules that
// listen for a sound. Throws when the video has no audio.
struct AudioReader {
  winrt::com_ptr<IMFSourceReader> reader;
  UINT32 rate = 48000;
  UINT32 channels = 2;
  int64_t position = 0;  // where the audio read so far ends
  bool done = false;

  explicit AudioReader(const std::filesystem::path& path) {
    winrt::check_hresult(MFCreateSourceReaderFromURL(path.c_str(), nullptr, reader.put()));
    winrt::check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
    winrt::check_hresult(reader->SetStreamSelection(Stream(), TRUE));
    winrt::com_ptr<IMFMediaType> type;
    winrt::check_hresult(MFCreateMediaType(type.put()));
    winrt::check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
    winrt::check_hresult(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
    winrt::check_hresult(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
    winrt::check_hresult(reader->SetCurrentMediaType(Stream(), nullptr, type.get()));
    winrt::com_ptr<IMFMediaType> current;
    winrt::check_hresult(reader->GetCurrentMediaType(Stream(), current.put()));
    rate = MFGetAttributeUINT32(current.get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
    channels = std::max<UINT32>(1, MFGetAttributeUINT32(current.get(), MF_MT_AUDIO_NUM_CHANNELS, 2));
  }

  static DWORD Stream() { return static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM); }

  void Seek(double seconds) {
    PROPVARIANT at;
    PropVariantInit(&at);
    at.vt = VT_I8;
    at.hVal.QuadPart = static_cast<int64_t>(std::llround(std::max(0.0, seconds) * kHnsPerSecond));
    winrt::check_hresult(reader->SetCurrentPosition(GUID_NULL, at));
    position = 0;
    done = false;
  }

  // The next stretch of audio and its time, or false at the end.
  bool Next(std::vector<int16_t>& pcm, int64_t& time) {
    while (true) {
      DWORD index = 0, flags = 0;
      LONGLONG at = 0;
      winrt::com_ptr<IMFSample> sample;
      winrt::check_hresult(reader->ReadSample(Stream(), 0, &index, &flags, &at, sample.put()));
      if (sample) {
        winrt::com_ptr<IMFMediaBuffer> buffer;
        winrt::check_hresult(sample->ConvertToContiguousBuffer(buffer.put()));
        BYTE* data = nullptr;
        DWORD bytes = 0;
        winrt::check_hresult(buffer->Lock(&data, nullptr, &bytes));
        pcm.assign(reinterpret_cast<const int16_t*>(data), reinterpret_cast<const int16_t*>(data) + bytes / 2);
        buffer->Unlock();
        time = at;
        position = at + static_cast<int64_t>(pcm.size() / channels) * kHnsPerSecond / rate;
        return true;
      }
      if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
        done = true;
        return false;
      }
    }
  }

  // Hands the watch the audio up to `until` (100 ns units).
  void ReadUntil(int64_t until, SoundWatch& watch, const SoundWatch::Found& found) {
    std::vector<int16_t> pcm;
    int64_t at = 0;
    while (!done && position < until && Next(pcm, at)) {
      watch.Push(pcm.data(), pcm.size() / channels, static_cast<int>(rate), static_cast<int>(channels), at, found);
    }
  }
};

// Decodes the frame showing at `seconds` (or the last one, past the end). videoLength gets the
// video's length in seconds, when it's known.
Picture LoadVideoFrame(const std::filesystem::path& path, double seconds, double* videoLength = nullptr) {
  VideoReader video(path, videoLength);
  const auto target = static_cast<int64_t>(std::llround(std::max(0.0, seconds) * kHnsPerSecond));
  video.Seek(seconds);

  // Seeking lands on the keyframe before the target; decode forward to it.
  winrt::com_ptr<IMFSample> chosen;
  int64_t time = 0;
  while (auto sample = video.Next(time)) {
    chosen = sample;
    if (time + kHnsPerSecond / 120 >= target) break;
  }
  if (!chosen) throw winrt::hresult_error(MF_E_INVALIDREQUEST, L"There's no video at that time");
  return video.ToPicture(chosen.get());
}

// The pixels of part of a frame, as detection copies them off the screen.
std::vector<uint8_t> Crop(const Picture& frame, UINT left, UINT top, UINT width, UINT height) {
  std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
  for (UINT y = 0; y < height; ++y) {
    std::memcpy(pixels.data() + static_cast<size_t>(y) * width * 4,
                frame.bgra.data() + (static_cast<size_t>(top + y) * frame.width + left) * 4, static_cast<size_t>(width) * 4);
  }
  return pixels;
}

// The pixel box of a region placed on a frame.
struct PixelBox {
  UINT left = 0, top = 0, right = 0, bottom = 0;
  bool TooSmall() const { return right < left + 8 || bottom < top + 8; }
  auto operator<=>(const PixelBox&) const = default;
};

PixelBox BoxOn(const ScreenRegion& region, UINT width, UINT height) {
  PixelBox box;
  box.left = static_cast<UINT>(std::floor(region.left * width));
  box.top = static_cast<UINT>(std::floor(region.top * height));
  box.right = std::min(width, static_cast<UINT>(std::ceil((region.left + region.width) * width)));
  box.bottom = std::min(height, static_cast<UINT>(std::ceil((region.top + region.height) * height)));
  return box;
}

// Big banners on a very large screen are read at half size at Low, as detection reads them.
UINT BannerShrink(const DetectionRule& rule, ReadQuality quality, UINT screenHeight, UINT boxHeight) {
  const bool banner = rule.position == LinePosition::Anywhere && rule.event != EventType::Kill &&
                      rule.event != EventType::Assist && rule.event != EventType::Revive;
  return ShrinksBanners(quality) && banner && screenHeight >= 2000 && boxHeight >= 480 ? 2 : 1;
}

ImageMatcher IconMatcher(const DetectionRule& rule, UINT width, UINT height, double hudScale) {
  const Picture icon = LoadPicture(std::filesystem::path(winrt::to_hstring(rule.image).c_str()));
  const int pictureScreen = rule.imageScreen > 0 ? rule.imageScreen : static_cast<int>(height);
  const double drawnAt = rule.imageScreen > 0 ? HudHeight(rule.region, width, height, hudScale) : height * hudScale;
  return ImageMatcher(ToGray(icon.bgra.data(), static_cast<int>(icon.width), static_cast<int>(icon.height), icon.width * 4, true),
                      pictureScreen, static_cast<int>(std::lround(drawnAt)));
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

int RunReadCommand(const std::filesystem::path& input, double seconds, const DetectionRule& rule, ReadQuality quality,
                   double hudScale) {
  try {
    const auto ocr = CreateOcrEngine();
    if (!ocr) {
      log::Error("Read failed: Windows text recognition isn't installed. Add English under Settings > Time & language > Language.");
      return 1;
    }
    const Picture frame = LoadFrame(input, seconds);
    // Where detection would look on a screen this shape, with the HUD at this size.
    const PixelBox box = BoxOn(PlaceRegion(rule.region, frame.width, frame.height, hudScale), frame.width, frame.height);
    if (box.TooSmall()) {
      log::Error("Read failed: the box is too small to read");
      return 1;
    }

    // The same pixels detection copies off the screen.
    const UINT width = box.right - box.left;
    const UINT height = box.bottom - box.top;
    const auto pixels = Crop(frame, box.left, box.top, width, height);
    const auto reads = ReadWords(ocr, pixels, width, height, rule.threshold, BannerShrink(rule, quality, frame.height, height), quality);
    std::vector<std::vector<std::vector<std::string>>> rows;
    for (const auto& words : reads) rows.push_back(GroupIntoRows(words));
    const size_t chosen = rule.ReadsText() ? ChooseRead(rule, rows) : 0;
    std::string text;
    for (const auto& word : reads[chosen]) text += (text.empty() ? "" : " ") + word.text;
    log::Info("Read: {}", text);
    int matches = rule.ReadsText() ? CountMatches(rule, rows[chosen]) : 0;

    if (rule.LooksForImage()) {
      const ImageMatcher matcher = IconMatcher(rule, frame.width, frame.height, hudScale);
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

int RunTrackCommand(const std::filesystem::path& input, const DetectionRule& rule, ReadQuality quality, double hudScale,
                    int everyMs) {
  try {
    const auto ocr = CreateOcrEngine();
    if (!ocr && rule.ReadsText()) {
      log::Error("Track failed: Windows text recognition isn't installed. Add English under Settings > Time & language > Language.");
      return 1;
    }
    double length = 0;
    VideoReader video(input, &length);
    const PixelBox box = BoxOn(PlaceRegion(rule.region, video.width, video.height, hudScale), video.width, video.height);
    if (box.TooSmall()) {
      log::Error("Track failed: the box is too small to read");
      return 1;
    }
    std::optional<ImageMatcher> matcher;
    if (rule.LooksForImage()) matcher.emplace(IconMatcher(rule, video.width, video.height, hudScale));
    const UINT width = box.right - box.left;
    const UINT height = box.bottom - box.top;
    const int intervalMs = std::clamp(everyMs, 50, 5000);

    int reads = 0, lastPercent = -1;
    int64_t dueMs = 0, time = 0;
    std::string lastText = "\n";
    int lastMatches = -1;
    while (auto sample = video.Next(time)) {
      const int64_t ms = time / kHnsPerMs;
      if (ms < dueMs) continue;
      dueMs = ms + intervalMs;
      const Picture frame = video.ToPicture(sample.get());
      const auto pixels = Crop(frame, box.left, box.top, width, height);
      std::string text;
      int matches = 0;
      if (rule.ReadsText()) {
        const auto read = ReadWords(ocr, pixels, width, height, rule.threshold, BannerShrink(rule, quality, frame.height, height), quality);
        std::vector<std::vector<std::vector<std::string>>> rows;
        for (const auto& words : read) rows.push_back(GroupIntoRows(words));
        if (rows.empty()) rows.emplace_back();
        const size_t chosen = ChooseRead(rule, rows);
        if (chosen < read.size()) {
          for (const auto& word : read[chosen]) text += (text.empty() ? "" : " ") + word.text;
        }
        matches = CountMatches(rule, rows[chosen]);
      }
      if (matcher) {
        const double alike = matcher->BestMatch(
            ToGray(pixels.data(), static_cast<int>(width), static_cast<int>(height), static_cast<size_t>(width) * 4, false));
        text = std::format("{}image {:.2f} alike", text.empty() ? std::string() : text + " | ", alike);
        if (alike >= rule.imageMatch) ++matches;
      }
      ++reads;
      // Only what changed, so a long clip doesn't print the same read hundreds of times.
      if (text != lastText || matches != lastMatches) {
        log::Info("Track at {:.2f}: {} | {}", ms / 1000.0, matches, text);
        lastText = text;
        lastMatches = matches;
      }
      if (length > 0) {
        const int percent = std::clamp(static_cast<int>(ms / 10.0 / length), 0, 100);
        if (percent / 5 != lastPercent / 5) {
          lastPercent = percent;
          log::Info("Track progress {}%", percent);
        }
      }
    }
    log::Info("Track done: {} reads", reads);
    return 0;
  } catch (const winrt::hresult_error& e) {
    log::Error("Track failed: {}", Describe(e));
  } catch (const std::exception& e) {
    log::Error("Track failed: {}", e.what());
  }
  return 1;
}

int RunCutSoundCommand(const std::filesystem::path& input, const std::filesystem::path& output, double seconds) {
  try {
    AudioReader audio(input);
    // From the lead-in before the sound to a second after it.
    const double from = std::max(0.0, seconds - kSoundLeadIn / 1000.0);
    const double to = seconds + 1.0;
    audio.Seek(from);
    SoundResampler resampler(static_cast<int>(audio.rate), static_cast<int>(audio.channels));
    std::vector<float> cut;
    std::vector<int16_t> pcm;
    int64_t at = 0;
    const auto fromHns = static_cast<int64_t>(from * kHnsPerSecond);
    const auto toHns = static_cast<int64_t>(to * kHnsPerSecond);
    while (audio.position < toHns && audio.Next(pcm, at)) {
      // Seeking lands a little early; drop what comes before the cut.
      const size_t frames = pcm.size() / audio.channels;
      size_t skip = 0;
      if (at < fromHns) skip = std::min<size_t>(frames, static_cast<size_t>((fromHns - at) * audio.rate / kHnsPerSecond));
      resampler.Push(pcm.data() + skip * audio.channels, frames - skip, cut);
    }
    cut.resize(std::min(cut.size(), static_cast<size_t>((to - from) * kSoundRate)));
    if (cut.size() < static_cast<size_t>(kSoundRate) / 2) {
      log::Error("Cut failed: there isn't enough of the clip's audio there");
      return 1;
    }
    if (output.has_parent_path()) std::filesystem::create_directories(output.parent_path());
    if (!SaveSoundCut(output, cut)) {
      log::Error("Cut failed: couldn't write {}", winrt::to_string(output.wstring()));
      return 1;
    }
    log::Info("Sound saved to {}", winrt::to_string(output.wstring()));
    return 0;
  } catch (const winrt::hresult_error& e) {
    log::Error("Cut failed: {}", Describe(e));
  } catch (const std::exception& e) {
    log::Error("Cut failed: {}", e.what());
  }
  return 1;
}

int RunScanCommand(const std::filesystem::path& input, const std::vector<DetectionRule>& rules, ReadQuality quality,
                   double hudScale, const std::vector<JournalRule>& journals) {
  try {
    if (rules.empty() && journals.empty()) {
      log::Error("Scan failed: this game has no rules that read the screen");
      return 1;
    }
    const auto ocr = CreateOcrEngine();
    double length = 0;
    VideoReader video(input, &length);

    // Each rule with its own tracker, as detection runs it in game.
    struct Watch {
      explicit Watch(const DetectionRule& r) : rule(r), tracker(rule) {}
      DetectionRule rule;
      RuleTracker tracker;
      PixelBox box;
      std::optional<ImageMatcher> matcher;
      int intervalMs = 250;
      int64_t dueMs = 0;
    };
    std::vector<std::unique_ptr<Watch>> watches;
    for (const auto& rule : rules) {
      if (rule.ListensForSound()) continue;  // see sounds below
      if (rule.ReadsText() && !ocr && !rule.LooksForImage()) continue;
      auto watch = std::make_unique<Watch>(rule);
      watch->box = BoxOn(PlaceRegion(rule.region, video.width, video.height, hudScale), video.width, video.height);
      if (watch->box.TooSmall()) continue;
      if (rule.LooksForImage()) {
        try {
          watch->matcher.emplace(IconMatcher(rule, video.width, video.height, hudScale));
        } catch (const winrt::hresult_error& e) {
          log::Warn("Scan: couldn't open {} for {} ({})", rule.image, rule.name, Describe(e));
          continue;
        }
        if (!watch->matcher->Valid()) continue;
      }
      watch->intervalMs = ScaledSampleMs(rule.sampleMs > 0 ? rule.sampleMs : 250, quality);
      watches.push_back(std::move(watch));
    }
    // Journal rules, each with its tracker, and the box that opens it if it has a when_region.
    struct JournalWatch {
      explicit JournalWatch(const JournalRule& r) : rule(r), tracker(rule) {}
      JournalRule rule;
      JournalTracker tracker;
      PixelBox box;
      std::optional<PixelBox> gate;
      int intervalMs = 1000;
      int64_t dueMs = 0;
      int64_t gateSeenMs = INT64_MIN / 2;
    };
    std::vector<std::unique_ptr<JournalWatch>> journalWatches;
    for (const auto& journal : ocr ? journals : std::vector<JournalRule>{}) {
      auto watch = std::make_unique<JournalWatch>(journal);
      watch->box = BoxOn(PlaceRegion(journal.region, video.width, video.height, hudScale), video.width, video.height);
      if (watch->box.TooSmall()) continue;
      if (journal.whenRegion && !journal.when.empty()) {
        const PixelBox gate = BoxOn(PlaceRegion(*journal.whenRegion, video.width, video.height, hudScale), video.width, video.height);
        if (!gate.TooSmall()) watch->gate = gate;
      }
      watch->intervalMs = ScaledSampleMs(journal.sampleMs, quality);
      journalWatches.push_back(std::move(watch));
    }
    int noted = 0;

    // The rules that listen hear the clip's game audio as it goes.
    SoundWatch sounds(rules, [](const std::string& warning) { log::Warn("Scan: {}", warning); });
    std::optional<AudioReader> audio;
    if (!sounds.Empty()) {
      try {
        audio.emplace(input);
      } catch (const winrt::hresult_error& e) {
        log::Warn("Scan: the clip's audio can't be read ({}), so its sounds aren't listened for", Describe(e));
      }
    }
    if (watches.empty() && journalWatches.empty() && !audio) {
      if (ocr) log::Error("Scan failed: none of this game's rules fit the video");
      else log::Error("Scan failed: Windows text recognition isn't installed. Add English under Settings > Time & language > Language.");
      return 1;
    }
    log::Info("Scanning {}x{} video with {} rules at reading quality {}", video.width, video.height, watches.size(),
              ToString(quality));

    // Moments seen by more than one rule count once, as in game.
    int found = 0;
    EventFuser fuser(
        [&](const GameEvent& e) {
          log::Info("Scan found {} at {:.2f}: {}", EventName(e.type), static_cast<double>(e.time) / kHnsPerSecond, e.text);
          for (const auto& w : e.witnesses) {
            log::Info("Scan witness {} {:+.2f}s {}: {}", w.rule, w.offset, w.counted ? "counted" : "agreed", w.text);
          }
          ++found;
        },
        [](const std::string& note) { log::Info("Scan note: {}", note); });
    if (!sounds.Empty()) fuser.SetHoldMs(EventFuser::kSoundHoldMs);
    // Kills in a killcam or while spectating aren't the player's, as in game.
    if (std::ranges::any_of(watches, [](const auto& w) { return w->rule.pauses; })) fuser.UsePauses();
    int lastPercent = -1;
    int64_t time = 0;
    while (auto sample = video.Next(time)) {
      const int64_t ms = time / kHnsPerMs;
      if (audio) {
        audio->ReadUntil(time, sounds, [&](const DetectionRule& rule, int count, int64_t at, const std::string& text) {
          fuser.Add(rule, count, at, text);
        });
      }
      fuser.Flush(time);
      if (std::ranges::none_of(watches, [&](const auto& w) { return w->dueMs <= ms; }) &&
          std::ranges::none_of(journalWatches, [&](const auto& w) { return w->dueMs <= ms; })) {
        continue;
      }
      const Picture frame = video.ToPicture(sample.get());
      // Rules that look at the same box share one read of it.
      std::map<std::pair<PixelBox, int>, std::vector<std::vector<WordBox>>> readsOf;
      auto rowsOf = [&](const PixelBox& box, int threshold) {
        auto key = std::make_pair(box, threshold);
        auto cached = readsOf.find(key);
        if (cached == readsOf.end()) {
          const UINT w = box.right - box.left;
          const UINT h = box.bottom - box.top;
          const auto pixels = Crop(frame, box.left, box.top, w, h);
          cached = readsOf.emplace(key, ReadWords(ocr, pixels, w, h, threshold, 1, quality)).first;
        }
        std::vector<std::vector<std::vector<std::string>>> rows;
        for (const auto& words : cached->second) rows.push_back(GroupIntoRows(words));
        if (rows.empty()) rows.emplace_back();
        return rows;
      };
      for (auto& watch : journalWatches) {
        if (watch->dueMs > ms) continue;
        watch->dueMs = ms + watch->intervalMs;
        bool open = true;
        if (watch->gate) {
          const auto gateRows = rowsOf(*watch->gate, watch->rule.threshold);
          if (std::ranges::any_of(gateRows, [&](const auto& r) { return ShowsAny(watch->rule.when, r); })) watch->gateSeenMs = ms;
          open = ms - watch->gateSeenMs <= std::max<int64_t>(2500, int64_t{2} * watch->intervalMs + 500);
          // As in game, the box itself isn't read while its menu is closed.
          if (!open) {
            watch->tracker.Update({}, ms, false);
            continue;
          }
        }
        const auto rows = rowsOf(watch->box, watch->rule.threshold);
        size_t chosen = 0, most = 0;
        for (size_t i = 0; rows.size() > 1 && i < rows.size(); ++i) {
          const size_t n = FindNotes(watch->rule, rows[i]).size();
          if (n > most) most = n, chosen = i;
        }
        for (const auto& note : watch->tracker.Update(rows[chosen], ms, open)) {
          ++noted;
          log::Info("Scan journal {} at {:.2f}: {}{}{}", note.note, ms / 1000.0, note.value.empty() ? note.text : note.value,
                    note.amount != 0 ? std::format(" ({})", note.amount) : "", note.xp != 0 ? std::format(" +{} XP", note.xp) : "");
        }
      }
      for (auto& watch : watches) {
        if (watch->dueMs > ms) continue;
        watch->dueMs = ms + watch->intervalMs;
        const DetectionRule& rule = watch->rule;
        const UINT width = watch->box.right - watch->box.left;
        const UINT height = watch->box.bottom - watch->box.top;
        std::vector<uint8_t> pixels;
        int fresh = 0;
        std::string text;
        if (rule.ReadsText() && ocr) {
          auto key = std::make_pair(watch->box, rule.threshold);
          auto cached = readsOf.find(key);
          if (cached == readsOf.end()) {
            pixels = Crop(frame, watch->box.left, watch->box.top, width, height);
            cached = readsOf.emplace(key, ReadWords(ocr, pixels, width, height, rule.threshold,
                                                    BannerShrink(rule, quality, frame.height, height), quality)).first;
          }
          std::vector<std::vector<std::vector<std::string>>> rows;
          for (const auto& words : cached->second) rows.push_back(GroupIntoRows(words));
          if (rows.empty()) rows.emplace_back();
          const size_t chosen = ChooseRead(rule, rows);
          if (!cached->second.empty()) {
            for (const auto& word : cached->second[chosen]) text += (text.empty() ? "" : " ") + word.text;
          }
          if (rule.pauses) {
            if (!FindMatches(rule, rows[chosen]).empty()) {
              if (!fuser.Paused(ms * kHnsPerMs)) log::Info("Scan note: [{}] is up at {:.2f}; not counting kills", rule.name, ms / 1000.0);
              fuser.Pause((ms - watch->intervalMs) * kHnsPerMs, (ms + rule.holdMs) * kHnsPerMs);
            }
            continue;
          }
          fresh = watch->tracker.Update(rows[chosen], ms);
        }
        if (watch->matcher) {
          if (pixels.empty()) pixels = Crop(frame, watch->box.left, watch->box.top, width, height);
          const double alike = watch->matcher->BestMatch(
              ToGray(pixels.data(), static_cast<int>(width), static_cast<int>(height), static_cast<size_t>(width) * 4, false));
          fresh += watch->tracker.UpdateMatches(alike >= rule.imageMatch ? std::vector<std::string>{""} : std::vector<std::string>{}, ms);
          if (text.empty()) text = "icon: " + rule.name;
        }
        if (text.size() > 120) text = text.substr(0, 120) + "...";
        Witness where;
        where.left = static_cast<double>(watch->box.left) / video.width;
        where.top = static_cast<double>(watch->box.top) / video.height;
        where.width = static_cast<double>(width) / video.width;
        where.height = static_cast<double>(height) / video.height;
        fuser.Add(rule, fresh, ms * kHnsPerMs, text, where);
      }
      if (length > 0) {
        const int percent = std::clamp(static_cast<int>(ms / 10.0 / length), 0, 100);
        if (percent / 5 != lastPercent / 5) {
          lastPercent = percent;
          log::Info("Scan progress {}%", percent);
        }
      }
    }
    fuser.FlushAll();
    if (!journalWatches.empty()) log::Info("Scan journal done: {} notes", noted);
    log::Info("Scan done: {} moments", found);
    return 0;
  } catch (const winrt::hresult_error& e) {
    log::Error("Scan failed: {}", Describe(e));
  } catch (const std::exception& e) {
    log::Error("Scan failed: {}", e.what());
  }
  return 1;
}

}  // namespace clip
