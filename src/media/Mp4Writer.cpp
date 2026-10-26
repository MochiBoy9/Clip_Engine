#include "media/Mp4Writer.h"

#include <mfreadwrite.h>

#include <cstring>

namespace clip {

void WriteMp4(const std::filesystem::path& file, const ReplayBuffer::Clip& clip) {
  winrt::com_ptr<IMFAttributes> attributes;
  winrt::check_hresult(MFCreateAttributes(attributes.put(), 3));
  winrt::check_hresult(attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4));
  winrt::check_hresult(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
  winrt::check_hresult(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE));

  winrt::com_ptr<IMFSinkWriter> writer;
  winrt::check_hresult(MFCreateSinkWriterFromURL(file.c_str(), nullptr, attributes.get(), writer.put()));

  std::array<bool, kStreamCount> hasPackets{};
  for (const auto& packet : clip.packets) hasPackets[packet->stream] = true;

  // Input type equal to output type means pass-through: packets are stored as they are.
  std::array<DWORD, kStreamCount> trackOf{};
  for (int s = 0; s < kStreamCount; ++s) {
    if (!clip.formats[s] || !hasPackets[s]) continue;
    winrt::check_hresult(writer->AddStream(clip.formats[s].get(), &trackOf[s]));
    winrt::check_hresult(writer->SetInputMediaType(trackOf[s], clip.formats[s].get(), nullptr));
  }
  winrt::check_hresult(writer->BeginWriting());

  for (const auto& packet : clip.packets) {
    if (!clip.formats[packet->stream]) continue;
    const auto size = static_cast<DWORD>(packet->data.size());
    auto sample = CreateMemorySample(size);
    winrt::com_ptr<IMFMediaBuffer> buffer;
    winrt::check_hresult(sample->GetBufferByIndex(0, buffer.put()));
    BYTE* bytes = nullptr;
    winrt::check_hresult(buffer->Lock(&bytes, nullptr, nullptr));
    std::memcpy(bytes, packet->data.data(), size);
    buffer->Unlock();
    winrt::check_hresult(buffer->SetCurrentLength(size));

    winrt::check_hresult(sample->SetSampleTime(packet->pts - clip.start));
    winrt::check_hresult(sample->SetSampleDuration(packet->duration));
    if (packet->keyframe) winrt::check_hresult(sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE));
    winrt::check_hresult(writer->WriteSample(trackOf[packet->stream], sample.get()));
  }
  winrt::check_hresult(writer->Finalize());
}

}  // namespace clip
