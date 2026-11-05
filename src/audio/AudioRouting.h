#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace clip {

// The audio tracks a recording can have, besides the mix of them all. Each has its own volume
// and can be muted while recording.
enum class AudioTrack { Game, Mic, Discord, Apps };
inline constexpr AudioTrack kAudioTracks[] = {AudioTrack::Game, AudioTrack::Mic, AudioTrack::Discord, AudioTrack::Apps};
inline constexpr size_t kAudioTrackCount = 4;

// "game", "mic", "discord" or "apps", as the settings and clip metadata name them.
std::string_view AudioTrackName(AudioTrack track);

// Which track an app's sound belongs on, from its file name ("Discord.exe"): voice chat apps go
// on the Discord track, Clip Engine itself on none (so clips it plays back aren't recorded again),
// and every other app on the apps track. The game is found by its process, not its name.
enum class AppAudioKind { VoiceChat, ClipEngine, Other };
AppAudioKind ClassifyAudioApp(std::string_view exeName);

// Whether pid is root or one of its descendants, by each process's parent.
bool IsInProcessTree(uint32_t pid, uint32_t root, const std::unordered_map<uint32_t, uint32_t>& parents);

// pids without those that descend from another pid in the list, since recording a process
// records its children too and they'd be heard twice.
std::vector<uint32_t> TreeRoots(const std::vector<uint32_t>& pids, const std::unordered_map<uint32_t, uint32_t>& parents);

// Turns 48 kHz stereo into the 16 kHz mono speech recognition listens to, averaging the channels
// and each three samples. `carry` keeps the samples left over between calls (up to two frames).
void DownmixForSpeech(const int16_t* pcm, uint32_t frames, std::vector<int32_t>& carry, std::vector<int16_t>& out);

}  // namespace clip
