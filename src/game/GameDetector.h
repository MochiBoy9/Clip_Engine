#pragma once

#include "core/Win.h"

#include <optional>
#include <string>
#include <vector>

namespace clip {

struct GameProfile {
  std::string id;    // stable key, also used for folder names
  std::string name;  // shown to the user
  std::vector<std::wstring> executables;
};

// The games Clip Engine supports so far.
const std::vector<GameProfile>& BuiltInGames();

struct DetectedGame {
  const GameProfile* profile = nullptr;
  DWORD processId = 0;
  HWND window = nullptr;
  HMONITOR monitor = nullptr;  // the monitor showing the game's main window
};

class GameDetector {
 public:
  // custom: games added in detection.ini. One with a built-in game's id adds executables to it.
  explicit GameDetector(const std::vector<GameProfile>& custom = {});

  // Every game it looks for, built-in ones first.
  const std::vector<GameProfile>& Games() const { return games_; }

  // Returns the first supported game that is running and has a visible window.
  std::optional<DetectedGame> Find() const;

 private:
  const GameProfile* MatchExecutable(const wchar_t* exeName) const;

  std::vector<GameProfile> games_;
};

}  // namespace clip
