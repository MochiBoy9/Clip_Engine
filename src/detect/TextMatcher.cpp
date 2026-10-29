#include "detect/TextMatcher.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <iterator>

namespace clip {
namespace {

bool IsAlnum(char c) { return std::isalnum(static_cast<unsigned char>(c)) != 0; }

// Digits OCR tends to return in place of letters.
char AsLetter(char c) {
  switch (c) {
    case '0': return 'O';
    case '1': return 'I';
    case '2': return 'Z';
    case '4': return 'A';
    case '5': return 'S';
    case '6': return 'G';
    case '7': return 'T';
    case '8': return 'B';
    default: return c;
  }
}

// Levenshtein distance, giving up once it exceeds limit.
int EditDistance(std::string_view a, std::string_view b, int limit) {
  if (static_cast<int>(a.size() > b.size() ? a.size() - b.size() : b.size() - a.size()) > limit) return limit + 1;
  std::vector<int> previous(b.size() + 1), current(b.size() + 1);
  for (size_t j = 0; j <= b.size(); ++j) previous[j] = static_cast<int>(j);
  for (size_t i = 1; i <= a.size(); ++i) {
    current[0] = static_cast<int>(i);
    int rowBest = current[0];
    for (size_t j = 1; j <= b.size(); ++j) {
      const int substitute = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
      current[j] = std::min({previous[j] + 1, current[j - 1] + 1, substitute});
      rowBest = std::min(rowBest, current[j]);
    }
    if (rowBest > limit) return limit + 1;
    std::swap(previous, current);
  }
  return previous[b.size()];
}

}  // namespace

std::vector<std::string> Tokenize(std::string_view text) {
  std::vector<std::string> tokens;
  std::string current;
  for (char c : text) {
    if (IsAlnum(c)) {
      current += static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    } else if (!current.empty()) {
      tokens.push_back(std::move(current));
      current.clear();
    }
  }
  if (!current.empty()) tokens.push_back(std::move(current));
  return tokens;
}

Phrase MakePhrase(std::string_view text) {
  std::string lower(text);
  std::ranges::transform(lower, lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  Phrase phrase;
  size_t start = 0;
  while (true) {
    const size_t at = lower.find(kNumberPlaceholder, start);
    for (auto& token : Tokenize(text.substr(start, at == std::string::npos ? std::string_view::npos : at - start))) {
      phrase.words.push_back(std::move(token));
    }
    if (at == std::string::npos) break;
    phrase.words.emplace_back(kNumberWord);
    start = at + kNumberPlaceholder.size();
  }
  return phrase;
}

bool HasNumber(const Phrase& phrase) { return std::ranges::find(phrase.words, kNumberWord) != phrase.words.end(); }

bool WordsMatch(std::string_view token, std::string_view word) {
  if (token.empty() || word.empty()) return false;
  // A number, maybe with an O read for a 0 or an S for a 5, but never a distance like 15M.
  if (word == kNumberWord) {
    const auto digits = std::ranges::count_if(token, [](char c) { return std::isdigit(static_cast<unsigned char>(c)) != 0; });
    const bool lookalikes = std::ranges::all_of(token, [](char c) {
      return std::isdigit(static_cast<unsigned char>(c)) != 0 || c == 'O' || c == 'I' || c == 'S' || c == 'B';
    });
    return lookalikes && digits * 2 >= std::ssize(token);
  }

  const bool wordIsLetters = std::ranges::all_of(word, [](char c) { return std::isalpha(static_cast<unsigned char>(c)) != 0; });
  std::string candidate(token);
  if (wordIsLetters) std::ranges::transform(candidate, candidate.begin(), AsLetter);
  if (candidate == word) return true;

  // Short words must match exactly; longer ones may lose or gain a letter.
  const int tolerance = word.size() >= 9 ? 2 : word.size() >= 5 ? 1 : 0;
  if (tolerance == 0) return false;

  // A plural is another word, and closer to the word without its S than with it.
  if (word.back() != 'S' && candidate.size() > 1 && candidate.back() == 'S' &&
      EditDistance(std::string_view(candidate).substr(0, candidate.size() - 1), word, tolerance) <
          EditDistance(candidate, word, tolerance)) {
    return false;
  }
  if (EditDistance(candidate, word, tolerance) <= tolerance) return true;

  // OCR often runs a keyword into the next word ("ELIMINATEDPLAYER"). Only a letter off
  // there, or another form of the word would pass for it (ELIMINATION for ELIMINATED).
  if (word.size() >= 6 && candidate.size() > word.size()) {
    return EditDistance(std::string_view(candidate).substr(0, word.size()), word, 1) <= 1;
  }
  return false;
}

bool SameText(std::string_view rawA, std::string_view rawB) {
  if (rawA == rawB) return true;
  std::string a(rawA), b(rawB);
  std::ranges::transform(a, a.begin(), AsLetter);
  std::ranges::transform(b, b.begin(), AsLetter);
  if (a == b) return true;
  const size_t shorter = std::min(a.size(), b.size());
  const int tolerance = shorter >= 9 ? 2 : shorter >= 4 ? 1 : 0;
  return tolerance > 0 && EditDistance(a, b, tolerance) <= tolerance;
}

int CountPhrase(const std::vector<std::string>& tokens, const Phrase& phrase) {
  const size_t n = phrase.words.size();
  if (n == 0 || tokens.size() < n) return 0;
  int count = 0;
  for (size_t i = 0; i + n <= tokens.size();) {
    bool match = true;
    for (size_t k = 0; k < n && match; ++k) match = WordsMatch(tokens[i + k], phrase.words[k]);
    if (match) {
      ++count;
      i += n;
    } else {
      ++i;
    }
  }
  return count;
}

namespace {

bool MatchesAt(const std::vector<std::string>& row, const Phrase& phrase, size_t at) {
  if (at + phrase.words.size() > row.size()) return false;
  for (size_t k = 0; k < phrase.words.size(); ++k) {
    if (!WordsMatch(row[at + k], phrase.words[k])) return false;
  }
  return true;
}

}  // namespace

bool PhraseAtStart(const std::vector<std::string>& row, const Phrase& phrase) {
  if (phrase.words.empty()) return false;
  if (MatchesAt(row, phrase, 0)) return true;
  return !row.empty() && row[0].size() <= 2 && MatchesAt(row, phrase, 1);
}

bool PhraseAtEnd(const std::vector<std::string>& row, const Phrase& phrase) {
  const size_t n = phrase.words.size();
  if (n == 0 || row.size() < n) return false;
  if (MatchesAt(row, phrase, row.size() - n)) return true;
  return row.size() > n && row.back().size() <= 2 && MatchesAt(row, phrase, row.size() - n - 1);
}

std::vector<std::vector<std::string>> GroupIntoRows(std::vector<WordBox> words) {
  std::ranges::sort(words, [](const WordBox& a, const WordBox& b) { return a.y + a.height / 2 < b.y + b.height / 2; });

  struct Row {
    double center = 0;
    double height = 0;
    std::vector<WordBox> words;
  };
  std::vector<Row> rows;
  for (auto& word : words) {
    const double center = word.y + word.height / 2;
    if (rows.empty() || std::abs(center - rows.back().center) > std::max(word.height, rows.back().height) / 2) {
      rows.push_back({center, word.height, {}});
    }
    Row& row = rows.back();
    row.words.push_back(std::move(word));
    // Keep the row's center as the average of its words, so a slightly taller word doesn't drag it.
    double sum = 0;
    for (const auto& w : row.words) sum += w.y + w.height / 2;
    row.center = sum / static_cast<double>(row.words.size());
    row.height = std::max(row.height, row.words.back().height);
  }

  std::vector<std::vector<std::string>> result;
  for (auto& row : rows) {
    std::ranges::sort(row.words, [](const WordBox& a, const WordBox& b) { return a.x < b.x; });
    std::vector<std::string> tokens;
    for (const auto& word : row.words) {
      for (auto& token : Tokenize(word.text)) tokens.push_back(std::move(token));
    }
    if (!tokens.empty()) result.push_back(std::move(tokens));
  }
  return result;
}

}  // namespace clip
