#include "media/ClipMetadata.h"

#include <array>
#include <cstdint>
#include <cstring>
#include <format>
#include <fstream>

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

// 2f6c1c3e-9b4a-4d27-8e51-c11ce4613ae0, chosen for Clip Engine.
constexpr std::array<uint8_t, 16> kMetadataUuid = {0x2f, 0x6c, 0x1c, 0x3e, 0x9b, 0x4a, 0x4d, 0x27,
                                                   0x8e, 0x51, 0xc1, 0x1c, 0xe4, 0x61, 0x3a, 0xe0};

uint32_t ReadBigEndian32(const uint8_t* p) {
  return (uint32_t{p[0]} << 24) | (uint32_t{p[1]} << 16) | (uint32_t{p[2]} << 8) | uint32_t{p[3]};
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


bool AppendMetadataBox(const std::filesystem::path& mp4, const std::string& json) {
  std::ofstream out(mp4, std::ios::binary | std::ios::app);
  if (!out) return false;
  const uint32_t size = static_cast<uint32_t>(8 + kMetadataUuid.size() + json.size());
  const uint8_t header[8] = {static_cast<uint8_t>(size >> 24), static_cast<uint8_t>(size >> 16),
                             static_cast<uint8_t>(size >> 8),  static_cast<uint8_t>(size),
                             'u', 'u', 'i', 'd'};
  out.write(reinterpret_cast<const char*>(header), sizeof(header));
  out.write(reinterpret_cast<const char*>(kMetadataUuid.data()), kMetadataUuid.size());
  out.write(json.data(), static_cast<std::streamsize>(json.size()));
  return static_cast<bool>(out);
}

std::optional<std::string> ReadMetadataBox(const std::filesystem::path& mp4) {
  std::ifstream in(mp4, std::ios::binary);
  if (!in) return std::nullopt;
  in.seekg(0, std::ios::end);
  const uint64_t fileSize = static_cast<uint64_t>(in.tellg());
  uint64_t position = 0;
  while (position + 8 <= fileSize) {
    uint8_t header[16]{};
    in.seekg(static_cast<std::streamoff>(position));
    if (!in.read(reinterpret_cast<char*>(header), 8)) return std::nullopt;
    uint64_t size = ReadBigEndian32(header);
    uint64_t headerSize = 8;
    if (size == 1) {
      if (!in.read(reinterpret_cast<char*>(header + 8), 8)) return std::nullopt;
      size = (uint64_t{ReadBigEndian32(header + 8)} << 32) | ReadBigEndian32(header + 12);
      headerSize = 16;
    } else if (size == 0) {
      size = fileSize - position;
    }
    if (size < headerSize || position + size > fileSize) return std::nullopt;

    if (std::memcmp(header + 4, "uuid", 4) == 0 && size >= headerSize + kMetadataUuid.size()) {
      std::array<uint8_t, 16> uuid{};
      in.seekg(static_cast<std::streamoff>(position + headerSize));
      if (in.read(reinterpret_cast<char*>(uuid.data()), uuid.size()) && uuid == kMetadataUuid) {
        std::string json(static_cast<size_t>(size - headerSize - uuid.size()), '\0');
        if (in.read(json.data(), static_cast<std::streamsize>(json.size()))) return json;
        return std::nullopt;
      }
    }
    position += size;
  }
  return std::nullopt;
}

}  // namespace clip
