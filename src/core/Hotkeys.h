#pragma once

#include <windows.h>

#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

struct Hotkey {
  UINT modifiers = 0;  // MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN
  UINT key = VK_F8;
};

// Parses "F8", "Alt+F10", "Ctrl+Shift+S" and similar.
std::optional<Hotkey> ParseHotkey(std::string_view text);

// A key that saves a clip of its own length: the save hotkey with clip_seconds, each of
// more_clip_hotkeys, or a game's own set from clip_hotkeys.<game id>.
struct ClipHotkey {
  Hotkey hotkey;
  std::string text;  // as the player wrote it, for the log
  int seconds = 30;
};

// "Alt+F8 15, Shift+F8 120": keys with how many seconds each one saves, separated by commas.
// A length may end in s. Entries this can't read are left out and, when bad is given, added
// to it as written.
std::vector<ClipHotkey> ParseClipHotkeys(std::string_view value, std::vector<std::string>* bad = nullptr);

// The setting that holds a game's own clip keys, which replace the save hotkey and
// more_clip_hotkeys while that game is recorded.
std::string GameClipHotkeysKey(std::string_view gameId);

// The game's own clip keys from the settings file as it is now, or nothing when the game
// uses the usual ones. Read each time the game starts, since Detection Studio can change them
// while the engine runs.
std::optional<std::vector<ClipHotkey>> ReadGameClipHotkeys(const std::filesystem::path& file, std::string_view gameId);

}  // namespace clip
