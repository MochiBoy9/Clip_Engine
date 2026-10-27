#include "core/Json.h"

#include <charconv>
#include <cmath>
#include <cstdint>

namespace clip {

class JsonParser {
 public:
  explicit JsonParser(std::string_view text) : text_(text) {}

  std::optional<Json> ParseDocument() {
    Json value;
    if (!ParseValue(value, 0)) return std::nullopt;
    SkipSpace();
    if (pos_ != text_.size()) return std::nullopt;
    return value;
  }

 private:
  static constexpr int kMaxDepth = 64;

  void SkipSpace() {
    while (pos_ < text_.size() && (text_[pos_] == ' ' || text_[pos_] == '\t' || text_[pos_] == '\n' || text_[pos_] == '\r')) {
      ++pos_;
    }
  }

  bool Literal(std::string_view word) {
    if (text_.substr(pos_, word.size()) != word) return false;
    pos_ += word.size();
    return true;
  }

  bool ParseValue(Json& out, int depth) {
    if (depth > kMaxDepth) return false;
    SkipSpace();
    if (pos_ >= text_.size()) return false;
    switch (text_[pos_]) {
      case '{': return ParseObject(out, depth);
      case '[': return ParseArray(out, depth);
      case '"':
        out.type_ = Json::Type::String;
        return ParseString(out.string_);
      case 't':
        out.type_ = Json::Type::Bool;
        out.bool_ = true;
        return Literal("true");
      case 'f':
        out.type_ = Json::Type::Bool;
        return Literal("false");
      case 'n': return Literal("null");
      default: return ParseNumber(out);
    }
  }

  bool ParseObject(Json& out, int depth) {
    out.type_ = Json::Type::Object;
    ++pos_;
    SkipSpace();
    if (pos_ < text_.size() && text_[pos_] == '}') {
      ++pos_;
      return true;
    }
    while (true) {
      SkipSpace();
      std::string key;
      if (pos_ >= text_.size() || text_[pos_] != '"' || !ParseString(key)) return false;
      SkipSpace();
      if (pos_ >= text_.size() || text_[pos_] != ':') return false;
      ++pos_;
      Json value;
      if (!ParseValue(value, depth + 1)) return false;
      out.members_.emplace_back(std::move(key), std::move(value));
      SkipSpace();
      if (pos_ >= text_.size()) return false;
      if (text_[pos_] == ',') {
        ++pos_;
        continue;
      }
      if (text_[pos_] != '}') return false;
      ++pos_;
      return true;
    }
  }

  bool ParseArray(Json& out, int depth) {
    out.type_ = Json::Type::Array;
    ++pos_;
    SkipSpace();
    if (pos_ < text_.size() && text_[pos_] == ']') {
      ++pos_;
      return true;
    }
    while (true) {
      Json value;
      if (!ParseValue(value, depth + 1)) return false;
      out.items_.push_back(std::move(value));
      SkipSpace();
      if (pos_ >= text_.size()) return false;
      if (text_[pos_] == ',') {
        ++pos_;
        continue;
      }
      if (text_[pos_] != ']') return false;
      ++pos_;
      return true;
    }
  }

  static void AppendUtf8(std::string& out, uint32_t cp) {
    if (cp < 0x80) {
      out += static_cast<char>(cp);
    } else if (cp < 0x800) {
      out += static_cast<char>(0xC0 | (cp >> 6));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
      out += static_cast<char>(0xE0 | (cp >> 12));
      out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
      out += static_cast<char>(0xF0 | (cp >> 18));
      out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
      out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    }
  }

  bool ParseHex4(uint32_t& out) {
    if (pos_ + 4 > text_.size()) return false;
    const auto [ptr, ec] = std::from_chars(text_.data() + pos_, text_.data() + pos_ + 4, out, 16);
    if (ec != std::errc() || ptr != text_.data() + pos_ + 4) return false;
    pos_ += 4;
    return true;
  }

  bool ParseString(std::string& out) {
    ++pos_;  // opening quote
    while (pos_ < text_.size()) {
      const char c = text_[pos_++];
      if (c == '"') return true;
      if (c != '\\') {
        out += c;
        continue;
      }
      if (pos_ >= text_.size()) return false;
      const char e = text_[pos_++];
      switch (e) {
        case '"': out += '"'; break;
        case '\\': out += '\\'; break;
        case '/': out += '/'; break;
        case 'b': out += '\b'; break;
        case 'f': out += '\f'; break;
        case 'n': out += '\n'; break;
        case 'r': out += '\r'; break;
        case 't': out += '\t'; break;
        case 'u': {
          uint32_t cp = 0;
          if (!ParseHex4(cp)) return false;
          // A surrogate pair encodes one character outside the basic plane.
          if (cp >= 0xD800 && cp <= 0xDBFF && text_.substr(pos_, 2) == "\\u") {
            pos_ += 2;
            uint32_t low = 0;
            if (!ParseHex4(low)) return false;
            cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
          }
          AppendUtf8(out, cp);
          break;
        }
        default: return false;
      }
    }
    return false;
  }

  bool ParseNumber(Json& out) {
    const size_t start = pos_;
    while (pos_ < text_.size() && std::string_view("+-0123456789.eE").find(text_[pos_]) != std::string_view::npos) ++pos_;
    if (pos_ == start) return false;
    double value = 0;
    const auto [ptr, ec] = std::from_chars(text_.data() + start, text_.data() + pos_, value);
    if (ec != std::errc() || ptr != text_.data() + pos_) return false;
    out.type_ = Json::Type::Number;
    out.number_ = value;
    return true;
  }

  std::string_view text_;
  size_t pos_ = 0;
};

std::optional<Json> Json::Parse(std::string_view text) { return JsonParser(text).ParseDocument(); }

namespace {
const Json& Null() {
  static const Json null;
  return null;
}
}  // namespace

const Json& Json::operator[](std::string_view key) const {
  for (const auto& [name, value] : members_) {
    if (name == key) return value;
  }
  return Null();
}

const Json& Json::operator[](size_t index) const { return index < items_.size() ? items_[index] : Null(); }

bool Json::Has(std::string_view key) const {
  for (const auto& member : members_) {
    if (member.first == key) return true;
  }
  return false;
}

double Json::Number(double fallback) const {
  if (type_ == Type::Number) return number_;
  if (type_ == Type::Bool) return bool_ ? 1 : 0;
  if (type_ == Type::String) {
    double value = 0;
    const auto [ptr, ec] = std::from_chars(string_.data(), string_.data() + string_.size(), value);
    if (ec == std::errc() && ptr == string_.data() + string_.size()) return value;
  }
  return fallback;
}

int Json::Int(int fallback) const {
  const double value = Number(std::nan(""));
  return std::isnan(value) ? fallback : static_cast<int>(std::lround(value));
}

bool Json::Bool(bool fallback) const {
  if (type_ == Type::Bool) return bool_;
  if (type_ == Type::Number) return number_ != 0;
  if (type_ == Type::String) {
    if (string_ == "true" || string_ == "True" || string_ == "TRUE" || string_ == "1") return true;
    if (string_ == "false" || string_ == "False" || string_ == "FALSE" || string_ == "0") return false;
  }
  return fallback;
}

std::string Json::Str() const {
  if (type_ == Type::String) return string_;
  if (type_ == Type::Number) {
    if (number_ == std::floor(number_) && std::abs(number_) < 1e15) return std::to_string(static_cast<long long>(number_));
    return std::to_string(number_);
  }
  if (type_ == Type::Bool) return bool_ ? "true" : "false";
  return {};
}

}  // namespace clip
