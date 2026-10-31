#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// Moments worth clipping. The names are what the log, detection.ini and the clip
// metadata files use, so keep them stable.
enum class EventType { Kill, Assist, Death, Revive, MatchStart, MatchEnd, Highlight };

inline constexpr std::array<EventType, 7> kAllEventTypes = {
    EventType::Kill,       EventType::Assist,   EventType::Death,    EventType::Revive,
    EventType::MatchStart, EventType::MatchEnd, EventType::Highlight,
};

inline std::string_view EventName(EventType type) {
  switch (type) {
    case EventType::Kill: return "kill";
    case EventType::Assist: return "assist";
    case EventType::Death: return "death";
    case EventType::Revive: return "revive";
    case EventType::MatchStart: return "match_start";
    case EventType::MatchEnd: return "match_end";
    default: return "highlight";
  }
}

inline std::optional<EventType> ParseEventType(std::string_view name) {
  for (EventType type : kAllEventTypes) {
    if (EventName(type) == name) return type;
  }
  return std::nullopt;
}

// One rule's sighting of a moment: what it read and where, so the app can show why a moment
// was counted. A moment seen by more than one rule (the killfeed and the score line) has a
// witness for each, and those that saw it but weren't counted say so.
struct Witness {
  std::string rule;  // the detection.ini section, like the_finals.kill.killfeed
  std::string text;  // what that rule read
  // The box it read, as fractions of the frame, where the HUD drew it on this screen.
  double left = 0, top = 0, width = 0, height = 0;
  double offset = 0;     // seconds from the moment's time
  bool counted = true;   // false: the same moment again, or another rule's count won
};

struct GameEvent {
  EventType type = EventType::Highlight;
  int64_t time = 0;  // QPC time in 100 ns units, like every other timestamp in the engine
  std::string text;  // what was read off the screen
  std::vector<Witness> witnesses;  // empty for moments from a game's own API
};

}  // namespace clip
