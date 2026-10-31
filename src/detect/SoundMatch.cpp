#include "detect/SoundMatch.h"

#include <algorithm>
#include <cmath>
#include <complex>
#include <cstring>
#include <fstream>
#include <numbers>

namespace clip {
namespace {

void Fft(std::vector<std::complex<float>>& a) {
  const size_t n = a.size();
  for (size_t i = 1, j = 0; i < n; ++i) {
    size_t bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) std::swap(a[i], a[j]);
  }
  for (size_t len = 2; len <= n; len <<= 1) {
    const double angle = -2 * std::numbers::pi / static_cast<double>(len);
    const std::complex<float> step(static_cast<float>(std::cos(angle)), static_cast<float>(std::sin(angle)));
    for (size_t i = 0; i < n; i += len) {
      std::complex<float> w(1, 0);
      for (size_t k = 0; k < len / 2; ++k) {
        const auto u = a[i + k];
        const auto v = a[i + k + len / 2] * w;
        a[i + k] = u + v;
        a[i + k + len / 2] = u - v;
        w *= step;
      }
    }
  }
}

}  // namespace

void SoundResampler::Add(float sample, std::vector<float>& out) {
  // Each output sample is the average of the step_ source samples it covers, which keeps
  // what's above the new rate from folding back into what's compared.
  double rest = 1;
  while (rest > 1e-9) {
    const double use = std::min(rest, step_ - taken_);
    sum_ += sample * use;
    taken_ += use;
    rest -= use;
    if (taken_ >= step_ - 1e-9) {
      out.push_back(static_cast<float>(sum_ / step_));
      sum_ = 0;
      taken_ = 0;
    }
  }
}

void SoundResampler::Push(const int16_t* pcm, size_t frames, std::vector<float>& out) {
  for (size_t f = 0; f < frames; ++f) {
    float mono = 0;
    for (int c = 0; c < channels_; ++c) mono += pcm[f * channels_ + c];
    Add(mono / (32768.0f * channels_), out);
  }
}

void SoundResampler::Push(const float* pcm, size_t frames, std::vector<float>& out) {
  for (size_t f = 0; f < frames; ++f) {
    float mono = 0;
    for (int c = 0; c < channels_; ++c) mono += pcm[f * channels_ + c];
    Add(mono / static_cast<float>(channels_), out);
  }
}

SoundFeatures::SoundFeatures() : window_(kSoundWindow) {
  for (int i = 0; i < kSoundWindow; ++i) {
    window_[i] = static_cast<float>(0.5 - 0.5 * std::cos(2 * std::numbers::pi * i / (kSoundWindow - 1)));
  }
  // Bands evenly spaced in pitch, from 150 Hz to 7.8 kHz.
  const double low = 150, high = 7800;
  for (int b = 0; b < kSoundBands; ++b) {
    const double from = low * std::pow(high / low, static_cast<double>(b) / kSoundBands);
    const double to = low * std::pow(high / low, static_cast<double>(b + 1) / kSoundBands);
    const double binHz = static_cast<double>(kSoundRate) / kSoundWindow;
    const int first = static_cast<int>(std::ceil(from / binHz));
    const int last = static_cast<int>(std::ceil(to / binHz));  // the first bin at or past `to`
    bins_.emplace_back(first, last);
  }
}

std::vector<SoundRow> SoundFeatures::Push(const float* samples, size_t count) {
  pending_.insert(pending_.end(), samples, samples + count);
  std::vector<SoundRow> rows;
  size_t at = 0;
  while (pending_.size() - at >= static_cast<size_t>(kSoundWindow)) {
    std::vector<std::complex<float>> spectrum(kSoundWindow);
    for (int i = 0; i < kSoundWindow; ++i) spectrum[i] = pending_[at + i] * window_[i];
    Fft(spectrum);
    SoundRow level{};
    for (int b = 0; b < kSoundBands; ++b) {
      double sum = 0;
      for (int k = bins_[b].first; k < bins_[b].second && k <= kSoundWindow / 2; ++k) sum += std::abs(spectrum[k]);
      level[b] = static_cast<float>(std::log(sum + 1e-6));
    }
    // What stands out from the moment before: each band above its average of the last look or so.
    history_.push_back(level);
    if (history_.size() > static_cast<size_t>(kSoundBackground) + 1) history_.erase(history_.begin());
    SoundRow row{};
    for (int b = 0; b < kSoundBands; ++b) {
      double background = 0;
      for (const auto& h : history_) background += h[b];
      background /= static_cast<double>(history_.size());
      row[b] = static_cast<float>(std::max(0.0, level[b] - background));
    }
    rows.push_back(row);
    ++looks_;
    at += kSoundHop;
  }
  pending_.erase(pending_.begin(), pending_.begin() + static_cast<std::ptrdiff_t>(at));
  return rows;
}

std::optional<SoundTemplate> SoundTemplate::FromCuts(const std::vector<std::vector<float>>& cuts) {
  // The lead-in only sets the background; the sound is what follows it, up to a second.
  constexpr size_t kSkip = kSoundLeadIn * kSoundRate / 1000 / kSoundHop;
  constexpr size_t kMost = 100;
  std::vector<std::vector<SoundRow>> all;
  for (const auto& cut : cuts) {
    SoundFeatures features;
    auto rows = features.Push(cut.data(), cut.size());
    if (rows.size() < kSkip + 10) continue;
    rows.erase(rows.begin(), rows.begin() + kSkip);
    all.push_back(std::move(rows));
  }
  if (all.empty()) return std::nullopt;
  size_t length = kMost;
  for (const auto& rows : all) length = std::min(length, rows.size());
  SoundTemplate sound;
  sound.rows_.assign(length, SoundRow{});
  for (const auto& rows : all) {
    for (size_t i = 0; i < length; ++i) {
      for (int b = 0; b < kSoundBands; ++b) sound.rows_[i][b] += rows[i][b] / static_cast<float>(all.size());
    }
  }
  return sound;
}

SoundMatcher::SoundMatcher(const SoundTemplate& sound) : recent_(sound.Length()) {
  for (const auto& row : sound.Rows()) template_.insert(template_.end(), row.begin(), row.end());
  double mean = 0;
  for (float v : template_) mean += v;
  mean /= static_cast<double>(template_.size());
  double norm = 0;
  for (float& v : template_) {
    v = static_cast<float>(v - mean);
    norm += static_cast<double>(v) * v;
  }
  norm = std::sqrt(norm);
  for (float& v : template_) v = static_cast<float>(norm > 0 ? v / norm : 0);
}

double SoundMatcher::Push(const SoundRow& row) {
  recent_[next_] = row;
  next_ = (next_ + 1) % recent_.size();
  if (filled_ < recent_.size()) ++filled_;
  if (filled_ < recent_.size()) return 0;
  // Pearson's correlation of the stretch with the template, oldest row first.
  double sum = 0, squares = 0, dot = 0;
  const size_t n = recent_.size() * kSoundBands;
  for (size_t i = 0; i < recent_.size(); ++i) {
    const SoundRow& r = recent_[(next_ + i) % recent_.size()];
    for (int b = 0; b < kSoundBands; ++b) {
      const double v = r[b];
      sum += v;
      squares += v * v;
      dot += v * template_[i * kSoundBands + b];
    }
  }
  // The template is centred, so the stretch's mean drops out of the dot product.
  const double variance = squares - sum * sum / static_cast<double>(n);
  return variance > 1e-12 ? dot / std::sqrt(variance) : 0;
}

namespace {

uint32_t Read32(const uint8_t* p) { return p[0] | (p[1] << 8) | (p[2] << 16) | (uint32_t{p[3]} << 24); }
uint16_t Read16(const uint8_t* p) { return static_cast<uint16_t>(p[0] | (p[1] << 8)); }

}  // namespace

std::optional<std::vector<float>> LoadSoundCut(const std::filesystem::path& path, std::string* error) {
  auto fail = [&](const char* why) -> std::optional<std::vector<float>> {
    if (error) *error = why;
    return std::nullopt;
  };
  std::ifstream in(path, std::ios::binary);
  if (!in) return fail("it can't be opened");
  std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
  if (bytes.size() < 12 || std::memcmp(bytes.data(), "RIFF", 4) != 0 || std::memcmp(bytes.data() + 8, "WAVE", 4) != 0) {
    return fail("it isn't a WAV file");
  }
  int format = 0, channels = 0, bits = 0;
  uint32_t rate = 0;
  const uint8_t* data = nullptr;
  size_t dataSize = 0;
  for (size_t at = 12; at + 8 <= bytes.size();) {
    const uint32_t size = Read32(bytes.data() + at + 4);
    const uint8_t* body = bytes.data() + at + 8;
    const size_t available = std::min<size_t>(size, bytes.size() - at - 8);
    if (std::memcmp(bytes.data() + at, "fmt ", 4) == 0 && available >= 16) {
      format = Read16(body);
      channels = Read16(body + 2);
      rate = Read32(body + 4);
      bits = Read16(body + 14);
      if (format == 0xFFFE && available >= 26) format = Read16(body + 24);  // WAVE_FORMAT_EXTENSIBLE's subformat
    } else if (std::memcmp(bytes.data() + at, "data", 4) == 0) {
      data = body;
      dataSize = available;
    }
    at += 8 + size + (size & 1);
  }
  if (!data || channels < 1 || rate < 8000) return fail("it has no audio in it");
  const bool isFloat = format == 3 && bits == 32;
  if (!isFloat && !(format == 1 && (bits == 16 || bits == 24 || bits == 32))) return fail("its audio format isn't PCM");

  const size_t bytesPerSample = bits / 8;
  const size_t frames = dataSize / (bytesPerSample * channels);
  std::vector<float> samples(frames * channels);
  for (size_t i = 0; i < samples.size(); ++i) {
    const uint8_t* p = data + i * bytesPerSample;
    if (isFloat) {
      float v;
      std::memcpy(&v, p, 4);
      samples[i] = v;
    } else if (bits == 16) {
      samples[i] = static_cast<int16_t>(Read16(p)) / 32768.0f;
    } else if (bits == 24) {
      const int32_t v = static_cast<int32_t>((p[0] << 8) | (p[1] << 16) | (uint32_t{p[2]} << 24)) >> 8;
      samples[i] = static_cast<float>(v / 8388608.0);
    } else {
      samples[i] = static_cast<float>(static_cast<int32_t>(Read32(p)) / 2147483648.0);
    }
  }
  std::vector<float> out;
  SoundResampler(static_cast<int>(rate), channels).Push(samples.data(), frames, out);
  return out;
}

bool SaveSoundCut(const std::filesystem::path& path, const std::vector<float>& samples) {
  std::ofstream out(path, std::ios::binary);
  if (!out) return false;
  auto put32 = [&](uint32_t v) { out.write(reinterpret_cast<const char*>(&v), 4); };
  auto put16 = [&](uint16_t v) { out.write(reinterpret_cast<const char*>(&v), 2); };
  const uint32_t dataSize = static_cast<uint32_t>(samples.size() * 2);
  out.write("RIFF", 4);
  put32(36 + dataSize);
  out.write("WAVEfmt ", 8);
  put32(16);
  put16(1);
  put16(1);
  put32(kSoundRate);
  put32(kSoundRate * 2);
  put16(2);
  put16(16);
  out.write("data", 4);
  put32(dataSize);
  for (float s : samples) put16(static_cast<uint16_t>(static_cast<int16_t>(std::lround(std::clamp(s, -1.0f, 1.0f) * 32767))));
  return static_cast<bool>(out);
}

}  // namespace clip
