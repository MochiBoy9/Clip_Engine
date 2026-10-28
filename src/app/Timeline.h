#pragma once

#include "detect/GameEvent.h"
#include "detect/Scoreboard.h"

#include <filesystem>
#include <fstream>
#include <string>

namespace clip {

// Everything one recording saw, as JSON lines in <data folder>\timeline\: every moment detected
// and every change to the player's totals, whether or not a clip was saved around it. The
// desktop app builds the player's records from these (ClipEngine.Desktop/Services/PlayerRecords.cs),
// so keep the format in step with it. Each line is written and flushed as it happens, so a crash
// loses nothing.
//
//   {"session": "2026-10-27T20:41:05", "game": "Valorant", "version": 1}
//   {"t": 312.5, "match": 1, "type": "kill", "text": "Enterro ELIMINATED Bot"}
//   {"t": 340.0, "match": 1, "source": "screen", "stats": {"kills": 12, "deaths": 3, "assists": 4}}
//
// t is seconds from the start of the recording; match counts the match starts so far, as in
// clip metadata.
std::string TimelineHeader(const std::string& session, const std::string& game);
std::string TimelineEvent(double seconds, int match, const GameEvent& event);
std::string TimelineScores(double seconds, int match, const Scoreboard& board);

// "2026-10-27T20-41-05 Valorant.jsonl": the session and game, safe as a file name.
std::filesystem::path TimelineFileName(const std::string& session, const std::string& game);

class TimelineWriter {
 public:
  TimelineWriter(const std::filesystem::path& folder, const std::string& session, const std::string& game);

  void Write(const std::string& line);
  bool Ok() const { return static_cast<bool>(out_); }

 private:
  std::ofstream out_;
};

}  // namespace clip
