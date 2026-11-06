#include "detect/Scoreboard.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <format>
#include <limits>

namespace clip {
namespace {

// A word's tokens, with where the word sits.
struct Token {
  std::string text;
  double left = 0, right = 0;
};

struct Line {
  double center = 0;
  double height = 0;
  std::vector<const WordBox*> words;  // left to right
  std::vector<Token> tokens;
};

// Words side by side as lines, top to bottom, like GroupIntoRows but keeping where each word is.
std::vector<Line> Lines(const std::vector<WordBox>& words) {
  std::vector<const WordBox*> sorted;
  for (const auto& w : words) sorted.push_back(&w);
  std::ranges::sort(sorted, [](const WordBox* a, const WordBox* b) { return a->y + a->height / 2 < b->y + b->height / 2; });

  std::vector<Line> lines;
  for (const WordBox* word : sorted) {
    const double center = word->y + word->height / 2;
    if (lines.empty() || std::abs(center - lines.back().center) > std::max(word->height, lines.back().height) / 2) {
      lines.push_back({center, word->height, {}, {}});
    }
    Line& line = lines.back();
    line.words.push_back(word);
    double sum = 0;
    for (const WordBox* w : line.words) sum += w->y + w->height / 2;
    line.center = sum / static_cast<double>(line.words.size());
    line.height = std::max(line.height, word->height);
  }
  for (auto& line : lines) {
    std::ranges::sort(line.words, [](const WordBox* a, const WordBox* b) { return a->x < b->x; });
    for (const WordBox* w : line.words) {
      for (auto& token : Tokenize(w->text)) line.tokens.push_back({std::move(token), w->x, w->x + w->width});
    }
  }
  return lines;
}

bool HasDigit(std::string_view s) {
  return std::ranges::any_of(s, [](unsigned char c) { return std::isdigit(c) != 0; });
}

// Where the words appear in a row of tokens, as the span they cover; headers never have digits,
// so a row of numbers can't pass for one ("4" reads like "A").
std::vector<std::pair<double, double>> Find(const std::vector<Token>& tokens, const Phrase& phrase, bool header) {
  std::vector<std::pair<double, double>> spans;
  const size_t n = phrase.words.size();
  if (n == 0) return spans;
  for (size_t i = 0; i + n <= tokens.size(); ++i) {
    bool match = true;
    for (size_t k = 0; k < n && match; ++k) {
      const std::string& token = tokens[i + k].text;
      match = (!header || !HasDigit(token)) && PhraseWordMatches(phrase, k, token);
    }
    if (match) spans.emplace_back(tokens[i].left, tokens[i + n - 1].right);
  }
  return spans;
}

// A number as a scoreboard shows it: "12", "1,250". Text recognition reads 0 as O now and then.
std::optional<int> Number(std::string_view text) {
  std::string digits;
  for (char c : text) {
    if (c == ',' || c == '.' || c == ' ') continue;
    if (c == 'O' || c == 'o') c = '0';
    if (!std::isdigit(static_cast<unsigned char>(c))) return std::nullopt;
    digits += c;
  }
  if (digits.empty() || digits.size() > 5) return std::nullopt;
  return std::stoi(digits);
}

}  // namespace

std::optional<int> StatValue(const Stats& stats, std::string_view name) {
  for (const auto& [stat, value] : stats) {
    if (stat == name) return value;
  }
  return std::nullopt;
}

std::string DescribeStats(const Stats& stats) {
  std::string text;
  for (const auto& [stat, value] : stats) text += std::format("{}{} {}", text.empty() ? "" : ", ", stat, value);
  return text;
}

std::optional<Stats> ReadScoreboard(const ScoreboardRule& rule, const std::vector<WordBox>& words,
                                    const std::vector<std::string>& playerNames) {
  if (rule.columns.empty() || playerNames.empty()) return std::nullopt;
  const auto lines = Lines(words);

  // The header row is the line with the most of the rule's column headers on it.
  struct Header {
    size_t column;
    double center, width;
  };
  std::vector<Header> headers;
  size_t headerLine = lines.size();
  for (size_t l = 0; l < lines.size(); ++l) {
    std::vector<Header> found;
    for (size_t c = 0; c < rule.columns.size(); ++c) {
      for (const auto& label : rule.columns[c].second) {
        const auto spans = Find(lines[l].tokens, MakePhrase(label), true);
        if (spans.empty()) continue;
        found.push_back({c, (spans[0].first + spans[0].second) / 2, spans[0].second - spans[0].first});
        break;
      }
    }
    if (found.size() > headers.size()) {
      headers = std::move(found);
      headerLine = l;
    }
  }
  // One header alone is too easy to find by chance ("A" in any sentence).
  if (headers.size() < std::min<size_t>(2, rule.columns.size())) return std::nullopt;

  // The player's row: the first line below the headers with one of their names on it.
  std::vector<Phrase> names;
  for (const auto& name : playerNames) {
    auto phrase = MakePhrase(name);
    phrase.name = true;
    if (!phrase.words.empty()) names.push_back(std::move(phrase));
  }
  const Line* row = nullptr;
  for (size_t l = headerLine + 1; l < lines.size() && !row; ++l) {
    for (const auto& name : names) {
      if (!Find(lines[l].tokens, name, false).empty()) {
        row = &lines[l];
        break;
      }
    }
  }
  if (!row) return std::nullopt;

  // Each header takes the number closest below it, nearest pairs first, each number once.
  struct Value {
    double center;
    int value;
    bool used = false;
  };
  std::vector<Value> values;
  for (const WordBox* w : row->words) {
    if (const auto n = Number(w->text)) values.push_back({w->x + w->width / 2, *n});
  }
  struct Pair {
    double distance;
    size_t header, value;
  };
  std::vector<Pair> pairs;
  for (size_t h = 0; h < headers.size(); ++h) {
    const double reach = 1.5 * std::max(headers[h].width, row->height);
    for (size_t v = 0; v < values.size(); ++v) {
      const double distance = std::abs(values[v].center - headers[h].center);
      if (distance <= reach) pairs.push_back({distance, h, v});
    }
  }
  std::ranges::sort(pairs, {}, &Pair::distance);
  std::vector<std::optional<int>> byColumn(rule.columns.size());
  std::vector<bool> headerDone(headers.size());
  for (const auto& p : pairs) {
    if (headerDone[p.header] || values[p.value].used) continue;
    headerDone[p.header] = true;
    values[p.value].used = true;
    byColumn[headers[p.header].column] = values[p.value].value;
  }

  Stats stats;
  for (size_t c = 0; c < rule.columns.size(); ++c) {
    if (byColumn[c]) stats.emplace_back(rule.columns[c].first, *byColumn[c]);
  }
  if (stats.empty()) return std::nullopt;
  return stats;
}

std::optional<Stats> ScoreboardTracker::Update(const std::optional<Stats>& read) {
  if (!read) {
    pending_.reset();
    return std::nullopt;
  }
  if (pending_ && *pending_ == *read && (!reported_ || *reported_ != *read)) {
    reported_ = read;
    return read;
  }
  pending_ = read;
  return std::nullopt;
}

}  // namespace clip
