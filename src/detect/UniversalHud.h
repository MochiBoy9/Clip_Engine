#pragma once

#include "detect/DetectionRules.h"
#include "detect/HudModel.h"
#include "detect/Scoreboard.h"

#include <filesystem>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// The universal HUD reader: for a game nobody has written rules for, the parts of the screen
// where HUDs usually show what happened are read, and HudModel decides what each line means.
// It's built from ordinary detection rules (with classify set), so it's read, counted once and
// shown in the player like any other rule, and costs no more.

// A part of the screen HUDs commonly use, laid out for 16:9 like the shipped rules, and kept to
// its edge or the middle on other screen shapes (PlaceRegion).
struct UniversalZone {
  std::string_view name;
  ScreenRegion region;
  int sampleMs;                     // how often it's read; killfeed lines stay up for seconds
  std::vector<EventType> events;    // the kinds of moment it can show
};

const std::vector<UniversalZone>& UniversalZones();

// The zone a box is most like, by how much they overlap, for a classify rule in detection.ini
// that doesn't say: its zone tells the model what kind of line to expect there.
std::string_view NearestZone(const ScreenRegion& region);

// One rule for each kind of moment in each zone, named <game>.<event>.universal_<zone>. A moment
// shown in two places (the killfeed and a popup) counts once: the rules merge (merge_ms), and a
// killfeed line, which names who died, says how many there were.
std::vector<DetectionRule> UniversalRules(const std::string& game, std::shared_ptr<const HudModel> model);

// Gives the game's classify rules from detection.ini the model, with their zone filled in.
void AttachHudModel(std::vector<DetectionRule>& rules, const std::shared_ptr<const HudModel>& model);

// A scoreboard rule that finds the common column headers (K, KILLS, ELIMS, D, DEATHS, A,
// ASSISTS...) anywhere in the middle of the screen, for a game without one of its own.
ScoreboardRule UniversalScoreboard(const std::string& game);

// Where what the model has learned is kept, next to clip_engine.ini.
inline constexpr std::string_view kHudLearnedFile = "hud_learned.txt";

// The model for a game: the built-in examples, then everything in hud_learned.txt (that game's
// own corrections counting most), and the player's names.
std::shared_ptr<const HudModel> LoadHudModel(const std::filesystem::path& learnedFile, const std::string& game,
                                             const std::vector<std::string>& playerNames);

// Adds examples to hud_learned.txt, skipping any already there. Returns how many were new.
int AppendHudExamples(const std::filesystem::path& learnedFile, const std::vector<HudExample>& examples);

}  // namespace clip
