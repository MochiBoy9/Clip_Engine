#include "app/App.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "media/ClipMetadata.h"
#include "media/Mp4Writer.h"

#include <algorithm>
#include <chrono>
#include <fstream>

namespace clip {
namespace {

constexpr int kSaveHotkeyId = 1;

// The desktop app signals these to save a clip or to shut down cleanly.
constexpr wchar_t kSaveEventName[] = L"Local\\ClipEngine.SaveClip";
constexpr wchar_t kQuitEventName[] = L"Local\\ClipEngine.Quit";
constexpr int64_t kPollInterval = 2 * kHnsPerSecond;
constexpr int64_t kRetryDelay = 10 * kHnsPerSecond;
// How long the main loop sleeps between checks for detected events and finished clips.
constexpr DWORD kLoopWaitMs = 250;

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

App::App(Config config, DetectionRules rules)
    : config_(std::move(config)),
      rules_(std::move(rules)),
      planner_(int64_t{config_.clipBeforeSeconds} * kHnsPerSecond, int64_t{config_.clipAfterSeconds} * kHnsPerSecond,
               int64_t{config_.bufferSeconds - 5} * kHnsPerSecond) {}

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
  if (config_.detection && config_.autoClip) {
    log::Info("Automatic clips: {} s before and {} s after each moment", config_.clipBeforeSeconds,
              config_.clipAfterSeconds);
  }

  std::string supported;
  for (const auto& game : BuiltInGames()) supported += (supported.empty() ? "" : ", ") + game.name;
  log::Info("Waiting for a supported game: {}", supported);
  if (config_.detection) PrepareGameIntegrations();

  const HANDLE events[] = {CreateEventW(nullptr, FALSE, FALSE, kSaveEventName),
                           CreateEventW(nullptr, FALSE, FALSE, kQuitEventName)};
  const bool haveEvents = events[0] && events[1];
  if (!haveEvents) log::Warn("Couldn't create the events the desktop app uses; only the hotkey will save clips");

  auto shutDown = [&] {
    log::Info("Shutting down");
    UnregisterHotKey(nullptr, kSaveHotkeyId);
    StopSession();
    for (auto& save : saves_) save.wait();
    for (HANDLE event : events) {
      if (event) CloseHandle(event);
    }
    return 0;
  };

  int64_t nextPoll = 0;
  while (true) {
    const DWORD wait = MsgWaitForMultipleObjects(haveEvents ? 2 : 0, haveEvents ? events : nullptr, FALSE,
                                                 kLoopWaitMs, QS_ALLINPUT);
    if (haveEvents && wait == WAIT_OBJECT_0) SaveHotkeyClip();
    if (haveEvents && wait == WAIT_OBJECT_0 + 1) return shutDown();
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
      if (msg.message == WM_QUIT) return shutDown();
      if (msg.message == WM_HOTKEY && msg.wParam == kSaveHotkeyId) SaveHotkeyClip();
      TranslateMessage(&msg);
      DispatchMessageW(&msg);
    }

    PumpIntegrationEvents();
    HandleEvents();
    SaveReadyAutoClips(false);
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
  std::string gameId;
  if (game) {
    monitor = game->monitor;
    label = gameName;
    gameId = game->profile->id;
  } else if (config_.recordWithoutGame) {
    monitor = MonitorFromPoint({0, 0}, MONITOR_DEFAULTTOPRIMARY);
    label = "Desktop";
  }

  if (session_ && (session_->Label() != label || session_->Monitor() != monitor || !session_->Healthy())) {
    if (!session_->Healthy()) log::Warn("Restarting the recording (display or encoder changed)");
    StopSession();
  }

  if (!session_ && monitor && NowHns() >= retryAt_) {
    std::vector<DetectionRule> rules;
    if (config_.detection && !gameId.empty()) {
      std::vector<std::string> needingName;
      rules = rules_.ForGame(gameId, ReadPlayerNames(config_.file), &needingName);
      if (!needingName.empty()) log::Warn("Add your {} name on the Games page to catch your kills in its killfeed", label);
      if (rules.empty() && game->profile->api == GameApi::None) log::Info("No detection rules for {}, so only the hotkey saves clips", label);
    }
    try {
      session_ = std::make_unique<RecordingSession>(config_, monitor, label, std::move(rules), rules_.sampleMs);
      sessionGameId_ = gameId;
    } catch (const winrt::hresult_error& e) {
      log::Error("Couldn't start recording {}: {}. Trying again in 10 seconds.", label, Describe(e));
      session_.reset();
      retryAt_ = NowHns() + kRetryDelay;
    }
  }

  SyncIntegration(game ? &*game : nullptr);
}

void App::SyncIntegration(const DetectedGame* game) {
  const bool wanted = game && config_.detection && game->profile->api != GameApi::None;
  if (wanted && integration_ && integrationGame_ == game->profile && integrationProcess_ == game->processId) return;
  integration_.reset();
  integrationGame_ = nullptr;
  integrationProcess_ = 0;
  if (!wanted) return;
  integration_ = StartGameIntegration(*game);
  if (!integration_) return;
  integrationGame_ = game->profile;
  integrationProcess_ = game->processId;
  log::Info("Reading {}'s own game events for kills, deaths and match results", game->profile->name);
}

void App::PumpIntegrationEvents() {
  if (!integration_) return;
  auto events = integration_->TakeEvents();
  // Only moments from the game being recorded belong in its clips.
  if (!session_ || session_->Label() != integrationGame_->name) return;
  for (auto& event : events) session_->AddEvent(event);
}

void App::StopSession() {
  if (!session_) return;
  // Keep the moments that happened just before the game closed.
  HandleEvents();
  SaveReadyAutoClips(true);
  log::Info("Stopped recording {}", session_->Label());
  session_.reset();
  sessionGameId_.clear();
}

void App::HandleEvents() {
  if (!session_) return;
  for (const auto& event : session_->TakeNewEvents()) {
    log::Info("Detected {} in {}: {}", EventName(event.type), session_->Label(), event.text);
    if (config_.autoClip && config_.autoClipEvents.contains(event.type)) planner_.Add(event.time);
  }
}

void App::SaveReadyAutoClips(bool all) {
  if (!session_) {
    planner_.TakeAll();
    return;
  }
  // Wait a moment past the end so the encoder has delivered the last frames.
  const auto windows = all ? planner_.TakeAll() : planner_.TakeReady(NowHns() - kHnsPerSecond / 2);
  for (const auto& window : windows) SaveRange(window.start, window.end, "auto");
}

void App::SaveHotkeyClip() {
  if (!session_) {
    log::Info("Nothing is being recorded right now, so there's no clip to save");
    return;
  }
  const int64_t end = NowHns();
  if (!SaveRange(end - int64_t{config_.clipSeconds} * kHnsPerSecond, end, "hotkey")) {
    log::Info("The recording just started; there's nothing to save yet");
  }
}

std::filesystem::path App::ClipPath() {
  SYSTEMTIME t;
  GetLocalTime(&t);
  const std::string label = SafeFileName(session_->Label());
  const auto folder = config_.outputDir / winrt::to_hstring(label).c_str();
  const std::string stem = std::format("{}_{:04}-{:02}-{:02}_{:02}-{:02}-{:02}", label, t.wYear, t.wMonth, t.wDay,
                                       t.wHour, t.wMinute, t.wSecond);
  // Two clips can start saving in the same second (a hotkey save during an automatic one),
  // before either file exists on disk.
  int n = stem == lastStem_ ? lastStemCount_ + 1 : 1;
  for (;; ++n) {
    const std::string file = n == 1 ? stem + ".mp4" : std::format("{}_{}.mp4", stem, n);
    auto path = folder / winrt::to_hstring(file).c_str();
    std::error_code ec;
    if (!std::filesystem::exists(path, ec)) {
      lastStem_ = stem;
      lastStemCount_ = n;
      return path;
    }
  }
}

bool App::SaveRange(int64_t start, int64_t end, const char* trigger) {
  auto clip = session_->Buffer().Extract(start, end);
  if (!clip || clip->packets.empty()) return false;

  ClipMetadata metadata;
  metadata.game = session_->Label();
  metadata.trigger = trigger;
  metadata.seconds = static_cast<double>(clip->packets.back()->pts - clip->start) / kHnsPerSecond;
  for (const auto& event : session_->EventsBetween(clip->start, end)) {
    metadata.markers.push_back({event.type, static_cast<double>(event.time - clip->start) / kHnsPerSecond, event.text});
  }

  const auto path = ClipPath();
  const bool beep = std::string_view(trigger) == "hotkey";
  saves_.push_back(std::async(std::launch::async, [clip = std::move(*clip), metadata = std::move(metadata), path, beep] {
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    // Low CPU and disk priority, so writing a clip never makes the game hitch.
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
    auto partial = path;
    partial += L".part";
    try {
      std::filesystem::create_directories(path.parent_path());
      WriteMp4(partial, clip);
      // The markers go both inside the file, so they travel with it when shared, and next to
      // it, so the app can read them without opening every video.
      const std::string json = ToJson(metadata);
      AppendMetadataBox(partial, json);
      auto sidecar = path;
      sidecar.replace_extension(L".json");
      std::ofstream(sidecar, std::ios::binary) << json;
      std::filesystem::rename(partial, path);
      const double mb = static_cast<double>(std::filesystem::file_size(path)) / (1024.0 * 1024.0);
      const std::string summary = Summarize(metadata.markers);
      log::Info("Saved {:.0f}s clip ({:.1f} MB) to {}", metadata.seconds, mb, winrt::to_string(path.wstring()));
      if (!summary.empty()) log::Info("Clip has {}", summary);
      if (beep) MessageBeep(MB_OK);
    } catch (const winrt::hresult_error& e) {
      log::Error("Couldn't save the clip: {}", Describe(e));
      std::error_code ec;
      std::filesystem::remove(partial, ec);
    } catch (const std::exception& e) {
      log::Error("Couldn't save the clip: {}", e.what());
    }
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
    winrt::uninit_apartment();
  }));
  return true;
}

}  // namespace clip
