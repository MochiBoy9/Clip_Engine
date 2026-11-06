#include "audio/AudioRouting.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <unordered_set>

namespace clip {

std::string_view AudioTrackName(AudioTrack track) {
  switch (track) {
    case AudioTrack::Game: return "game";
    case AudioTrack::Mic: return "mic";
    case AudioTrack::Discord: return "discord";
    case AudioTrack::Apps: return "apps";
  }
  return "game";
}

AppAudioKind ClassifyAudioApp(std::string_view exeName) {
  std::string name(exeName);
  std::ranges::transform(name, name.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  // Voice chat: Discord and its test builds, TeamSpeak and Mumble.
  static constexpr std::array<std::string_view, 8> kVoiceChat = {
      "discord.exe",          "discordptb.exe",    "discordcanary.exe", "discorddevelopment.exe",
      "ts3client_win64.exe", "ts3client_win32.exe", "teamspeak.exe",     "mumble.exe"};
  if (std::ranges::find(kVoiceChat, name) != kVoiceChat.end()) return AppAudioKind::VoiceChat;
  if (name == "clip_engine.exe" || name == "clipengine.exe") return AppAudioKind::ClipEngine;
  return AppAudioKind::Other;
}

bool IsInProcessTree(uint32_t pid, uint32_t root, const std::unordered_map<uint32_t, uint32_t>& parents) {
  if (root == 0) return false;
  // Parent ids can be stale (a parent exited and its id was reused), so stop on a loop.
  for (int depth = 0; depth < 64 && pid != 0; ++depth) {
    if (pid == root) return true;
    const auto parent = parents.find(pid);
    if (parent == parents.end() || parent->second == pid) return false;
    pid = parent->second;
  }
  return false;
}

std::vector<uint32_t> TreeRoots(const std::vector<uint32_t>& pids, const std::unordered_map<uint32_t, uint32_t>& parents) {
  std::vector<uint32_t> roots;
  std::unordered_set<uint32_t> seen;
  for (uint32_t pid : pids) {
    if (!seen.insert(pid).second) continue;
    const bool underAnother = std::ranges::any_of(pids, [&](uint32_t other) {
      return other != pid && IsInProcessTree(pid, other, parents);
    });
    if (!underAnother) roots.push_back(pid);
  }
  return roots;
}

void DownmixForSpeech(const int16_t* pcm, uint32_t frames, std::vector<int32_t>& carry, std::vector<int16_t>& out) {
  out.clear();
  for (uint32_t f = 0; f < frames; ++f) {
    carry.push_back((int32_t{pcm[f * 2]} + pcm[f * 2 + 1]) / 2);
    if (carry.size() == 3) {
      out.push_back(static_cast<int16_t>((carry[0] + carry[1] + carry[2]) / 3));
      carry.clear();
    }
  }
}

}  // namespace clip
