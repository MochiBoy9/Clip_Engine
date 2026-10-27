#include "integrations/IntegrationSetup.h"

#include "core/Json.h"
#include "core/Log.h"

#include <algorithm>
#include <cstdlib>
#include <format>
#include <fstream>
#include <sstream>
#include <vector>

namespace clip {
namespace {

namespace fs = std::filesystem;

std::string Narrow(const fs::path& path) { return winrt::to_string(path.wstring()); }

std::string ReadFile(const fs::path& file) {
  std::ifstream in(file, std::ios::binary);
  std::ostringstream text;
  text << in.rdbuf();
  return text.str();
}

// Writes the file unless it already holds exactly this text.
SetupResult WriteIfChanged(const fs::path& file, const std::string& text) {
  std::error_code ec;
  if (fs::exists(file, ec) && ReadFile(file) == text) return SetupResult::AlreadyOn;
  fs::create_directories(file.parent_path(), ec);
  std::ofstream out(file, std::ios::binary | std::ios::trunc);
  out << text;
  out.close();
  if (!out) {
    log::Warn("Couldn't write {}; Windows may need Clip Engine to run as administrator once for that folder",
              Narrow(file));
    return SetupResult::Failed;
  }
  return SetupResult::TurnedOn;
}

std::string GsiConfig(std::string_view path, std::string_view data) {
  return std::format(R"("Clip Engine"
{{
  "uri"       "http://127.0.0.1:{}/{}"
  "timeout"   "1.0"
  "buffer"    "0.0"
  "throttle"  "0.1"
  "heartbeat" "10.0"
  "auth"
  {{
    "token" "{}"
  }}
  "data"
  {{
{}  }}
}}
)",
                     kGsiPort, path, kGsiToken, data);
}

// cs2.exe and dota2.exe live in <install>\game\bin\win64.
fs::path ValveGameDir(const fs::path& gameExe) { return gameExe.parent_path().parent_path().parent_path(); }

// RocketLeague.exe lives in <install>\Binaries\Win64.
fs::path RocketLeagueDir(const fs::path& gameExe) { return gameExe.parent_path().parent_path().parent_path(); }

std::wstring ReadRegistryString(HKEY root, const wchar_t* key, const wchar_t* value) {
  wchar_t buffer[MAX_PATH * 2]{};
  DWORD size = sizeof(buffer);
  if (RegGetValueW(root, key, value, RRF_RT_REG_SZ, nullptr, buffer, &size) != ERROR_SUCCESS) return {};
  return buffer;
}

// Steam's own folder plus every library listed in steamapps\libraryfolders.vdf.
std::vector<fs::path> SteamLibraries() {
  std::vector<fs::path> libraries;
  const std::wstring steam = ReadRegistryString(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath");
  if (steam.empty()) return libraries;
  libraries.push_back(fs::path(steam).make_preferred());

  std::istringstream vdf(ReadFile(libraries[0] / L"steamapps" / L"libraryfolders.vdf"));
  std::string line;
  while (std::getline(vdf, line)) {
    // "path"		"D:\\SteamLibrary"
    const auto key = line.find("\"path\"");
    if (key == std::string::npos) continue;
    const auto open = line.find('"', key + 6);
    const auto close = open == std::string::npos ? open : line.rfind('"');
    if (open == std::string::npos || close <= open) continue;
    std::string path = line.substr(open + 1, close - open - 1);
    for (size_t i = path.find("\\\\"); i != std::string::npos; i = path.find("\\\\", i + 1)) path.erase(i, 1);
    fs::path library(winrt::to_hstring(path).c_str());
    library.make_preferred();
    std::error_code ec;
    if (std::ranges::none_of(libraries, [&](const fs::path& p) { return fs::equivalent(p, library, ec); })) {
      libraries.push_back(library);
    }
  }
  return libraries;
}

// Install folders of Epic Games launcher games, by their display name.
std::vector<fs::path> EpicInstalls(std::string_view displayName) {
  std::vector<fs::path> installs;
  const fs::path manifests = L"C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\Manifests";
  std::error_code ec;
  for (const auto& entry : fs::directory_iterator(manifests, ec)) {
    if (entry.path().extension() != L".item") continue;
    const auto manifest = Json::Parse(ReadFile(entry.path()));
    if (manifest && (*manifest)["DisplayName"].Str() == displayName) {
      installs.emplace_back(winrt::to_hstring((*manifest)["InstallLocation"].Str()).c_str());
    }
  }
  return installs;
}

void Report(SetupResult result, std::string_view game, const fs::path& where) {
  if (result == SetupResult::TurnedOn) {
    log::Info("Turned on {}'s game events in {}. If {} is open, restart it once.", game, Narrow(where), game);
  }
}

}  // namespace

SetupResult InstallCs2Config(const fs::path& gameExe) {
  const fs::path cfg = ValveGameDir(gameExe) / L"csgo" / L"cfg";
  std::error_code ec;
  if (!fs::is_directory(cfg, ec)) return SetupResult::NotInstalled;
  const auto result = WriteIfChanged(cfg / L"gamestate_integration_clipengine.cfg",
                                     GsiConfig("cs2", R"(    "provider"            "1"
    "map"                 "1"
    "round"               "1"
    "player_id"           "1"
    "player_state"        "1"
    "player_match_stats"  "1"
    "player_weapons"      "1"
)"));
  Report(result, "Counter-Strike 2", cfg);
  return result;
}

SetupResult InstallDota2Config(const fs::path& gameExe) {
  const fs::path cfg = ValveGameDir(gameExe) / L"dota" / L"cfg";
  std::error_code ec;
  if (!fs::is_directory(cfg, ec)) return SetupResult::NotInstalled;
  const auto result = WriteIfChanged(cfg / L"gamestate_integration" / L"gamestate_integration_clipengine.cfg",
                                     GsiConfig("dota2", R"(    "provider"  "1"
    "map"       "1"
    "player"    "1"
    "hero"      "1"
)"));
  Report(result, "Dota 2", cfg);
  return result;
}

SetupResult EnableRocketLeagueStats(const fs::path& gameExe, unsigned short& port) {
  port = kRocketLeagueDefaultPort;
  const fs::path ini = RocketLeagueDir(gameExe) / L"TAGame" / L"Config" / L"DefaultStatsAPI.ini";
  std::error_code ec;
  if (!fs::exists(ini, ec)) return SetupResult::NotInstalled;

  std::istringstream in(ReadFile(ini));
  std::vector<std::string> lines;
  bool sawRate = false;
  bool changed = false;
  for (std::string line; std::getline(in, line);) {
    if (!line.empty() && line.back() == '\r') line.pop_back();
    const auto eq = line.find('=');
    const std::string key = eq == std::string::npos ? "" : line.substr(0, eq);
    const std::string value = eq == std::string::npos ? "" : line.substr(eq + 1);
    if (key == "Port") {
      if (const int p = std::atoi(value.c_str()); p > 0 && p < 65536) port = static_cast<unsigned short>(p);
    } else if (key == "PacketSendRate") {
      sawRate = true;
      // Leave a rate the player picked; only turn it on when it's off.
      if (std::atof(value.c_str()) <= 0) {
        line = "PacketSendRate=10";
        changed = true;
      }
    }
    lines.push_back(line);
  }
  if (!sawRate) {
    lines.push_back("PacketSendRate=10");
    changed = true;
  }
  if (!changed) return SetupResult::AlreadyOn;

  std::string text;
  for (const auto& line : lines) text += line + "\r\n";
  const auto result = WriteIfChanged(ini, text);
  Report(result, "Rocket League", ini.parent_path());
  return result;
}

void PrepareGameIntegrations() {
  unsigned short port = 0;
  for (const auto& library : SteamLibraries()) {
    const fs::path common = library / L"steamapps" / L"common";
    InstallCs2Config(common / L"Counter-Strike Global Offensive" / L"game" / L"bin" / L"win64" / L"cs2.exe");
    InstallDota2Config(common / L"dota 2 beta" / L"game" / L"bin" / L"win64" / L"dota2.exe");
    EnableRocketLeagueStats(common / L"rocketleague" / L"Binaries" / L"Win64" / L"RocketLeague.exe", port);
  }
  for (const auto& install : EpicInstalls("Rocket League")) {
    EnableRocketLeagueStats(install / L"Binaries" / L"Win64" / L"RocketLeague.exe", port);
  }
}

fs::path ProcessImagePath(DWORD processId) {
  const HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
  if (!process) return {};
  std::wstring path(32768, L'\0');
  DWORD size = static_cast<DWORD>(path.size());
  const BOOL ok = QueryFullProcessImageNameW(process, 0, path.data(), &size);
  CloseHandle(process);
  if (!ok) return {};
  path.resize(size);
  return path;
}

}  // namespace clip
