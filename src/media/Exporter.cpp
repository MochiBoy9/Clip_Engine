#include "media/Exporter.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "media/ClipMetadata.h"
#include "media/MediaUtil.h"

#include <mferror.h>
#include <mfreadwrite.h>
#include <strmif.h>
#include <codecapi.h>

#include <algorithm>
#include <cstring>
#include <fstream>
#include <sstream>
#include <vector>

namespace clip {
namespace {

constexpr UINT32 kRate = 48000;
constexpr UINT32 kChannels = 2;
constexpr UINT32 kAudioBitsPerSecond = 96000;
constexpr UINT32 kAacBytesPerSecond = kAudioBitsPerSecond / 8;

struct SourceInfo {
  int64_t duration = 0;
  DWORD videoStream = MAXDWORD;
  UINT32 width = 0;
  UINT32 height = 0;
  double fps = 60;
  std::vector<DWORD> audioStreams;
};

struct Plan {
  UINT32 width = 0;
  UINT32 height = 0;
  UINT32 fps = 30;
  UINT32 videoBitrate = 0;
};

winrt::com_ptr<IMFSourceReader> OpenReader(const std::filesystem::path& input) {
  winrt::com_ptr<IMFAttributes> attributes;
  winrt::check_hresult(MFCreateAttributes(attributes.put(), 1));
  // Lets the reader decode, convert to NV12 and resize in one go.
  winrt::check_hresult(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE));
  winrt::com_ptr<IMFSourceReader> reader;
  winrt::check_hresult(MFCreateSourceReaderFromURL(input.c_str(), attributes.get(), reader.put()));
  return reader;
}

SourceInfo Inspect(IMFSourceReader* reader) {
  SourceInfo info;
  PROPVARIANT duration;
  PropVariantInit(&duration);
  winrt::check_hresult(reader->GetPresentationAttribute(static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION,
                                                        &duration));
  info.duration = static_cast<int64_t>(duration.uhVal.QuadPart);
  PropVariantClear(&duration);

  for (DWORD i = 0;; ++i) {
    winrt::com_ptr<IMFMediaType> type;
    const HRESULT hr = reader->GetNativeMediaType(i, 0, type.put());
    if (hr == MF_E_INVALIDSTREAMNUMBER) break;
    winrt::check_hresult(hr);
    GUID major{};
    winrt::check_hresult(type->GetGUID(MF_MT_MAJOR_TYPE, &major));
    if (major == MFMediaType_Video && info.videoStream == MAXDWORD) {
      info.videoStream = i;
      MFGetAttributeSize(type.get(), MF_MT_FRAME_SIZE, &info.width, &info.height);
      UINT32 num = 0, den = 0;
      if (SUCCEEDED(MFGetAttributeRatio(type.get(), MF_MT_FRAME_RATE, &num, &den)) && num > 0 && den > 0) {
        info.fps = static_cast<double>(num) / den;
      }
    } else if (major == MFMediaType_Audio && info.audioStreams.size() < 2) {
      info.audioStreams.push_back(i);
    }
  }
  if (info.videoStream == MAXDWORD || info.width == 0 || info.height == 0) {
    throw winrt::hresult_error(MF_E_INVALIDMEDIATYPE, L"The file has no video");
  }
  if (info.duration <= 0) throw winrt::hresult_error(MF_E_INVALIDMEDIATYPE, L"The file has no length");
  return info;
}

// The biggest picture that still looks clean at the bitrate that fits.
Plan PlanFor(const SourceInfo& source, UINT32 videoBitrate, unsigned maxFps) {
  Plan plan;
  plan.videoBitrate = videoBitrate;
  UINT32 height = videoBitrate >= 5'000'000 ? 1080 : videoBitrate >= 2'200'000 ? 720 : videoBitrate >= 1'000'000 ? 540 : 480;
  height = std::min(height, source.height);
  plan.height = height & ~1u;
  plan.width = static_cast<UINT32>(static_cast<double>(source.width) * plan.height / source.height + 0.5) & ~1u;
  const double fpsCap = videoBitrate < 1'500'000 ? 30.0 : static_cast<double>(maxFps);
  plan.fps = static_cast<UINT32>(std::max(1.0, std::min(source.fps, fpsCap) + 0.5));
  return plan;
}

void SetPcmType(IMFSourceReader* reader, DWORD stream) {
  winrt::com_ptr<IMFMediaType> type;
  winrt::check_hresult(MFCreateMediaType(type.put()));
  winrt::check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
  winrt::check_hresult(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
  winrt::check_hresult(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
  winrt::check_hresult(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, kRate));
  winrt::check_hresult(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, kChannels));
  winrt::check_hresult(type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, kChannels * 2));
  winrt::check_hresult(type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kRate * kChannels * 2));
  winrt::check_hresult(reader->SetCurrentMediaType(stream, nullptr, type.get()));
}

// Decodes every audio track and adds them together, so game sound and voice end up in the
// one track that chat apps play.
std::vector<int16_t> MixAudio(IMFSourceReader* reader, const SourceInfo& source) {
  if (source.audioStreams.empty()) return {};
  winrt::check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
  for (DWORD stream : source.audioStreams) {
    SetPcmType(reader, stream);
    winrt::check_hresult(reader->SetStreamSelection(stream, TRUE));
  }

  std::vector<int32_t> mix;
  size_t ended = 0;
  while (ended < source.audioStreams.size()) {
    DWORD stream = 0, flags = 0;
    LONGLONG time = 0;
    winrt::com_ptr<IMFSample> sample;
    winrt::check_hresult(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &stream, &flags, &time,
                                            sample.put()));
    if (flags & MF_SOURCE_READERF_ENDOFSTREAM) ++ended;
    if (!sample) continue;

    winrt::com_ptr<IMFMediaBuffer> buffer;
    winrt::check_hresult(sample->ConvertToContiguousBuffer(buffer.put()));
    BYTE* data = nullptr;
    DWORD length = 0;
    winrt::check_hresult(buffer->Lock(&data, nullptr, &length));
    const auto* pcm = reinterpret_cast<const int16_t*>(data);
    const size_t values = length / sizeof(int16_t);
    const size_t offset = static_cast<size_t>(std::max<LONGLONG>(0, time) * kRate / kHnsPerSecond) * kChannels;
    if (mix.size() < offset + values) mix.resize(offset + values, 0);
    for (size_t i = 0; i < values; ++i) mix[offset + i] += pcm[i];
    buffer->Unlock();
  }

  std::vector<int16_t> out(mix.size());
  for (size_t i = 0; i < mix.size(); ++i) out[i] = static_cast<int16_t>(std::clamp(mix[i], -32768, 32767));
  return out;
}

void SetCodecValue(ICodecAPI* codec, const GUID& property, UINT32 value) {
  VARIANT v{};
  v.vt = VT_UI4;
  v.ulVal = value;
  codec->SetValue(&property, &v);
}

void Encode(const std::filesystem::path& input, const std::filesystem::path& output, const SourceInfo& source,
            const Plan& plan, const std::vector<int16_t>& audio) {
  auto reader = OpenReader(input);
  winrt::check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
  winrt::check_hresult(reader->SetStreamSelection(source.videoStream, TRUE));
  {
    winrt::com_ptr<IMFMediaType> type;
    winrt::check_hresult(MFCreateMediaType(type.put()));
    winrt::check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    winrt::check_hresult(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
    winrt::check_hresult(MFSetAttributeSize(type.get(), MF_MT_FRAME_SIZE, plan.width, plan.height));
    winrt::check_hresult(reader->SetCurrentMediaType(source.videoStream, nullptr, type.get()));
  }

  winrt::com_ptr<IMFAttributes> attributes;
  winrt::check_hresult(MFCreateAttributes(attributes.put(), 2));
  winrt::check_hresult(attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4));
  winrt::check_hresult(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE));
  winrt::com_ptr<IMFSinkWriter> writer;
  winrt::check_hresult(MFCreateSinkWriterFromURL(output.c_str(), nullptr, attributes.get(), writer.put()));

  DWORD videoOut = 0;
  {
    winrt::com_ptr<IMFMediaType> out;
    winrt::check_hresult(MFCreateMediaType(out.put()));
    winrt::check_hresult(out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    winrt::check_hresult(out->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
    winrt::check_hresult(out->SetUINT32(MF_MT_AVG_BITRATE, plan.videoBitrate));
    winrt::check_hresult(MFSetAttributeSize(out.get(), MF_MT_FRAME_SIZE, plan.width, plan.height));
    winrt::check_hresult(MFSetAttributeRatio(out.get(), MF_MT_FRAME_RATE, plan.fps, 1));
    winrt::check_hresult(MFSetAttributeRatio(out.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    winrt::check_hresult(out->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    winrt::check_hresult(out->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High));
    winrt::check_hresult(writer->AddStream(out.get(), &videoOut));

    winrt::com_ptr<IMFMediaType> in;
    winrt::check_hresult(MFCreateMediaType(in.put()));
    winrt::check_hresult(in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    winrt::check_hresult(in->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
    winrt::check_hresult(MFSetAttributeSize(in.get(), MF_MT_FRAME_SIZE, plan.width, plan.height));
    winrt::check_hresult(MFSetAttributeRatio(in.get(), MF_MT_FRAME_RATE, plan.fps, 1));
    winrt::check_hresult(MFSetAttributeRatio(in.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    winrt::check_hresult(in->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    winrt::check_hresult(writer->SetInputMediaType(videoOut, in.get(), nullptr));

    winrt::com_ptr<ICodecAPI> codec;
    if (SUCCEEDED(writer->GetServiceForStream(videoOut, GUID_NULL, IID_PPV_ARGS(codec.put())))) {
      SetCodecValue(codec.get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR);
      SetCodecValue(codec.get(), CODECAPI_AVEncCommonMeanBitRate, plan.videoBitrate);
    }
  }

  const bool hasAudio = !audio.empty();
  DWORD audioOut = 0;
  if (hasAudio) {
    winrt::com_ptr<IMFMediaType> out;
    winrt::check_hresult(MFCreateMediaType(out.put()));
    winrt::check_hresult(out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
    winrt::check_hresult(out->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC));
    winrt::check_hresult(out->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
    winrt::check_hresult(out->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, kRate));
    winrt::check_hresult(out->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, kChannels));
    winrt::check_hresult(out->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kAacBytesPerSecond));
    winrt::check_hresult(writer->AddStream(out.get(), &audioOut));

    winrt::com_ptr<IMFMediaType> in;
    winrt::check_hresult(MFCreateMediaType(in.put()));
    winrt::check_hresult(in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
    winrt::check_hresult(in->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
    winrt::check_hresult(in->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
    winrt::check_hresult(in->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, kRate));
    winrt::check_hresult(in->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, kChannels));
    winrt::check_hresult(in->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, kChannels * 2));
    winrt::check_hresult(in->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kRate * kChannels * 2));
    winrt::check_hresult(writer->SetInputMediaType(audioOut, in.get(), nullptr));
  }

  winrt::check_hresult(writer->BeginWriting());

  const size_t totalFrames = audio.size() / kChannels;
  size_t audioWritten = 0;
  // Audio goes in alongside the video, a little ahead of it, so the file is interleaved.
  auto writeAudioUntil = [&](int64_t until) {
    const size_t target = std::min(totalFrames, static_cast<size_t>(std::max<int64_t>(0, until) * kRate / kHnsPerSecond));
    while (audioWritten < target) {
      const size_t frames = std::min<size_t>(target - audioWritten, kRate / 10);
      const DWORD bytes = static_cast<DWORD>(frames * kChannels * sizeof(int16_t));
      auto sample = CreateMemorySample(bytes);
      winrt::com_ptr<IMFMediaBuffer> buffer;
      winrt::check_hresult(sample->GetBufferByIndex(0, buffer.put()));
      BYTE* data = nullptr;
      winrt::check_hresult(buffer->Lock(&data, nullptr, nullptr));
      std::memcpy(data, audio.data() + audioWritten * kChannels, bytes);
      buffer->Unlock();
      winrt::check_hresult(buffer->SetCurrentLength(bytes));
      winrt::check_hresult(sample->SetSampleTime(static_cast<LONGLONG>(audioWritten * kHnsPerSecond / kRate)));
      winrt::check_hresult(sample->SetSampleDuration(static_cast<LONGLONG>(frames * kHnsPerSecond / kRate)));
      winrt::check_hresult(writer->WriteSample(audioOut, sample.get()));
      audioWritten += frames;
    }
  };

  const int64_t frameDuration = kHnsPerSecond / plan.fps;
  int64_t nextFrame = 0;
  int lastPercent = -1;
  while (true) {
    DWORD stream = 0, flags = 0;
    LONGLONG time = 0;
    winrt::com_ptr<IMFSample> sample;
    winrt::check_hresult(reader->ReadSample(source.videoStream, 0, &stream, &flags, &time, sample.put()));
    if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
    if (!sample) continue;
    // Drop frames to reach the planned frame rate.
    if (time + frameDuration / 2 < nextFrame) continue;
    nextFrame = std::max(nextFrame + frameDuration, time + frameDuration / 2);

    if (hasAudio) writeAudioUntil(time + kHnsPerSecond / 2);
    winrt::check_hresult(sample->SetSampleTime(time));
    winrt::check_hresult(sample->SetSampleDuration(frameDuration));
    winrt::check_hresult(writer->WriteSample(videoOut, sample.get()));

    const int percent = static_cast<int>(std::clamp<int64_t>(time * 100 / source.duration, 0, 99));
    if (percent / 5 != lastPercent / 5) {
      log::Info("Export progress {}%", percent);
      lastPercent = percent;
    }
  }
  if (hasAudio) writeAudioUntil(INT64_MAX / 2);
  winrt::check_hresult(writer->Finalize());
}

// The clip's markers, from inside the file or from the .json next to it.
std::optional<std::string> SourceMetadata(const std::filesystem::path& input) {
  if (auto json = ReadMetadataBox(input)) return json;
  auto sidecar = input;
  sidecar.replace_extension(L".json");
  std::ifstream in(sidecar, std::ios::binary);
  if (!in) return std::nullopt;
  std::stringstream text;
  text << in.rdbuf();
  return text.str();
}

}  // namespace

int RunExport(const ExportOptions& options) {
  try {
    auto reader = OpenReader(options.input);
    const SourceInfo source = Inspect(reader.get());
    const std::vector<int16_t> audio = MixAudio(reader.get(), source);
    reader = nullptr;

    const double seconds = static_cast<double>(source.duration) / kHnsPerSecond;
    const double limitBytes = options.maxMegabytes * 1024 * 1024;
    // Leave room for the container and for the encoder overshooting a little.
    double budgetBits = limitBytes * 8 * 0.92 - (audio.empty() ? 0 : kAudioBitsPerSecond * seconds);
    std::error_code ec;

    for (int attempt = 1; attempt <= 3; ++attempt) {
      const auto videoBitrate = static_cast<UINT32>(std::clamp(budgetBits / seconds, 150'000.0, 40'000'000.0));
      const Plan plan = PlanFor(source, videoBitrate, options.maxFps);
      log::Info("Exporting {}x{} at {} fps, {:.1f} Mbps", plan.width, plan.height, plan.fps, plan.videoBitrate / 1e6);
      std::filesystem::remove(options.output, ec);
      Encode(options.input, options.output, source, plan, audio);

      const auto size = static_cast<double>(std::filesystem::file_size(options.output));
      if (size <= limitBytes || attempt == 3) {
        if (auto json = SourceMetadata(options.input)) AppendMetadataBox(options.output, *json);
        log::Info("Export progress 100%");
        log::Info("Exported {:.1f} MB to {}", size / (1024 * 1024), winrt::to_string(options.output.wstring()));
        if (size > limitBytes) log::Warn("The export is a little over {:.0f} MB; this clip is too long to fit", options.maxMegabytes);
        return 0;
      }
      // Came out too big: aim lower by how much it missed, plus a margin.
      budgetBits *= limitBytes / size * 0.95;
      log::Info("Export came out at {:.1f} MB, trying again smaller", size / (1024 * 1024));
    }
  } catch (const winrt::hresult_error& e) {
    std::error_code ec;
    std::filesystem::remove(options.output, ec);
    log::Error("Export failed: {}", Describe(e));
  } catch (const std::exception& e) {
    log::Error("Export failed: {}", e.what());
  }
  return 1;
}

}  // namespace clip
