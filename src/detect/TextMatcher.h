#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace clip {

// Matching for text read off the screen. OCR on game HUDs is noisy: letters come back as
// look-alike digits, words lose a letter or run together. These helpers tolerate that.

// Upper-cases and splits into runs of letters and digits.
std::vector<std::string> Tokenize(std::string_view text);

// A keyword or short phrase such as "ELIMINATED" or "KILLED BY".
struct Phrase {
  std::vector<std::string> words;
};
Phrase MakePhrase(std::string_view text);

// True when an OCR'd word is close enough to the expected word.
bool WordsMatch(std::string_view token, std::string_view word);

// True when two runs of OCR'd text, such as player names, are close enough to be the same
// text read twice: equal, or a letter or two off for longer text.
bool SameText(std::string_view a, std::string_view b);

// How many separate times the phrase appears in the tokens.
int CountPhrase(const std::vector<std::string>& tokens, const Phrase& phrase);

// Whether the phrase starts (or ends) a row of text. One stray token of one or two characters
// before (or after) it is allowed, since icons next to names tend to read as a letter or two.
bool PhraseAtStart(const std::vector<std::string>& row, const Phrase& phrase);
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
