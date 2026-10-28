#include "app/RecordingSession.h"

#include "core/Clock.h"
#include "core/Log.h"

#include <avrt.h>

#include <algorithm>
#include <utility>

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

UINT AutoBitrate(UINT width, UINT height, UINT fps) {
  const double bits = 0.1 * width * height * fps;  // about 12 Mbps at 1080p60, 60 Mbps at 1440p165
  return static_cast<UINT>(std::clamp(bits, 8e6, 150e6));
}

}  // namespace

RecordingSession::RecordingSession(const Config& config, HMONITOR monitor, std::string label,
                                   std::vector<DetectionRule> rules, std::vector<ScoreboardRule> scoreboards,
                                   std::vector<std::string> playerNames, int detectionSampleMs)
    : label_(std::move(label)),
      monitor_(monitor),
      buffer_(int64_t{config.bufferSeconds} * kHnsPerSecond),
      bufferDuration_(int64_t{config.bufferSeconds} * kHnsPerSecond) {
  gpu_ = GpuDevice::Create();
  fps_ = config.fps > 0 ? static_cast<UINT>(config.fps) : std::clamp<UINT>(RefreshRate(monitor), 24, 240);
  capture_ = std::make_unique<MonitorCapture>(gpu_, monitor, fps_);

  VideoSettings video;
  video.width = capture_->Width() & ~1u;
  video.height = capture_->Height() & ~1u;
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

  if (!rules.empty() || !scoreboards.empty()) {
    try {
      detector_ = std::make_unique<EventDetector>(
          gpu_, *capture_, std::move(rules), std::move(scoreboards), std::move(playerNames), detectionSampleMs,
          config.detectionDebug, [this](const GameEvent& e) { OnEvent(e); },
          [this](const Scoreboard& board) {
            std::lock_guard lock(eventsMutex_);
            newScoreboards_.push_back(board);
          });
    } catch (const winrt::hresult_error& e) {
      log::Warn("Recording without event detection: {}", Describe(e));
    }
  }

  pacer_ = std::thread([this] { PaceFrames(); });
}

RecordingSession::~RecordingSession() {
  stop_ = true;
  if (pacer_.joinable()) pacer_.join();
  detector_.reset();
  micAudio_.reset();
  gameAudio_.reset();
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
    capture = std::make_unique<AudioCapture>(source, [target](const int16_t* pcm, uint32_t frames, int64_t pts) {
      try {
        target->Push(pcm, frames, pts);
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
    {
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
