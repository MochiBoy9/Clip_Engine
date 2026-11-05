#include "core/AudioSettings.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <fstream>

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

std::vector<std::string> SplitCommas(std::string_view value) {
  std::vector<std::string> parts;
  while (!value.empty()) {
    const auto comma = value.find(',');
    parts.push_back(Trim(value.substr(0, comma)));
    value = comma == std::string_view::npos ? std::string_view() : value.substr(comma + 1);
  }
  return parts;
}

std::optional<int> ParseInt(std::string_view s) {
  int value = 0;
  const auto [ptr, ec] = std::from_chars(s.data(), s.data() + s.size(), value);
  if (ec != std::errc() || ptr != s.data() + s.size()) return std::nullopt;
  return value;
}

std::optional<AudioTrack> ParseTrack(std::string_view name) {
  for (AudioTrack track : kAudioTracks) {
    if (AudioTrackName(track) == name) return track;
  }
  if (name == "microphone") return AudioTrack::Mic;
  if (name == "other" || name == "other apps") return AudioTrack::Apps;
  return std::nullopt;
}

}  // namespace

bool AudioLevels::Apply(std::string_view key, std::string_view value, std::string* bad) {
  if (key == "muted_audio") {
    muted.fill(false);
    for (const auto& name : SplitCommas(value)) {
      if (name.empty()) continue;
      if (const auto track = ParseTrack(Lower(name))) muted[static_cast<size_t>(*track)] = true;
      else if (bad) *bad = name;
    }
    return true;
  }
  constexpr std::string_view kSuffix = "_volume";
  if (!key.ends_with(kSuffix)) return false;
  const auto track = ParseTrack(key.substr(0, key.size() - kSuffix.size()));
  if (!track) return false;
  std::string number = Trim(value);
  if (!number.empty() && number.back() == '%') number.pop_back();
  if (const auto percent = ParseInt(number)) volume[static_cast<size_t>(*track)] = std::clamp(*percent, 0, kMaxVolume);
  else if (bad) *bad = std::string(value);
  return true;
}

AudioLevels ReadAudioLevels(const std::filesystem::path& file) {
  AudioLevels levels;
  std::ifstream in(file);
  std::string line;
  while (std::getline(in, line)) {
    if (const auto hash = line.find('#'); hash != std::string::npos) line.resize(hash);
    const auto eq = line.find('=');
    if (eq == std::string::npos) continue;
    levels.Apply(Lower(Trim(std::string_view(line).substr(0, eq))), Trim(std::string_view(line).substr(eq + 1)));
  }
  return levels;
}

std::vector<VoicePhrase> ParseVoicePhrases(std::string_view value, std::vector<std::string>* bad) {
  std::vector<VoicePhrase> phrases;
  for (const auto& entry : SplitCommas(value)) {
    if (entry.empty()) continue;
    // Words, then perhaps a length: "save that clip 60" or "clip it 2m".
    std::vector<std::string> words;
    size_t at = 0;
    while (at < entry.size()) {
      const auto start = entry.find_first_not_of(" \t", at);
      if (start == std::string::npos) break;
      const auto end = entry.find_first_of(" \t", start);
      words.push_back(entry.substr(start, end == std::string::npos ? std::string::npos : end - start));
      at = end == std::string::npos ? entry.size() : end;
    }
    VoicePhrase phrase;
    if (words.size() > 1) {
      std::string last = Lower(words.back());
      int unit = 1;
      if (last.ends_with("s")) last.pop_back();
      else if (last.ends_with("m")) {
        last.pop_back();
        unit = 60;
      }
      if (const auto n = ParseInt(last); n && *n > 0) {
        phrase.seconds = *n * unit;
        words.pop_back();
      }
    }
    bool ok = !words.empty();
    for (const auto& word : words) {
      ok = ok && std::ranges::all_of(word, [](unsigned char c) { return std::isalnum(c) || c == '\'' || c >= 0x80; }) &&
           std::ranges::any_of(word, [](unsigned char c) { return std::isalpha(c) || c >= 0x80; });
      if (!phrase.words.empty()) phrase.words += ' ';
      phrase.words += Lower(word);
    }
    if (!ok) {
      if (bad) bad->push_back(entry);
      continue;
    }
    if (std::ranges::none_of(phrases, [&](const VoicePhrase& p) { return p.words == phrase.words; })) {
      phrases.push_back(std::move(phrase));
    }
  }
  return phrases;
}

std::optional<VoiceSensitivity> ParseVoiceSensitivity(std::string_view value) {
  const std::string v = Lower(Trim(value));
  if (v == "low") return VoiceSensitivity::Low;
  if (v == "medium") return VoiceSensitivity::Medium;
  if (v == "high") return VoiceSensitivity::High;
  return std::nullopt;
}

std::string_view ToString(VoiceSensitivity sensitivity) {
  switch (sensitivity) {
    case VoiceSensitivity::Low: return "low";
    case VoiceSensitivity::High: return "high";
    case VoiceSensitivity::Medium: break;
  }
  return "medium";
}

}  // namespace clip
