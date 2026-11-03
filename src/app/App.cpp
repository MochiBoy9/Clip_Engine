#include "app/App.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "core/Power.h"
#include "integrations/IntegrationSetup.h"
#include "media/ClipMetadata.h"
#include "media/Mp4Writer.h"

#include <algorithm>
#include <chrono>
#include <fstream>

namespace clip {
namespace {

constexpr int kSaveHotkeyId = 1;
constexpr int kRecordHotkeyId = 2;

// The desktop app signals these to save a clip, start or stop a long recording, or shut down cleanly.
constexpr wchar_t kSaveEventName[] = L"Local\\ClipEngine.SaveClip";
constexpr wchar_t kQuitEventName[] = L"Local\\ClipEngine.Quit";
constexpr wchar_t kRecordEventName[] = L"Local\\ClipEngine.ToggleRecording";
// A long recording stops before the drive is this full, so the PC doesn't run out of space.
constexpr uint64_t kMinFreeBytes = 2ull << 30;
constexpr int64_t kPollInterval = 2 * kHnsPerSecond;
constexpr int64_t kRetryDelay = 10 * kHnsPerSecond;
// How long the main loop sleeps between checks for detected events and finished clips.
constexpr DWORD kLoopWaitMs = 250;
// Clip Engine's application on Discord, which the Discord status belongs to. discord_app_id in
// the settings can name another one.
constexpr char kDiscordAppId[] = "1565522043003338886";

DWORD g_mainThread = 0;

BOOL WINAPI OnConsoleClose(DWORD) {
  PostThreadMessageW(g_mainThread, WM_QUIT, 0, 0);
  // Give the main thread time to finish writing any clip before Windows ends the process.
  Sleep(4000);
  return TRUE;
}

// "2026-10-27T20:41:05", in local time.
std::string LocalTimestamp() {
  SYSTEMTIME t;
  GetLocalTime(&t);
  return std::format("{:04}-{:02}-{:02}T{:02}:{:02}:{:02}", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
}

// Folder and file names can't contain these characters.
std::string SafeFileName(std::string name) {
  std::ranges::replace_if(name, [](char c) { return std::string_view("<>:\"/\\|?*").find(c) != std::string_view::npos; }, '_');
  return name;
}

}  // namespace

namespace {

std::vector<GameProfile> CustomProfiles(const DetectionRules& rules) {
  std::vector<GameProfile> profiles;
  for (const auto& game : rules.games) {
    GameProfile& profile = profiles.emplace_back();
    profile.id = game.id;
    profile.name = game.name;
    for (const auto& exe : game.executables) profile.executables.push_back(std::wstring(winrt::to_hstring(exe)));
  }
  return profiles;
}

}  // namespace

App::App(Config config, DetectionRules rules)
    : config_(std::move(config)),
      rules_(std::move(rules)),
      detector_(CustomProfiles(rules_)),
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
  if (!RegisterHotKey(nullptr, kRecordHotkeyId, config_.recordHotkey.modifiers | MOD_NOREPEAT, config_.recordHotkey.key)) {
    log::Error("Couldn't register the long recording hotkey {}; another app may be using it. Change record_hotkey in "
               "the settings.",
               config_.recordHotkeyText);
  } else {
    log::Info("Press {} to start or stop a long recording", config_.recordHotkeyText);
  }
  if (config_.detection && config_.autoClip) {
    log::Info("Automatic clips: {} s before and {} s after each moment", config_.clipBeforeSeconds,
              config_.clipAfterSeconds);
  }

  std::string supported;
  for (const auto& game : detector_.Games()) supported += (supported.empty() ? "" : ", ") + game.name;
  log::Info("Waiting for a supported game: {}", supported);
  if (config_.detection) PrepareGameIntegrations();
  if (config_.discordPresence) {
    const std::string appId = config_.discordAppId.empty() ? kDiscordAppId : config_.discordAppId;
    if (!appId.empty()) presence_ = std::make_unique<DiscordPresence>(appId);
  }

  const HANDLE events[] = {CreateEventW(nullptr, FALSE, FALSE, kSaveEventName),
                           CreateEventW(nullptr, FALSE, FALSE, kQuitEventName),
                           CreateEventW(nullptr, FALSE, FALSE, kRecordEventName)};
  const bool haveEvents = events[0] && events[1] && events[2];
  if (!haveEvents) log::Warn("Couldn't create the events the desktop app uses; only the hotkey will save clips");
  // The desktop app's commands come in on a fourth handle, waited on with the others.
  std::vector<HANDLE> waitOn;
  if (haveEvents) waitOn.assign(std::begin(events), std::end(events));
  if (commands_.Listening()) waitOn.push_back(commands_.Event());

  auto shutDown = [&] {
    log::Info("Shutting down");
    UnregisterHotKey(nullptr, kSaveHotkeyId);
    UnregisterHotKey(nullptr, kRecordHotkeyId);
    StopSession();
    presence_.reset();
    for (auto& save : saves_) save.wait();
    for (HANDLE event : events) {
      if (event) CloseHandle(event);
    }
    return 0;
  };

  int64_t nextPoll = 0;
  while (true) {
    const DWORD wait = MsgWaitForMultipleObjects(static_cast<DWORD>(waitOn.size()), waitOn.empty() ? nullptr : waitOn.data(),
                                                 FALSE, kLoopWaitMs, QS_ALLINPUT);
    if (haveEvents && wait == WAIT_OBJECT_0) SaveHotkeyClip();
    if (haveEvents && wait == WAIT_OBJECT_0 + 1) return shutDown();
    if (haveEvents && wait == WAIT_OBJECT_0 + 2) ToggleLongRecording();
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
      if (msg.message == WM_QUIT) return shutDown();
      if (msg.message == WM_HOTKEY && msg.wParam == kSaveHotkeyId) SaveHotkeyClip();
      if (msg.message == WM_HOTKEY && msg.wParam == kRecordHotkeyId) ToggleLongRecording();
      TranslateMessage(&msg);
      DispatchMessageW(&msg);
    }

    PumpIntegrationEvents();
    HandleEvents();
    HandleCommands();
    PumpScoreboards();
    SaveReadyAutoClips(false);
    SaveReadySquadClips(false);
    if (NowHns() >= nextPoll) {
      CheckLongRecording();
      Poll();
      nextPoll = NowHns() + kPollInterval;
    }
    UpdatePresence();
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
    std::vector<ScoreboardRule> scoreboards;
    std::vector<std::string> playerNames;
    double hudScale = 1;
    if (config_.detection && !gameId.empty()) {
      std::vector<std::string> needingName;
      playerNames = ReadPlayerNames(config_.file);
      rules = rules_.ForGame(gameId, playerNames, &needingName);
      scoreboards = rules_.ScoreboardsForGame(gameId);
      hudScale = ReadHudScale(config_.file, gameId);
      if (!needingName.empty()) log::Warn("Add your {} name on the Games page to catch your kills in its killfeed", label);
      if (rules.empty() && game->profile->api == GameApi::None) log::Info("No detection rules for {}, so only the hotkey saves clips", label);
    }
    try {
      session_ = std::make_unique<RecordingSession>(config_, monitor, label, std::move(rules), std::move(scoreboards),
                                                    std::move(playerNames), rules_.sampleMs, hudScale);
      sessionGameId_ = gameId;
      sessionId_ = LocalTimestamp();
      sessionStart_ = NowHns();
      matchStarts_.clear();
      presenceState_ = PresenceState{};
      presenceState_.game = label;
      presenceState_.startUnix = std::chrono::duration_cast<std::chrono::seconds>(
                                     std::chrono::system_clock::now().time_since_epoch())
                                     .count();
      // Only games have moments worth keeping for the records.
      if (config_.detection && !gameId.empty()) {
        timeline_ = std::make_unique<TimelineWriter>(config_.file.parent_path() / L"timeline", sessionId_, label);
        if (!timeline_->Ok()) timeline_.reset();
      }
    } catch (const winrt::hresult_error& e) {
      log::Error("Couldn't start recording {}: {}. Trying again in 10 seconds.", label, Describe(e));
      session_.reset();
      retryAt_ = NowHns() + kRetryDelay;
    }
  }

  if (session_) session_->SetGameProcess(game ? game->processId : 0);
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

void App::UpdatePresence() {
  if (!presence_) return;
  // Only games go on the status, not a recording of the desktop.
  if (!session_ || sessionGameId_.empty()) {
    presence_->Set(std::nullopt);
    return;
  }
  presenceState_.longRecording = longRecording_ != nullptr;
  presenceState_.away = !session_->GameInFocus();
  presence_->Set(presenceState_);
}

void App::PumpScoreboards() {
  std::vector<Scoreboard> boards;
  if (session_) boards = session_->TakeScoreboards();
  if (integration_) {
    auto board = integration_->TakeScoreboard();
    if (board && session_ && session_->Label() == integrationGame_->name) boards.push_back(std::move(*board));
  }
  for (const auto& board : boards) {
    log::Info("Scoreboard in {}: {}", session_->Label(), DescribeStats(board.stats));
    if (timeline_) timeline_->Write(TimelineScores(SessionSeconds(board.time), MatchAt(board.time), board));
  }
}

double App::SessionSeconds(int64_t time) const { return static_cast<double>(time - sessionStart_) / kHnsPerSecond; }

int App::MatchAt(int64_t time) const {
  return static_cast<int>(std::ranges::count_if(matchStarts_, [&](int64_t t) { return t <= time; }));
}

void App::StopSession() {
  if (!session_) return;
  // Keep the moments that happened just before the game closed.
  PumpIntegrationEvents();
  HandleEvents();
  PumpScoreboards();
  SaveReadyAutoClips(true);
  SaveReadySquadClips(true);
  // The long recording reads from this session's buffer, so it ends with it.
  if (longRecording_) FinishLongRecording("the recording it was part of ended");
  log::Info("Stopped recording {}", session_->Label());
  session_.reset();
  sessionGameId_.clear();
  timeline_.reset();
}

void App::HandleEvents() {
  if (!session_) return;
  for (const auto& event : session_->TakeNewEvents()) {
    log::Info("Detected {} in {}: {}", EventName(event.type), session_->Label(), event.text);
    // When it happened by the wall clock, for squad sync to line squadmates' views up with.
    log::Info("Moment {} at {}", EventName(event.type), UnixMsAtHns(NowClocks(), event.time));
    if (event.witnesses.size() > 1) {
      std::string seen;
      for (const auto& w : event.witnesses) {
        seen += std::format("{}[{}] {:+.1f}s{}", seen.empty() ? "" : ", ", w.rule, w.offset, w.counted ? " (counted)" : "");
      }
      log::Info("Seen by {}", seen);
    }
    if (event.type == EventType::MatchStart) {
      matchStarts_.push_back(event.time);
      presenceState_.kills = presenceState_.deaths = presenceState_.assists = 0;
    }
    if (event.type == EventType::Kill) ++presenceState_.kills;
    if (event.type == EventType::Death) ++presenceState_.deaths;
    if (event.type == EventType::Assist) ++presenceState_.assists;
    if (timeline_) timeline_->Write(TimelineEvent(SessionSeconds(event.time), MatchAt(event.time), event));
    if (longRecording_ && event.time >= longRecording_->Start()) longEvents_.push_back(event);
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

void App::HandleCommands() {
  if (!commands_.Listening()) return;
  for (const auto& line : commands_.Take()) {
    const auto request = ParseSquadSave(line);
    if (!request) {
      log::Warn("Unknown command from the app: {}", line.substr(0, 80));
      continue;
    }
    // Only a view of the same game is worth saving; a squadmate in another game isn't playing
    // with this player.
    if (!session_ || sessionGameId_.empty() || (!request->game.empty() && request->game != session_->Label())) {
      log::Info("Squad moment {} skipped: not recording {}", request->moment, request->game.empty() ? "a game" : request->game);
      continue;
    }
    const ClockPair now = NowClocks();
    SquadSaveQueue::Window window{request->moment, HnsAtUnixMs(now, request->startUnixMs), HnsAtUnixMs(now, request->endUnixMs)};
    if (window.end < sessionStart_) {
      log::Info("Squad moment {} skipped: it was before this recording started", request->moment);
      continue;
    }
    if (squadSaves_.Add(std::move(window))) log::Info("Saving your view of squad moment {}", request->moment);
  }
}

void App::SaveReadySquadClips(bool all) {
  if (!session_) {
    squadSaves_.TakeAll();
    return;
  }
  const auto windows = all ? squadSaves_.TakeAll() : squadSaves_.TakeReady(NowHns() - kHnsPerSecond / 2);
  for (const auto& window : windows) {
    if (!SaveRange(window.start, window.end, "squad", window.moment)) {
      log::Info("Squad moment {} skipped: it's no longer in the replay buffer", window.moment);
    }
  }
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

std::filesystem::path App::ClipPath(std::string_view suffix) {
  SYSTEMTIME t;
  GetLocalTime(&t);
  const std::string label = SafeFileName(session_->Label());
  const auto folder = config_.outputDir / winrt::to_hstring(label).c_str();
  const std::string stem = std::format("{}_{:04}-{:02}-{:02}_{:02}-{:02}-{:02}{}", label, t.wYear, t.wMonth, t.wDay,
                                       t.wHour, t.wMinute, t.wSecond, suffix);
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

bool App::SaveRange(int64_t start, int64_t end, const char* trigger, std::string_view squadMoment) {
  auto clip = session_->Buffer().Extract(start, end);
  if (!clip || clip->packets.empty()) return false;

  ClipMetadata metadata;
  metadata.game = session_->Label();
  metadata.trigger = trigger;
  metadata.seconds = static_cast<double>(clip->packets.back()->pts - clip->start) / kHnsPerSecond;
  metadata.session = sessionId_;
  metadata.sessionSeconds = static_cast<double>(clip->start - sessionStart_) / kHnsPerSecond;
  // A clip belongs to the last match that had started by its end, so a clip of the match
  // start itself counts toward the new match.
  metadata.match = MatchAt(end);
  metadata.utcStartMs = UnixMsAtHns(NowClocks(), clip->start);
  metadata.squadMoment = std::string(squadMoment);
  for (const auto& event : session_->EventsBetween(clip->start, end)) {
    metadata.markers.push_back(
        {event.type, static_cast<double>(event.time - clip->start) / kHnsPerSecond, event.text, event.witnesses});
  }

  const auto path = ClipPath();
  ++presenceState_.clips;
  const bool beep = std::string_view(trigger) == "hotkey";
  saves_.push_back(std::async(std::launch::async, [clip = std::move(*clip), metadata = std::move(metadata), path, beep] {
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    // Low CPU and disk priority, so writing a clip never makes the game hitch.
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
    const EfficiencyMode efficiency;
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
      if (!metadata.squadMoment.empty()) {
        log::Info("Squad view {} saved to {}", metadata.squadMoment, winrt::to_string(path.wstring()));
      }
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

void App::ToggleLongRecording() {
  if (longRecording_) {
    FinishLongRecording("");
    return;
  }
  if (!session_) {
    log::Info("Nothing is being recorded right now, so a long recording can't start");
    return;
  }
  // Settle the moments already found, so they're counted once below.
  HandleEvents();
  try {
    longRecording_ = std::make_unique<LongRecorder>(session_->Buffer(), ClipPath("_long"));
  } catch (const std::exception& e) {
    log::Error("Couldn't start a long recording: {}", e.what());
    return;
  } catch (const winrt::hresult_error& e) {
    log::Error("Couldn't start a long recording: {}", Describe(e));
    return;
  }
  // Moments from the stretch of buffer it opens with count too.
  longEvents_ = session_->EventsBetween(longRecording_->Start(), NowHns());
  log::Info("Long recording started in {}; press {} again to stop it", session_->Label(), config_.recordHotkeyText);
  MessageBeep(MB_OK);
}

void App::CheckLongRecording() {
  if (!longRecording_) return;
  if (longRecording_->Failed()) {
    FinishLongRecording("it couldn't write to the drive");
    return;
  }
  ULARGE_INTEGER free{};
  if (GetDiskFreeSpaceExW(longRecording_->File().parent_path().c_str(), &free, nullptr, nullptr) &&
      free.QuadPart < kMinFreeBytes) {
    log::Warn("The clips drive is nearly full");
    FinishLongRecording("the drive is nearly full");
  }
}

void App::FinishLongRecording(std::string_view why) {
  if (!longRecording_ || !session_) return;
  std::shared_ptr<LongRecorder> recorder(std::move(longRecording_));
  recorder->StopTaking();

  ClipMetadata metadata;
  metadata.game = session_->Label();
  metadata.trigger = "long";
  metadata.session = sessionId_;
  metadata.sessionSeconds = SessionSeconds(recorder->Start());
  metadata.match = MatchAt(recorder->Start());
  metadata.utcStartMs = UnixMsAtHns(NowClocks(), recorder->Start());
  for (const auto& event : longEvents_) {
    metadata.markers.push_back(
        {event.type, static_cast<double>(event.time - recorder->Start()) / kHnsPerSecond, event.text, event.witnesses});
  }
  longEvents_.clear();
  const double seconds = SessionSeconds(NowHns()) - metadata.sessionSeconds;
  if (why.empty()) log::Info("Long recording stopped after {:.0f}s", seconds);
  else log::Info("Long recording stopped after {:.0f}s because {}", seconds, why);

  saves_.push_back(std::async(std::launch::async, [recorder, metadata = std::move(metadata)]() mutable {
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    const EfficiencyMode efficiency;
    try {
      metadata.seconds = recorder->Finish();
      const auto& path = recorder->File();
      const std::string json = ToJson(metadata);
      AppendMetadataBox(path, json);
      auto sidecar = path;
      sidecar.replace_extension(L".json");
      std::ofstream(sidecar, std::ios::binary) << json;
      const double mb = static_cast<double>(std::filesystem::file_size(path)) / (1024.0 * 1024.0);
      log::Info("Saved {:.0f}s long recording ({:.1f} MB) to {}", metadata.seconds, mb, winrt::to_string(path.wstring()));
      if (const std::string summary = Summarize(metadata.markers); !summary.empty()) log::Info("Recording has {}", summary);
      MessageBeep(MB_OK);
    } catch (const winrt::hresult_error& e) {
      log::Error("Couldn't save the long recording: {}", Describe(e));
    } catch (const std::exception& e) {
      log::Error("Couldn't save the long recording: {}", e.what());
    }
    winrt::uninit_apartment();
  }));
}

}  // namespace clip
