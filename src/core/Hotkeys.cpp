#include "core/Hotkeys.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <fstream>

namespace clip {
namespace {

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

std::optional<int> ParseInt(std::string_view s) {
  int value = 0;
  const auto [ptr, ec] = std::from_chars(s.data(), s.data() + s.size(), value);
  if (ec != std::errc() || ptr != s.data() + s.size()) return std::nullopt;
  return value;
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
        const auto n = ParseInt(std::string_view(token).substr(1));
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

std::vector<ClipHotkey> ParseClipHotkeys(std::string_view value, std::vector<std::string>* bad) {
  std::vector<ClipHotkey> keys;
  std::string rest(value);
  while (!rest.empty()) {
    const auto comma = rest.find(',');
    const std::string entry = Trim(std::string_view(rest).substr(0, comma));
    rest = comma == std::string::npos ? std::string() : rest.substr(comma + 1);
    if (entry.empty()) continue;

    // The length is the last word; the key is everything before it ("Ctrl + F8 20").
    const auto space = entry.find_last_of(" \t");
    std::optional<Hotkey> hotkey;
    std::optional<int> seconds;
    if (space != std::string::npos) {
      std::string number = Trim(std::string_view(entry).substr(space + 1));
      if (!number.empty() && (number.back() == 's' || number.back() == 'S')) number.pop_back();
      seconds = ParseInt(number);
      hotkey = ParseHotkey(Trim(std::string_view(entry).substr(0, space)));
    }
    if (!hotkey || !seconds || *seconds <= 0) {
      if (bad) bad->push_back(entry);
      continue;
    }
    keys.push_back({*hotkey, Trim(std::string_view(entry).substr(0, space)), *seconds});
  }
  return keys;
}

std::string GameClipHotkeysKey(std::string_view gameId) { return "clip_hotkeys." + Lower(std::string(gameId)); }

std::optional<std::vector<ClipHotkey>> ReadGameClipHotkeys(const std::filesystem::path& file, std::string_view gameId) {
  const std::string wanted = GameClipHotkeysKey(gameId);
  std::ifstream in(file);
  std::string line;
  std::optional<std::vector<ClipHotkey>> keys;
  while (std::getline(in, line)) {
    if (const auto hash = line.find('#'); hash != std::string::npos) line.resize(hash);
    const auto eq = line.find('=');
    if (eq == std::string::npos || Lower(Trim(std::string_view(line).substr(0, eq))) != wanted) continue;
    auto parsed = ParseClipHotkeys(Trim(std::string_view(line).substr(eq + 1)));
    keys = parsed.empty() ? std::nullopt : std::optional(std::move(parsed));
  }
  return keys;
}

}  // namespace clip
