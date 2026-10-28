#include "media/Trimmer.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "media/ClipMetadata.h"
#include "media/Mp4Writer.h"

#include <mferror.h>
#include <mfreadwrite.h>

#include <algorithm>
#include <cmath>
#include <fstream>

namespace clip {
namespace {

// Reads the clip's packets between start and end as they are stored, without decoding.
ReplayBuffer::Clip ReadRange(const std::filesystem::path& input, int64_t start, int64_t end) {
  winrt::com_ptr<IMFSourceReader> reader;
  winrt::check_hresult(MFCreateSourceReaderFromURL(input.c_str(), nullptr, reader.put()));

  // Same stream layout as clips written from the replay buffer: video, then up to two audio tracks.
  ReplayBuffer::Clip clip;
  std::array<DWORD, kStreamCount> sourceOf;
  sourceOf.fill(MAXDWORD);
  int audioTracks = 0;
  bool hevc = false;
  for (DWORD i = 0;; ++i) {
    winrt::com_ptr<IMFMediaType> type;
    const HRESULT hr = reader->GetNativeMediaType(i, 0, type.put());
    if (hr == MF_E_INVALIDSTREAMNUMBER) break;
    winrt::check_hresult(hr);
    GUID major{};
    GUID subtype{};
    winrt::check_hresult(type->GetGUID(MF_MT_MAJOR_TYPE, &major));
    type->GetGUID(MF_MT_SUBTYPE, &subtype);
    int stream = -1;
    if (major == MFMediaType_Video && sourceOf[kVideoStream] == MAXDWORD) {
      stream = kVideoStream;
      hevc = subtype == MFVideoFormat_HEVC;
    } else if (major == MFMediaType_Audio && audioTracks < 2) {
      stream = kGameAudioStream + audioTracks++;
    }
    if (stream < 0) continue;
    sourceOf[stream] = i;
    clip.formats[stream] = CopyMediaType(type.get());
  }
  if (sourceOf[kVideoStream] == MAXDWORD) throw winrt::hresult_error(MF_E_INVALIDMEDIATYPE, L"The file has no video");

  winrt::check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
  for (DWORD source : sourceOf) {
    if (source != MAXDWORD) winrt::check_hresult(reader->SetStreamSelection(source, TRUE));
  }

  // Lands on a keyframe at or before start; any frames before the first keyframe are skipped below.
  PROPVARIANT position;
  PropVariantInit(&position);
  position.vt = VT_I8;
  position.hVal.QuadPart = start;
  winrt::check_hresult(reader->SetCurrentPosition(GUID_NULL, position));

  std::array<bool, kStreamCount> done{};
  for (int s = 0; s < kStreamCount; ++s) done[s] = sourceOf[s] == MAXDWORD;
  int64_t videoStart = -1;
  std::vector<EncodedPacket> packets;
  while (!std::ranges::all_of(done, [](bool d) { return d; })) {
    DWORD index = 0, flags = 0;
    LONGLONG time = 0;
    winrt::com_ptr<IMFSample> sample;
    winrt::check_hresult(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &index, &flags, &time,
                                            sample.put()));
    const auto found = std::ranges::find(sourceOf, index);
    if (found == sourceOf.end()) continue;
    const int stream = static_cast<int>(found - sourceOf.begin());
    if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
      done[stream] = true;
      continue;
    }
    if (!sample || done[stream]) continue;
    if (time > end) {
      done[stream] = true;
      reader->SetStreamSelection(index, FALSE);
      continue;
    }

    EncodedPacket packet = PacketFromSample(sample.get(), stream);
    packet.pts = time;
    if (stream == kVideoStream) {
      packet.keyframe = packet.keyframe || ContainsKeyframe(packet.data, hevc);
      if (videoStart < 0) {
        if (!packet.keyframe) continue;
        videoStart = time;
      }
    }
    packets.push_back(std::move(packet));
  }
  if (videoStart < 0) throw winrt::hresult_error(MF_E_INVALIDREQUEST, L"There's no video in that part of the clip");

  // Audio that came before the first frame would have a negative time in the new file.
  std::erase_if(packets, [&](const EncodedPacket& p) { return p.pts < videoStart; });
  std::ranges::stable_sort(packets, {}, &EncodedPacket::pts);
  clip.start = videoStart;
  for (auto& packet : packets) {
    clip.end = std::max(clip.end, packet.pts + packet.duration);
    clip.packets.push_back(std::make_shared<const EncodedPacket>(std::move(packet)));
  }
  return clip;
}

}  // namespace

int RunTrim(const TrimOptions& options) {
  auto partial = options.output;
  partial += L".part";
  try {
    const auto start = static_cast<int64_t>(std::llround(std::max(0.0, options.startSeconds) * kHnsPerSecond));
    const auto end = static_cast<int64_t>(std::llround(options.endSeconds * kHnsPerSecond));
    if (end <= start) {
      log::Error("Trim failed: the end has to come after the start");
      return 1;
    }

    const auto clip = ReadRange(options.input, start, end);
    if (options.output.has_parent_path()) std::filesystem::create_directories(options.output.parent_path());
    WriteMp4(partial, clip);

    const double from = static_cast<double>(clip.start) / kHnsPerSecond;
    const double to = static_cast<double>(clip.end) / kHnsPerSecond;
    if (auto json = ReadClipMetadataJson(options.input)) {
      if (auto metadata = ParseMetadataJson(*json)) {
        const std::string trimmed = ToJson(Trimmed(*metadata, from, to));
        AppendMetadataBox(partial, trimmed);
        auto sidecar = options.output;
        sidecar.replace_extension(L".json");
        std::ofstream(sidecar, std::ios::binary) << trimmed;
      }
    }
    std::error_code ec;
    std::filesystem::remove(options.output, ec);
    std::filesystem::rename(partial, options.output);
    log::Info("Trimmed {:.2f}s from {:.2f}s to {}", to - from, from, winrt::to_string(options.output.wstring()));
    return 0;
  } catch (const winrt::hresult_error& e) {
    log::Error("Trim failed: {}", Describe(e));
  } catch (const std::exception& e) {
    log::Error("Trim failed: {}", e.what());
  }
  std::error_code ec;
  std::filesystem::remove(partial, ec);
  return 1;
}

}  // namespace clip
