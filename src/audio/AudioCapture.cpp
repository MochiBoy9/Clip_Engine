#include "audio/AudioCapture.h"

#include "core/Clock.h"
#include "core/Log.h"

#include <audioclient.h>
#include <mmdeviceapi.h>

#include <vector>

namespace clip {
namespace {

int64_t FramesToHns(uint64_t frames) {
  return static_cast<int64_t>(frames * kHnsPerSecond / AudioCapture::kSampleRate);
}

const char* SourceName(AudioCapture::Source source) {
  return source == AudioCapture::Source::GameAudio ? "game audio" : "microphone";
}

}  // namespace

AudioCapture::AudioCapture(Source source, Sink sink) : source_(source), sink_(std::move(sink)) {
  thread_ = std::thread([this] { Run(); });
}

AudioCapture::~AudioCapture() {
  stop_ = true;
  if (thread_.joinable()) thread_.join();
}

void AudioCapture::Run() {
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  const bool loopback = source_ == Source::GameAudio;
  try {
    auto enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
    winrt::com_ptr<IMMDevice> device;
    winrt::check_hresult(enumerator->GetDefaultAudioEndpoint(loopback ? eRender : eCapture, eConsole, device.put()));
    winrt::com_ptr<IAudioClient> client;
    winrt::check_hresult(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void()));

    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = kChannels;
    format.nSamplesPerSec = kSampleRate;
    format.wBitsPerSample = 16;
    format.nBlockAlign = static_cast<WORD>(kChannels * 2);
    format.nAvgBytesPerSec = kSampleRate * format.nBlockAlign;

    const DWORD flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY |
                        (loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
    constexpr REFERENCE_TIME kBufferDuration = 200 * kHnsPerMs;
    winrt::check_hresult(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, kBufferDuration, 0, &format, nullptr));
    winrt::com_ptr<IAudioCaptureClient> capture;
    winrt::check_hresult(client->GetService(IID_PPV_ARGS(capture.put())));
    winrt::check_hresult(client->Start());
    log::Info("Capturing {}", SourceName(source_));

    std::vector<int16_t> silence;
    int64_t capturedUntil = NowHns();
    while (!stop_) {
      Sleep(10);

      UINT32 packetFrames = 0;
      while (SUCCEEDED(capture->GetNextPacketSize(&packetFrames)) && packetFrames > 0) {
        BYTE* data = nullptr;
        UINT32 frames = 0;
        DWORD bufferFlags = 0;
        UINT64 qpcPosition = 0;
        winrt::check_hresult(capture->GetBuffer(&data, &frames, &bufferFlags, nullptr, &qpcPosition));
        const int64_t pts = (bufferFlags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)
                                ? NowHns() - FramesToHns(frames)
                                : static_cast<int64_t>(qpcPosition);
        if (bufferFlags & AUDCLNT_BUFFERFLAGS_SILENT) {
          silence.assign(static_cast<size_t>(frames) * kChannels, 0);
          sink_(silence.data(), frames, pts);
        } else {
          sink_(reinterpret_cast<const int16_t*>(data), frames, pts);
        }
        winrt::check_hresult(capture->ReleaseBuffer(frames));
        capturedUntil = pts + FramesToHns(frames);
      }

      // Windows sends nothing from the speakers while nothing plays. Fill that with silence
      // so the game audio track stays continuous.
      if (loopback) {
        const int64_t fillUntil = NowHns() - 30 * kHnsPerMs;
        if (fillUntil - capturedUntil > 100 * kHnsPerMs) {
          const auto frames = static_cast<uint32_t>((fillUntil - capturedUntil) * kSampleRate / kHnsPerSecond);
          silence.assign(static_cast<size_t>(frames) * kChannels, 0);
          sink_(silence.data(), frames, capturedUntil);
          capturedUntil += FramesToHns(frames);
        }
      }
    }
    client->Stop();
  } catch (const winrt::hresult_error& e) {
    log::Error("Stopped capturing {}: {}", SourceName(source_), Describe(e));
  }
  winrt::uninit_apartment();
}

}  // namespace clip
