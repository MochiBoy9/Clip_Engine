#pragma once

#include "core/Win.h"

#include <filesystem>
#include <string>
#include <vector>

namespace clip {

// Where the engine listens for Game State Integration (CS2 and Dota 2 post to it).
constexpr unsigned short kGsiPort = 47317;
// Sent back by the game with every post, so only our own config is accepted.
constexpr char kGsiToken[] = "clip_engine_gsi";
// Rocket League's Stats API port when its config doesn't say otherwise.
constexpr unsigned short kRocketLeagueDefaultPort = 49123;

// What the setup did to one game's install.
enum class SetupResult { AlreadyOn, TurnedOn, Failed, NotInstalled };

// CS2 and Dota 2 only send their game state to programs listed in a config file in the game's
// folder, read when the game starts. These write ours (from the folder holding cs2.exe or
// dota2.exe, ...\game\bin\win64).
SetupResult InstallCs2Config(const std::filesystem::path& gameExe);
SetupResult InstallDota2Config(const std::filesystem::path& gameExe);

// Rocket League's Stats API is off until PacketSendRate in TAGame\Config\DefaultStatsAPI.ini is
// above 0. Turns it on and reports the port it uses.
SetupResult EnableRocketLeagueStats(const std::filesystem::path& gameExe, unsigned short& port);

// Team Fortress 2 prints every killfeed line to its console. This adds a line to
// tf\cfg\autoexec.cfg that has the game write its console to kTf2ConsoleLog (in the tf folder)
// as well, from the folder holding tf_win64.exe. A log grown very large is emptied first.
constexpr wchar_t kTf2ConsoleLog[] = L"clip_engine_console.log";
SetupResult EnableTf2ConsoleLog(const std::filesystem::path& gameExe);
// The console logs Team Fortress 2 may be writing: ours, and console.log from -condebug.
std::vector<std::filesystem::path> Tf2ConsoleLogs(const std::filesystem::path& gameExe);

// Runs the above for every install found through Steam and the Epic Games launcher, so the
// APIs are already on the first time a game is launched. Logs what it changed.
void PrepareGameIntegrations();

// Full path of a running process's executable, or empty.
std::filesystem::path ProcessImagePath(DWORD processId);

}  // namespace clip
