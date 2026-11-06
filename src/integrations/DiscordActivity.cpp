#include "integrations/DiscordActivity.h"

#include "core/Json.h"

#include <format>

namespace clip {
namespace {

std::string Count(int n, std::string_view one, std::string_view many) {
  return std::format("{} {}", n, n == 1 ? one : many);
}

// Discord wants 2 to 128 characters in each line of the status.
std::string Fit(std::string text) {
  if (text.size() > 128) {
    text.resize(125);
    // Don't cut a UTF-8 character in half.
    while (!text.empty() && (static_cast<unsigned char>(text.back()) & 0xC0) == 0x80) text.pop_back();
    if (!text.empty() && static_cast<unsigned char>(text.back()) >= 0xC0) text.pop_back();
    text += "...";
  }
  while (text.size() < 2) text += ' ';
  return text;
}

}  // namespace

std::string PresenceDetails(const PresenceState& state) { return Fit(state.game); }

std::string PresenceLine(const PresenceState& state) {
  std::string line;
  auto add = [&](const std::string& part) { line += (line.empty() ? "" : " \xC2\xB7 ") + part; };
  if (state.away) add("Tabbed out");
  else if (!state.flash.empty()) add(state.flash);
  else if (state.longRecording) add("Long recording");
  if (state.kills || state.deaths || state.assists) {
    add(Count(state.kills, "kill", "kills"));
    add(Count(state.deaths, "death", "deaths"));
    if (state.assists) add(Count(state.assists, "assist", "assists"));
  }
  if (state.streak >= 3 && state.flash.empty()) add(std::format("{}-kill streak", state.streak));
  if (state.clips) add(Count(state.clips, "clip", "clips"));
  if (line.empty()) line = "Recording with Clip Engine";
  return Fit(line);
}

std::string PresenceActivityJson(const PresenceState& state, std::string_view version, bool showGameName) {
  std::string json = "{\"type\":0";
  // 2 = the details line, which holds the game's name.
  if (showGameName) json += ",\"status_display_type\":2";
  json += std::format(",\"details\":{},\"state\":{}", JsonString(PresenceDetails(state)), JsonString(PresenceLine(state)));
  const int64_t start = state.matchStartUnix > 0 ? state.matchStartUnix : state.startUnix;
  if (start > 0) json += std::format(",\"timestamps\":{{\"start\":{}}}", start);
  // Discord shows the party as "(2 of 4)" after the state line.
  if (!state.partyId.empty() && state.squadPlaying >= 1 && state.squadMax >= state.squadPlaying) {
    json += std::format(",\"party\":{{\"id\":{},\"size\":[{},{}]}}", JsonString(state.partyId), state.squadPlaying, state.squadMax);
  }
  if (!state.joinUrl.empty()) {
    json += std::format(",\"buttons\":[{{\"label\":\"Join my squad\",\"url\":{}}}]", JsonString(state.joinUrl));
  }
  json += std::format(",\"assets\":{{\"large_image\":\"logo\",\"large_text\":{}}}",
                      JsonString(std::format("Clip Engine {}", version)));
  json += ",\"instance\":false}";
  return json;
}

std::string SetActivityCommand(uint32_t pid, std::string_view activityJson, std::string_view nonce) {
  return std::format(R"({{"cmd":"SET_ACTIVITY","args":{{"pid":{},"activity":{}}},"nonce":{}}})", pid,
                     activityJson.empty() ? std::string_view("null") : activityJson, JsonString(nonce));
}

std::string DiscordFrame(uint32_t opcode, std::string_view json) {
  std::string frame;
  frame.reserve(8 + json.size());
  auto put = [&](uint32_t v) {
    for (int i = 0; i < 4; ++i) frame += static_cast<char>((v >> (8 * i)) & 0xFF);
  };
  put(opcode);
  put(static_cast<uint32_t>(json.size()));
  frame += json;
  return frame;
}

}  // namespace clip
