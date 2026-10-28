#include "detect/DetectionRules.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <format>
#include <sstream>

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

std::vector<std::string> Split(std::string_view s, char separator) {
  std::vector<std::string> parts;
  size_t start = 0;
  while (start <= s.size()) {
    const auto end = s.find(separator, start);
    parts.push_back(Trim(s.substr(start, end == std::string_view::npos ? std::string_view::npos : end - start)));
    if (end == std::string_view::npos) break;
    start = end + 1;
  }
  return parts;
}

bool ParseDouble(const std::string& s, double& out) {
  const auto [ptr, ec] = std::from_chars(s.data(), s.data() + s.size(), out);
  return ec == std::errc() && ptr == s.data() + s.size();
}

bool ParseInt(const std::string& s, int& out) {
  const auto [ptr, ec] = std::from_chars(s.data(), s.data() + s.size(), out);
  return ec == std::errc() && ptr == s.data() + s.size();
}

bool IsId(std::string_view id) {
  return !id.empty() && std::ranges::all_of(id, [](unsigned char c) { return std::isalnum(c) || c == '_' || c == '-'; });
}

}  // namespace

std::vector<Phrase> ParsePhrases(const std::string& value) {
  std::vector<Phrase> phrases;
  for (const auto& part : Split(value, '|')) {
    auto phrase = MakePhrase(part);
    if (!phrase.words.empty()) phrases.push_back(std::move(phrase));
  }
  return phrases;
}

int CountMatches(const DetectionRule& rule, const std::vector<std::string>& tokens) {
  if (std::ranges::any_of(rule.exclude, [&](const Phrase& p) { return CountPhrase(tokens, p) > 0; })) return 0;
  int count = 0;
  for (const auto& phrase : rule.words) count += CountPhrase(tokens, phrase);
  return count;
}

std::vector<DetectionRule> DetectionRules::ForGame(const std::string& game) const {
  std::vector<DetectionRule> result;
  std::ranges::copy_if(rules, std::back_inserter(result), [&](const DetectionRule& r) { return r.game == game; });
  return result;
}

DetectionRules ParseDetectionRules(std::istream& in) {
  DetectionRules result;
  DetectionRule* rule = nullptr;
  CustomGame* game = nullptr;
  bool inGlobal = false;
  bool skipping = false;
  std::string line;
  int lineNumber = 0;

  auto warn = [&](std::string message) { result.warnings.push_back(std::format("line {}: {}", lineNumber, message)); };
  auto finishRule = [&] {
    if (rule && rule->words.empty()) {
      warn(std::format("[{}] has no words, so it can never match; ignoring it", rule->name));
      result.rules.pop_back();
    }
    if (game && (game->name.empty() || game->executables.empty())) {
      warn(std::format("[game.{}] needs a name and an exe; ignoring it", game->id));
      result.games.pop_back();
    }
    rule = nullptr;
    game = nullptr;
  };

  while (std::getline(in, line)) {
    ++lineNumber;
    if (const auto hash = line.find('#'); hash != std::string::npos) line.resize(hash);
    const std::string text = Trim(line);
    if (text.empty()) continue;

    if (text.front() == '[') {
      finishRule();
      inGlobal = skipping = false;
      if (text.back() != ']') {
        warn("section header is missing its ']'");
        skipping = true;
        continue;
      }
      const std::string name = Lower(Trim(std::string_view(text).substr(1, text.size() - 2)));
      if (name == "detection") {
        inGlobal = true;
        continue;
      }
      const auto parts = Split(name, '.');
      if (parts.size() == 2 && parts[0] == "game") {
        if (!IsId(parts[1])) {
          warn(std::format("[{}]: a game id may only use letters, digits, _ and -", name));
          skipping = true;
          continue;
        }
        std::erase_if(result.games, [&](const CustomGame& g) { return g.id == parts[1]; });
        game = &result.games.emplace_back();
        game->id = parts[1];
        continue;
      }
      const auto event = parts.size() >= 2 ? ParseEventType(parts[1]) : std::nullopt;
      if (parts.size() < 2 || parts[0].empty() || !event) {
        warn(std::format("[{}] should be [<game>.<event>], with event one of kill, assist, death, revive, "
                         "match_start, match_end or highlight",
                         name));
        skipping = true;
        continue;
      }
      DetectionRule& added = result.rules.emplace_back();
      added.name = name;
      added.game = parts[0];
      added.event = *event;
      rule = &added;
      continue;
    }

    if (skipping) continue;
    const auto eq = text.find('=');
    if (eq == std::string::npos) {
      warn("expected key = value");
      continue;
    }
    const std::string key = Lower(Trim(std::string_view(text).substr(0, eq)));
    const std::string value = Trim(std::string_view(text).substr(eq + 1));

    if (inGlobal) {
      int ms = 0;
      if (key == "sample_ms" && ParseInt(value, ms)) result.sampleMs = std::clamp(ms, 100, 2000);
      else warn(std::format("unknown setting '{}' in [detection]", key));
      continue;
    }
    if (game) {
      if (key == "name") {
        game->name = value;
      } else if (key == "exe") {
        for (auto& exe : Split(value, '|')) {
          if (!exe.empty()) game->executables.push_back(std::move(exe));
        }
      } else {
        warn(std::format("[game.{}] unknown setting '{}'", game->id, key));
      }
      continue;
    }
    if (!rule) {
      warn("setting outside of a section");
      continue;
    }

    if (key == "region") {
      const auto numbers = Split(value, ',');
      double v[4]{};
      bool ok = numbers.size() == 4;
      for (size_t i = 0; ok && i < 4; ++i) ok = ParseDouble(numbers[i], v[i]) && v[i] >= 0 && v[i] <= 1;
      if (ok && v[2] > 0 && v[3] > 0 && v[0] + v[2] <= 1.0001 && v[1] + v[3] <= 1.0001) {
        rule->region = {v[0], v[1], v[2], v[3]};
      } else {
        warn(std::format("[{}] region must be four fractions: left, top, width, height", rule->name));
      }
    } else if (key == "words") {
      rule->words = ParsePhrases(value);
    } else if (key == "exclude") {
      rule->exclude = ParsePhrases(value);
    } else if (key == "cooldown_ms") {
      int ms = 0;
      if (ParseInt(value, ms) && ms >= 0) rule->cooldownMs = ms;
      else warn(std::format("[{}] cooldown_ms must be a whole number", rule->name));
    } else if (key == "threshold") {
      int t = 0;
      if (ParseInt(value, t) && t >= 0 && t <= 255) rule->threshold = t;
      else warn(std::format("[{}] threshold must be 0 to 255", rule->name));
    } else {
      warn(std::format("[{}] unknown setting '{}'", rule->name, key));
    }
  }
  finishRule();
  return result;
}

int RuleTracker::Update(const std::vector<std::string>& tokens, int64_t nowMs) {
  const int count = CountMatches(*rule_, tokens);

  if (count > shown_) {
    const int fresh = count - shown_;
    shown_ = count;
    lowerReads_ = 0;
    if (nowMs - lastFiredMs_ < rule_->cooldownMs) return 0;
    lastFiredMs_ = nowMs;
    return std::min(fresh, 5);
  }
  // OCR misses text now and then. Only believe a message is gone after two reads without it,
  // so a flicker doesn't count the same kill twice.
  if (count < shown_) {
    if (++lowerReads_ >= 2) {
      shown_ = count;
      lowerReads_ = 0;
    }
  } else {
    lowerReads_ = 0;
  }
  return 0;
}

}  // namespace clip
