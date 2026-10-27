#include "detect/TextMatcher.h"

#include <algorithm>
#include <cctype>

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

Phrase MakePhrase(std::string_view text) { return Phrase{Tokenize(text)}; }

bool WordsMatch(std::string_view token, std::string_view word) {
  if (token.empty() || word.empty()) return false;

  const bool wordIsLetters = std::ranges::all_of(word, [](char c) { return std::isalpha(static_cast<unsigned char>(c)) != 0; });
  std::string candidate(token);
  if (wordIsLetters) std::ranges::transform(candidate, candidate.begin(), AsLetter);
  if (candidate == word) return true;

  // Short words must match exactly; longer ones may lose or gain a letter.
  const int tolerance = word.size() >= 9 ? 2 : word.size() >= 5 ? 1 : 0;
  if (tolerance == 0) return false;
  if (EditDistance(candidate, word, tolerance) <= tolerance) return true;

  // OCR often runs a keyword into the next word ("ELIMINATEDPLAYER").
  if (word.size() >= 6 && candidate.size() > word.size()) {
    return EditDistance(std::string_view(candidate).substr(0, word.size()), word, tolerance) <= tolerance;
  }
  return false;
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

}  // namespace clip
