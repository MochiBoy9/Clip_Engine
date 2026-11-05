#include "audio/AppAudioCapture.h"

#include "core/Clock.h"
#include "core/Log.h"

#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <audiopolicy.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <tlhelp32.h>

#include <algorithm>
#include <cstdlib>
#include <filesystem>
#include <map>

namespace clip {
namespace {

// How far behind real time the apps' audio is mixed, so every packet has arrived.
constexpr int64_t kMixLatency = 60 * kHnsPerMs;
constexpr int64_t kRefreshEvery = 2 * kHnsPerSecond;
// An app whose audio couldn't be recorded is tried again after this.
constexpr int64_t kRetryAfter = 30 * kHnsPerSecond;

std::string ExeName(DWORD pid) {
  winrt::handle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
  if (!process) return {};
  wchar_t path[MAX_PATH];
  DWORD size = MAX_PATH;
  if (!QueryFullProcessImageNameW(process.get(), 0, path, &size)) return {};
  return winrt::to_string(std::filesystem::path(std::wstring(path, size)).filename().wstring());
}

// Hands the audio client over once Windows has made it.
struct ActivationHandler : winrt::implements<ActivationHandler, IActivateAudioInterfaceCompletionHandler> {
  winrt::handle done{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
  HRESULT result = E_FAIL;
  winrt::com_ptr<IAudioClient> client;

  HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) noexcept override {
    HRESULT activated = E_FAIL;
    winrt::com_ptr<IUnknown> unknown;
    HRESULT hr = operation->GetActivateResult(&activated, unknown.put());
    if (SUCCEEDED(hr)) hr = activated;
    if (SUCCEEDED(hr)) hr = unknown->QueryInterface(IID_PPV_ARGS(client.put()));
    result = hr;
    SetEvent(done.get());
    return S_OK;
  }
};

struct Client {
  winrt::com_ptr<IAudioClient> client;
  winrt::com_ptr<IAudioCaptureClient> capture;
};

// An audio client that records what the process and its children play, started.
Client StartProcessLoopback(DWORD pid) {
  AUDIOCLIENT_ACTIVATION_PARAMS params{};
  params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
  params.ProcessLoopbackParams.TargetProcessId = pid;
  params.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
  PROPVARIANT activation{};
  activation.vt = VT_BLOB;
  activation.blob.cbSize = sizeof(params);
  activation.blob.pBlobData = reinterpret_cast<BYTE*>(&params);

  auto handler = winrt::make_self<ActivationHandler>();
  winrt::com_ptr<IActivateAudioInterfaceAsyncOperation> operation;
  winrt::check_hresult(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient),
                                                   &activation, handler.get(), operation.put()));
  if (WaitForSingleObject(handler->done.get(), 5000) != WAIT_OBJECT_0) {
    throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT), L"Windows didn't start the app's audio");
  }
  winrt::check_hresult(handler->result);

  // Process loopback has no format of its own; Windows converts to the one asked for.
  WAVEFORMATEX format{};
  format.wFormatTag = WAVE_FORMAT_PCM;
  format.nChannels = PcmMixer::kChannels;
  format.nSamplesPerSec = PcmMixer::kRate;
  format.wBitsPerSample = 16;
  format.nBlockAlign = static_cast<WORD>(PcmMixer::kChannels * 2);
  format.nAvgBytesPerSec = PcmMixer::kRate * format.nBlockAlign;
  Client c;
  c.client = handler->client;
  constexpr REFERENCE_TIME kBufferDuration = 200 * kHnsPerMs;
  winrt::check_hresult(c.client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                            AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                                                AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                            kBufferDuration, 0, &format, nullptr));
  winrt::check_hresult(c.client->GetService(IID_PPV_ARGS(c.capture.put())));
  winrt::check_hresult(c.client->Start());
  return c;
}

}  // namespace

std::vector<AudioApp> AppsWithAudio() {
  std::vector<AudioApp> apps;
  try {
    auto enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
    winrt::com_ptr<IMMDeviceCollection> devices;
    winrt::check_hresult(enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, devices.put()));
    UINT deviceCount = 0;
    winrt::check_hresult(devices->GetCount(&deviceCount));
    for (UINT d = 0; d < deviceCount; ++d) {
      winrt::com_ptr<IMMDevice> device;
      winrt::com_ptr<IAudioSessionManager2> manager;
      winrt::com_ptr<IAudioSessionEnumerator> sessions;
      if (FAILED(devices->Item(d, device.put())) ||
          FAILED(device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, manager.put_void())) ||
          FAILED(manager->GetSessionEnumerator(sessions.put()))) {
        continue;
      }
      int count = 0;
      sessions->GetCount(&count);
      for (int i = 0; i < count; ++i) {
        winrt::com_ptr<IAudioSessionControl> control;
        if (FAILED(sessions->GetSession(i, control.put()))) continue;
        auto control2 = control.try_as<IAudioSessionControl2>();
        AudioSessionState state = AudioSessionStateExpired;
        DWORD pid = 0;
        if (!control2 || control2->IsSystemSoundsSession() == S_OK || FAILED(control->GetState(&state)) ||
            state == AudioSessionStateExpired || FAILED(control2->GetProcessId(&pid)) || pid == 0) {
          continue;
        }
        if (std::ranges::any_of(apps, [&](const AudioApp& a) { return a.processId == pid; })) continue;
        apps.push_back({pid, ExeName(pid)});
      }
    }
  } catch (const winrt::hresult_error& e) {
    log::Warn("Couldn't list the apps playing sound: {}", Describe(e));
  }
  return apps;
}

std::unordered_map<uint32_t, uint32_t> ParentProcesses() {
  std::unordered_map<uint32_t, uint32_t> parents;
  winrt::file_handle snapshot{CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)};
  if (!snapshot) return parents;
  PROCESSENTRY32W entry{};
  entry.dwSize = sizeof(entry);
  for (BOOL ok = Process32FirstW(snapshot.get(), &entry); ok; ok = Process32NextW(snapshot.get(), &entry)) {
    parents[entry.th32ProcessID] = entry.th32ParentProcessID;
  }
  return parents;
}

bool AppAudioCapture::Supported() {
  static const bool supported = [] {
    bool ok = false;
    std::thread([&] {
      winrt::init_apartment(winrt::apartment_type::multi_threaded);
      try {
        Client c = StartProcessLoopback(GetCurrentProcessId());
        c.client->Stop();
        ok = true;
      } catch (const winrt::hresult_error& e) {
        log::Info("This version of Windows can't record apps on their own tracks ({})", Describe(e));
      }
      winrt::uninit_apartment();
    }).join();
    return ok;
  }();
  return supported;
}

AppAudioCapture::AppAudioCapture(std::string name, Targets targets, Sink sink)
    : name_(std::move(name)), targets_(std::move(targets)) {
  clock_ = std::make_unique<MixClock>(mixer_, kMixLatency, std::move(sink));
  thread_ = std::thread([this] { Run(); });
}

AppAudioCapture::~AppAudioCapture() {
  stop_ = true;
  if (thread_.joinable()) thread_.join();
  clock_.reset();
}

void AppAudioCapture::Run() {
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  DWORD taskIndex = 0;
  HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);

  std::map<DWORD, Client> clients;
  std::map<DWORD, int64_t> retryAt;
  int64_t refreshAt = 0;
  while (!stop_) {
    Sleep(10);
    const int64_t now = NowHns();

    if (now >= refreshAt || refresh_.exchange(false)) {
      refreshAt = now + kRefreshEvery;
      std::vector<AudioApp> wanted;
      try {
        wanted = targets_();
      } catch (const winrt::hresult_error& e) {
        log::Warn("Couldn't tell which apps go on the {} track: {}", name_, Describe(e));
      }
      for (auto it = clients.begin(); it != clients.end();) {
        if (std::ranges::any_of(wanted, [&](const AudioApp& a) { return a.processId == it->first; })) {
          ++it;
          continue;
        }
        it->second.client->Stop();
        mixer_.Remove(static_cast<int>(it->first));
        it = clients.erase(it);
      }
      for (const auto& app : wanted) {
        if (clients.contains(app.processId)) continue;
        if (auto retry = retryAt.find(app.processId); retry != retryAt.end() && now < retry->second) continue;
        try {
          clients.emplace(app.processId, StartProcessLoopback(app.processId));
          retryAt.erase(app.processId);
          log::Info("Recording {} on the {} track", app.exe.empty() ? "it" : app.exe, name_);
        } catch (const winrt::hresult_error& e) {
          if (!retryAt.contains(app.processId)) {
            log::Warn("Couldn't record {} on the {} track: {}", app.exe.empty() ? "an app" : app.exe, name_, Describe(e));
          }
          retryAt[app.processId] = now + kRetryAfter;
        }
      }
    }

    for (auto it = clients.begin(); it != clients.end();) {
      auto& [pid, c] = *it;
      try {
        UINT32 packetFrames = 0;
        while (SUCCEEDED(c.capture->GetNextPacketSize(&packetFrames)) && packetFrames > 0) {
          BYTE* data = nullptr;
          UINT32 frames = 0;
          DWORD flags = 0;
          UINT64 qpcPosition = 0;
          winrt::check_hresult(c.capture->GetBuffer(&data, &frames, &flags, nullptr, &qpcPosition));
          // Process loopback doesn't always say when its audio was played; then it was just now.
          const int64_t arrived = NowHns() - PcmMixer::FramesToHns(frames);
          int64_t pts = static_cast<int64_t>(qpcPosition);
          if ((flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) || pts <= 0 || std::llabs(pts - arrived) > kHnsPerSecond) {
            pts = arrived;
          }
          // Silence needn't be pushed: the mixer hears nothing from an input as silence.
          if (!(flags & AUDCLNT_BUFFERFLAGS_SILENT)) {
            mixer_.Push(static_cast<int>(pid), reinterpret_cast<const int16_t*>(data), frames, pts);
          }
          winrt::check_hresult(c.capture->ReleaseBuffer(frames));
        }
        ++it;
      } catch (const winrt::hresult_error& e) {
        // The app closed, or its audio device went away; it's picked up again if it comes back.
        log::Info("Stopped recording an app on the {} track: {}", name_, Describe(e));
        mixer_.Remove(static_cast<int>(pid));
        it = clients.erase(it);
      }
    }
  }
  for (auto& [pid, c] : clients) c.client->Stop();
  clients.clear();
  if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
  winrt::uninit_apartment();
}

}  // namespace clip
