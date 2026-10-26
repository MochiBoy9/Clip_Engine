#include "game/GameDetector.h"

#include <tlhelp32.h>

namespace clip {

const std::vector<GameProfile>& BuiltInGames() {
  static const std::vector<GameProfile> games = {
      {"the_finals", "The Finals", {L"Discovery.exe"}},
      {"overwatch", "Overwatch", {L"Overwatch.exe"}},
      {"battlefield_6", "Battlefield 6", {L"bf6.exe"}},
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

const GameProfile* MatchExecutable(const wchar_t* exeName) {
  for (const auto& game : BuiltInGames()) {
    for (const auto& exe : game.executables) {
      if (_wcsicmp(exe.c_str(), exeName) == 0) return &game;
    }
  }
  return nullptr;
}

}  // namespace

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
