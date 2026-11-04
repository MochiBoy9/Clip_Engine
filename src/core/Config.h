#pragma once

#include "core/Hotkeys.h"
#include "core/Win.h"
#include "detect/GameEvent.h"
#include "detect/OcrImage.h"

#include <filesystem>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// The player_names setting from the file as it is now. The desktop app can change it while the
// engine runs, so it's read again each time a game starts.
std::vector<std::string> ReadPlayerNames(const std::filesystem::path& file);

// The size the player set a game's HUD to, from the hud_scale setting ("overwatch 90,
// fortnite 120", percent per game id), as a fraction: 1 when the game isn't listed. Read again
// each time a game starts, like the player's names, since the app can change it.
double ReadHudScale(const std::filesystem::path& file, std::string_view gameId);

struct Config {
  std::filesystem::path outputDir;  // where clips are saved; one folder per game
  int bufferSeconds = 60;           // recent gameplay kept in memory
  int clipSeconds = 30;             // length of a clip saved with the hotkey
  int fps = 0;                      // 0 = match the monitor's refresh rate
  int bitrateMbps = 0;              // 0 = pick from resolution and frame rate
  int resolution = 0;               // the most lines to record, like 1440 for 1440p; 0 = the monitor's own
  std::string codec = "auto";       // auto | h264 | hevc
  bool gameAudio = true;
  bool micAudio = true;
  Hotkey saveHotkey;
  std::string saveHotkeyText = "F8";
  // More keys that save a clip, each of its own length ("Alt+F8 15, Shift+F8 120").
  std::vector<ClipHotkey> moreClipHotkeys;
  // Starts a recording that runs until it's pressed again (a "long recording"), however long that is.
  Hotkey recordHotkey{0, VK_F9};
  std::string recordHotkeyText = "F9";
  bool recordWithoutGame = false;  // keep a buffer of the main monitor when no game is open
  // While a game is recorded, record only the game: when another window comes to the front,
  // the video shows a "Game Not Found" card and game audio goes quiet until the game is back.
  bool gameFocus = true;

  // Show what's being played and recorded as the user's Discord status.
  bool discordPresence = true;
  std::string discordAppId;  // the Discord application the status belongs to; empty = the built-in one

  bool detection = true;          // read the screen for kills, deaths and other moments
  bool detectionDebug = false;    // log everything read off the screen, for tuning detection.ini
  ReadQuality readingQuality = ReadQuality::Medium;  // how hard to work at reading the screen
  bool autoClip = true;           // save a clip around each detected moment
  std::set<EventType> autoClipEvents{kAllEventTypes.begin(), kAllEventTypes.end()};
  int clipBeforeSeconds = 10;     // how much of an automatic clip comes before the moment
  int clipAfterSeconds = 5;       // and after it
  std::vector<std::string> playerNames;  // the player's in-game names, for reading killfeeds

  std::filesystem::path file;  // where these came from

  // Every key that saves a clip: the save hotkey with clip_seconds first, then the others.
  std::vector<ClipHotkey> ClipHotkeys() const;

  // Reads key = value settings, writing a commented default file first if none exists.
  static Config Load(const std::filesystem::path& file);
};

}  // namespace clip
