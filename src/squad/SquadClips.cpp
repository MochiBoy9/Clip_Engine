#include "squad/SquadClips.h"

#include <charconv>

namespace clip {
namespace {

constexpr int64_t kMaxWindowMs = 120'000;
constexpr size_t kRemembered = 256;

std::string_view NextWord(std::string_view& rest) {
  while (!rest.empty() && (rest.front() == ' ' || rest.front() == '\t')) rest.remove_prefix(1);
  size_t end = 0;
  while (end < rest.size() && rest[end] != ' ' && rest[end] != '\t') ++end;
  const std::string_view word = rest.substr(0, end);
  rest.remove_prefix(end);
  return word;
}

std::optional<int64_t> ParseInt(std::string_view s) {
  int64_t value = 0;
  const auto [end, error] = std::from_chars(s.data(), s.data() + s.size(), value);
  if (error != std::errc() || end != s.data() + s.size() || s.empty()) return std::nullopt;
  return value;
}

std::string_view Trim(std::string_view s) {
  while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.remove_prefix(1);
  while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\r' || s.back() == '\n')) s.remove_suffix(1);
  return s;
}

}  // namespace

bool IsSquadMomentId(std::string_view id) {
  if (id.empty() || id.size() > 64) return false;
  return std::ranges::all_of(id, [](char c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
  });
}

std::optional<SquadSaveRequest> ParseSquadSave(std::string_view line) {
  std::string_view rest = Trim(line);
  if (NextWord(rest) != "squad-save") return std::nullopt;
  SquadSaveRequest request;
  request.moment = std::string(NextWord(rest));
  const auto start = ParseInt(NextWord(rest));
  const auto end = ParseInt(NextWord(rest));
  if (!IsSquadMomentId(request.moment) || !start || !end) return std::nullopt;
  if (*end <= *start || *end - *start > kMaxWindowMs) return std::nullopt;
  request.startUnixMs = *start;
  request.endUnixMs = *end;
  request.game = std::string(Trim(rest));
  return request;
}

bool SquadSaveQueue::Add(Window window) {
  if (std::ranges::find(seen_, window.moment) != seen_.end()) {
    // The same moment again (the app retrying after a reconnect): widen the waiting window to
    // cover both, but never save it twice.
    for (auto& w : pending_) {
      if (w.moment == window.moment) {
        w.start = std::min(w.start, window.start);
        w.end = std::max(w.end, window.end);
      }
    }
    return false;
  }
  seen_.push_back(window.moment);
  if (seen_.size() > kRemembered) seen_.erase(seen_.begin());
  pending_.push_back(std::move(window));
  return true;
}

std::vector<SquadSaveQueue::Window> SquadSaveQueue::TakeReady(int64_t now) {
  std::vector<Window> ready;
  std::erase_if(pending_, [&](Window& w) {
    if (w.end > now) return false;
    ready.push_back(std::move(w));
    return true;
  });
  return ready;
}

std::vector<SquadSaveQueue::Window> SquadSaveQueue::TakeAll() {
  auto all = std::move(pending_);
  pending_.clear();
  return all;
}

}  // namespace clip
