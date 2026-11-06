#include "app/Timeline.h"

#include "core/Json.h"

#include <algorithm>
#include <format>

namespace clip {

std::string TimelineHeader(const std::string& session, const std::string& game) {
  return std::format("{{\"session\": {}, \"game\": {}, \"version\": 1}}", JsonString(session), JsonString(game));
}

std::string TimelineEvent(double seconds, int match, const GameEvent& event) {
  return std::format("{{\"t\": {:.2f}, \"match\": {}, \"type\": \"{}\", \"text\": {}}}", seconds, match,
                     EventName(event.type), JsonString(event.text));
}

std::string TimelineScores(double seconds, int match, const Scoreboard& board) {
  std::string stats;
  for (const auto& [name, value] : board.stats) {
    stats += std::format("{}{}: {}", stats.empty() ? "" : ", ", JsonString(name), value);
  }
  return std::format("{{\"t\": {:.2f}, \"match\": {}, \"source\": {}, \"stats\": {{{}}}}}", seconds, match,
                     JsonString(board.source), stats);
}

std::string TimelineNote(double seconds, int match, const JournalNote& note) {
  std::string line = std::format("{{\"t\": {:.2f}, \"match\": {}, \"note\": {}", seconds, match, JsonString(note.note));
  if (!note.value.empty()) line += std::format(", \"value\": {}", JsonString(note.value));
  if (note.amount != 0) line += std::format(", \"amount\": {}", note.amount);
  if (note.xp != 0) line += std::format(", \"xp\": {}", note.xp);
  line += std::format(", \"text\": {}}}", JsonString(note.text));
  return line;
}

std::filesystem::path TimelineFileName(const std::string& session, const std::string& game) {
  std::string name = session + " " + game;
  std::ranges::replace_if(name, [](char c) { return std::string_view("<>:\"/\\|?*").find(c) != std::string_view::npos; }, '-');
  return std::filesystem::path(std::u8string(name.begin(), name.end()) + u8".jsonl");
}

TimelineWriter::TimelineWriter(const std::filesystem::path& folder, const std::string& session, const std::string& game) {
  std::error_code ec;
  std::filesystem::create_directories(folder, ec);
  out_.open(folder / TimelineFileName(session, game), std::ios::binary | std::ios::app);
  Write(TimelineHeader(session, game));
}

void TimelineWriter::Write(const std::string& line) {
  if (!out_) return;
  out_ << line << '\n';
  out_.flush();
}

}  // namespace clip
