#pragma once

#include "audio/AacEncoder.h"
#include "audio/AppAudioCapture.h"
#include "audio/AudioCapture.h"
#include "audio/MixClock.h"
#include "audio/PcmMixer.h"
#include "audio/VoiceCommands.h"
#include "capture/MonitorCapture.h"
#include "core/Config.h"
#include "detect/EventDetector.h"
#include "media/ReplayBuffer.h"
#include "video/VideoEncoder.h"

#include <array>
#include <atomic>
#include <deque>
#include <memory>
#include <string>
#include <thread>

namespace clip {

// Everything that runs while one game (or the desktop) is being recorded: monitor capture,
// a frame pacer feeding the hardware encoder, the audio tracks (the game, the mic, Discord and
// other apps, and all of them mixed), voice commands, and the replay buffer.
class RecordingSession {
 public:
  // isGame: false for a recording of the desktop, which has no game audio of its own.
  // rules: what to look for on screen in this game; empty means no event detection.
  // scoreboards: where the game shows the player's totals, found by playerNames.
  RecordingSession(const Config& config, HMONITOR monitor, std::string label, bool isGame,
                   std::vector<DetectionRule> rules, std::vector<ScoreboardRule> scoreboards,
                   std::vector<std::string> playerNames, int detectionSampleMs, double hudScale = 1);
  ~RecordingSession();

  RecordingSession(const RecordingSession&) = delete;
  RecordingSession& operator=(const RecordingSession&) = delete;

  const std::string& Label() const { return label_; }
  HMONITOR Monitor() const { return monitor_; }

  // False when the session can't continue (resolution changed, encoder failed) and should be
  // recreated.
  bool Healthy() const;

  const ReplayBuffer& Buffer() const { return buffer_; }
  ReplayBuffer& Buffer() { return buffer_; }

  // The game's process, so the screen is read only while the game is in front, and, with
  // game_focus on, only the game is recorded.
  void SetGameProcess(DWORD processId);

  // Each audio track's volume and mute, applied at once.
  void SetAudioLevels(const AudioLevels& levels);
  // The audio tracks clips get, in order: "all" (the mix) when there's more than one, then
  // "game" (or "system", everything heard, where Windows can't record the game alone), "mic",
  // "discord" and "apps".
  const std::vector<std::string>& AudioTracks() const { return audioTracks_; }
  // Clip lengths, in seconds, that the player asked for by voice since the last call.
  std::vector<int> TakeVoiceClips();

  // False while the recording shows "Game Not Found" because another window is in front.
  bool GameInFocus() const { return !away_; }

  // Adds a moment found some other way than reading the screen (the game's own API).
  void AddEvent(const GameEvent& event) { OnEvent(event); }
  // The game's own API now reports these kinds of moment, so the screen's sightings of them
  // are dropped instead of counted twice. The screen rules stay as a fallback until then.
  void PreferApiFor(const std::vector<EventType>& types) {
    unsigned mask = 0;
    for (EventType type : types) mask |= 1u << static_cast<unsigned>(type);
    apiTypes_ |= mask;
  }

  // Events detected since the last call.
  std::vector<GameEvent> TakeNewEvents();
  // Scoreboard readings since the last call.
  std::vector<Scoreboard> TakeScoreboards();
  // Every detected event still in the replay buffer between start and end.
  std::vector<GameEvent> EventsBetween(int64_t start, int64_t end) const;

 private:
  void PaceFrames();
  // Whether the game's own frames should be recorded, for game_focus. Called by the frame pacer.
  bool CheckFocus();
  void OnEvent(const GameEvent& event);
  void StartAudio(const Config& config, bool isGame);
  // Audio from one source, on its capture thread: heard by detection or voice commands, then
  // scaled by its volume and encoded on its track and into the mix.
  void OnAudio(AudioTrack track, const int16_t* pcm, uint32_t frames, int64_t pts);
  // Keeps the replay buffer within the memory the PC can spare.
  void LimitBufferMemory(const Config& config, UINT videoBitrate);

  std::string label_;
  HMONITOR monitor_;
  UINT fps_ = 60;

  // Declared in dependency order; the destructor tears down in reverse.
  std::shared_ptr<GpuDevice> gpu_;
  ReplayBuffer buffer_;
  std::unique_ptr<MonitorCapture> capture_;
  std::unique_ptr<VideoEncoder> video_;

  struct Track {
    bool recorded = false;  // has a track of its own in clips
    std::unique_ptr<AacEncoder> encoder;
    std::atomic<float> gain{1};
    float lastGain = 1;            // used only on the source's capture thread
    std::vector<int16_t> scaled;   // likewise
  };
  std::array<Track, kAudioTrackCount> tracks_;
  // All recorded sources added together, on the first audio track, when there's more than one.
  PcmMixer mixer_;
  std::unique_ptr<AacEncoder> mixEncoder_;
  std::unique_ptr<MixClock> mixClock_;
  // Everything heard, for the game track where Windows can't record the game alone.
  std::unique_ptr<AudioCapture> systemAudio_;
  std::unique_ptr<AudioCapture> micAudio_;
  std::unique_ptr<AppAudioCapture> gameAudio_;
  std::unique_ptr<AppAudioCapture> discordAudio_;
  std::unique_ptr<AppAudioCapture> appsAudio_;
  std::vector<std::string> audioTracks_;
  std::vector<int16_t> awaySilence_;  // game audio as detection hears it while the game isn't in front

  std::unique_ptr<VoiceCommands> voice_;
  std::mutex voiceMutex_;
  std::vector<int> voiceClips_;

  std::unique_ptr<EventDetector> detector_;
  std::atomic<EventDetector*> listener_{nullptr};  // detector_ while it listens to the game's audio
  std::atomic<unsigned> apiTypes_{0};  // PreferApiFor, one bit per EventType

  int64_t bufferDuration_;
  mutable std::mutex eventsMutex_;
  std::deque<GameEvent> history_;
  std::vector<GameEvent> newEvents_;
  std::vector<Scoreboard> newScoreboards_;

  // game_focus: while another window is in front, the "Game Not Found" frame is recorded
  // instead of the screen, and game audio is silent.
  bool focusOnly_ = false;
  std::atomic<DWORD> gameProcess_{0};
  std::atomic<bool> away_{false};
  int64_t backSince_ = 0;  // when the game came back in front
  winrt::com_ptr<ID3D11Texture2D> notFoundFrame_;
  bool notFoundFailed_ = false;

  std::atomic<bool> stop_{false};
  std::thread pacer_;
};

}  // namespace clip
