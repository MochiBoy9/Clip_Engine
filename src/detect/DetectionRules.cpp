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

bool MentionsPlayer(const std::string& text) { return Lower(text).find(kPlayerPlaceholder) != std::string::npos; }

// Phrases from "A | B | C". Those mentioning {player} go to withPlayer instead, as written.
std::vector<Phrase> ParsePhrases(const std::string& value, std::vector<std::string>& withPlayer) {
  std::vector<Phrase> phrases;
  withPlayer.clear();
  for (const auto& part : Split(value, '|')) {
    if (MentionsPlayer(part)) {
      withPlayer.push_back(part);
      continue;
    }
    auto phrase = MakePhrase(part);
    if (!phrase.words.empty()) phrases.push_back(std::move(phrase));
  }
  return phrases;
}

// Each phrase once for every player name.
void AddPlayerPhrases(const std::vector<std::string>& sources, const std::vector<std::string>& names,
                      std::vector<Phrase>& out) {
  for (const auto& source : sources) {
    const auto at = Lower(source).find(kPlayerPlaceholder);
    for (const auto& name : names) {
      std::string text = source;
      text.replace(at, kPlayerPlaceholder.size(), " " + name + " ");
      auto phrase = MakePhrase(text);
      if (!phrase.words.empty()) out.push_back(std::move(phrase));
    }
  }
}

}  // namespace

std::vector<Phrase> ParsePhrases(const std::string& value) {
  std::vector<std::string> withPlayer;
  return ParsePhrases(value, withPlayer);
}

std::vector<DetectionRule> DetectionRules::ForGame(const std::string& game, const std::vector<std::string>& playerNames,
                                                   std::vector<std::string>* needingName) const {
  std::vector<DetectionRule> result;
  for (const auto& rule : rules) {
    if (rule.game != game) continue;
    if (!rule.NeedsPlayer()) {
      result.push_back(rule);
      continue;
    }
    if (playerNames.empty()) {
      if (needingName) needingName->push_back(rule.name);
      continue;
    }
    DetectionRule filled = rule;
    AddPlayerPhrases(rule.playerWords, playerNames, filled.words);
    AddPlayerPhrases(rule.playerExclude, playerNames, filled.exclude);
    if (!filled.words.empty()) result.push_back(std::move(filled));
  }
  return result;
}

std::vector<ScoreboardRule> DetectionRules::ScoreboardsForGame(const std::string& game) const {
  std::vector<ScoreboardRule> result;
  for (const auto& board : scoreboards) {
    if (board.game == game) result.push_back(board);
  }
  return result;
}

DetectionRules ParseDetectionRules(std::istream& in) {
  DetectionRules result;
  DetectionRule* rule = nullptr;
  ScoreboardRule* board = nullptr;
  CustomGame* game = nullptr;
  bool inGlobal = false;
  bool skipping = false;
  std::string line;
  int lineNumber = 0;

  auto warn = [&](std::string message) { result.warnings.push_back(std::format("line {}: {}", lineNumber, message)); };
  auto finishRule = [&] {
    if (rule && rule->words.empty() && rule->playerWords.empty()) {
      warn(std::format("[{}] has no words, so it can never match; ignoring it", rule->name));
      result.rules.pop_back();
    }
    if (game && (game->name.empty() || game->executables.empty())) {
      warn(std::format("[game.{}] needs a name and an exe; ignoring it", game->id));
      result.games.pop_back();
    }
    if (board && board->columns.empty()) {
      warn(std::format("[{}] lists no columns (like kills = K | KILLS), so it can't read anything; ignoring it",
                       board->name));
      result.scoreboards.pop_back();
    }
    rule = nullptr;
    board = nullptr;
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
      if (parts.size() >= 2 && parts[1] == "scoreboard" && !parts[0].empty()) {
        board = &result.scoreboards.emplace_back();
        board->name = name;
        board->game = parts[0];
        continue;
      }
      const auto event = parts.size() >= 2 ? ParseEventType(parts[1]) : std::nullopt;
      if (parts.size() < 2 || parts[0].empty() || !event) {
        warn(std::format("[{}] should be [<game>.<event>], with event one of kill, assist, death, revive, "
                         "match_start, match_end, highlight or scoreboard",
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
    if (!rule && !board) {
      warn("setting outside of a section");
      continue;
    }

    const std::string& section = rule ? rule->name : board->name;
    if (key == "region") {
      const auto numbers = Split(value, ',');
      double v[4]{};
      bool ok = numbers.size() == 4;
      for (size_t i = 0; ok && i < 4; ++i) ok = ParseDouble(numbers[i], v[i]) && v[i] >= 0 && v[i] <= 1;
      if (ok && v[2] > 0 && v[3] > 0 && v[0] + v[2] <= 1.0001 && v[1] + v[3] <= 1.0001) {
        (rule ? rule->region : board->region) = {v[0], v[1], v[2], v[3]};
      } else {
        warn(std::format("[{}] region must be four fractions: left, top, width, height", section));
      }
    } else if (key == "threshold") {
      int t = 0;
      if (ParseInt(value, t) && t >= 0 && t <= 255) (rule ? rule->threshold : board->threshold) = t;
      else warn(std::format("[{}] threshold must be 0 to 255", section));
    } else if (board) {
      if (key == "sample_ms") {
        int ms = 0;
        if (ParseInt(value, ms)) board->sampleMs = std::clamp(ms, 250, 10000);
        else warn(std::format("[{}] sample_ms must be a whole number", board->name));
      } else if (IsId(key)) {
        // Any other key is a stat, and its value the column headers it shows under.
        std::vector<std::string> labels;
        for (auto& label : Split(value, '|')) {
          if (!MakePhrase(label).words.empty()) labels.push_back(std::move(label));
        }
        if (labels.empty()) warn(std::format("[{}] {} needs the column headers it shows under", board->name, key));
        else board->columns.emplace_back(key, std::move(labels));
      } else {
        warn(std::format("[{}] unknown setting '{}'", board->name, key));
      }
    } else if (key == "words") {
      rule->words = ParsePhrases(value, rule->playerWords);
    } else if (key == "exclude") {
      rule->exclude = ParsePhrases(value, rule->playerExclude);
    } else if (key == "position") {
      const std::string v = Lower(value);
      if (v == "anywhere") rule->position = LinePosition::Anywhere;
      else if (v == "start") rule->position = LinePosition::Start;
      else if (v == "end") rule->position = LinePosition::End;
      else warn(std::format("[{}] position must be anywhere, start or end", rule->name));
    } else if (key == "cooldown_ms") {
      int ms = 0;
      if (ParseInt(value, ms) && ms >= 0) rule->cooldownMs = ms;
      else warn(std::format("[{}] cooldown_ms must be a whole number", rule->name));
    } else {
      warn(std::format("[{}] unknown setting '{}'", rule->name, key));
    }
  }
  finishRule();
  return result;
}

int CountMatches(const DetectionRule& rule, const std::vector<std::vector<std::string>>& rows) {
  auto excludedIn = [&](const std::vector<std::string>& tokens) {
    return std::ranges::any_of(rule.exclude, [&](const Phrase& p) { return CountPhrase(tokens, p) > 0; });
  };

  if (rule.position == LinePosition::Anywhere) {
    // The whole region as one run of text: a phrase may wrap onto the next row.
    std::vector<std::string> tokens;
    for (const auto& row : rows) tokens.insert(tokens.end(), row.begin(), row.end());
    if (excludedIn(tokens)) return 0;
    int count = 0;
    for (const auto& phrase : rule.words) count += CountPhrase(tokens, phrase);
    return count;
  }

  // Row by row, as in a killfeed: each row that starts (or ends) with the words counts once,
  // and exclude only cancels the rows it's on.
  int count = 0;
  for (const auto& row : rows) {
    if (excludedIn(row)) continue;
    const bool match = std::ranges::any_of(rule.words, [&](const Phrase& p) {
      return rule.position == LinePosition::Start ? PhraseAtStart(row, p) : PhraseAtEnd(row, p);
    });
    if (match) ++count;
  }
  return count;
}

int RuleTracker::Update(const std::vector<std::vector<std::string>>& rows, int64_t nowMs) {
  const int count = CountMatches(*rule_, rows);

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
