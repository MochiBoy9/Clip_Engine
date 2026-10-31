#pragma once

#include <array>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace clip {

// Hearing a game's sounds, for HUDs that play one with each kill: The Finals chimes for every
// elimination of yours. A sound is compared by its shape over time in 40 bands from 150 Hz to
// 7.8 kHz, with each band's level of the moment before taken off, so gunfire, music and a
// teammate's voice under it matter little, and how loud the game is set matters not at all.

inline constexpr int kSoundRate = 16000;      // samples per second the comparison works at
inline constexpr int kSoundHop = 160;         // one look every 10 ms
inline constexpr int kSoundWindow = 512;      // of 32 ms
inline constexpr int kSoundBands = 40;
inline constexpr int kSoundBackground = 30;   // looks before the current one that make its background
inline constexpr int kSoundLeadIn = 300;      // ms before the sound itself a cut-out sound starts with

using SoundRow = std::array<float, kSoundBands>;

// Turns audio at any rate and channel count into one channel at kSoundRate.
class SoundResampler {
 public:
  SoundResampler(int rate, int channels) : step_(static_cast<double>(rate) / kSoundRate), channels_(channels) {}
  void Push(const int16_t* pcm, size_t frames, std::vector<float>& out);
  void Push(const float* pcm, size_t frames, std::vector<float>& out);

 private:
  void Add(float sample, std::vector<float>& out);
  double step_;
  int channels_;
  double sum_ = 0;
  double taken_ = 0;  // how many source samples are in sum_
};

// Turns kSoundRate audio into one SoundRow per look, as it comes.
class SoundFeatures {
 public:
  SoundFeatures();
  // Adds samples; returns the rows of the looks they complete.
  std::vector<SoundRow> Push(const float* samples, size_t count);
  // How many looks have been made so far.
  int64_t Looks() const { return looks_; }

 private:
  SoundRow Look();
  std::vector<float> pending_;
  std::vector<float> window_;
  std::vector<std::pair<int, int>> bins_;  // the FFT bins each band sums, [first, last)
  std::vector<SoundRow> history_;          // recent levels, for the background
  int64_t looks_ = 0;
};

// What to listen for: the rows of one or more cut-out sounds, averaged. Each cut starts
// kSoundLeadIn before the sound, so the background is known, and the rest is the sound.
class SoundTemplate {
 public:
  // From cuts at kSoundRate. Empty when none are long enough.
  static std::optional<SoundTemplate> FromCuts(const std::vector<std::vector<float>>& cuts);
  size_t Length() const { return rows_.size(); }
  const std::vector<SoundRow>& Rows() const { return rows_; }

 private:
  std::vector<SoundRow> rows_;
};

// How alike the latest stretch of rows is to the template: -1 to 1, with 1 the same shape.
class SoundMatcher {
 public:
  explicit SoundMatcher(const SoundTemplate& sound);
  double Push(const SoundRow& row);

 private:
  std::vector<float> template_;  // centred and of length 1
  std::vector<SoundRow> recent_;  // a ring of the last Length() rows
  size_t next_ = 0;
  size_t filled_ = 0;
};

// Reads a WAV file (16-bit, 24-bit, 32-bit or float PCM, any rate and channels) at kSoundRate.
std::optional<std::vector<float>> LoadSoundCut(const std::filesystem::path& path, std::string* error = nullptr);

// Writes one channel of kSoundRate audio as a 16-bit WAV, for a sound cut from a clip.
bool SaveSoundCut(const std::filesystem::path& path, const std::vector<float>& samples);

}  // namespace clip
