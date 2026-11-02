#include "detect/UniversalHud.h"

#include <algorithm>
#include <format>
#include <fstream>
#include <set>

namespace clip {

const std::vector<UniversalZone>& UniversalZones() {
  using enum EventType;
  static const std::vector<UniversalZone> zones = {
      // Killfeeds, by the corners, and the chat-and-feed area low on the left.
      {"feed_right", {0.66, 0.02, 0.33, 0.26}, 500, {Kill, Death, Assist, Revive}},
      {"feed_left", {0.01, 0.02, 0.30, 0.26}, 750, {Kill, Death, Assist, Revive}},
      {"feed_bottom", {0.01, 0.50, 0.30, 0.30}, 750, {Kill, Death, Revive}},
      // Kill popups and score lines under the crosshair.
      {"center", {0.30, 0.50, 0.40, 0.26}, 400, {Kill, Assist, Revive, Death, Highlight}},
      // The big banner: death screens, round and match results, medals.
      {"banner", {0.20, 0.18, 0.60, 0.34}, 1000, {Death, MatchStart, MatchEnd, Highlight}},
      // Round state along the top: round starts, kill cams, spectating.
      {"top", {0.33, 0.00, 0.34, 0.12}, 1000, {MatchStart, MatchEnd, Death}},
      // Messages over the ability bar ("YOU KILLED <name> WITH <gun>").
      {"bottom", {0.28, 0.76, 0.44, 0.16}, 600, {Kill, Death, Revive}},
  };
  return zones;
}

std::string_view NearestZone(const ScreenRegion& region) {
  std::string_view best = "center";
  double bestOverlap = 0;
  for (const auto& zone : UniversalZones()) {
    const ScreenRegion& z = zone.region;
    const double w = std::min(region.left + region.width, z.left + z.width) - std::max(region.left, z.left);
    const double h = std::min(region.top + region.height, z.top + z.height) - std::max(region.top, z.top);
    if (w <= 0 || h <= 0) continue;
    // As a share of the smaller box, so a small box well inside a zone is in it.
    const double overlap = w * h / std::min(region.width * region.height, z.width * z.height);
    if (overlap > bestOverlap) {
      bestOverlap = overlap;
      best = zone.name;
    }
  }
  return best;
}

std::vector<DetectionRule> UniversalRules(const std::string& game, std::shared_ptr<const HudModel> model) {
  std::vector<DetectionRule> rules;
  for (const auto& zone : UniversalZones()) {
    const bool feed = zone.name.starts_with("feed");
    for (EventType event : zone.events) {
      DetectionRule& rule = rules.emplace_back();
      rule.name = std::format("{}.{}.universal_{}", game, EventName(event), zone.name);
      rule.game = game;
      rule.event = event;
      rule.region = zone.region;
      rule.classify = true;
      rule.hudZone = std::string(zone.name);
      rule.hudModel = model;
      rule.sampleMs = zone.sampleMs;
      rule.sampleMsSet = true;
      switch (event) {
        case EventType::Kill:
        case EventType::Assist:
        case EventType::Revive:
          // Each line is told apart by the names in it, so a quick double kill is two.
          rule.cooldownMs = 0;
          rule.mergeMs = 4000;
          break;
        case EventType::Death:
          rule.cooldownMs = 3000;
          rule.mergeMs = 6000;
          break;
        case EventType::MatchStart:
        case EventType::MatchEnd:
          rule.cooldownMs = 10000;
          rule.mergeMs = 15000;
          break;
        default:
          rule.cooldownMs = 1500;
          rule.mergeMs = 3000;
          break;
      }
      // A killfeed line names who died, so it knows best how many there were.
      rule.priority = feed ? 2 : 1;
    }
  }
  return rules;
}

void AttachHudModel(std::vector<DetectionRule>& rules, const std::shared_ptr<const HudModel>& model) {
  for (auto& rule : rules) {
    if (!rule.classify) continue;
    rule.hudModel = model;
    if (rule.hudZone.empty()) rule.hudZone = std::string(NearestZone(rule.region));
  }
}

ScoreboardRule UniversalScoreboard(const std::string& game) {
  ScoreboardRule board;
  board.name = game + ".scoreboard.universal";
  board.game = game;
  board.region = {0.08, 0.08, 0.84, 0.84};
  board.sampleMs = 1500;
  board.columns = {
      {"kills", {"K", "KILLS", "ELIMS", "ELIMINATIONS", "E", "KO", "KOS", "FRAGS"}},
      {"deaths", {"D", "DEATHS", "DTH"}},
      {"assists", {"A", "ASSISTS", "AST"}},
      {"revives", {"REVIVES", "RES", "REV"}},
      {"score", {"SCORE", "PTS", "POINTS"}},
  };
  return board;
}

std::shared_ptr<const HudModel> LoadHudModel(const std::filesystem::path& learnedFile, const std::string& game,
                                             const std::vector<std::string>& playerNames) {
  std::vector<HudExample> learned;
  if (std::ifstream in(learnedFile); in) learned = ReadHudExamples(in);
  return std::make_shared<const HudModel>(learned, game, playerNames);
}

int AppendHudExamples(const std::filesystem::path& learnedFile, const std::vector<HudExample>& examples) {
  std::set<std::string> known;
  auto line = [](const HudExample& e) {
    return (e.game.empty() ? std::string("*") : e.game) + '\t' + std::string(HudLabelName(e.label)) + '\t' + e.zone + '\t' + e.text;
  };
  bool exists = false;
  if (std::ifstream in(learnedFile); in) {
    exists = true;
    for (const auto& e : ReadHudExamples(in)) known.insert(line(e));
  }
  std::ofstream out(learnedFile, std::ios::app);
  if (!out) return 0;
  if (!exists) {
    out << "# What Clip Engine's universal HUD reader has learned from your corrections in Check detection.\n"
           "# One line per example: game (* for any), what it means, the part of the screen, and the text,\n"
           "# separated by tabs, with {P} for your in-game name. Delete a line to forget it.\n";
  }
  int added = 0;
  for (const auto& example : examples) {
    if (!known.insert(line(example)).second) continue;
    WriteHudExample(out, example);
    ++added;
  }
  return added;
}

}  // namespace clip
