#include "app/App.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "media/Mp4Writer.h"

#include <algorithm>
#include <chrono>

namespace clip {
namespace {

constexpr int kSaveHotkeyId = 1;
constexpr int64_t kPollInterval = 2 * kHnsPerSecond;
constexpr int64_t kRetryDelay = 10 * kHnsPerSecond;

DWORD g_mainThread = 0;

BOOL WINAPI OnConsoleClose(DWORD) {
  PostThreadMessageW(g_mainThread, WM_QUIT, 0, 0);
  // Give the main thread time to finish writing any clip before Windows ends the process.
  Sleep(4000);
  return TRUE;
}

// Folder and file names can't contain these characters.
std::string SafeFileName(std::string name) {
  std::ranges::replace_if(name, [](char c) { return std::string_view("<>:\"/\\|?*").find(c) != std::string_view::npos; }, '_');
  return name;
}

}  // namespace

App::App(Config config) : config_(std::move(config)) {}

int App::Run() {
  g_mainThread = GetCurrentThreadId();
  SetConsoleCtrlHandler(OnConsoleClose, TRUE);

  // Without a window, WM_HOTKEY is posted to this thread's message queue.
  if (!RegisterHotKey(nullptr, kSaveHotkeyId, config_.saveHotkey.modifiers | MOD_NOREPEAT, config_.saveHotkey.key)) {
    log::Error("Couldn't register the save hotkey {}; another app may be using it. Change save_hotkey in the settings.",
               config_.saveHotkeyText);
  } else {
    log::Info("Press {} to save the last {} seconds", config_.saveHotkeyText, config_.clipSeconds);
  }

  std::string supported;
  for (const auto& game : BuiltInGames()) supported += (supported.empty() ? "" : ", ") + game.name;
  log::Info("Waiting for a supported game: {}", supported);

  int64_t nextPoll = 0;
  while (true) {
    MsgWaitForMultipleObjects(0, nullptr, FALSE, 500, QS_ALLINPUT);
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
      if (msg.message == WM_QUIT) {
        log::Info("Shutting down");
        UnregisterHotKey(nullptr, kSaveHotkeyId);
        StopSession();
        for (auto& save : saves_) save.wait();
        return 0;
      }
      if (msg.message == WM_HOTKEY && msg.wParam == kSaveHotkeyId) SaveClip();
      TranslateMessage(&msg);
      DispatchMessageW(&msg);
    }

    if (NowHns() >= nextPoll) {
      Poll();
      nextPoll = NowHns() + kPollInterval;
    }
    std::erase_if(saves_, [](std::future<void>& f) { return f.wait_for(std::chrono::seconds(0)) == std::future_status::ready; });
  }
}

void App::Poll() {
  const auto game = detector_.Find();
  const std::string gameName = game ? game->profile->name : std::string();
  if (gameName != lastGame_) {
    if (!lastGame_.empty()) log::Info("{} closed", lastGame_);
    if (!gameName.empty()) log::Info("{} detected", gameName);
    lastGame_ = gameName;
  }

  HMONITOR monitor = nullptr;
  std::string label;
  if (game) {
    monitor = game->monitor;
    label = gameName;
  } else if (config_.recordWithoutGame) {
    monitor = MonitorFromPoint({0, 0}, MONITOR_DEFAULTTOPRIMARY);
    label = "Desktop";
  }

  if (session_ && (session_->Label() != label || session_->Monitor() != monitor || !session_->Healthy())) {
    if (!session_->Healthy()) log::Warn("Restarting the recording (display or encoder changed)");
    StopSession();
  }

  if (!session_ && monitor && NowHns() >= retryAt_) {
    try {
      session_ = std::make_unique<RecordingSession>(config_, monitor, label);
    } catch (const winrt::hresult_error& e) {
      log::Error("Couldn't start recording {}: {}. Trying again in 10 seconds.", label, Describe(e));
      session_.reset();
      retryAt_ = NowHns() + kRetryDelay;
    }
  }
}

void App::StopSession() {
  if (!session_) return;
  log::Info("Stopped recording {}", session_->Label());
  session_.reset();
}

std::filesystem::path App::ClipPath() const {
  SYSTEMTIME t;
  GetLocalTime(&t);
  const std::string label = SafeFileName(session_->Label());
  const std::string file = std::format("{}_{:04}-{:02}-{:02}_{:02}-{:02}-{:02}.mp4", label, t.wYear, t.wMonth, t.wDay,
                                       t.wHour, t.wMinute, t.wSecond);
  return config_.outputDir / winrt::to_hstring(label).c_str() / winrt::to_hstring(file).c_str();
}

void App::SaveClip() {
  if (!session_) {
    log::Info("Nothing is being recorded right now, so there's no clip to save");
    return;
  }
  const int64_t end = NowHns();
  auto clip = session_->Buffer().Extract(end - int64_t{config_.clipSeconds} * kHnsPerSecond, end);
  if (!clip || clip->packets.empty()) {
    log::Info("The recording just started; there's nothing to save yet");
    return;
  }

  const auto path = ClipPath();
  saves_.push_back(std::async(std::launch::async, [clip = std::move(*clip), path] {
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    auto partial = path;
    partial += L".part";
    try {
      std::filesystem::create_directories(path.parent_path());
      WriteMp4(partial, clip);
      std::filesystem::rename(partial, path);
      const double seconds = static_cast<double>(clip.packets.back()->pts - clip.start) / kHnsPerSecond;
      const double mb = static_cast<double>(std::filesystem::file_size(path)) / (1024.0 * 1024.0);
      log::Info("Saved {:.0f}s clip ({:.1f} MB) to {}", seconds, mb, winrt::to_string(path.wstring()));
      MessageBeep(MB_OK);
    } catch (const winrt::hresult_error& e) {
      log::Error("Couldn't save the clip: {}", Describe(e));
      std::error_code ec;
      std::filesystem::remove(partial, ec);
    } catch (const std::exception& e) {
      log::Error("Couldn't save the clip: {}", e.what());
    }
    winrt::uninit_apartment();
  }));
}

}  // namespace clip
