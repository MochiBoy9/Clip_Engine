#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace clip {

// Matching for text read off the screen. OCR on game HUDs is noisy: letters come back as
// look-alike digits, words lose a letter or run together. These helpers tolerate that.

// Upper-cases and splits into runs of letters and digits. Accented Latin letters (UTF-8) count
// as the letter without its accent, so a name like José or Łukasz is read as JOSE or LUKASZ.
std::vector<std::string> Tokenize(std::string_view text);

// A keyword or short phrase such as "ELIMINATED" or "KILLED BY". "{number}" in it stands for
// any number, as in "ELIMINATION {number}" for the score line a kill brings up; it's kept in
// the phrase as kNumberWord.
struct Phrase {
  std::vector<std::string> words;
  // A player's in-game name on its own ({player} in a rule), which is matched more strictly
  // than a keyword: another player whose name starts with it isn't the player (NameMatches).
  bool name = false;
};
Phrase MakePhrase(std::string_view text);

inline constexpr std::string_view kNumberPlaceholder = "{number}";
inline constexpr std::string_view kNumberWord = "#";

// Whether a phrase has a {number} in it.
bool HasNumber(const Phrase& phrase);

// True when an OCR'd word is close enough to the expected word, or for kNumberWord, when it's
// a number. A plural never matches the word it's the plural of: ELIMINATIONS and REVIVES are
// scoreboard headings, not a kill or a revive.
bool WordsMatch(std::string_view token, std::string_view word);

// WordsMatch for a word of a player's name. Names can't run into the next word or gain a
// letter at the end, as keywords can: ENTERROR is another player, not ENTERRO misread. A name of four
// letters may have one letter misread as one that looks like it (I for L, O for D), which
// text recognition does often enough on small killfeed text to miss kills.
bool NameMatches(std::string_view token, std::string_view word);

// Whether the token matches the phrase's k-th word, as a name for a name phrase.
bool PhraseWordMatches(const Phrase& phrase, size_t k, std::string_view token);

// When text recognition ran a keyword into the next word ("ELIMINATEDBY", "ELIMINATEDXXSNIPER"),
// how many of the token's characters are the keyword; 0 when the token isn't one of those.
// Another form of the word isn't a run-on: DEFEATED isn't DEFEAT, nor ASSISTED ASSIST.
size_t RunOnLength(std::string_view token, std::string_view word);

// True when two runs of OCR'd text, such as player names, are close enough to be the same
// text read twice: equal, or a letter or two off for longer text.
bool SameText(std::string_view a, std::string_view b);

// How many separate times the phrase appears in the tokens.
int CountPhrase(const std::vector<std::string>& tokens, const Phrase& phrase);

// Whether the phrase starts (or ends) a row of text. One stray token of one or two characters
// before (or after) it is allowed, since icons next to names tend to read as a letter or two.
// With skipTag, a squad tag of up to six characters may come first too.
bool PhraseAtStart(const std::vector<std::string>& row, const Phrase& phrase, bool skipTag = false);
// The same, giving where in the row the phrase ends (one past its last token), or 0 when it
// doesn't start the row.
size_t PhraseStartEnd(const std::vector<std::string>& row, const Phrase& phrase, bool skipTag = false);
bool PhraseAtEnd(const std::vector<std::string>& row, const Phrase& phrase);

// A word read off the screen and where it was, in pixels.
struct WordBox {
  std::string text;
  double x = 0, y = 0, width = 0, height = 0;
};

// Puts words that sit side by side into one row, top to bottom and each row left to right,
// and tokenizes each row. Text recognition sometimes splits one line of a killfeed in two
// where an icon sits between the names; this joins them back up.
std::vector<std::vector<std::string>> GroupIntoRows(std::vector<WordBox> words);

}  // namespace clip
