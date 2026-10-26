#include "audio/AacEncoder.h"

#include "audio/AudioCapture.h"
#include "core/Clock.h"

#include <mferror.h>
#include <wmcodecdsp.h>

#include <algorithm>
#include <cstring>
#include <vector>

namespace clip {
namespace {

constexpr uint32_t kRate = AudioCapture::kSampleRate;
constexpr uint32_t kChannels = AudioCapture::kChannels;
constexpr UINT32 kAacBytesPerSecond = 24000;  // 192 kbps
// Drift under 10 ms is ordinary capture jitter and is ignored.
constexpr int64_t kToleranceFrames = kRate / 100;
// After a gap longer than this, restart the timeline instead of writing minutes of silence.
constexpr int64_t kMaxGapFrames = kRate * 10;

int64_t FramesToHns(uint64_t frames) { return static_cast<int64_t>(frames * kHnsPerSecond / kRate); }

}  // namespace

AacEncoder::AacEncoder(int stream, ReplayBuffer& buffer) : stream_(stream), buffer_(buffer) {
  transform_ = winrt::create_instance<IMFTransform>(CLSID_AACMFTEncoder);

  winrt::com_ptr<IMFMediaType> input;
  winrt::check_hresult(MFCreateMediaType(input.put()));
  winrt::check_hresult(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
  winrt::check_hresult(input->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
  winrt::check_hresult(input->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
  winrt::check_hresult(input->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, kRate));
  winrt::check_hresult(input->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, kChannels));
  winrt::check_hresult(input->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, kChannels * 2));
  winrt::check_hresult(input->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kRate * kChannels * 2));

  winrt::com_ptr<IMFMediaType> output;
  winrt::check_hresult(MFCreateMediaType(output.put()));
  winrt::check_hresult(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
  winrt::check_hresult(output->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC));
  winrt::check_hresult(output->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
  winrt::check_hresult(output->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, kRate));
  winrt::check_hresult(output->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, kChannels));
  winrt::check_hresult(output->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kAacBytesPerSecond));

  winrt::check_hresult(transform_->SetInputType(0, input.get(), 0));
  winrt::check_hresult(transform_->SetOutputType(0, output.get(), 0));

  MFT_OUTPUT_STREAM_INFO info{};
  winrt::check_hresult(transform_->GetOutputStreamInfo(0, &info));
  outputSize_ = std::max<DWORD>(info.cbSize, 8192);

  // The current output type carries the AudioSpecificConfig the MP4 file needs.
  winrt::com_ptr<IMFMediaType> current;
  winrt::check_hresult(transform_->GetOutputCurrentType(0, current.put()));
  buffer_.SetFormat(stream_, current.get());

  winrt::check_hresult(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
  winrt::check_hresult(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
}

AacEncoder::~AacEncoder() {
  if (transform_) transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
}

void AacEncoder::Push(const int16_t* pcm, uint32_t frames, int64_t pts) {
  if (frames == 0) return;
  if (!started_) {
    started_ = true;
    startPts_ = pts;
  }

  const int64_t expected = startPts_ + FramesToHns(framesEncoded_);
  const int64_t driftFrames = (pts - expected) * kRate / kHnsPerSecond;

  if (driftFrames > kMaxGapFrames) {
    startPts_ = pts;
    framesEncoded_ = 0;
  } else if (driftFrames > kToleranceFrames) {
    const std::vector<int16_t> silence(static_cast<size_t>(driftFrames) * kChannels, 0);
    Encode(silence.data(), static_cast<uint32_t>(driftFrames));
  } else if (driftFrames < -kToleranceFrames) {
    const auto overlap = static_cast<uint32_t>(std::min<int64_t>(-driftFrames, frames));
    pcm += static_cast<size_t>(overlap) * kChannels;
    frames -= overlap;
    if (frames == 0) return;
  }
  Encode(pcm, frames);
}

void AacEncoder::Encode(const int16_t* pcm, uint32_t frames) {
  const DWORD bytes = frames * kChannels * sizeof(int16_t);
  auto sample = CreateMemorySample(bytes);
  winrt::com_ptr<IMFMediaBuffer> buffer;
  winrt::check_hresult(sample->GetBufferByIndex(0, buffer.put()));
  BYTE* data = nullptr;
  winrt::check_hresult(buffer->Lock(&data, nullptr, nullptr));
  std::memcpy(data, pcm, bytes);
  buffer->Unlock();
  winrt::check_hresult(buffer->SetCurrentLength(bytes));
  winrt::check_hresult(sample->SetSampleTime(startPts_ + FramesToHns(framesEncoded_)));
  winrt::check_hresult(sample->SetSampleDuration(FramesToHns(frames)));
  framesEncoded_ += frames;

  HRESULT hr = transform_->ProcessInput(0, sample.get(), 0);
  if (hr == MF_E_NOTACCEPTING) {
    DrainOutput();
    hr = transform_->ProcessInput(0, sample.get(), 0);
  }
  winrt::check_hresult(hr);
  DrainOutput();
}

void AacEncoder::DrainOutput() {
  while (true) {
    auto sample = CreateMemorySample(outputSize_);
    MFT_OUTPUT_DATA_BUFFER output{};
    output.pSample = sample.get();
    DWORD status = 0;
    const HRESULT hr = transform_->ProcessOutput(0, 1, &output, &status);
    if (output.pEvents) output.pEvents->Release();
    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
    winrt::check_hresult(hr);

    EncodedPacket packet = PacketFromSample(sample.get(), stream_);
    packet.keyframe = true;  // every AAC frame decodes on its own
    if (!packet.data.empty()) buffer_.Push(std::move(packet));
  }
}

}  // namespace clip
