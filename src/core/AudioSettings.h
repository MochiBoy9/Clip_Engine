#pragma once

#include "audio/AudioRouting.h"

#include <array>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// How loud each audio track is recorded, and which are muted. The desktop app changes these while
// a game is recorded, and the engine takes them at once.
struct AudioLevels {
  static constexpr int kMaxVolume = 200;  // percent

  std::array<int, kAudioTrackCount> volume{100, 100, 100, 100};  // percent, by AudioTrack
  std::array<bool, kAudioTrackCount> muted{};

  float Gain(AudioTrack track) const {
    const auto i = static_cast<size_t>(track);
    return muted[i] ? 0.0f : static_cast<float>(volume[i]) / 100.0f;
  }

  // Takes one line of the settings file: game_volume, mic_volume, discord_volume, apps_volume
  // or muted_audio ("mic, discord"). False when the key is none of those. bad gets a value that
  // couldn't be read.
  bool Apply(std::string_view key, std::string_view value, std::string* bad = nullptr);
};

// The levels in the settings file as it is now.
AudioLevels ReadAudioLevels(const std::filesystem::path& file);

// A phrase that saves a clip when it's said, and the length of that clip (0 = the usual length).
struct VoicePhrase {
  std::string words;
  int seconds = 0;
};

// "clip that, save that clip 60" from voice_phrases: phrases separated by commas, each optionally
// ending in a length in seconds. Letters, digits, apostrophes and spaces only; bad gets each one
// that isn't a phrase.
std::vector<VoicePhrase> ParseVoicePhrases(std::string_view value, std::vector<std::string>* bad = nullptr);

// How readily a phrase counts as heard: low needs the clearest match (fewest clips by accident),
// high takes the loosest (for noisy rooms and accents speech recognition finds hard).
enum class VoiceSensitivity { Low, Medium, High };
std::optional<VoiceSensitivity> ParseVoiceSensitivity(std::string_view value);
std::string_view ToString(VoiceSensitivity sensitivity);

}  // namespace clip
