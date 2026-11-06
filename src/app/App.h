#pragma once

#include "app/AutoClipPlanner.h"
#include "app/CommandChannel.h"
#include "app/RecordingSession.h"
#include "app/Timeline.h"
#include "core/Config.h"
#include "detect/DetectionRules.h"
#include "game/GameDetector.h"
#include "integrations/DiscordPresence.h"
#include "integrations/GameIntegration.h"
#include "media/LongRecorder.h"
#include "squad/SquadClips.h"

#include <filesystem>
#include <future>
#include <memory>
#include <optional>
#include <vector>

namespace clip {

// Watches for supported games, records while one is running, saves clips on the hotkey and
// around the moments the screen reader detects, runs long recordings on their own hotkey, and
// keeps a timeline of every moment and scoreboard for the player's records.
class App {
 public:
  App(Config config, DetectionRules rules);
  int Run();

 private:
  void Poll();
  void StopSession();
  void HandleEvents();
  void SaveReadyAutoClips(bool all);
  // Saves the clips the player asked for by voice ("clip that").
  void HandleVoiceClips();
  // Saves the last seconds of the recording, for a clip key or the app's Save clip button.
  void SaveHotkeyClip(int seconds);
  // Registers these keys to save clips, in place of the ones registered before. game names the
  // game they belong to, or is empty for the ones from the settings.
  void UseClipHotkeys(std::vector<ClipHotkey> keys, std::string_view game);
  // Uses the recorded game's own clip keys when it has some (clip_hotkeys.<game id>, read from
  // the settings file as it is now), and the usual ones otherwise.
  void ApplyGameClipHotkeys();
  // Saves [start, end] of the replay buffer. Returns false when there's nothing to save yet.
  // squadMoment names the squad moment it shows, for clips saved for squad sync.
  bool SaveRange(int64_t start, int64_t end, const char* trigger, std::string_view squadMoment = {});
  // Commands from the desktop app: a squadmate's moment to save this player's view of.
  void HandleCommands();
  void SaveReadySquadClips(bool all);
  // The squad's part of the Discord status, from the desktop app.
  void ApplySquadPresence();
  // A new file name in the game's clip folder; suffix goes before the extension.
  std::filesystem::path ClipPath(std::string_view suffix = {});
  // Starts a long recording, or stops the one running.
  void ToggleLongRecording();
  // Stops the long recording and saves it with every moment it caught.
  void FinishLongRecording(std::string_view why);
  // Stops it early when it can't go on (a write failed, the drive is nearly full).
  void CheckLongRecording();
  // Writes changes to the player's totals, from the game's API or its scoreboard, to the timeline.
  void PumpScoreboards();
  // Writes the journal notes from the screen and the game's own files into the timeline.
  void PumpJournal();
  double SessionSeconds(int64_t time) const;
  // Which match of the recording a moment belongs to: the match starts detected by then.
  int MatchAt(int64_t time) const;
  // Starts or stops reading the running game's own API, for games that have one.
  void SyncIntegration(const DetectedGame* game);
  // Hands moments from the game's API to the recording, like the ones read off the screen.
  void PumpIntegrationEvents();
  // Shows the game, the match so far and the clips saved as the user's Discord status.
  void UpdatePresence();

  Config config_;
  DetectionRules rules_;
  GameDetector detector_;
  std::unique_ptr<RecordingSession> session_;
  std::string sessionGameId_;
  // When the current recording started, and the match starts detected in it, for grouping
  // clips by match.
  std::string sessionId_;
  int64_t sessionStart_ = 0;
  std::vector<int64_t> matchStarts_;
  AutoClipPlanner planner_;
  int64_t retryAt_ = 0;
  std::string lastGame_;
  std::vector<std::future<void>> saves_;
  std::string lastStem_;
  int lastStemCount_ = 0;
  std::unique_ptr<GameIntegration> integration_;
  const GameProfile* integrationGame_ = nullptr;
  DWORD integrationProcess_ = 0;

  // The keys that save a clip right now, and the game whose own keys they are (empty for the
  // settings' save hotkey and more_clip_hotkeys).
  std::vector<ClipHotkey> clipHotkeys_;
  std::string clipHotkeysGame_;

  std::unique_ptr<LongRecorder> longRecording_;
  std::vector<GameEvent> longEvents_;  // the moments it caught, for its markers
  std::unique_ptr<TimelineWriter> timeline_;

  CommandChannel commands_;
  SquadSaveQueue squadSaves_;

  std::unique_ptr<DiscordPresence> presence_;
  PresenceState presenceState_;  // the game being recorded, counted since its match started
  int64_t flashUntil_ = 0;       // when the highlight on the status comes off
  SquadPresence squadPresence_;
};

}  // namespace clip
