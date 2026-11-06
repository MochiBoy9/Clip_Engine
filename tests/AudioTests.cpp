// Tests for the audio tracks and voice clips: mixing sources that arrive on their own clocks, which
// app goes on which track, volumes and mutes from the settings file, the phrases that save a clip,
// and the tracks a clip lists in its metadata.

#include "audio/AudioRouting.h"
#include "audio/PcmMixer.h"
#include "core/AudioSettings.h"
#include "media/ClipMetadata.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

int g_failures = 0;

void Check(bool ok, const char* expression, int line) {
  if (ok) return;
  ++g_failures;
  std::printf("FAILED line %d: %s\n", line, expression);
}

#define CHECK(x) Check((x), #x, __LINE__)

using namespace clip;

constexpr int64_t kSecond = 10'000'000;

// Stereo PCM of `frames` frames, every sample `value`.
std::vector<int16_t> Tone(uint32_t frames, int16_t value) { return std::vector<int16_t>(size_t{frames} * 2, value); }

// pts of frame number n.
int64_t At(int64_t frame) { return PcmMixer::FramesToHns(frame); }

void TestMixerAddsInputs() {
  PcmMixer mixer;
  const auto a = Tone(480, 1000);
  const auto b = Tone(480, -300);
  mixer.Push(0, a.data(), 480, At(48000));
  mixer.Push(1, b.data(), 480, At(48000));
  std::vector<int16_t> out;
  mixer.Mix(48000, 480, out);
  CHECK(out.size() == 960);
  CHECK(out.front() == 700 && out.back() == 700);
  CHECK(mixer.Inputs() == 2);
}

void TestMixerLinesUpByTime() {
  // The second input starts 100 frames later, so its audio lands 100 frames in.
  PcmMixer mixer;
  const auto a = Tone(480, 100);
  const auto b = Tone(480, 10);
  mixer.Push(0, a.data(), 480, At(96000));
  mixer.Push(1, b.data(), 480, At(96100));
  std::vector<int16_t> out;
  mixer.Mix(96000, 480, out);
  CHECK(out[0] == 100);
  CHECK(out[99 * 2] == 100);
  CHECK(out[100 * 2] == 110);
  CHECK(out[479 * 2 + 1] == 110);
  // The rest of the second input comes in the next stretch.
  mixer.Mix(96480, 480, out);
  CHECK(out[0] == 10 && out[99 * 2] == 10 && out[100 * 2] == 0);
}

void TestMixerIgnoresJitterAndFillsGaps() {
  PcmMixer mixer;
  const auto a = Tone(480, 50);
  mixer.Push(0, a.data(), 480, At(0));
  // 3 ms late: ordinary jitter, so it carries straight on.
  mixer.Push(0, a.data(), 480, At(480 + 144));
  // 50 ms later than expected: a real gap of silence.
  mixer.Push(0, a.data(), 480, At(960 + 2400));
  std::vector<int16_t> out;
  mixer.Mix(0, 960 + 2400 + 480, out);
  CHECK(out[0] == 50 && out[959 * 2] == 50);
  CHECK(out[960 * 2] == 0 && out[(960 + 2399) * 2] == 0);
  CHECK(out[(960 + 2400) * 2] == 50);
}

void TestMixerDropsOverlapsAndLateAudio() {
  PcmMixer mixer;
  const auto a = Tone(960, 7);
  mixer.Push(0, a.data(), 960, At(0));
  // Starts 660 frames before the last packet ended, more than jitter: the overlap is dropped.
  mixer.Push(0, a.data(), 960, At(300));
  std::vector<int16_t> out;
  mixer.Mix(0, 2000, out);
  CHECK(out[0] == 7 && out[1259 * 2] == 7 && out[1260 * 2] == 0);
  // Audio for a stretch already mixed is too late to be heard.
  mixer.Push(0, a.data(), 480, At(1000));
  mixer.Mix(2000, 480, out);
  CHECK(out[0] == 0);
}

void TestMixerSaturatesAndRemoves() {
  PcmMixer mixer;
  const auto loud = Tone(100, 30000);
  mixer.Push(0, loud.data(), 100, At(0));
  mixer.Push(1, loud.data(), 100, At(0));
  std::vector<int16_t> out;
  mixer.Mix(0, 100, out);
  CHECK(out[0] == 32767);
  mixer.Remove(1);
  CHECK(mixer.Inputs() == 1);
}

void TestGain() {
  auto pcm = Tone(4, 1000);
  ApplyGain(pcm.data(), 4, 2, 0.5f, 0.5f);
  CHECK(pcm[0] == 500 && pcm[7] == 500);
  pcm = Tone(4, 1000);
  ApplyGain(pcm.data(), 4, 2, 0, 0);
  CHECK(pcm[0] == 0 && pcm[7] == 0);
  pcm = Tone(4, 20000);
  ApplyGain(pcm.data(), 4, 2, 2, 2);
  CHECK(pcm[0] == 32767);
  // A change of volume ramps across the packet instead of jumping.
  pcm = Tone(4, 1000);
  ApplyGain(pcm.data(), 4, 2, 1, 0);
  CHECK(pcm[0] == 1000 && pcm[2] == 750 && pcm[6] == 250);
}

void TestClassifyApps() {
  CHECK(ClassifyAudioApp("Discord.exe") == AppAudioKind::VoiceChat);
  CHECK(ClassifyAudioApp("DiscordPTB.exe") == AppAudioKind::VoiceChat);
  CHECK(ClassifyAudioApp("ts3client_win64.exe") == AppAudioKind::VoiceChat);
  CHECK(ClassifyAudioApp("ClipEngine.exe") == AppAudioKind::ClipEngine);
  CHECK(ClassifyAudioApp("clip_engine.exe") == AppAudioKind::ClipEngine);
  CHECK(ClassifyAudioApp("Spotify.exe") == AppAudioKind::Other);
  CHECK(ClassifyAudioApp("") == AppAudioKind::Other);
}

void TestProcessTrees() {
  // 10 is the game, 11 and 12 its children, 20 a browser with its audio process 21.
  const std::unordered_map<uint32_t, uint32_t> parents{{10, 1}, {11, 10}, {12, 11}, {20, 1}, {21, 20}, {30, 30}};
  CHECK(IsInProcessTree(12, 10, parents));
  CHECK(IsInProcessTree(10, 10, parents));
  CHECK(!IsInProcessTree(21, 10, parents));
  CHECK(!IsInProcessTree(21, 0, parents));
  CHECK(!IsInProcessTree(30, 10, parents));  // its own parent: no endless loop
  const auto roots = TreeRoots({21, 20, 12, 21}, parents);
  CHECK(roots.size() == 2 && roots[0] == 20 && roots[1] == 12);
}

void TestDownmixForSpeech() {
  std::vector<int32_t> carry;
  std::vector<int16_t> out;
  const int16_t pcm[] = {300, 100, 300, 100, 300, 100, 600, 0};  // four frames: 200, 200, 200, 300
  DownmixForSpeech(pcm, 4, carry, out);
  CHECK(out.size() == 1 && out[0] == 200);
  CHECK(carry.size() == 1);
  const int16_t more[] = {300, 300, 300, 300};
  DownmixForSpeech(more, 2, carry, out);
  CHECK(out.size() == 1 && out[0] == 300 && carry.empty());
}

void TestAudioLevels() {
  AudioLevels levels;
  CHECK(levels.Gain(AudioTrack::Mic) == 1.0f);
  CHECK(levels.Apply("mic_volume", "150"));
  CHECK(levels.Apply("game_volume", "80%"));
  CHECK(levels.Apply("discord_volume", "900"));
  CHECK(levels.Apply("muted_audio", "Discord, apps"));
  CHECK(!levels.Apply("clip_seconds", "30"));
  CHECK(levels.Gain(AudioTrack::Mic) == 1.5f);
  CHECK(levels.Gain(AudioTrack::Game) == 0.8f);
  CHECK(levels.volume[static_cast<size_t>(AudioTrack::Discord)] == AudioLevels::kMaxVolume);
  CHECK(levels.Gain(AudioTrack::Discord) == 0.0f);
  CHECK(levels.Gain(AudioTrack::Apps) == 0.0f);
  std::string bad;
  CHECK(levels.Apply("muted_audio", "mic, radio", &bad));
  CHECK(bad == "radio" && levels.muted[static_cast<size_t>(AudioTrack::Mic)] && !levels.muted[static_cast<size_t>(AudioTrack::Apps)]);
  bad.clear();
  CHECK(levels.Apply("apps_volume", "loud", &bad) && bad == "loud");
}

void TestReadAudioLevels(const std::filesystem::path& dir) {
  const auto file = dir / "audio_levels_test.ini";
  std::ofstream(file) << "# settings\nclip_seconds = 30\nMic_Volume = 40 # quieter\nmuted_audio = game\n";
  const auto levels = ReadAudioLevels(file);
  CHECK(levels.volume[static_cast<size_t>(AudioTrack::Mic)] == 40);
  CHECK(levels.muted[static_cast<size_t>(AudioTrack::Game)]);
  CHECK(!levels.muted[static_cast<size_t>(AudioTrack::Mic)]);
  std::filesystem::remove(file);
  CHECK(ReadAudioLevels(file).volume[0] == 100);
}

void TestVoicePhrases() {
  const auto phrases = ParseVoicePhrases("Clip that,  save   the last minute 60, clip it 2m, CLIP THAT, keep that 45s");
  CHECK(phrases.size() == 4);
  if (phrases.size() == 4) {
    CHECK(phrases[0].words == "clip that" && phrases[0].seconds == 0);
    CHECK(phrases[1].words == "save the last minute" && phrases[1].seconds == 60);
    CHECK(phrases[2].words == "clip it" && phrases[2].seconds == 120);
    CHECK(phrases[3].words == "keep that" && phrases[3].seconds == 45);
  }
  std::vector<std::string> bad;
  const auto some = ParseVoicePhrases("that's a clip, 60, clip-that, ,nice one", &bad);
  CHECK(some.size() == 2);
  if (some.size() == 2) CHECK(some[0].words == "that's a clip" && some[1].words == "nice one");
  CHECK(bad.size() == 2);
  if (bad.size() == 2) CHECK(bad[0] == "60" && bad[1] == "clip-that");
  // A word that ends in s isn't a length.
  const auto words = ParseVoicePhrases("clip this");
  CHECK(words.size() == 1 && words[0].words == "clip this" && words[0].seconds == 0);
  CHECK(ParseVoicePhrases("").empty());
}

void TestVoiceSensitivity() {
  CHECK(ParseVoiceSensitivity(" High ") == VoiceSensitivity::High);
  CHECK(ParseVoiceSensitivity("low") == VoiceSensitivity::Low);
  CHECK(!ParseVoiceSensitivity("max"));
  CHECK(ToString(VoiceSensitivity::Medium) == "medium");
}

void TestMetadataAudioTracks() {
  ClipMetadata metadata;
  metadata.game = "Valorant";
  metadata.trigger = "voice";
  metadata.audioTracks = {"all", "game", "mic", "discord"};
  const auto json = ToJson(metadata);
  CHECK(json.find("\"audio_tracks\": [\"all\", \"game\", \"mic\", \"discord\"]") != std::string::npos);
  const auto parsed = ParseMetadataJson(json);
  CHECK(parsed && parsed->trigger == "voice" && parsed->audioTracks == metadata.audioTracks);
  // Older clips have no list.
  const auto old = ParseMetadataJson(R"({"game": "x", "trigger": "hotkey", "seconds": 3, "events": []})");
  CHECK(old && old->audioTracks.empty());
  CHECK(ToJson(*old).find("audio_tracks") == std::string::npos);
}

}  // namespace

int main(int argc, char** argv) {
  const std::filesystem::path dir = argc > 1 ? argv[1] : std::filesystem::temp_directory_path();
  TestMixerAddsInputs();
  TestMixerLinesUpByTime();
  TestMixerIgnoresJitterAndFillsGaps();
  TestMixerDropsOverlapsAndLateAudio();
  TestMixerSaturatesAndRemoves();
  TestGain();
  TestClassifyApps();
  TestProcessTrees();
  TestDownmixForSpeech();
  TestAudioLevels();
  TestReadAudioLevels(dir);
  TestVoicePhrases();
  TestVoiceSensitivity();
  TestMetadataAudioTracks();
  if (g_failures == 0) std::printf("All audio tests passed\n");
  return g_failures == 0 ? 0 : 1;
}
