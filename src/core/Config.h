#pragma once

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

struct Hotkey {
  UINT modifiers = 0;  // MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN
  UINT key = VK_F8;
};

// The player_names setting from the file as it is now. The desktop app can change it while the
// engine runs, so it's read again each time a game starts.
std::vector<std::string> ReadPlayerNames(const std::filesystem::path& file);

// The size the player set a game's HUD to, from the hud_scale setting ("overwatch 90,
// fortnite 120", percent per game id), as a fraction: 1 when the game isn't listed. Read again
// each time a game starts, like the player's names, since the app can change it.
double ReadHudScale(const std::filesystem::path& file, std::string_view gameId);

// Parses "F8", "Alt+F10", "Ctrl+Shift+S" and similar.
std::optional<Hotkey> ParseHotkey(std::string_view text);

struct Config {
  std::filesystem::path outputDir;  // where clips are saved; one folder per game
  int bufferSeconds = 60;           // recent gameplay kept in memory
  int clipSeconds = 30;             // length of a clip saved with the hotkey
  int fps = 0;                      // 0 = match the monitor's refresh rate
  int bitrateMbps = 0;              // 0 = pick from resolution and frame rate
  std::string codec = "auto";       // auto | h264 | hevc
  bool gameAudio = true;
  bool micAudio = true;
  Hotkey saveHotkey;
  std::string saveHotkeyText = "F8";
  // Starts a recording that runs until it's pressed again (a "long recording"), however long that is.
  Hotkey recordHotkey{0, VK_F9};
  std::string recordHotkeyText = "F9";
  bool recordWithoutGame = false;  // keep a buffer of the main monitor when no game is open

  bool detection = true;          // read the screen for kills, deaths and other moments
  bool detectionDebug = false;    // log everything read off the screen, for tuning detection.ini
  ReadQuality readingQuality = ReadQuality::Medium;  // how hard to work at reading the screen
  bool autoClip = true;           // save a clip around each detected moment
  std::set<EventType> autoClipEvents{kAllEventTypes.begin(), kAllEventTypes.end()};
  int clipBeforeSeconds = 10;     // how much of an automatic clip comes before the moment
  int clipAfterSeconds = 5;       // and after it
  std::vector<std::string> playerNames;  // the player's in-game names, for reading killfeeds

  std::filesystem::path file;  // where these came from

  // Reads key = value settings, writing a commented default file first if none exists.
  static Config Load(const std::filesystem::path& file);
};

}  // namespace clip
