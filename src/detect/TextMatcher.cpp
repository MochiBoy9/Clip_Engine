#include "detect/TextMatcher.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdint>
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

namespace {

// The unaccented letters of U+00C0 to U+00FF, upper case; '-' where the character isn't a letter
// (the multiplication and division signs). AE and SS are spelled out below.
constexpr std::string_view kLatin1 = "AAAAAA?CEEEEIIIIDNOOOOO-OUUUUYTSAAAAAA?CEEEEIIIIDNOOOOO-OUUUUYTY";
// And of U+0100 to U+017F (Latin Extended-A), two by two upper and lower case.
constexpr std::string_view kLatinA =
    "AAAAAACCCCCCCCDDDDEEEEEEEEEEGGGGGGGGHHHHIIIIIIIIIIIIJJKKKLLLLLLLLLLNNNNNNNNNOOOOOOOORRRRRRSSSSSSSSTTTTTTUUUUUUUUUUUUWWYYYZZZZZZS";

// The letters an accented Latin letter stands for, or empty when the character isn't one.
std::string_view Unaccented(uint32_t c) {
  if (c == 0xC6 || c == 0xE6) return "AE";
  if (c == 0xDF) return "SS";
  if (c >= 0xC0 && c <= 0xFF) {
    const auto& letter = kLatin1[c - 0xC0];
    return letter == '-' ? std::string_view() : std::string_view(&letter, 1);
  }
  if (c >= 0x100 && c <= 0x17F) return std::string_view(&kLatinA[c - 0x100], 1);
  return {};
}

}  // namespace

std::vector<std::string> Tokenize(std::string_view text) {
  std::vector<std::string> tokens;
  std::string current;
  auto split = [&] {
    if (!current.empty()) tokens.push_back(std::move(current));
    current.clear();
  };
  for (size_t i = 0; i < text.size(); ++i) {
    const auto c = static_cast<unsigned char>(text[i]);
    if (c < 0x80) {
      if (IsAlnum(static_cast<char>(c))) current += static_cast<char>(std::toupper(c));
      else split();
      continue;
    }
    // A two-byte UTF-8 character: Latin-1 and Latin Extended-A letters are folded to their
    // letters; anything else (symbols, other scripts) separates words, as before.
    if ((c & 0xE0) == 0xC0 && i + 1 < text.size() && (static_cast<unsigned char>(text[i + 1]) & 0xC0) == 0x80) {
      const uint32_t code = ((c & 0x1Fu) << 6) | (static_cast<unsigned char>(text[i + 1]) & 0x3Fu);
      ++i;
      if (const auto letters = Unaccented(code); !letters.empty()) {
        current += letters;
        continue;
      }
    }
    split();
  }
  split();
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

namespace {

// Endings that make another word of a keyword: DEFEATED, ASSISTED, REVIVED, ELIMINATING,
// and plurals, which are scoreboard headings (ELIMINATIONS).
bool IsWordEnding(std::string_view word, std::string_view rest) {
  if (rest == "ED" || rest == "ING" || rest == "ER" || rest == "ERS" || rest == "S" || rest == "ES") return true;
  return rest == "D" && !word.empty() && word.back() == 'E';
}

// The token with digits read as the letters they look like, when the word is all letters.
std::string AsWordLetters(std::string_view token, std::string_view word) {
  const bool wordIsLetters = std::ranges::all_of(word, [](char c) { return std::isalpha(static_cast<unsigned char>(c)) != 0; });
  std::string candidate(token);
  if (wordIsLetters) std::ranges::transform(candidate, candidate.begin(), AsLetter);
  return candidate;
}

int Tolerance(std::string_view word) { return word.size() >= 9 ? 2 : word.size() >= 5 ? 1 : 0; }

// Letters text recognition mistakes for one another on small HUD text.
bool LookAlike(char a, char b) {
  static constexpr std::string_view kPairs[] = {"IL", "IJ", "OD", "OQ", "DQ", "UV", "CG", "MN", "EF"};
  return std::ranges::any_of(kPairs, [&](std::string_view p) { return (p[0] == a && p[1] == b) || (p[0] == b && p[1] == a); });
}

}  // namespace

size_t RunOnLength(std::string_view token, std::string_view word) {
  // OCR often runs a keyword into the next word ("ELIMINATEDPLAYER"). Only a letter off
  // there, or another form of the word would pass for it (ELIMINATION for ELIMINATED).
  // Only with at least two more characters: one more is a misread the word itself allows for.
  if (word.size() < 6 || token.size() < word.size() + 2 || word == kNumberWord) return 0;
  const std::string candidate = AsWordLetters(token, word);
  if (EditDistance(std::string_view(candidate).substr(0, word.size()), word, 1) > 1) return 0;
  if (IsWordEnding(word, std::string_view(candidate).substr(word.size()))) return 0;
  return word.size();
}

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

  const std::string candidate = AsWordLetters(token, word);
  if (candidate == word) return true;

  // Short words must match exactly; longer ones may lose or gain a letter.
  const int tolerance = Tolerance(word);
  if (tolerance == 0) return false;

  // A plural is another word, and closer to the word without its S than with it.
  if (word.back() != 'S' && candidate.size() > 1 && candidate.back() == 'S' &&
      EditDistance(std::string_view(candidate).substr(0, candidate.size() - 1), word, tolerance) <
          EditDistance(candidate, word, tolerance)) {
    return false;
  }
  // So is the word with an ending: REVIVED isn't REVIVE, DEFEATED isn't DEFEAT.
  if (candidate.size() > word.size() && candidate.starts_with(word) &&
      IsWordEnding(word, std::string_view(candidate).substr(word.size()))) {
    return false;
  }
  if (EditDistance(candidate, word, tolerance) <= tolerance) return true;
  return RunOnLength(token, word) > 0;
}

bool NameMatches(std::string_view token, std::string_view word) {
  if (token.empty() || word.empty()) return false;
  const std::string candidate = AsWordLetters(token, word);
  if (candidate == word) return true;
  // Another player whose name begins with this one: ENTERROR isn't ENTERRO.
  if (candidate.size() > word.size() && candidate.starts_with(word)) return false;
  if (candidate.size() >= word.size() + 2) return false;
  const int tolerance = Tolerance(word);
  if (tolerance > 0) return EditDistance(candidate, word, tolerance) <= tolerance;
  // Four letters: one look-alike letter misread, and nothing else.
  if (word.size() != 4 || candidate.size() != 4) return false;
  int misread = 0;
  for (size_t i = 0; i < 4; ++i) {
    if (candidate[i] == word[i]) continue;
    if (!LookAlike(candidate[i], word[i])) return false;
    ++misread;
  }
  return misread <= 1;
}

bool PhraseWordMatches(const Phrase& phrase, size_t k, std::string_view token) {
  return phrase.name ? NameMatches(token, phrase.words[k]) : WordsMatch(token, phrase.words[k]);
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
    for (size_t k = 0; k < n && match; ++k) match = PhraseWordMatches(phrase, k, tokens[i + k]);
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
    if (!PhraseWordMatches(phrase, k, row[at + k])) return false;
  }
  return true;
}

}  // namespace

size_t PhraseStartEnd(const std::vector<std::string>& row, const Phrase& phrase, bool skipTag) {
  const size_t n = phrase.words.size();
  if (n == 0) return 0;
  if (MatchesAt(row, phrase, 0)) return n;
  if (!row.empty() && row[0].size() <= 2 && MatchesAt(row, phrase, 1)) return n + 1;
  if (!skipTag || row.empty() || row[0].size() > 6) return 0;
  // The tag, maybe with its frame read as a stray letter or two after it. Something must
  // follow the name, as the player killed does in a killfeed: a row that ends with it after
  // a short word is someone else's kill, of the player, by someone without a tag.
  for (size_t at = 1; at <= 3 && at < row.size(); ++at) {
    if (at > 1 && row[at - 1].size() > 2) break;
    if (at + n < row.size() && MatchesAt(row, phrase, at)) return at + n;
  }
  return 0;
}

bool PhraseAtStart(const std::vector<std::string>& row, const Phrase& phrase, bool skipTag) {
  return PhraseStartEnd(row, phrase, skipTag) > 0;
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
