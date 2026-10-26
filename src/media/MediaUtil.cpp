#include "media/MediaUtil.h"

namespace clip {
namespace {

// Calls fn(nal) for every NAL unit in an Annex B byte stream.
template <class Fn>
void ForEachNal(std::span<const uint8_t> data, Fn&& fn) {
  const size_t n = data.size();
  auto nextPayload = [&](size_t from, size_t& startCode) {
    for (size_t i = from; i + 2 < n; ++i) {
      if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) {
        startCode = i;
        return i + 3;
      }
    }
    startCode = n;
    return n;
  };

  size_t startCode = 0;
  size_t payload = nextPayload(0, startCode);
  while (payload < n) {
    size_t nextStart = 0;
    const size_t following = nextPayload(payload, nextStart);
    size_t end = nextStart;
    // The leading zero of a 4-byte start code belongs to the next NAL, not this one.
    while (end > payload && data[end - 1] == 0) --end;
    if (end > payload) fn(data.subspan(payload, end - payload));
    payload = following;
  }
}

int NalType(std::span<const uint8_t> nal, bool hevc) {
  return hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
}

}  // namespace

winrt::com_ptr<IMFMediaType> CopyMediaType(IMFMediaType* source) {
  winrt::com_ptr<IMFMediaType> copy;
  winrt::check_hresult(MFCreateMediaType(copy.put()));
  winrt::check_hresult(source->CopyAllItems(copy.get()));
  return copy;
}

EncodedPacket PacketFromSample(IMFSample* sample, int stream) {
  EncodedPacket packet;
  packet.stream = stream;
  LONGLONG time = 0;
  LONGLONG duration = 0;
  sample->GetSampleTime(&time);
  sample->GetSampleDuration(&duration);
  packet.pts = time;
  packet.duration = duration;
  packet.keyframe = MFGetAttributeUINT32(sample, MFSampleExtension_CleanPoint, FALSE) != 0;

  winrt::com_ptr<IMFMediaBuffer> buffer;
  winrt::check_hresult(sample->ConvertToContiguousBuffer(buffer.put()));
  BYTE* bytes = nullptr;
  DWORD length = 0;
  winrt::check_hresult(buffer->Lock(&bytes, nullptr, &length));
  packet.data.assign(bytes, bytes + length);
  buffer->Unlock();
  return packet;
}

winrt::com_ptr<IMFSample> CreateMemorySample(DWORD capacity) {
  winrt::com_ptr<IMFMediaBuffer> buffer;
  winrt::check_hresult(MFCreateMemoryBuffer(capacity, buffer.put()));
  winrt::com_ptr<IMFSample> sample;
  winrt::check_hresult(MFCreateSample(sample.put()));
  winrt::check_hresult(sample->AddBuffer(buffer.get()));
  return sample;
}

bool ContainsKeyframe(std::span<const uint8_t> annexB, bool hevc) {
  bool keyframe = false;
  ForEachNal(annexB, [&](std::span<const uint8_t> nal) {
    const int type = NalType(nal, hevc);
    if (hevc ? (type >= 16 && type <= 21) : type == 5) keyframe = true;
  });
  return keyframe;
}

std::vector<uint8_t> ExtractParameterSets(std::span<const uint8_t> annexB, bool hevc) {
  std::vector<uint8_t> out;
  ForEachNal(annexB, [&](std::span<const uint8_t> nal) {
    const int type = NalType(nal, hevc);
    const bool parameterSet = hevc ? (type >= 32 && type <= 34) : (type == 7 || type == 8);
    if (!parameterSet) return;
    out.insert(out.end(), {0, 0, 0, 1});
    out.insert(out.end(), nal.begin(), nal.end());
  });
  return out;
}

}  // namespace clip
