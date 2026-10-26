#pragma once

#include "core/Win.h"

#include <filesystem>
#include <optional>
#include <string>
#include <string_view>

namespace clip {

struct Hotkey {
  UINT modifiers = 0;  // MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN
  UINT key = VK_F8;
};

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
  bool recordWithoutGame = false;  // keep a buffer of the main monitor when no game is open

  // Reads key = value settings, writing a commented default file first if none exists.
  static Config Load(const std::filesystem::path& file);
};

}  // namespace clip
