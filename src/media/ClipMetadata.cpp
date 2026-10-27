#include "media/ClipMetadata.h"

#include <format>

namespace clip {
namespace {

std::string Escape(std::string_view s) {
  std::string out;
  out.reserve(s.size() + 2);
  for (char c : s) {
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      default:
        if (static_cast<unsigned char>(c) < 0x20) out += std::format("\\u{:04x}", static_cast<int>(c));
        else out += c;
    }
  }
  return out;
}

std::string_view Plural(EventType type, int count) {
  const bool one = count == 1;
  switch (type) {
    case EventType::Kill: return one ? "kill" : "kills";
    case EventType::Assist: return one ? "assist" : "assists";
    case EventType::Death: return one ? "death" : "deaths";
    case EventType::Revive: return one ? "revive" : "revives";
    case EventType::MatchStart: return one ? "match start" : "match starts";
    case EventType::MatchEnd: return one ? "match end" : "match ends";
    default: return one ? "highlight" : "highlights";
  }
}

}  // namespace

std::string ToJson(const ClipMetadata& metadata) {
  std::string json = std::format("{{\n  \"version\": 1,\n  \"game\": \"{}\",\n  \"trigger\": \"{}\",\n  \"seconds\": {:.2f},\n",
                                 Escape(metadata.game), Escape(metadata.trigger), metadata.seconds);
  json += std::format("  \"title\": \"{}\",\n  \"events\": [", Escape(Summarize(metadata.markers)));
  for (size_t i = 0; i < metadata.markers.size(); ++i) {
    const auto& m = metadata.markers[i];
    json += std::format("{}\n    {{ \"type\": \"{}\", \"time\": {:.2f}, \"text\": \"{}\" }}", i == 0 ? "" : ",",
                        EventName(m.type), m.seconds, Escape(m.text));
  }
  json += metadata.markers.empty() ? "]\n}\n" : "\n  ]\n}\n";
  return json;
}

std::string Summarize(const std::vector<ClipMetadata::Marker>& markers) {
  std::string summary;
  for (EventType type : kAllEventTypes) {
    int count = 0;
    for (const auto& m : markers) count += m.type == type ? 1 : 0;
    if (count == 0) continue;
    if (!summary.empty()) summary += ", ";
    summary += std::format("{} {}", count, Plural(type, count));
  }
  return summary;
}

}  // namespace clip
