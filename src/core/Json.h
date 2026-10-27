#pragma once

#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace clip {

// A small read-only JSON value, enough for what games send over their local APIs.
// Looking up a missing key or index gives a null value, so lookups can be chained
// (state["player"]["match_stats"]["kills"]) without checking every step.
class Json {
 public:
  enum class Type { Null, Bool, Number, String, Array, Object };

  static std::optional<Json> Parse(std::string_view text);

  Type GetType() const { return type_; }
  bool IsNull() const { return type_ == Type::Null; }
  bool IsObject() const { return type_ == Type::Object; }
  bool IsArray() const { return type_ == Type::Array; }
  bool IsString() const { return type_ == Type::String; }

  const Json& operator[](std::string_view key) const;
  const Json& operator[](size_t index) const;
  bool Has(std::string_view key) const;

  // Games aren't consistent about types ("Stolen": "True", "kills": "3"), so these convert
  // between strings, numbers and booleans where it makes sense.
  double Number(double fallback = 0) const;
  int Int(int fallback = 0) const;
  bool Bool(bool fallback = false) const;
  std::string Str() const;

  const std::vector<Json>& Items() const { return items_; }
  const std::vector<std::pair<std::string, Json>>& Members() const { return members_; }

 private:
  friend class JsonParser;

  Type type_ = Type::Null;
  bool bool_ = false;
  double number_ = 0;
  std::string string_;
  std::vector<Json> items_;
  std::vector<std::pair<std::string, Json>> members_;
};

}  // namespace clip
