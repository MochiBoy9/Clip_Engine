#include "core/Config.h"

#include "core/Log.h"

#include <shlobj.h>

#include <algorithm>
#include <cctype>
#include <charconv>
#include <fstream>
#include <vector>

namespace clip {
namespace {

constexpr std::string_view kDefaultFile = R"(# Clip Engine settings. Delete this file to restore the defaults.

# Folder for saved clips. Empty means Videos\Clip_Engine.
output_dir =

# Seconds of recent gameplay kept in memory, and the length of a clip saved with the hotkey.
buffer_seconds = 60
clip_seconds = 30

# 0 means match the monitor's refresh rate.
fps = 0

# Video bitrate in megabits per second. 0 picks one from resolution and frame rate.
bitrate_mbps = 0

# auto, h264 or hevc. auto uses H.264 and switches to HEVC when H.264 can't handle the resolution.
codec = auto

game_audio = true
mic_audio = true

# Examples: F8, Alt+F10, Ctrl+Shift+S
save_hotkey = F8

# Keep recording the main monitor even when no supported game is running.
record_without_game = false

# Read the screen for kills, deaths and other moments (rules are in detection.ini).
detection = true

# Save a clip around each detected moment, and which moments count. Moments close together
# share one clip.
auto_clip = true
auto_clip_events = kill, assist, death, revive, match_start, match_end, highlight
clip_before_seconds = 10
clip_after_seconds = 5

# Log everything read off the screen, to see what words a game uses when tuning detection.ini.
detection_debug = false
)";

std::string Trim(std::string_view s) {
  const auto first = s.find_first_not_of(" \t\r\n");
  if (first == std::string_view::npos) return {};
  const auto last = s.find_last_not_of(" \t\r\n");
  return std::string(s.substr(first, last - first + 1));
}

std::string Lower(std::string s) {
  std::ranges::transform(s, s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  return s;
}

std::optional<int> ParseInt(const std::string& s) {
  int value = 0;
  const auto [ptr, ec] = std::from_chars(s.data(), s.data() + s.size(), value);
  if (ec != std::errc() || ptr != s.data() + s.size()) return std::nullopt;
  return value;
}

std::optional<bool> ParseBool(const std::string& s) {
  const auto v = Lower(s);
  if (v == "true" || v == "yes" || v == "1" || v == "on") return true;
  if (v == "false" || v == "no" || v == "0" || v == "off") return false;
  return std::nullopt;
}

std::filesystem::path DefaultOutputDir() {
  PWSTR videos = nullptr;
  std::filesystem::path dir;
  if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Videos, 0, nullptr, &videos))) dir = videos;
  CoTaskMemFree(videos);
  if (dir.empty()) dir = std::filesystem::current_path();
  return dir / L"Clip_Engine";
}

}  // namespace

std::optional<Hotkey> ParseHotkey(std::string_view text) {
  Hotkey hotkey{};
  bool haveKey = false;
  std::string rest(text);
  while (!rest.empty()) {
    const auto plus = rest.find('+');
    const std::string token = Lower(Trim(rest.substr(0, plus)));
    rest = plus == std::string::npos ? std::string() : rest.substr(plus + 1);
    if (token.empty()) return std::nullopt;

    if (token == "ctrl" || token == "control") {
      hotkey.modifiers |= MOD_CONTROL;
    } else if (token == "alt") {
      hotkey.modifiers |= MOD_ALT;
    } else if (token == "shift") {
      hotkey.modifiers |= MOD_SHIFT;
    } else if (token == "win") {
      hotkey.modifiers |= MOD_WIN;
    } else if (haveKey) {
      return std::nullopt;
    } else {
      haveKey = true;
      if (token.size() == 1 && std::isalnum(static_cast<unsigned char>(token[0]))) {
        hotkey.key = static_cast<UINT>(std::toupper(static_cast<unsigned char>(token[0])));
      } else if (token[0] == 'f' && token.size() <= 3) {
        const auto n = ParseInt(token.substr(1));
        if (!n || *n < 1 || *n > 24) return std::nullopt;
        hotkey.key = VK_F1 + static_cast<UINT>(*n - 1);
      } else if (token == "printscreen" || token == "print") {
        hotkey.key = VK_SNAPSHOT;
      } else if (token == "insert") {
        hotkey.key = VK_INSERT;
      } else if (token == "home") {
        hotkey.key = VK_HOME;
      } else if (token == "end") {
        hotkey.key = VK_END;
      } else if (token == "pageup") {
        hotkey.key = VK_PRIOR;
      } else if (token == "pagedown") {
        hotkey.key = VK_NEXT;
      } else if (token == "pause") {
        hotkey.key = VK_PAUSE;
      } else {
        return std::nullopt;
      }
    }
  }
  if (!haveKey) return std::nullopt;
  return hotkey;
}

Config Config::Load(const std::filesystem::path& file) {
  Config config;

  if (!std::filesystem::exists(file)) {
    std::error_code ec;
    std::filesystem::create_directories(file.parent_path(), ec);
    std::ofstream(file) << kDefaultFile;
  }

  std::ifstream in(file);
  std::string line;
  int lineNumber = 0;
  while (std::getline(in, line)) {
    ++lineNumber;
    if (const auto hash = line.find('#'); hash != std::string::npos) line.resize(hash);
    const auto eq = line.find('=');
    if (eq == std::string::npos) {
      if (!Trim(line).empty()) log::Warn("Settings line {} has no '=', ignoring it", lineNumber);
      continue;
    }
    const std::string key = Lower(Trim(std::string_view(line).substr(0, eq)));
    const std::string value = Trim(std::string_view(line).substr(eq + 1));

    auto setInt = [&](int& target) {
      if (const auto v = ParseInt(value)) target = *v;
      else log::Warn("Setting {} needs a whole number, got '{}'", key, value);
    };
    auto setBool = [&](bool& target) {
      if (const auto v = ParseBool(value)) target = *v;
      else log::Warn("Setting {} needs true or false, got '{}'", key, value);
    };

    if (key == "output_dir") {
      if (!value.empty()) config.outputDir = winrt::to_hstring(value).c_str();
    } else if (key == "buffer_seconds") {
      setInt(config.bufferSeconds);
    } else if (key == "clip_seconds") {
      setInt(config.clipSeconds);
    } else if (key == "fps") {
      setInt(config.fps);
    } else if (key == "bitrate_mbps") {
      setInt(config.bitrateMbps);
    } else if (key == "codec") {
      const auto v = Lower(value);
      if (v == "auto" || v == "h264" || v == "hevc") config.codec = v;
      else log::Warn("Setting codec must be auto, h264 or hevc, got '{}'", value);
    } else if (key == "game_audio") {
      setBool(config.gameAudio);
    } else if (key == "mic_audio") {
      setBool(config.micAudio);
    } else if (key == "save_hotkey") {
      if (const auto hk = ParseHotkey(value)) {
        config.saveHotkey = *hk;
        config.saveHotkeyText = value;
      } else {
        log::Warn("Setting save_hotkey '{}' isn't a key this app understands, keeping {}", value,
                  config.saveHotkeyText);
      }
    } else if (key == "record_without_game") {
      setBool(config.recordWithoutGame);
    } else if (key == "detection") {
      setBool(config.detection);
    } else if (key == "detection_debug") {
      setBool(config.detectionDebug);
    } else if (key == "auto_clip") {
      setBool(config.autoClip);
    } else if (key == "auto_clip_events") {
      config.autoClipEvents.clear();
      std::string rest = value;
      while (!rest.empty()) {
        const auto comma = rest.find(',');
        const std::string name = Lower(Trim(rest.substr(0, comma)));
        rest = comma == std::string::npos ? std::string() : rest.substr(comma + 1);
        if (name.empty()) continue;
        if (const auto type = ParseEventType(name)) config.autoClipEvents.insert(*type);
        else log::Warn("auto_clip_events: '{}' isn't an event this app knows", name);
      }
    } else if (key == "clip_before_seconds") {
      setInt(config.clipBeforeSeconds);
    } else if (key == "clip_after_seconds") {
      setInt(config.clipAfterSeconds);
    } else {
      log::Warn("Unknown setting '{}' on line {}", key, lineNumber);
    }
  }

  if (config.outputDir.empty()) config.outputDir = DefaultOutputDir();
  config.bufferSeconds = std::clamp(config.bufferSeconds, 10, 600);
  config.clipSeconds = std::clamp(config.clipSeconds, 5, config.bufferSeconds);
  config.fps = std::clamp(config.fps, 0, 360);
  config.bitrateMbps = std::clamp(config.bitrateMbps, 0, 300);
  config.clipBeforeSeconds = std::clamp(config.clipBeforeSeconds, 0, config.bufferSeconds - 5);
  config.clipAfterSeconds = std::clamp(config.clipAfterSeconds, 0, 30);
  return config;
}

}  // namespace clip
