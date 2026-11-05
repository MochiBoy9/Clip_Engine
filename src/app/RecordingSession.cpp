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

}  // namespace

RecordingSession::RecordingSession(const Config& config, HMONITOR monitor, std::string label,
                                   std::vector<DetectionRule> rules, std::vector<ScoreboardRule> scoreboards,
                                   std::vector<std::string> playerNames, int detectionSampleMs, double hudScale,
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

  if (config.gameAudio) StartAudio(AudioCapture::Source::GameAudio, kGameAudioStream, gameAudioEncoder_, gameAudio_);
  if (config.micAudio) StartAudio(AudioCapture::Source::Microphone, kMicAudioStream, micAudioEncoder_, micAudio_);

  if (!rules.empty() || !scoreboards.empty() || !journals.empty()) {
    try {
      detector_ = std::make_unique<EventDetector>(
          gpu_, *capture_, std::move(rules), std::move(scoreboards), std::move(playerNames), detectionSampleMs,
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
        if (gameAudio_) listener_ = detector_.get();
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
  // The audio thread hands sounds to the detector, so it stops first.
  listener_ = nullptr;
  micAudio_.reset();
  gameAudio_.reset();
  detector_.reset();
  micAudioEncoder_.reset();
  gameAudioEncoder_.reset();
  capture_.reset();
  video_.reset();
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

void RecordingSession::StartAudio(AudioCapture::Source source, int stream, std::unique_ptr<AacEncoder>& encoder,
                                  std::unique_ptr<AudioCapture>& capture) {
  try {
    encoder = std::make_unique<AacEncoder>(stream, buffer_);
    AacEncoder* target = encoder.get();
    // Game audio is everything the PC plays, so while the game isn't in front it's silenced
    // with the picture. The mic carries on: it's the player's own voice.
    const bool followFocus = source == AudioCapture::Source::GameAudio;
    capture = std::make_unique<AudioCapture>(source, [this, target, followFocus, silence = std::vector<int16_t>()](
                                                         const int16_t* pcm, uint32_t frames, int64_t pts) mutable {
      try {
        if (followFocus && away_) {
          silence.assign(size_t{frames} * AudioCapture::kChannels, 0);
          pcm = silence.data();
        }
        target->Push(pcm, frames, pts);
        if (followFocus) {
          if (EventDetector* detector = listener_) detector->Hear(pcm, frames, pts);
        }
      } catch (const winrt::hresult_error& e) {
        log::Error("Audio encoding failed: {}", Describe(e));
      }
    });
  } catch (const winrt::hresult_error& e) {
    log::Warn("Recording without {}: {}",
              source == AudioCapture::Source::GameAudio ? "game audio" : "microphone", Describe(e));
    capture.reset();
    encoder.reset();
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
