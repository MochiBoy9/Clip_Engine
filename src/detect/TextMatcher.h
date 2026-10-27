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

// How many separate times the phrase appears in the tokens.
int CountPhrase(const std::vector<std::string>& tokens, const Phrase& phrase);

}  // namespace clip
