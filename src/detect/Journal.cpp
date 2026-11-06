#include "detect/Journal.h"

#include <algorithm>
#include <cctype>
#include <span>

namespace clip {
namespace {

std::string Trim(std::string_view s) {
  const auto first = s.find_first_not_of(" \t\r\n");
  if (first == std::string_view::npos) return {};
  const auto last = s.find_last_not_of(" \t\r\n");
  return std::string(s.substr(first, last - first + 1));
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

bool IsDigits(std::string_view token) {
  return !token.empty() && std::ranges::all_of(token, [](unsigned char c) { return std::isdigit(c) != 0; });
}

bool HasLetters(std::string_view token, size_t least) {
  return static_cast<size_t>(std::ranges::count_if(token, [](unsigned char c) { return std::isalpha(c) != 0; })) >= least;
}

// A number as read, with the letters OCR puts in place of digits read back as digits.
int ReadNumber(std::string_view token) {
  int value = 0;
  for (char c : token) {
    int digit = -1;
    if (std::isdigit(static_cast<unsigned char>(c))) digit = c - '0';
    else if (c == 'O') digit = 0;
    else if (c == 'I') digit = 1;
    else if (c == 'S') digit = 5;
    else if (c == 'B') digit = 8;
    if (digit < 0) continue;
    if (value > 100'000'000) break;
    value = value * 10 + digit;
  }
  return value;
}

// "METAL PARTS" as "Metal Parts".
std::string TitleCase(std::span<const std::string> tokens) {
  std::string text;
  for (const auto& token : tokens) {
    if (!text.empty()) text += ' ';
    for (size_t i = 0; i < token.size(); ++i) {
      const char c = token[i];
      text += i == 0 ? c : static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
  }
  return text;
}

std::string Join(const std::vector<std::string>& tokens) {
  std::string text;
  for (const auto& token : tokens) text += (text.empty() ? "" : " ") + token;
  return text;
}

// Whether a word at a value's edge is something other than its name: a bit of an icon, a
// number ("100", "100XP", "X3") or the XP after one.
bool EdgeJunk(const std::string& token) {
  if (token.size() < 2 || token == "XP") return true;
  return std::ranges::any_of(token, [](unsigned char c) { return std::isdigit(c) != 0; });
}

// A value's words without the bits of icons and numbers OCR leaves at its edges.
std::span<const std::string> CleanValue(std::span<const std::string> tokens) {
  while (!tokens.empty() && EdgeJunk(tokens.front())) tokens = tokens.subspan(1);
  while (!tokens.empty() && EdgeJunk(tokens.back())) tokens = tokens.first(tokens.size() - 1);
  // A value is a name, a few words at most; more is a whole line misread as one.
  if (tokens.size() > 6) tokens = tokens.first(6);
  return tokens;
}

bool ShowsPhrase(const std::vector<std::string>& row, const Phrase& phrase) {
  return !phrase.words.empty() && CountPhrase(row, phrase) > 0;
}

struct PatternMatch {
  size_t valueBegin = 0, valueEnd = 0;
  bool hasValue = false;
  int amount = 0;
  int xp = 0;
};

// Whether pattern words from k on match the row from j on, filling in the match. {value}
// takes as few words as it can, so the rest of the pattern still finds its own.
bool MatchFrom(const Phrase& pattern, size_t k, const std::vector<std::string>& row, size_t j, PatternMatch& match) {
  if (k == pattern.words.size()) return true;
  const std::string& word = pattern.words[k];
  if (word == kValueWord) {
    if (j >= row.size()) return false;
    if (k + 1 == pattern.words.size()) {
      match.valueBegin = j;
      match.valueEnd = row.size();
      match.hasValue = true;
      return true;
    }
    for (size_t end = j + 1; end < row.size(); ++end) {
      PatternMatch rest = match;
      if (MatchFrom(pattern, k + 1, row, end, rest)) {
        match = rest;
        match.valueBegin = j;
        match.valueEnd = end;
        match.hasValue = true;
        return true;
      }
    }
    return false;
  }
  const bool xp = word == kXpWord;
  if (j >= row.size() || !WordsMatch(row[j], xp ? kNumberWord : std::string_view(word))) return false;
  size_t next = j + 1;
  if (word == kNumberWord || xp) {
    // "1,250" reads as 1 and 250.
    std::string digits = row[j];
    while (IsDigits(digits) && digits.size() <= 6 && next < row.size() && row[next].size() == 3 && IsDigits(row[next])) {
      digits += row[next++];
    }
    (xp ? match.xp : match.amount) = ReadNumber(digits);
  }
  return MatchFrom(pattern, k + 1, row, next, match);
}

std::optional<PatternMatch> MatchPattern(const Phrase& pattern, const std::vector<std::string>& row) {
  for (size_t start = 0; start < row.size(); ++start) {
    PatternMatch match;
    if (MatchFrom(pattern, 0, row, start, match)) return match;
  }
  return std::nullopt;
}

// The number on a row that isn't part of its words, like the 3 in "Metal Parts x3".
int AmountOn(const std::vector<std::string>& row) {
  for (const auto& token : row) {
    if (IsDigits(token)) return ReadNumber(token);
    // "X3", as OCR reads "x3".
    if (token.size() >= 2 && token[0] == 'X' && IsDigits(std::string_view(token).substr(1))) return ReadNumber(token.substr(1));
  }
  return 0;
}

}  // namespace

int DefaultJournalSampleMs(const JournalRule& rule) {
  return rule.ReadsFeed() && !rule.lines ? 500 : 1500;
}

std::vector<JournalValue> ParseJournalValues(const std::string& text) {
  std::vector<JournalValue> values;
  for (const auto& part : Split(text, '|')) {
    JournalValue value;
    const auto open = part.find('(');
    value.name = Trim(std::string_view(part).substr(0, open));
    if (open != std::string::npos) {
      const auto close = part.find(')', open);
      for (const auto& alias : Split(std::string_view(part).substr(open + 1, close == std::string::npos ? std::string::npos : close - open - 1), ',')) {
        auto phrase = MakePhrase(alias);
        if (!phrase.words.empty()) value.phrases.push_back(std::move(phrase));
      }
    }
    if (value.phrases.empty()) {
      auto phrase = MakePhrase(value.name);
      if (!phrase.words.empty()) value.phrases.push_back(std::move(phrase));
    }
    if (!value.name.empty() && !value.phrases.empty()) values.push_back(std::move(value));
  }
  // Longer names first, so STELLA MONTIS isn't taken for a value that's only part of it.
  std::ranges::stable_sort(values, [](const JournalValue& a, const JournalValue& b) {
    auto longest = [](const JournalValue& v) {
      size_t n = 0;
      for (const auto& p : v.phrases) n = std::max(n, p.words.size());
      return n;
    };
    return longest(a) > longest(b);
  });
  return values;
}

std::vector<Phrase> ParseJournalPatterns(const std::string& text) {
  std::vector<Phrase> patterns;
  for (const auto& part : Split(text, '|')) {
    std::string lower = part;
    std::ranges::transform(lower, lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    Phrase pattern;
    size_t start = 0;
    while (true) {
      const auto value = lower.find(kValuePlaceholder, start);
      const auto xp = lower.find(kXpPlaceholder, start);
      const auto at = std::min(value, xp);
      const auto piece = MakePhrase(std::string_view(part).substr(start, at == std::string::npos ? std::string::npos : at - start));
      pattern.words.insert(pattern.words.end(), piece.words.begin(), piece.words.end());
      if (at == std::string::npos) break;
      if (at == xp) {
        pattern.words.emplace_back(kXpWord);
        start = at + kXpPlaceholder.size();
        continue;
      }
      // Two {value}s side by side can't be told apart.
      if (pattern.words.empty() || pattern.words.back() != kValueWord) pattern.words.emplace_back(kValueWord);
      start = at + kValuePlaceholder.size();
    }
    // A pattern needs a word of its own to look for, or it would match every line.
    const bool hasWord = std::ranges::any_of(pattern.words, [](const std::string& w) {
      return w != kValueWord && w != kNumberWord && w != kXpWord;
    });
    if (hasWord) patterns.push_back(std::move(pattern));
  }
  return patterns;
}

bool ShowsAny(const std::vector<Phrase>& phrases, const std::vector<std::vector<std::string>>& rows) {
  return std::ranges::any_of(rows, [&](const auto& row) {
    return std::ranges::any_of(phrases, [&](const Phrase& p) { return ShowsPhrase(row, p); });
  });
}

std::vector<JournalNote> FindNotes(const JournalRule& rule, const std::vector<std::vector<std::string>>& rows) {
  std::vector<JournalNote> notes;
  for (const auto& row : rows) {
    if (row.empty()) continue;
    if (std::ranges::any_of(rule.exclude, [&](const Phrase& p) { return ShowsPhrase(row, p); })) continue;

    JournalNote note;
    note.note = rule.note;
    note.text = Join(row);
    bool found = false;

    for (const auto& pattern : rule.patterns) {
      const auto match = MatchPattern(pattern, row);
      if (!match) continue;
      if (match->hasValue) {
        const auto value = CleanValue(std::span(row).subspan(match->valueBegin, match->valueEnd - match->valueBegin));
        // {value} that came out as nothing but icon bits isn't this line.
        if (value.empty()) continue;
        note.value = TitleCase(value);
      }
      note.amount = match->amount;
      note.xp = match->xp;
      found = true;
      break;
    }
    if (!found) {
      for (const auto& value : rule.values) {
        if (std::ranges::any_of(value.phrases, [&](const Phrase& p) { return ShowsPhrase(row, p); })) {
          note.value = value.name;
          found = true;
          break;
        }
      }
    }
    if (!found && std::ranges::any_of(rule.words, [&](const Phrase& p) { return ShowsPhrase(row, p); })) found = true;
    if (!found && rule.lines) {
      // A list's title isn't one of its items.
      const bool title = std::ranges::any_of(rule.when, [&](const Phrase& p) { return ShowsPhrase(row, p); });
      std::vector<std::string> words;
      for (const auto& token : row) {
        if (HasLetters(token, 1)) words.push_back(token);
      }
      // An item's name is a few words; a longer line is its description in a tooltip.
      if (words.size() > 6) continue;
      const auto value = CleanValue(words);
      const bool named = std::ranges::any_of(value, [](const std::string& w) { return HasLetters(w, 3); });
      if (!title && named) {
        note.value = TitleCase(value);
        note.amount = AmountOn(row);
        found = true;
      }
    }
    if (found) notes.push_back(std::move(note));
  }
  return notes;
}

int64_t JournalTracker::ForgetMs() const {
  if (rule_->forgetMs > 0) return rule_->forgetMs;
  // Feed lines scroll and fade within seconds; a menu or a results screen stays up, and the
  // same map picked again is only a new note after the player has been away from it a while.
  if (rule_->ReadsFeed() && !rule_->lines) return std::max<int64_t>(4000, int64_t{3} * rule_->sampleMs);
  return 30000;
}

std::vector<JournalNote> JournalTracker::Update(const std::vector<std::vector<std::string>>& rows, int64_t nowMs,
                                                bool gateOpen) {
  std::erase_if(seen_, [&](const Seen& s) { return nowMs - s.lastSeenMs > ForgetMs(); });
  if (!gateOpen) return {};
  if (!rule_->whenRegion && !rule_->when.empty() && !ShowsAny(rule_->when, rows)) return {};

  auto found = FindNotes(*rule_, rows);
  // A menu showing several of the values at once (every map, to choose from) hasn't said which
  // one was picked yet.
  if (rule_->pick && !rule_->ReadsFeed() && !rule_->values.empty()) {
    const bool several = std::ranges::any_of(found, [&](const JournalNote& n) { return n.value != found.front().value; });
    if (several) return {};
  }
  // The same line more than once in this read: two Wasps destroyed one after the other.
  auto keyOf = [](const JournalNote& n) { return n.value.empty() ? n.text : n.value + "|" + std::to_string(n.amount); };
  std::vector<std::pair<std::string, int>> counts;
  for (const auto& note : found) {
    const std::string key = keyOf(note);
    auto same = std::ranges::find_if(counts, [&](const auto& c) { return SameText(c.first, key); });
    if (same != counts.end()) ++same->second;
    else counts.emplace_back(key, 1);
  }

  std::vector<JournalNote> fresh;
  const size_t limit = rule_->lines ? 80 : 12;
  for (const auto& [key, count] : counts) {
    auto known = std::ranges::find_if(seen_, [&](const Seen& s) { return SameText(s.key, key); });
    int added = count;
    if (known == seen_.end()) {
      seen_.push_back({key, count, nowMs});
    } else {
      added = std::max(0, count - known->count);
      known->count = std::max(known->count, count);
      known->lastSeenMs = nowMs;
    }
    // The first notes with this key, as many as are new.
    for (const auto& note : found) {
      if (added == 0 || fresh.size() >= limit) break;
      if (!SameText(keyOf(note), key)) continue;
      fresh.push_back(note);
      --added;
    }
  }
  if (fresh.empty()) return fresh;
  if (nowMs - lastNoteMs_ < rule_->cooldownMs) return {};
  lastNoteMs_ = nowMs;
  return fresh;
}

}  // namespace clip
