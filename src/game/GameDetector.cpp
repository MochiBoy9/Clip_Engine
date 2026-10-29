#include "game/GameDetector.h"

#include <tlhelp32.h>

#include <algorithm>

namespace clip {

const std::vector<GameProfile>& BuiltInGames() {
  static const std::vector<GameProfile> games = {
      {"the_finals", "The Finals", {L"Discovery.exe"}},
      {"overwatch", "Overwatch", {L"Overwatch.exe"}},
      {"battlefield_6", "Battlefield 6", {L"bf6.exe"}},
      // Official game APIs.
      {"cs2", "Counter-Strike 2", {L"cs2.exe"}, GameApi::Cs2Gsi},
      {"dota2", "Dota 2", {L"dota2.exe"}, GameApi::Dota2Gsi},
      {"league_of_legends", "League of Legends", {L"League of Legends.exe"}, GameApi::LeagueLiveClient},
      {"rocket_league", "Rocket League", {L"RocketLeague.exe"}, GameApi::RocketLeagueStats},
      // Read off the screen.
      {"valorant", "Valorant", {L"VALORANT-Win64-Shipping.exe"}},
      {"fortnite", "Fortnite", {L"FortniteClient-Win64-Shipping.exe"}},
      {"apex_legends", "Apex Legends", {L"r5apex.exe", L"r5apex_dx12.exe"}},
      {"pubg", "PUBG", {L"TslGame.exe"}},
      {"marvel_rivals", "Marvel Rivals", {L"Marvel-Win64-Shipping.exe"}},
      {"rainbow_six_siege", "Rainbow Six Siege", {L"RainbowSix.exe", L"RainbowSix_Vulkan.exe", L"RainbowSix_DX11.exe"}},
      {"call_of_duty", "Call of Duty", {L"cod.exe", L"cod24-cod.exe", L"cod25-cod.exe"}},
      {"arc_raiders", "ARC Raiders", {L"PioneerGame.exe"}},
      {"delta_force", "Delta Force", {L"DeltaForceClient-Win64-Shipping.exe"}},
      {"deadlock", "Deadlock", {L"deadlock.exe", L"project8.exe"}},
      {"halo_infinite", "Halo Infinite", {L"HaloInfinite.exe"}},
      {"destiny_2", "Destiny 2", {L"destiny2.exe"}},
      {"team_fortress_2", "Team Fortress 2", {L"tf_win64.exe", L"tf.exe"}},
      {"escape_from_tarkov", "Escape from Tarkov", {L"EscapeFromTarkov.exe"}},
      // Recorded, with clips from the hotkey; their HUDs differ from one experience or mod to the next.
      {"roblox", "Roblox", {L"RobloxPlayerBeta.exe"}},
      {"minecraft", "Minecraft", {L"Minecraft.Windows.exe"}},
  };
  return games;
}

namespace {

struct WindowSearch {
  DWORD processId;
  HWND best = nullptr;
  LONGLONG bestArea = 0;
};

BOOL CALLBACK FindLargestWindow(HWND hwnd, LPARAM param) {
  auto* search = reinterpret_cast<WindowSearch*>(param);
  DWORD pid = 0;
  GetWindowThreadProcessId(hwnd, &pid);
  if (pid != search->processId || !IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != nullptr) {
    return TRUE;
  }
  RECT rect{};
  GetWindowRect(hwnd, &rect);
  const LONGLONG area = static_cast<LONGLONG>(rect.right - rect.left) * (rect.bottom - rect.top);
  if (area > search->bestArea) {
    search->best = hwnd;
    search->bestArea = area;
  }
  return TRUE;
}

}  // namespace

GameDetector::GameDetector(const std::vector<GameProfile>& custom) : games_(BuiltInGames()) {
  for (const auto& game : custom) {
    auto known = std::ranges::find(games_, game.id, &GameProfile::id);
    if (known == games_.end()) {
      games_.push_back(game);
    } else {
      known->executables.insert(known->executables.end(), game.executables.begin(), game.executables.end());
    }
  }
}

const GameProfile* GameDetector::MatchExecutable(const wchar_t* exeName) const {
  for (const auto& game : games_) {
    for (const auto& exe : game.executables) {
      if (_wcsicmp(exe.c_str(), exeName) == 0) return &game;
    }
  }
  return nullptr;
}

std::optional<DetectedGame> GameDetector::Find() const {
  const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if (snapshot == INVALID_HANDLE_VALUE) return std::nullopt;

  std::optional<DetectedGame> found;
  PROCESSENTRY32W entry{};
  entry.dwSize = sizeof(entry);
  for (BOOL ok = Process32FirstW(snapshot, &entry); ok && !found; ok = Process32NextW(snapshot, &entry)) {
    const GameProfile* profile = MatchExecutable(entry.szExeFile);
    if (!profile) continue;

    // A game that is still launching has no window yet; it will be picked up on a later poll.
    WindowSearch search{entry.th32ProcessID};
    EnumWindows(FindLargestWindow, reinterpret_cast<LPARAM>(&search));
    if (!search.best) continue;

    found = DetectedGame{profile, entry.th32ProcessID, search.best,
                         MonitorFromWindow(search.best, MONITOR_DEFAULTTONEAREST)};
  }
  CloseHandle(snapshot);
  return found;
}

}  // namespace clip
