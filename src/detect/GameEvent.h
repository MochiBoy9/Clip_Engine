#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

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

struct GameEvent {
  EventType type = EventType::Highlight;
  int64_t time = 0;  // QPC time in 100 ns units, like every other timestamp in the engine
  std::string text;  // what was read off the screen
};

}  // namespace clip
