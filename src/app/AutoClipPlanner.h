#pragma once

#include "detect/GameEvent.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <cstdint>
#include <deque>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// How much of an automatic clip comes before and after one kind of moment, in seconds; -1
// takes clip_before_seconds or clip_after_seconds.
struct MomentLength {
  int before = -1;
  int after = -1;
  bool operator==(const MomentLength&) const = default;
};
using MomentLengths = std::map<EventType, MomentLength>;

// The defaults: a match end keeps more of the fight that won it and a moment of the result,
// a match start a little of the lead-in and more of the start, and a death less after it.
inline MomentLengths DefaultMomentLengths() {
  return {{EventType::MatchStart, {5, 10}}, {EventType::MatchEnd, {15, 8}}, {EventType::Death, {-1, 3}}};
}

// Reads moment_clip_seconds: "match_end 15/8, death -/3", each moment with its seconds before and
// after, - for the general setting. Nothing when it can't be read.
inline std::optional<MomentLengths> ParseMomentLengths(std::string_view text) {
  MomentLengths lengths;
  auto trim = [](std::string_view s) {
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.front()))) s.remove_prefix(1);
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.back()))) s.remove_suffix(1);
    return s;
  };
  auto seconds = [&](std::string_view s, int& out) {
    s = trim(s);
    if (s == "-") return true;
    const auto [ptr, ec] = std::from_chars(s.data(), s.data() + s.size(), out);
    return ec == std::errc() && ptr == s.data() + s.size() && out >= 0 && out <= 120;
  };
  while (!trim(text).empty()) {
    const auto comma = text.find(',');
    const std::string_view entry = trim(text.substr(0, comma));
    text = comma == std::string_view::npos ? std::string_view() : text.substr(comma + 1);
    if (entry.empty()) continue;
    const auto space = entry.find_first_of(" \t");
    const auto slash = entry.find('/');
    if (space == std::string_view::npos || slash == std::string_view::npos || slash < space) return std::nullopt;
    std::string name(entry.substr(0, space));
    std::ranges::transform(name, name.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    const auto type = ParseEventType(name);
    MomentLength length;
    if (!type || !seconds(entry.substr(space, slash - space), length.before) ||
        !seconds(entry.substr(slash + 1), length.after)) {
      return std::nullopt;
    }
    lengths[*type] = length;
  }
  return lengths;
}

// Decides which stretches of gameplay to save as automatic clips. Each event asks for a
// window from a little before it to a little after; events close together share one clip,
// so a triple kill is one clip instead of three overlapping ones.
class AutoClipPlanner {
 public:
  struct Window {
    int64_t start = 0;
    int64_t end = 0;
  };

  AutoClipPlanner(int64_t before, int64_t after, int64_t maxLength)
      : before_(before), after_(after), maxLength_(std::max(maxLength, before + after)) {}

  void Add(int64_t eventTime) { Add(eventTime, before_, after_); }

  // A moment with its own lengths. alone false: it doesn't make a clip of its own, only joins
  // one it overlaps (a death in a fight is kept with the fight, a death on its own isn't).
  // Moments can arrive a little out of order (some wait a second for other rules to agree), so
  // an earlier one widens the clip back to cover it.
  void Add(int64_t eventTime, int64_t before, int64_t after, bool alone = true) {
    const int64_t start = eventTime - before;
    const int64_t end = eventTime + after;
    if (!pending_.empty() && start <= pending_.back().end && end >= pending_.back().start) {
      Window& last = pending_.back();
      const int64_t joinedStart = std::min(start, last.start);
      const int64_t joinedEnd = std::max(end, last.end);
      if (joinedEnd - joinedStart <= maxLength_) {
        last.start = joinedStart;
        last.end = joinedEnd;
        return;
      }
      if (end <= last.end) {
        // Too long to reach all the way back: as far as a clip can.
        last.start = std::min(last.start, std::max(start, last.end - maxLength_));
        return;
      }
      // Too long for one clip: carry on in a new one that starts where the last ends.
      if (alone) pending_.push_back({last.end, end});
      return;
    }
    if (!alone) return;
    // Before the last one entirely (late, and out of order): keep the windows in time order.
    auto at = std::ranges::upper_bound(pending_, start, {}, &Window::start);
    pending_.insert(at, {start, end});
  }

  // Windows that have fully happened by now.
  std::vector<Window> TakeReady(int64_t now) {
    std::vector<Window> ready;
    while (!pending_.empty() && pending_.front().end <= now) {
      ready.push_back(pending_.front());
      pending_.pop_front();
    }
    return ready;
  }

  // Everything still waiting, for when recording stops early.
  std::vector<Window> TakeAll() {
    std::vector<Window> all(pending_.begin(), pending_.end());
    pending_.clear();
    return all;
  }

  bool Empty() const { return pending_.empty(); }

 private:
  int64_t before_;
  int64_t after_;
  int64_t maxLength_;
  std::deque<Window> pending_;
};

}  // namespace clip
