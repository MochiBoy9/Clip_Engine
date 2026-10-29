#pragma once

#include <cstdint>
#include <string>
#include <string_view>

namespace clip {

// What the user's Discord status says while a game is being recorded.
struct PresenceState {
  std::string game;        // the game's name, as shown everywhere else
  int64_t startUnix = 0;   // when the recording started, in seconds since 1970, for Discord's timer
  int kills = 0;           // in the current match, or the recording when no match start was seen
  int deaths = 0;
  int assists = 0;
  int clips = 0;           // clips saved since the game started
  bool longRecording = false;
  bool away = false;       // another window is in front of the game

  bool operator==(const PresenceState&) const = default;
};

// The two lines of the status: "Valorant" and "12 kills · 3 deaths · 2 clips".
std::string PresenceDetails(const PresenceState& state);
std::string PresenceLine(const PresenceState& state);

// The activity object for Discord's SET_ACTIVITY command. showGameName asks Discord to show
// the game's name in the member list ("Playing Valorant") instead of the app's; older Discord
// clients reject the field, so it can be left out.
std::string PresenceActivityJson(const PresenceState& state, std::string_view version, bool showGameName);

// The SET_ACTIVITY command for this process; an empty activity clears the status.
std::string SetActivityCommand(uint32_t pid, std::string_view activityJson, std::string_view nonce);

// A message on Discord's local pipe: opcode and length, both little-endian, then the JSON.
std::string DiscordFrame(uint32_t opcode, std::string_view json);

}  // namespace clip
