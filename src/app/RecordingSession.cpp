#include "app/RecordingSession.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "video/GameNotFoundFrame.h"
#include "video/OutputSize.h"

#include <avrt.h>

#include <algorithm>
#include <utility>
#include <vector>

namespace clip {
namespace {

UINT RefreshRate(HMONITOR monitor) {
  MONITORINFOEXW info{};
  info.cbSize = sizeof(info);
  DEVMODEW mode{};
  mode.dmSize = sizeof(mode);
  if (GetMonitorInfoW(monitor, &info) && EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode) &&
      mode.dmDisplayFrequency > 1) {
    return mode.dmDisplayFrequency;
  }
  return 60;
}

// H.264 level 5.2 tops out around 1440p at 144 fps. Past that, HEVC is needed.
bool NeedsHevc(UINT width, UINT height, UINT fps) {
  const uint64_t macroblocksPerSecond = uint64_t{(width + 15) / 16} * ((height + 15) / 16) * fps;
  return width > 4096 || height > 2304 || macroblocksPerSecond > 2'073'600;
}

// How long the game must be back in front before its frames are used again: the capture runs a
// frame or two behind, so the first frames after switching back can still show the other window.
constexpr int64_t kFocusSettle = 250 * kHnsPerMs;
// SetWindowDisplayAffinity's "leave out of captures", as Clip Engine's own side panel uses.
constexpr DWORD kExcludedFromCapture = 0x11;

// Matching the monitor goes up to this. Every frame is copied, converted and encoded on the
// GPU the game is using, so recording a 240 Hz monitor at 240 fps costs the game twice what
// 120 fps does for clips that look much the same; a higher fps setting still records faster.
constexpr UINT kMaxAutoFps = 120;

UINT AutoBitrate(UINT width, UINT height, UINT fps) {
  const double bits = 0.1 * width * height * fps;  // about 12 Mbps at 1080p60, 60 Mbps at 1440p165
  return static_cast<UINT>(std::clamp(bits, 8e6, 150e6));
}

// The mix is made this far behind real time, after every source's audio has arrived: the apps'
// tracks are themselves mixed 60 ms behind.
constexpr int64_t kMixLatency = 150 * kHnsPerMs;
// The replay buffer gets at most this share of the PC's memory.
constexpr double kBufferMemoryShare = 0.4;
// What an audio track costs in the buffer: 192 kbps AAC.
constexpr UINT kAudioBitrate = 192'000;

constexpr int StreamOf(AudioTrack track) {
  switch (track) {
    case AudioTrack::Game: return kGameAudioStream;
    case AudioTrack::Mic: return kMicAudioStream;
    case AudioTrack::Discord: return kDiscordAudioStream;
    case AudioTrack::Apps: return kAppsAudioStream;
  }
  return kGameAudioStream;
}

// The apps with sound right now that go on a track of this kind, leaving out the game's own processes.
std::vector<AudioApp> AppsFor(AppAudioKind kind, DWORD game) {
  auto apps = AppsWithAudio();
  if (apps.empty()) return apps;
  const auto parents = ParentProcesses();
  std::erase_if(apps, [&](const AudioApp& app) {
    return ClassifyAudioApp(app.exe) != kind || IsInProcessTree(app.processId, game, parents) ||
           app.processId == GetCurrentProcessId();
  });
  std::vector<uint32_t> pids;
  for (const auto& app : apps) pids.push_back(app.processId);
  const auto roots = TreeRoots(pids, parents);
  std::erase_if(apps, [&](const AudioApp& app) { return std::ranges::find(roots, app.processId) == roots.end(); });
  return apps;
}

}  // namespace

RecordingSession::RecordingSession(const Config& config, HMONITOR monitor, std::string label, bool isGame,
                                   std::vector<DetectionRule> rules, std::vector<ScoreboardRule> scoreboards,
                                   std::vector<std::string> playerNames, DetectionSettings detection, double hudScale,
                                   std::vector<JournalRule> journals)
    : label_(std::move(label)),
      monitor_(monitor),
      buffer_(int64_t{config.bufferSeconds} * kHnsPerSecond),
      bufferDuration_(int64_t{config.bufferSeconds} * kHnsPerSecond),
      focusOnly_(config.gameFocus) {
  gpu_ = GpuDevice::Create();
  fps_ = config.fps > 0 ? static_cast<UINT>(config.fps) : std::clamp<UINT>(RefreshRate(monitor), 24, kMaxAutoFps);
  capture_ = std::make_unique<MonitorCapture>(gpu_, monitor, fps_);

  VideoSettings video;
  const auto size = OutputSize(capture_->Width(), capture_->Height(), config.resolution);
  video.width = size.width;
  video.height = size.height;
  video.fps = fps_;
  video.bitrate = config.bitrateMbps > 0 ? static_cast<UINT>(config.bitrateMbps) * 1'000'000
                                         : AutoBitrate(video.width, video.height, video.fps);
  video.hevc = config.codec == "hevc" || (config.codec == "auto" && NeedsHevc(video.width, video.height, fps_));

  try {
    video_ = std::make_unique<VideoEncoder>(gpu_, capture_->Width(), capture_->Height(), video, buffer_);
  } catch (const winrt::hresult_error& e) {
    if (!video.hevc || config.codec != "auto") throw;
    // Older GPUs lack HEVC; H.264 may still cope with a little loss of quality.
    log::Warn("HEVC encoder unavailable ({}), trying H.264", Describe(e));
    video.hevc = false;
    video_ = std::make_unique<VideoEncoder>(gpu_, capture_->Width(), capture_->Height(), video, buffer_);
  }

  log::Info("Recording {}: {}x{} at {} fps, {} {} Mbps on {}", label_, video.width, video.height, fps_,
            video.hevc ? "HEVC" : "H.264", video.bitrate / 1'000'000, video_->Name());

  LimitBufferMemory(config, video.bitrate);
  StartAudio(config, isGame);

  if (!rules.empty() || !scoreboards.empty() || !journals.empty()) {
    try {
      detector_ = std::make_unique<EventDetector>(
          gpu_, *capture_, std::move(rules), std::move(scoreboards), std::move(playerNames), detection,
          config.readingQuality, hudScale, config.detectionDebug,
          [this](const GameEvent& e) {
            if (!(apiTypes_.load() & (1u << static_cast<unsigned>(e.type)))) OnEvent(e);
          },
          [this](const Scoreboard& board) {
            std::lock_guard lock(eventsMutex_);
            newScoreboards_.push_back(board);
          },
          std::move(journals),
          [this](const JournalNote& note) {
            std::lock_guard lock(eventsMutex_);
            newNotes_.push_back(note);
          });
      if (detector_->Listens()) {
        if (gameAudio_ || systemAudio_) listener_ = detector_.get();
        else log::Info("Game audio is off in Settings, so the game's sounds aren't listened for");
      }
    } catch (const winrt::hresult_error& e) {
      log::Warn("Recording without event detection: {}", Describe(e));
    }
  }

  pacer_ = std::thread([this] { PaceFrames(); });
}

RecordingSession::~RecordingSession() {
  stop_ = true;
  if (pacer_.joinable()) pacer_.join();
  // The audio threads hand sounds to the detector, voice commands, the mix and the encoders, so
  // they stop first, then the mix.
  listener_ = nullptr;
  micAudio_.reset();
  systemAudio_.reset();
  gameAudio_.reset();
  discordAudio_.reset();
  appsAudio_.reset();
  mixClock_.reset();
  voice_.reset();
  detector_.reset();
  mixEncoder_.reset();
  for (auto& track : tracks_) track.encoder.reset();
  capture_.reset();
  video_.reset();
}

void RecordingSession::SetGameProcess(DWORD processId) {
  if (gameProcess_.exchange(processId) != processId && gameAudio_) gameAudio_->Refresh();
  if (detector_) detector_->SetGameProcess(processId);
}

void RecordingSession::SetAudioLevels(const AudioLevels& levels) {
  for (AudioTrack track : kAudioTracks) tracks_[static_cast<size_t>(track)].gain = levels.Gain(track);
}

std::vector<int> RecordingSession::TakeVoiceClips() {
  std::lock_guard lock(voiceMutex_);
  return std::exchange(voiceClips_, {});
}

void RecordingSession::LimitBufferMemory(const Config& config, UINT videoBitrate) {
  MEMORYSTATUSEX memory{};
  memory.dwLength = sizeof(memory);
  if (!GlobalMemoryStatusEx(&memory) || memory.ullTotalPhys == 0) return;
  const auto limit = static_cast<size_t>(static_cast<double>(memory.ullTotalPhys) * kBufferMemoryShare);
  buffer_.SetMaxBytes(limit);

  const double bytesPerSecond = (videoBitrate + 5.0 * kAudioBitrate) / 8;  // at most five audio tracks
  const double needed = bytesPerSecond * config.bufferSeconds;
  constexpr double kGb = 1024.0 * 1024 * 1024;
  if (needed > static_cast<double>(limit)) {
    log::Warn("A {} minute replay at {} Mbps needs about {:.1f} GB of memory, more than the {:.1f} GB this PC can "
              "spare, so it keeps about {:.0f} minutes. A lower resolution or bitrate keeps more.",
              config.bufferSeconds / 60, videoBitrate / 1'000'000, needed / kGb, static_cast<double>(limit) / kGb,
              static_cast<double>(limit) / bytesPerSecond / 60);
  } else if (config.bufferSeconds > 600) {
    log::Info("The {} minute replay takes about {:.1f} GB of memory", config.bufferSeconds / 60, needed / kGb);
  }
}

bool RecordingSession::Healthy() const { return !capture_->SizeChanged() && !video_->Failed(); }

void RecordingSession::OnEvent(const GameEvent& event) {
  std::lock_guard lock(eventsMutex_);
  history_.push_back(event);
  while (!history_.empty() && history_.front().time < event.time - bufferDuration_) history_.pop_front();
  newEvents_.push_back(event);
}

std::vector<GameEvent> RecordingSession::TakeNewEvents() {
  std::lock_guard lock(eventsMutex_);
  return std::exchange(newEvents_, {});
}

std::vector<Scoreboard> RecordingSession::TakeScoreboards() {
  std::lock_guard lock(eventsMutex_);
  return std::exchange(newScoreboards_, {});
}

std::vector<JournalNote> RecordingSession::TakeJournalNotes() {
  std::lock_guard lock(eventsMutex_);
  return std::exchange(newNotes_, {});
}

std::vector<GameEvent> RecordingSession::EventsBetween(int64_t start, int64_t end) const {
  std::lock_guard lock(eventsMutex_);
  std::vector<GameEvent> result;
  for (const auto& e : history_) {
    if (e.time >= start && e.time <= end) result.push_back(e);
  }
  return result;
}

void RecordingSession::StartAudio(const Config& config, bool isGame) {
  // Windows 10 version 2004 and later can record one app's audio. Before that, the game's track
  // has everything heard, and Discord and other apps can't be told apart from it.
  const bool separate = (config.gameAudio || config.discordAudio || config.appsAudio) && AppAudioCapture::Supported();
  auto& game = tracks_[static_cast<size_t>(AudioTrack::Game)];
  auto& mic = tracks_[static_cast<size_t>(AudioTrack::Mic)];
  auto& discord = tracks_[static_cast<size_t>(AudioTrack::Discord)];
  auto& apps = tracks_[static_cast<size_t>(AudioTrack::Apps)];
  game.recorded = config.gameAudio && (isGame || !separate);
  mic.recorded = config.micAudio;
  discord.recorded = separate && config.discordAudio;
  apps.recorded = separate && config.appsAudio;
  if (!separate && (config.discordAudio || config.appsAudio)) {
    log::Info("This version of Windows can't record Discord and other apps on tracks of their own, so they're "
              "in the game audio track{}", config.gameAudio ? "" : ", which is off");
  }
  SetAudioLevels(config.levels);

  // Each source's encoder, then the mix's when there's more than one source.
  int recorded = 0;
  for (AudioTrack which : kAudioTracks) {
    auto& track = tracks_[static_cast<size_t>(which)];
    if (!track.recorded) continue;
    try {
      track.encoder = std::make_unique<AacEncoder>(StreamOf(which), buffer_);
      ++recorded;
    } catch (const winrt::hresult_error& e) {
      log::Warn("Recording without the {} track: {}", AudioTrackName(which), Describe(e));
      track.recorded = false;
    }
  }
  if (recorded > 1) {
    try {
      mixEncoder_ = std::make_unique<AacEncoder>(kMixAudioStream, buffer_);
      audioTracks_.push_back("all");
    } catch (const winrt::hresult_error& e) {
      log::Warn("Recording without the track of all audio mixed: {}", Describe(e));
    }
  }
  for (AudioTrack which : kAudioTracks) {
    if (!tracks_[static_cast<size_t>(which)].recorded) continue;
    audioTracks_.push_back(which == AudioTrack::Game && !separate ? "system" : std::string(AudioTrackName(which)));
  }

  auto sink = [this](AudioTrack which) {
    return [this, which](const int16_t* pcm, uint32_t frames, int64_t pts) { OnAudio(which, pcm, frames, pts); };
  };
  try {
    if (game.recorded && separate) {
      gameAudio_ = std::make_unique<AppAudioCapture>(
          "game",
          [this]() -> std::vector<AudioApp> {
            const DWORD pid = gameProcess_;
            if (pid == 0) return {};
            return {AudioApp{pid, label_}};
          },
          sink(AudioTrack::Game));
    } else if (game.recorded) {
      systemAudio_ = std::make_unique<AudioCapture>(AudioCapture::Source::GameAudio, sink(AudioTrack::Game));
    }
    if (discord.recorded) {
      discordAudio_ = std::make_unique<AppAudioCapture>(
          "Discord", [this] { return AppsFor(AppAudioKind::VoiceChat, gameProcess_); }, sink(AudioTrack::Discord));
    }
    if (apps.recorded) {
      appsAudio_ = std::make_unique<AppAudioCapture>(
          "other apps", [this] { return AppsFor(AppAudioKind::Other, gameProcess_); }, sink(AudioTrack::Apps));
    }

    // Voice commands listen to the mic even when its track is off or muted.
    if (config.voiceClips) {
      const int usual = config.voiceClipSeconds > 0 ? config.voiceClipSeconds : config.clipSeconds;
      voice_ = std::make_unique<VoiceCommands>(config.voicePhrases, config.voiceSensitivity,
                                               [this, usual](const VoicePhrase& phrase) {
                                                 std::lock_guard lock(voiceMutex_);
                                                 voiceClips_.push_back(phrase.seconds > 0 ? phrase.seconds : usual);
                                               });
    }
    if (mic.recorded || voice_) {
      micAudio_ = std::make_unique<AudioCapture>(AudioCapture::Source::Microphone, sink(AudioTrack::Mic));
    }
  } catch (const winrt::hresult_error& e) {
    log::Warn("Recording with some audio missing: {}", Describe(e));
  }

  if (mixEncoder_) {
    mixClock_ = std::make_unique<MixClock>(mixer_, kMixLatency, [this](const int16_t* pcm, uint32_t frames, int64_t pts) {
      mixEncoder_->Push(pcm, frames, pts);
    });
  }
}

void RecordingSession::OnAudio(AudioTrack which, const int16_t* pcm, uint32_t frames, int64_t pts) {
  auto& track = tracks_[static_cast<size_t>(which)];
  // Game audio and other apps go quiet with the picture while the game isn't in front, so
  // nothing from another window ends up in a clip. The mic and voice chat carry on: they're the
  // players' own voices.
  const bool followsFocus = which == AudioTrack::Game || which == AudioTrack::Apps;
  const bool quiet = followsFocus && away_;
  try {
    if (which == AudioTrack::Mic && voice_) voice_->Hear(pcm, frames);
    if (which == AudioTrack::Game) {
      if (EventDetector* detector = listener_) {
        if (quiet) {
          awaySilence_.assign(size_t{frames} * AudioCapture::kChannels, 0);
          detector->Hear(awaySilence_.data(), frames, pts);
        } else {
          detector->Hear(pcm, frames, pts);
        }
      }
    }
    if (!track.recorded) return;
    const float gain = quiet ? 0.0f : track.gain.load();
    track.scaled.assign(pcm, pcm + size_t{frames} * AudioCapture::kChannels);
    ApplyGain(track.scaled.data(), frames, AudioCapture::kChannels, track.lastGain, gain);
    track.lastGain = gain;
    if (track.encoder) track.encoder->Push(track.scaled.data(), frames, pts);
    if (mixEncoder_) mixer_.Push(static_cast<int>(which), track.scaled.data(), frames, pts);
  } catch (const winrt::hresult_error& e) {
    log::Error("Audio encoding failed: {}", Describe(e));
  }
}

bool RecordingSession::CheckFocus() {
  const DWORD game = gameProcess_;
  if (!focusOnly_ || game == 0) {
    away_ = false;
    return true;
  }
  // Checked on every frame: Windows changes the window in front before the new one is drawn,
  // so the switch is caught before the capture can show it.
  const int64_t now = NowHns();
  // No window is in front for a moment while switching; keep things as they are.
  HWND front = GetForegroundWindow();
  if (!front) return !away_ && now - backSince_ >= kFocusSettle;
  DWORD process = 0;
  GetWindowThreadProcessId(front, &process);
  DWORD affinity = 0;
  // A window left out of captures, like Clip Engine's side panel, doesn't show in the video anyway.
  const bool inFront = process == game ||
                       (GetWindowDisplayAffinity(front, &affinity) && affinity == kExcludedFromCapture);

  if (!inFront && !away_) {
    away_ = true;
    log::Info("{} isn't in front, so the recording shows Game Not Found until you're back", label_);
  } else if (inFront && away_) {
    away_ = false;
    backSince_ = now;
    log::Info("Back in {}, recording it again", label_);
  }
  return !away_ && now - backSince_ >= kFocusSettle;
}

void RecordingSession::PaceFrames() {
  // Hand the encoder one frame per tick at a constant rate, repeating the last frame when the
  // screen didn't change, so clips play back smoothly in every player and editor.
  // Windows' multimedia scheduler gives the pacer steady, short time slices, which keeps
  // frame timing even without raising its priority over the game.
  DWORD taskIndex = 0;
  HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Capture", &taskIndex);
  if (!mmcss) SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
  HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
  if (!timer) timer = CreateWaitableTimerW(nullptr, TRUE, nullptr);

  int64_t start = NowHns();
  uint64_t frame = 0;
  uint64_t lastDropped = 0;
  while (!stop_) {
    ++frame;
    const int64_t due = start + static_cast<int64_t>(frame * kHnsPerSecond / fps_);
    const int64_t wait = due - NowHns();
    if (wait > 0) {
      LARGE_INTEGER relative;
      relative.QuadPart = -wait;
      SetWaitableTimer(timer, &relative, 0, nullptr, nullptr, FALSE);
      WaitForSingleObject(timer, 1000);
    } else if (wait < -kHnsPerSecond / 4) {
      // Fell far behind (system stall); start a fresh schedule rather than bursting frames.
      start = NowHns();
      frame = 0;
      continue;
    }

    ID3D11Texture2D* latest = nullptr;
    if (!CheckFocus()) {
      if (!notFoundFrame_ && !notFoundFailed_) {
        try {
          notFoundFrame_ = MakeGameNotFoundFrame(gpu_->device.get(), capture_->Width(), capture_->Height(),
                                                 winrt::to_hstring(label_));
        } catch (const winrt::hresult_error& e) {
          log::Error("Couldn't draw the Game Not Found frame: {}", Describe(e));
          notFoundFailed_ = true;
        }
      }
      // Without the card, nothing is recorded until the game is back, rather than the other window.
      latest = notFoundFrame_.get();
    } else {
      std::lock_guard lock(gpu_->contextLock);
      latest = capture_->LatestFrame();
    }
    if (latest) video_->Submit(latest, due);

    if (const uint64_t dropped = video_->DroppedFrames(); dropped / 100 > lastDropped / 100) {
      log::Warn("Encoder is falling behind: {} frames dropped so far", dropped);
      lastDropped = dropped;
    }
  }
  CloseHandle(timer);
  if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
}

}  // namespace clip
