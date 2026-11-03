#include "media/ClipMetadata.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <format>
#include <fstream>
#include <sstream>

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

// Just enough JSON to read clip metadata back: objects, arrays, strings, numbers, true, false, null.
class JsonReader {
 public:
  explicit JsonReader(std::string_view text) : s_(text) {}

  bool Done() {
    SkipSpace();
    return i_ == s_.size();
  }

  // Calls onField(name) for each field of an object; onField reads the value or skips it.
  template <class Fn>
  bool Object(Fn&& onField) {
    if (!Eat('{')) return false;
    if (Eat('}')) return true;
    do {
      std::string name;
      if (!String(name) || !Eat(':') || !onField(name)) return false;
    } while (Eat(','));
    return Eat('}');
  }

  template <class Fn>
  bool Array(Fn&& onItem) {
    if (!Eat('[')) return false;
    if (Eat(']')) return true;
    do {
      if (!onItem()) return false;
    } while (Eat(','));
    return Eat(']');
  }

  bool String(std::string& out) {
    if (!Eat('"')) return false;
    out.clear();
    while (i_ < s_.size()) {
      const char c = s_[i_++];
      if (c == '"') return true;
      if (c != '\\') {
        out += c;
        continue;
      }
      if (i_ >= s_.size()) return false;
      const char e = s_[i_++];
      switch (e) {
        case 'n': out += '\n'; break;
        case 'r': out += '\r'; break;
        case 't': out += '\t'; break;
        case 'b': out += '\b'; break;
        case 'f': out += '\f'; break;
        case 'u': {
          if (i_ + 4 > s_.size()) return false;
          unsigned code = 0;
          for (int k = 0; k < 4; ++k) {
            const char h = s_[i_++];
            code <<= 4;
            if (h >= '0' && h <= '9') code |= h - '0';
            else if (h >= 'a' && h <= 'f') code |= h - 'a' + 10;
            else if (h >= 'A' && h <= 'F') code |= h - 'A' + 10;
            else return false;
          }
          // UTF-8; a surrogate pair half comes out as its own (invalid) code point, which only
          // matters for text read off the screen.
          if (code < 0x80) {
            out += static_cast<char>(code);
          } else if (code < 0x800) {
            out += static_cast<char>(0xC0 | (code >> 6));
            out += static_cast<char>(0x80 | (code & 0x3F));
          } else {
            out += static_cast<char>(0xE0 | (code >> 12));
            out += static_cast<char>(0x80 | ((code >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (code & 0x3F));
          }
          break;
        }
        default: out += e;  // \" \\ \/
      }
    }
    return false;
  }

  bool Number(double& out) {
    SkipSpace();
    const size_t begin = i_;
    while (i_ < s_.size() && std::string_view("+-0123456789.eE").find(s_[i_]) != std::string_view::npos) ++i_;
    if (i_ == begin) return false;
    try {
      out = std::stod(std::string(s_.substr(begin, i_ - begin)));
    } catch (const std::exception&) {
      return false;
    }
    return true;
  }

  bool Bool(bool& out) {
    SkipSpace();
    for (const auto& [word, value] : {std::pair{std::string_view("true"), true}, std::pair{std::string_view("false"), false}}) {
      if (s_.substr(i_, word.size()) == word) {
        i_ += word.size();
        out = value;
        return true;
      }
    }
    return false;
  }

  bool Skip() {
    SkipSpace();
    if (i_ >= s_.size()) return false;
    switch (s_[i_]) {
      case '{': return Object([&](const std::string&) { return Skip(); });
      case '[': return Array([&] { return Skip(); });
      case '"': {
        std::string ignored;
        return String(ignored);
      }
      default: {
        for (std::string_view word : {"true", "false", "null"}) {
          if (s_.substr(i_, word.size()) == word) {
            i_ += word.size();
            return true;
          }
        }
        double ignored = 0;
        return Number(ignored);
      }
    }
  }

 private:
  void SkipSpace() {
    while (i_ < s_.size() && (s_[i_] == ' ' || s_[i_] == '\n' || s_[i_] == '\r' || s_[i_] == '\t')) ++i_;
  }

  bool Eat(char c) {
    SkipSpace();
    if (i_ < s_.size() && s_[i_] == c) {
      ++i_;
      return true;
    }
    return false;
  }

  std::string_view s_;
  size_t i_ = 0;
};

}  // namespace

std::string ToJson(const ClipMetadata& metadata) {
  std::string json = std::format("{{\n  \"version\": 2,\n  \"game\": \"{}\",\n  \"trigger\": \"{}\",\n  \"seconds\": {:.2f},\n",
                                 Escape(metadata.game), Escape(metadata.trigger), metadata.seconds);
  if (!metadata.session.empty()) {
    json += std::format("  \"session\": \"{}\",\n  \"session_seconds\": {:.2f},\n  \"match\": {},\n",
                        Escape(metadata.session), metadata.sessionSeconds, metadata.match);
  }
  if (metadata.utcStartMs > 0) json += std::format("  \"utc_start\": {},\n", metadata.utcStartMs);
  if (!metadata.squadMoment.empty()) json += std::format("  \"squad_moment\": \"{}\",\n", Escape(metadata.squadMoment));
  json += std::format("  \"title\": \"{}\",\n  \"events\": [", Escape(Summarize(metadata.markers)));
  for (size_t i = 0; i < metadata.markers.size(); ++i) {
    const auto& m = metadata.markers[i];
    json += std::format("{}\n    {{ \"type\": \"{}\", \"time\": {:.2f}, \"text\": \"{}\"", i == 0 ? "" : ",",
                        EventName(m.type), m.seconds, Escape(m.text));
    if (!m.witnesses.empty()) {
      json += ", \"found_by\": [";
      for (size_t k = 0; k < m.witnesses.size(); ++k) {
        const auto& w = m.witnesses[k];
        json += std::format("{}\n      {{ \"rule\": \"{}\", \"text\": \"{}\", \"offset\": {:.2f}, \"counted\": {}, "
                            "\"box\": [{:.4f}, {:.4f}, {:.4f}, {:.4f}] }}",
                            k == 0 ? "" : ",", Escape(w.rule), Escape(w.text), w.offset, w.counted ? "true" : "false",
                            w.left, w.top, w.width, w.height);
      }
      json += "\n    ]";
    }
    json += " }";
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

std::optional<ClipMetadata> ParseMetadataJson(std::string_view json) {
  ClipMetadata m;
  JsonReader r(json);
  const bool ok = r.Object([&](const std::string& field) {
    double number = 0;
    if (field == "game") return r.String(m.game);
    if (field == "trigger") return r.String(m.trigger);
    if (field == "session") return r.String(m.session);
    if (field == "seconds") return r.Number(m.seconds);
    if (field == "session_seconds") return r.Number(m.sessionSeconds);
    if (field == "squad_moment") return r.String(m.squadMoment);
    if (field == "utc_start") {
      if (!r.Number(number)) return false;
      m.utcStartMs = number > 0 ? static_cast<int64_t>(number) : 0;
      return true;
    }
    if (field == "match") {
      if (!r.Number(number)) return false;
      m.match = static_cast<int>(number);
      return true;
    }
    if (field != "events") return r.Skip();
    return r.Array([&] {
      ClipMetadata::Marker marker{EventType::Highlight, 0, {}};
      std::string type;
      if (!r.Object([&](const std::string& name) {
            if (name == "type") return r.String(type);
            if (name == "time") return r.Number(marker.seconds);
            if (name == "text") return r.String(marker.text);
            if (name == "found_by") {
              return r.Array([&] {
                Witness w;
                if (!r.Object([&](const std::string& key) {
                      if (key == "rule") return r.String(w.rule);
                      if (key == "text") return r.String(w.text);
                      if (key == "offset") return r.Number(w.offset);
                      if (key == "counted") return r.Bool(w.counted);
                      if (key == "box") {
                        double v[4]{};
                        size_t n = 0;
                        if (!r.Array([&] { return n < 4 ? r.Number(v[n++]) : r.Skip(); })) return false;
                        w.left = v[0], w.top = v[1], w.width = v[2], w.height = v[3];
                        return true;
                      }
                      return r.Skip();
                    })) {
                  return false;
                }
                marker.witnesses.push_back(std::move(w));
                return true;
              });
            }
            return r.Skip();
          })) {
        return false;
      }
      marker.type = ParseEventType(type).value_or(EventType::Highlight);
      m.markers.push_back(std::move(marker));
      return true;
    });
  });
  if (!ok || !r.Done()) return std::nullopt;
  return m;
}

ClipMetadata Trimmed(const ClipMetadata& metadata, double from, double to) {
  ClipMetadata out = metadata;
  out.seconds = std::max(0.0, to - from);
  if (out.sessionSeconds >= 0) out.sessionSeconds += from;
  if (out.utcStartMs > 0) out.utcStartMs += static_cast<int64_t>(from * 1000 + 0.5);
  out.markers.clear();
  for (const auto& m : metadata.markers) {
    if (m.seconds >= from && m.seconds <= to) out.markers.push_back({m.type, m.seconds - from, m.text, m.witnesses});
  }
  return out;
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

std::optional<std::string> ReadClipMetadataJson(const std::filesystem::path& mp4) {
  // The .json wins, like in the desktop app, since that's the copy the app edits.
  auto sidecar = mp4;
  sidecar.replace_extension(".json");
  if (std::ifstream in(sidecar, std::ios::binary); in) {
    std::stringstream text;
    text << in.rdbuf();
    return text.str();
  }
  return ReadMetadataBox(mp4);
}

}  // namespace clip
