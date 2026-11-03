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

std::optional<SquadPresence> ParseSquadPresence(std::string_view line) {
  std::string_view rest = Trim(line);
  if (NextWord(rest) != "squad-presence") return std::nullopt;
  const auto playing = ParseInt(NextWord(rest));
  if (!playing || *playing < 0 || *playing > 64) return std::nullopt;
  SquadPresence presence;
  if (*playing == 0) return presence;
  const auto max = ParseInt(NextWord(rest));
  presence.partyId = std::string(NextWord(rest));
  if (!max || *max < *playing || *max > 64 || !IsSquadMomentId(presence.partyId)) return std::nullopt;
  presence.playing = static_cast<int>(*playing);
  presence.max = static_cast<int>(*max);
  const std::string_view url = NextWord(rest);
  const bool safe = url.size() <= 256 && std::ranges::none_of(url, [](char c) { return c == '"' || c == '\\' || static_cast<unsigned char>(c) < 0x21; });
  if (url.starts_with("https://") && safe) presence.joinUrl = std::string(url);
  return presence;
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

std::optional<MontagePart> ParseMontagePart(std::string_view spec) {
  const size_t second = spec.rfind('|');
  if (second == std::string_view::npos || second == 0) return std::nullopt;
  const size_t first = spec.rfind('|', second - 1);
  if (first == std::string_view::npos || first == 0) return std::nullopt;
  auto number = [](std::string_view s) -> std::optional<double> {
    s = Trim(s);
    if (s.empty()) return std::nullopt;
    try {
      size_t used = 0;
      const double v = std::stod(std::string(s), &used);
      return used == s.size() ? std::optional(v) : std::nullopt;
    } catch (const std::exception&) {
      return std::nullopt;
    }
  };
  const auto from = number(spec.substr(first + 1, second - first - 1));
  const auto to = number(spec.substr(second + 1));
  if (!from || !to || *from < 0 || *to - *from < 0.1 || *to - *from > 600) return std::nullopt;
  return MontagePart{std::string(spec.substr(0, first)), *from, *to};
}

ClipMetadata MontageMetadata(const std::vector<MontagePart>& parts, const std::vector<std::optional<ClipMetadata>>& metadata) {
  ClipMetadata out;
  out.trigger = "squad_highlight";
  double at = 0;
  for (size_t i = 0; i < parts.size(); ++i) {
    const auto& part = parts[i];
    const auto& m = i < metadata.size() ? metadata[i] : std::nullopt;
    if (m) {
      if (out.game.empty()) out.game = m->game;
      for (const auto& marker : Trimmed(*m, part.from, part.to).markers) {
        out.markers.push_back({marker.type, marker.seconds + at, marker.text, marker.witnesses});
      }
    }
    at += part.to - part.from;
  }
  out.seconds = at;
  return out;
}

}  // namespace clip
