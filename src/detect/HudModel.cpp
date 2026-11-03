#include "detect/HudModel.h"

#include "detect/TextMatcher.h"

#include <algorithm>
#include <charconv>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <map>
#include <set>
#include <sstream>

namespace clip {

std::string_view HudLabelName(HudLabel label) {
  switch (label) {
    case HudLabel::Menu: return "menu";
    case HudLabel::Kill: return "kill";
    case HudLabel::Assist: return "assist";
    case HudLabel::Death: return "death";
    case HudLabel::Revive: return "revive";
    case HudLabel::MatchStart: return "match_start";
    case HudLabel::MatchEnd: return "match_end";
    case HudLabel::Highlight: return "highlight";
    default: return "none";
  }
}

std::optional<HudLabel> ParseHudLabel(std::string_view name) {
  for (int i = 0; i < kHudLabelCount; ++i) {
    const auto label = static_cast<HudLabel>(i);
    if (HudLabelName(label) == name) return label;
  }
  return std::nullopt;
}

std::optional<EventType> HudLabelEvent(HudLabel label) {
  switch (label) {
    case HudLabel::Kill: return EventType::Kill;
    case HudLabel::Assist: return EventType::Assist;
    case HudLabel::Death: return EventType::Death;
    case HudLabel::Revive: return EventType::Revive;
    case HudLabel::MatchStart: return EventType::MatchStart;
    case HudLabel::MatchEnd: return EventType::MatchEnd;
    case HudLabel::Highlight: return EventType::Highlight;
    default: return std::nullopt;
  }
}

HudLabel HudLabelFor(EventType event) {
  switch (event) {
    case EventType::Kill: return HudLabel::Kill;
    case EventType::Assist: return HudLabel::Assist;
    case EventType::Death: return HudLabel::Death;
    case EventType::Revive: return HudLabel::Revive;
    case EventType::MatchStart: return HudLabel::MatchStart;
    case EventType::MatchEnd: return HudLabel::MatchEnd;
    default: return HudLabel::Highlight;
  }
}

namespace {

constexpr std::string_view kPlayerToken = "{P}";
constexpr std::string_view kNameToken = "{N}";
constexpr std::string_view kNumberToken = "#";
constexpr std::string_view kFirstToken = "#1";
// Laplace smoothing for the word counts.
constexpr double kSmoothing = 0.1;

bool IsPlaceholder(std::string_view token) {
  return token == kPlayerToken || token == kNameToken || token == kNumberToken || token == kFirstToken;
}

bool IsNumberish(const std::string& token) { return WordsMatch(token, kNumberWord); }

// An example's text as tokens, keeping {P}, {N} and # as they are.
std::vector<std::string> ExampleTokens(std::string_view text) {
  std::vector<std::string> tokens;
  std::istringstream words{std::string(text)};
  std::string word;
  while (words >> word) {
    std::string upper = word;
    std::ranges::transform(upper, upper.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
    if (IsPlaceholder(upper)) {
      tokens.push_back(upper);
      continue;
    }
    for (auto& token : Tokenize(word)) tokens.push_back(std::move(token));
  }
  return tokens;
}

std::string Trim(std::string_view s) {
  const auto first = s.find_first_not_of(" \t\r\n");
  if (first == std::string_view::npos) return {};
  const auto last = s.find_last_not_of(" \t\r\n");
  return std::string(s.substr(first, last - first + 1));
}

}  // namespace

// The built-in examples: the words games commonly put on screen for each kind of moment, where
// on the screen they show, and plenty of HUD text that means nothing, so the model learns what
// to leave alone. {P} is the player's own name, {N} anyone else's, # a number. Zones are those
// of UniversalZones; an empty zone is anywhere.
std::vector<HudExample> SeedHudExamples() {
  struct Seed {
    HudLabel label;
    const char* zone;
    const char* text;
  };
  using enum HudLabel;
  static const Seed seeds[] = {
      // Killfeeds: the killer first, the one who died last, so the player first is a kill.
      {Kill, "feed_right", "{P} {N}"}, {Kill, "feed_right", "{P} {N}"}, {Kill, "feed_right", "{P} {N}"},
      {Kill, "feed_left", "{P} {N}"}, {Kill, "feed_left", "{P} {N}"}, {Kill, "feed_right", "{N} {P} {N}"},
      {Kill, "feed_right", "{N} {P} {N}"}, {Kill, "feed_left", "{N} {P} {N}"}, {Kill, "feed_right", "{P} # {N}"},
      {Kill, "feed_right", "{P} KILLED {N}"}, {Kill, "feed_right", "{P} ELIMINATED {N}"},
      {Kill, "feed_left", "{P} KILLED {N}"}, {Kill, "feed_left", "{P} ELIMINATED {N}"},
      {Kill, "feed_bottom", "{P} KILLED {N}"}, {Kill, "feed_bottom", "{P} ELIMINATED {N}"},
      {Kill, "feed_bottom", "{P} KNOCKED OUT {N}"}, {Kill, "feed_right", "{P} KNOCKED {N}"},
      {Kill, "feed_right", "{P} SHOT {N}"}, {Kill, "feed_right", "{P} FRAGGED {N}"},
      {Kill, "feed_right", "{P} SLEW {N}"}, {Kill, "feed_right", "{P} DEFEATED {N}"},
      {Kill, "feed_right", "{P} DOWNED {N}"}, {Kill, "feed_left", "{P} DOWNED {N}"},
      {Kill, "feed_bottom", "{P} FINALLY KILLED {N}"}, {Kill, "feed_bottom", "{P} HEADSHOT {N}"},
      // Kill popups and score lines, mostly in the middle of the screen.
      {Kill, "center", "ELIMINATED {N}"}, {Kill, "center", "ELIMINATED {N}"}, {Kill, "center", "YOU ELIMINATED {N}"},
      {Kill, "center", "{N} ELIMINATED"}, {Kill, "center", "ELIMINATION"}, {Kill, "center", "ELIMINATION #"},
      {Kill, "center", "ELIMINATION #"}, {Kill, "center", "ELIMINATION # #"}, {Kill, "center", "KILL"},
      {Kill, "center", "KILL #"}, {Kill, "center", "KILL #"}, {Kill, "center", "# KILL"}, {Kill, "center", "ENEMY KILLED"},
      {Kill, "center", "ENEMY KILLED #"}, {Kill, "center", "ENEMY DOWNED"}, {Kill, "center", "ENEMY DOWNED #"},
      {Kill, "center", "HEADSHOT KILL"}, {Kill, "center", "HEADSHOT KILL #"}, {Kill, "center", "KILLED {N}"},
      {Kill, "center", "YOU KILLED {N}"}, {Kill, "bottom", "YOU KILLED {N}"}, {Kill, "bottom", "YOU KILLED {N} WITH {N}"},
      {Kill, "bottom", "YOU FINALLY KILLED {N}"}, {Kill, "center", "YOU DEFEATED {N}"}, {Kill, "center", "DEFEATED {N}"},
      {Kill, "center", "YOU DOWNED {N}"}, {Kill, "center", "DOWNED {N}"}, {Kill, "center", "KNOCKED DOWN {N}"},
      {Kill, "center", "YOU KNOCKED {N}"}, {Kill, "center", "KNOCKED {N}"}, {Kill, "bottom", "YOU KNOCKED OUT {N}"},
      {Kill, "center", "SLAIN {N}"}, {Kill, "center", "YOU SLEW {N}"}, {Kill, "center", "FRAGGED {N}"},
      {Kill, "center", "YOU FRAGGED {N}"}, {Kill, "center", "KILL CONFIRMED"}, {Kill, "center", "ENEMY ELIMINATED"},
      {Kill, "center", "ENEMY NEUTRALIZED"}, {Kill, "center", "TARGET ELIMINATED"}, {Kill, "center", "ELIMINATED"},
      {Kill, "center", "TAKEDOWN"}, {Kill, "center", "TAKEDOWN {N}"}, {Kill, "center", "FINAL BLOW"},
      {Kill, "center", "FINAL BLOW {N}"}, {Kill, "center", "KILLING BLOW"}, {Kill, "center", "KO {N}"},
      {Kill, "center", "KNOCKOUT"}, {Kill, "center", "FINISHED {N}"}, {Kill, "center", "YOU FINISHED {N}"},
      {Kill, "center", "ELIMINADO {N}"}, {Kill, "center", "ELIMINIERT {N}"}, {Kill, "center", "ELIMINATO {N}"},
      {Kill, "center", "ABATIDO {N}"}, {Kill, "center", "ABATTU {N}"}, {Kill, "", "ELIMINATED {N}"},
      {Kill, "", "YOU KILLED {N}"}, {Kill, "", "KILL #"},
      // Assists.
      {Assist, "center", "ASSIST"}, {Assist, "center", "ASSIST #"}, {Assist, "center", "ASSIST #"},
      {Assist, "center", "KILL ASSIST"}, {Assist, "center", "KILL ASSIST #"}, {Assist, "center", "ASSIST {N}"},
      {Assist, "center", "ASSISTED {N}"}, {Assist, "center", "ASSIST KILL"}, {Assist, "center", "ELIMINATION ASSIST"},
      {Assist, "center", "ELIMINATION ASSIST #"}, {Assist, "center", "ASSIST ELIMINATION"}, {Assist, "center", "ASSISTANCE"},
      {Assist, "center", "DAMAGE ASSIST #"}, {Assist, "center", "SPOT ASSIST #"}, {Assist, "feed_right", "{N} {P} {N} ASSIST"},
      {Assist, "", "ASSIST #"},
      // Deaths: someone else first in a killfeed with the player last, death screens, respawn timers.
      {Death, "feed_right", "{N} {P}"}, {Death, "feed_right", "{N} {P}"}, {Death, "feed_right", "{N} {P}"},
      {Death, "feed_left", "{N} {P}"}, {Death, "feed_left", "{N} {P}"}, {Death, "feed_right", "{N} # {P}"},
      {Death, "feed_right", "{N} KILLED {P}"}, {Death, "feed_right", "{N} ELIMINATED {P}"},
      {Death, "feed_bottom", "{N} KILLED {P}"}, {Death, "feed_bottom", "{N} KNOCKED OUT {P}"},
      {Death, "feed_left", "{N} KILLED {P}"}, {Death, "feed_right", "{N} DOWNED {P}"},
      {Death, "banner", "YOU DIED"}, {Death, "banner", "YOU DIED"}, {Death, "banner", "YOU ARE DEAD"},
      {Death, "banner", "YOU WERE ELIMINATED"}, {Death, "banner", "YOU HAVE BEEN ELIMINATED"},
      {Death, "banner", "YOU WERE KILLED"}, {Death, "banner", "YOU WERE KILLED BY {N}"},
      {Death, "banner", "ELIMINATED BY {N}"}, {Death, "banner", "ELIMINATED BY {N}"}, {Death, "center", "ELIMINATED BY {N}"},
      {Death, "banner", "KILLED BY {N}"}, {Death, "banner", "KILLED BY {N}"}, {Death, "center", "KILLED BY {N}"},
      {Death, "bottom", "KILLED BY {N}"}, {Death, "banner", "DEFEATED BY {N}"}, {Death, "banner", "SLAIN BY {N}"},
      {Death, "banner", "KNOCKED DOWN BY {N}"}, {Death, "banner", "KNOCKED OUT BY {N}"}, {Death, "banner", "DOWNED BY {N}"},
      {Death, "banner", "YOU WERE DOWNED"}, {Death, "banner", "YOU ARE DOWN"}, {Death, "banner", "YOU HAVE BEEN DOWNED"},
      {Death, "banner", "WASTED"}, {Death, "banner", "RESPAWN IN #"}, {Death, "banner", "RESPAWNING IN #"},
      {Death, "bottom", "RESPAWN IN #"}, {Death, "bottom", "RESPAWNING IN #"}, {Death, "banner", "RESPAWN #"},
      {Death, "banner", "REDEPLOY"}, {Death, "banner", "REDEPLOY IN #"}, {Death, "banner", "KILLCAM"},
      {Death, "banner", "KILL CAM"}, {Death, "top", "KILLCAM"}, {Death, "banner", "DEATH RECAP"},
      {Death, "banner", "SPECTATING {N}"}, {Death, "top", "SPECTATING {N}"}, {Death, "banner", "KILLED IN ACTION"},
      {Death, "banner", "YOU WERE SLAIN"}, {Death, "banner", "YOU HAVE FALLEN"}, {Death, "banner", "DEAD"},
      {Death, "banner", "YOU WERE ELIMINATED BY {N}"}, {Death, "", "ELIMINATED BY {N}"}, {Death, "", "KILLED BY {N}"},
      {Death, "", "YOU DIED"},
      // Revives of a teammate. A prompt to revive, or the player being revived, isn't one.
      {Revive, "center", "REVIVE #"}, {Revive, "center", "REVIVE #"}, {Revive, "center", "REVIVED {N}"},
      {Revive, "center", "YOU REVIVED {N}"}, {Revive, "center", "TEAMMATE REVIVED"}, {Revive, "center", "TEAMMATE REVIVED #"},
      {Revive, "center", "SQUAD REVIVE"}, {Revive, "center", "SQUAD REVIVE #"}, {Revive, "center", "REVIVED"},
      {Revive, "center", "RESURRECTED {N}"}, {Revive, "center", "RESCUED {N}"}, {Revive, "center", "YOU RESCUED {N}"},
      {Revive, "center", "REBOOTED {N}"}, {Revive, "center", "RESPAWNED {N}"}, {Revive, "center", "PICKED UP {N}"},
      {Revive, "feed_right", "{P} REVIVED {N}"}, {Revive, "feed_left", "{P} REVIVED {N}"},
      {Revive, "feed_bottom", "{P} REVIVED {N}"}, {Revive, "", "REVIVE #"}, {Revive, "", "REVIVED {N}"},
      // Rounds and matches starting.
      {MatchStart, "banner", "MATCH START"}, {MatchStart, "banner", "MATCH STARTED"}, {MatchStart, "banner", "MATCH STARTING"},
      {MatchStart, "banner", "MATCH BEGINS"}, {MatchStart, "banner", "THE MATCH HAS BEGUN"},
      {MatchStart, "banner", "GAME STARTING"}, {MatchStart, "banner", "GAME START"}, {MatchStart, "banner", "ROUND 1"},
      {MatchStart, "banner", "ROUND 1 BEGINS"}, {MatchStart, "top", "ROUND 1"}, {MatchStart, "banner", "ROUND STARTED"},
      {MatchStart, "banner", "ROUND STARTING"}, {MatchStart, "banner", "PREPARE FOR BATTLE"},
      {MatchStart, "banner", "PREPARE TO ATTACK"}, {MatchStart, "banner", "PREPARE YOUR DEFENSES"},
      {MatchStart, "banner", "ASSEMBLE YOUR TEAM"}, {MatchStart, "banner", "BATTLE BUS"}, {MatchStart, "banner", "JUMPMASTER"},
      {MatchStart, "banner", "DEPLOYING"}, {MatchStart, "banner", "DROP ZONE"}, {MatchStart, "banner", "FIGHT"},
      {MatchStart, "banner", "BUY PHASE"}, {MatchStart, "top", "BUY PHASE"}, {MatchStart, "banner", "MATCH FOUND"},
      {MatchStart, "banner", "GET READY"}, {MatchStart, "banner", "BEGIN"}, {MatchStart, "", "MATCH STARTING"},
      // Matches ending.
      {MatchEnd, "banner", "VICTORY"}, {MatchEnd, "banner", "VICTORY"}, {MatchEnd, "banner", "DEFEAT"},
      {MatchEnd, "banner", "DEFEAT"}, {MatchEnd, "banner", "DRAW"}, {MatchEnd, "banner", "YOU WIN"},
      {MatchEnd, "banner", "YOU LOSE"}, {MatchEnd, "banner", "YOU WON"}, {MatchEnd, "banner", "YOU LOST"},
      {MatchEnd, "banner", "MATCH OVER"}, {MatchEnd, "banner", "MATCH COMPLETE"}, {MatchEnd, "banner", "GAME OVER"},
      {MatchEnd, "banner", "YOUR TEAM WON"}, {MatchEnd, "banner", "YOUR TEAM LOST"}, {MatchEnd, "banner", "VICTORY ROYALE"},
      {MatchEnd, "banner", "WINNER WINNER CHICKEN DINNER"}, {MatchEnd, "banner", "CHICKEN DINNER"},
      {MatchEnd, "banner", "BETTER LUCK NEXT TIME"}, {MatchEnd, "banner", "YOU ARE THE CHAMPION"},
      {MatchEnd, "banner", "CHAMPION SQUAD"}, {MatchEnd, "banner", "SQUAD ELIMINATED"}, {MatchEnd, "banner", "YOU PLACED #"},
      {MatchEnd, "banner", "MISSION COMPLETE"}, {MatchEnd, "banner", "MISSION FAILED"}, {MatchEnd, "banner", "STALEMATE"},
      {MatchEnd, "banner", "EXTRACTED"}, {MatchEnd, "banner", "EXTRACTION SUCCESSFUL"}, {MatchEnd, "banner", "RAID OVER"},
      {MatchEnd, "banner", "SURVIVED"}, {MatchEnd, "banner", "MATCH SUMMARY"}, {MatchEnd, "banner", "{N} WINS"},
      {MatchEnd, "banner", "TEAM WINS"}, {MatchEnd, "banner", "FLAWLESS VICTORY"}, {MatchEnd, "", "VICTORY"},
      {MatchEnd, "", "DEFEAT"},
      // Medals and multi-kills.
      {Highlight, "center", "DOUBLE KILL"}, {Highlight, "center", "TRIPLE KILL"}, {Highlight, "center", "QUAD KILL"},
      {Highlight, "center", "MULTI KILL"}, {Highlight, "center", "MULTIKILL"}, {Highlight, "center", "PENTA KILL"},
      {Highlight, "center", "PENTAKILL"}, {Highlight, "center", "MEGA KILL"}, {Highlight, "center", "ULTRA KILL"},
      {Highlight, "center", "KILLING SPREE"}, {Highlight, "center", "RAMPAGE"}, {Highlight, "center", "UNSTOPPABLE"},
      {Highlight, "center", "DOMINATING"}, {Highlight, "center", "GODLIKE"}, {Highlight, "center", "HEADSHOT"},
      {Highlight, "center", "LONGSHOT"}, {Highlight, "center", "LONG SHOT"}, {Highlight, "center", "FIRST BLOOD"},
      {Highlight, "center", "ACE"}, {Highlight, "banner", "ACE"}, {Highlight, "center", "TEAM ACE"},
      {Highlight, "center", "CLUTCH"}, {Highlight, "banner", "CLUTCH"}, {Highlight, "center", "TEAM WIPE"},
      {Highlight, "center", "TEAM WIPED"}, {Highlight, "center", "SQUAD WIPE"}, {Highlight, "center", "SQUAD WIPED"},
      {Highlight, "center", "DOUBLE ELIMINATION"}, {Highlight, "center", "TRIPLE ELIMINATION"},
      {Highlight, "center", "KILL STREAK"}, {Highlight, "center", "KILLSTREAK"}, {Highlight, "center", "REVENGE"},
      {Highlight, "center", "PAYBACK"}, {Highlight, "center", "ON FIRE"}, {Highlight, "banner", "PLAY OF THE GAME"},
      {Highlight, "banner", "MVP"}, {Highlight, "center", "FLAWLESS"}, {Highlight, "center", "OVERKILL"},
      {Highlight, "center", "SHUTDOWN"}, {Highlight, "center", "BUZZER BEATER"}, {Highlight, "center", "COLLATERAL"},
      {Highlight, "center", "NO SCOPE"}, {Highlight, "center", "WALLBANG"}, {Highlight, "", "DOUBLE KILL"},
      {Highlight, "", "TRIPLE KILL"},
      // Menus and lobbies: while one is up, nothing there counts.
      {Menu, "banner", "PLAY"}, {Menu, "banner", "FIND MATCH"}, {Menu, "banner", "READY UP"}, {Menu, "banner", "READY"},
      {Menu, "banner", "SEARCHING FOR MATCH"}, {Menu, "banner", "MATCHMAKING"}, {Menu, "banner", "QUICK PLAY"},
      {Menu, "banner", "COMPETITIVE"}, {Menu, "banner", "RANKED"}, {Menu, "banner", "CUSTOM GAME"},
      {Menu, "banner", "SETTINGS"}, {Menu, "banner", "OPTIONS"}, {Menu, "banner", "RESUME"}, {Menu, "banner", "RESUME GAME"},
      {Menu, "banner", "QUIT"}, {Menu, "banner", "QUIT GAME"}, {Menu, "banner", "EXIT TO MAIN MENU"},
      {Menu, "banner", "RETURN TO LOBBY"}, {Menu, "banner", "MAIN MENU"}, {Menu, "banner", "LOADOUT"},
      {Menu, "banner", "LOADOUTS"}, {Menu, "banner", "BATTLE PASS"}, {Menu, "banner", "STORE"}, {Menu, "banner", "SHOP"},
      {Menu, "banner", "CAREER"}, {Menu, "banner", "INVENTORY"}, {Menu, "banner", "PARTY"}, {Menu, "banner", "INVITE"},
      {Menu, "banner", "PAUSED"}, {Menu, "banner", "GRAPHICS"}, {Menu, "banner", "CONTROLS"}, {Menu, "banner", "AUDIO"},
      {Menu, "banner", "ACCEPT"}, {Menu, "banner", "CANCEL"}, {Menu, "banner", "CONFIRM"}, {Menu, "banner", "BACK"},
      {Menu, "top", "PLAY"}, {Menu, "top", "STORE"}, {Menu, "top", "BATTLE PASS"}, {Menu, "top", "CAREER"},
      {Menu, "top", "LOADOUT"}, {Menu, "center", "FIND MATCH"}, {Menu, "center", "READY UP"}, {Menu, "center", "PLAY"},
      {Menu, "center", "SETTINGS"}, {Menu, "center", "RESUME"}, {Menu, "center", "LOADOUT"},
      {Menu, "feed_left", "SETTINGS"}, {Menu, "feed_left", "PLAY"}, {Menu, "feed_left", "STORE"},
      {Menu, "feed_left", "LOADOUT"}, {Menu, "feed_left", "CAREER"},
      // Everything else a HUD shows: other players' kills, prompts, ammo, objectives, chat.
      {None, "feed_right", "{N} {N}"}, {None, "feed_right", "{N} {N}"}, {None, "feed_right", "{N} {N}"},
      {None, "feed_right", "{N} {N}"}, {None, "feed_right", "{N} {N}"}, {None, "feed_left", "{N} {N}"},
      {None, "feed_left", "{N} {N}"}, {None, "feed_left", "{N} {N}"}, {None, "feed_bottom", "{N} {N}"},
      {None, "feed_right", "{N} # {N}"}, {None, "feed_right", "{N} # {N}"}, {None, "feed_right", "{N}"},
      {None, "feed_right", "{N}"}, {None, "feed_left", "{N}"}, {None, "feed_right", "{N} KILLED {N}"},
      {None, "feed_right", "{N} ELIMINATED {N}"}, {None, "feed_left", "{N} KILLED {N}"},
      {None, "feed_bottom", "{N} KILLED {N}"}, {None, "feed_bottom", "{N} KNOCKED OUT {N}"},
      {None, "feed_bottom", "{N} ELIMINATED {N}"}, {None, "feed_right", "{N} DOWNED {N}"},
      {None, "feed_right", "{N} REVIVED {N}"}, {None, "feed_right", "{N} JOINED THE GAME"},
      {None, "feed_right", "{N} LEFT THE GAME"}, {None, "feed_right", "{N} HAS JOINED"}, {None, "feed_right", "{N} HAS LEFT"},
      {None, "feed_left", "{N} JOINED"}, {None, "feed_left", "{N} LEFT"}, {None, "feed_left", "{N} GG"},
      {None, "feed_left", "{N} NICE"}, {None, "feed_left", "{N} ALL {N}"}, {None, "feed_left", "TEAM {N}"},
      {None, "feed_left", "{N} {N} {N}"}, {None, "feed_right", "{N} {N} {N}"}, {None, "feed_right", "{N} {N} {N} {N}"},
      {None, "feed_left", "SQUAD"}, {None, "feed_left", "{P}"}, {None, "feed_left", "{P} #"}, {None, "feed_right", "{P}"},
      {None, "feed_left", "{P} {N} {N}"}, {None, "feed_left", "PARTY {P}"}, {None, "feed_left", "{P} LEVEL #"},
      {None, "center", "{P}"}, {None, "center", "{N}"}, {None, "center", "{N}"}, {None, "center", "{N} {N}"},
      {None, "center", "#"}, {None, "center", "#"}, {None, "center", "# #"}, {None, "center", "# # #"},
      {None, "center", "RELOAD"}, {None, "center", "RELOADING"}, {None, "center", "NO AMMO"}, {None, "center", "OUT OF AMMO"},
      {None, "center", "LOW AMMO"}, {None, "center", "PRESS F TO INTERACT"}, {None, "center", "HOLD E TO REVIVE"},
      {None, "center", "HOLD E TO REVIVE {N}"}, {None, "center", "HOLD F TO REVIVE"}, {None, "center", "REVIVE"},
      {None, "center", "REVIVING"}, {None, "center", "REVIVING {N}"}, {None, "center", "BEING REVIVED"},
      {None, "center", "{N} NEEDS HELP"}, {None, "center", "{N} IS DOWN"}, {None, "center", "TEAMMATE DOWN"},
      {None, "center", "PICK UP"}, {None, "center", "PICK UP {N}"}, {None, "center", "OPEN"}, {None, "center", "SEARCH"},
      {None, "center", "LOOT"}, {None, "center", "SWAP"}, {None, "center", "EQUIP"}, {None, "center", "USE"},
      {None, "center", "CAPTURING"}, {None, "center", "CAPTURING #"}, {None, "center", "CONTESTED"},
      {None, "center", "OBJECTIVE"}, {None, "center", "DEFEND"}, {None, "center", "ATTACK"}, {None, "center", "ESCORT"},
      {None, "center", "CAPTURE THE POINT"}, {None, "center", "DEFUSING"}, {None, "center", "PLANTING"},
      {None, "center", "OUT OF BOUNDS"}, {None, "center", "RETURN TO THE COMBAT AREA"}, {None, "center", "LEAVING THE AREA"},
      {None, "center", "HIT"}, {None, "center", "DAMAGE #"}, {None, "center", "# DAMAGE"}, {None, "center", "SHIELD BREAK"},
      {None, "center", "ARMOR BROKEN"}, {None, "center", "CRITICAL"}, {None, "center", "BLOCKED"},
      {None, "center", "ENEMY SPOTTED"}, {None, "center", "SPOTTED"}, {None, "center", "PING"}, {None, "center", "ENEMY"},
      {None, "center", "ENEMY HERE"}, {None, "center", "LOOTING"}, {None, "center", "HEALING"}, {None, "center", "USING"},
      {None, "center", "LEVEL UP"}, {None, "center", "CHALLENGE COMPLETE"}, {None, "center", "XP #"}, {None, "center", "# XP"},
      {None, "center", "CHECKPOINT"}, {None, "center", "WAITING FOR PLAYERS"}, {None, "center", "SWITCHING SIDES"},
      {None, "center", "ZONE CLOSING"}, {None, "center", "THE STORM IS SHRINKING"}, {None, "center", "RING CLOSING"},
      {None, "bottom", "#"}, {None, "bottom", "# #"}, {None, "bottom", "HEALTH"}, {None, "bottom", "HEALTH #"},
      {None, "bottom", "HP #"}, {None, "bottom", "ARMOR #"}, {None, "bottom", "SHIELD #"}, {None, "bottom", "AMMO # #"},
      {None, "bottom", "RELOAD"}, {None, "bottom", "PRESS F TO INTERACT"}, {None, "bottom", "PRESS E TO OPEN"},
      {None, "bottom", "{N} {N}"}, {None, "bottom", "{N}"}, {None, "bottom", "{P}"}, {None, "bottom", "{P} #"},
      {None, "bottom", "ULTIMATE READY"}, {None, "bottom", "ULTIMATE #"}, {None, "bottom", "ABILITY READY"},
      {None, "bottom", "PLAYERS LEFT #"}, {None, "bottom", "ALIVE #"}, {None, "bottom", "SQUADS LEFT #"},
      {None, "top", "#"}, {None, "top", "# #"}, {None, "top", "# # #"}, {None, "top", "{N}"}, {None, "top", "{N} {N}"},
      {None, "top", "N"}, {None, "top", "NE"}, {None, "top", "NW"}, {None, "top", "SE"}, {None, "top", "SW"},
      {None, "top", "ROUND #"}, {None, "top", "ROUND #"}, {None, "top", "ROUND # #"}, {None, "top", "# ALIVE"},
      {None, "top", "ALIVE #"}, {None, "top", "PLAYERS #"}, {None, "top", "KILLS #"}, {None, "top", "SCORE #"},
      {None, "top", "TIME #"}, {None, "top", "OVERTIME"}, {None, "top", "{N} #"}, {None, "top", "TEAM {N}"},
      {None, "top", "CAPTURING"}, {None, "top", "OBJECTIVE"}, {None, "top", "PING #"}, {None, "top", "FPS #"},
      {None, "banner", "{N}"}, {None, "banner", "{N} {N}"}, {None, "banner", "#"}, {None, "banner", "{P}"},
      {None, "banner", "ROUND #"}, {None, "banner", "OBJECTIVE"}, {None, "banner", "LOADING"},
      {None, "banner", "WAITING FOR PLAYERS"}, {None, "banner", "CONNECTING"}, {None, "banner", "TIP"},
      {None, "banner", "{N} IS THE MVP"}, {None, "banner", "SUDDEN DEATH"}, {None, "banner", "OVERTIME"},
      {None, "banner", "ROUND WON"}, {None, "banner", "ROUND LOST"}, {None, "banner", "ROUND OVER"},
      {None, "banner", "SWITCHING SIDES"}, {None, "banner", "HALFTIME"}, {None, "banner", "DOUBLE XP"},
      {None, "", "{N}"}, {None, "", "{N} {N}"}, {None, "", "#"}, {None, "", "# #"}, {None, "", "{P}"},
      {None, "bottom", "{P} #"}, {None, "bottom", "{P} # #"}, {None, "feed_left", "{P} #"}, {None, "feed_left", "{P} # #"},
      {None, "top", "{P} #"}, {None, "top", "{P}"}, {None, "center", "{P} #"}, {None, "banner", "{P} #"},
      {None, "feed_right", "{P} #"}, {None, "", "{P} #"}, {None, "", "{P} # #"},
      {Kill, "center", "# KILL"}, {Kill, "center", "# ELIMINATION"}, {Kill, "center", "KILL # #"},
      {Kill, "center", "ELIMINATION"}, {Kill, "center", "ELIMINATED {N} #"}, {Kill, "", "ELIMINATION #"},
      {Kill, "", "# KILL"}, {Kill, "center", "ENEMY KILLED"}, {Kill, "center", "KILLED"},
      {None, "", "PRESS F TO INTERACT"}, {None, "", "RELOAD"}, {None, "", "OBJECTIVE"}, {None, "", "HEALTH #"},
  };
  std::vector<HudExample> examples;
  examples.reserve(std::size(seeds));
  for (const auto& seed : seeds) examples.push_back({seed.label, seed.zone, seed.text, {}});
  return examples;
}

HudModel::HudModel(const std::vector<HudExample>& learned, const std::string& game, std::vector<std::string> playerNames)
    : playerNames_(std::move(playerNames)) {
  for (const auto& name : playerNames_) {
    std::vector<std::string> tokens;
    for (auto& token : Tokenize(name)) {
      // A tag's number (NAME#1234) isn't shown in game.
      if (!IsNumberish(token)) tokens.push_back(std::move(token));
    }
    if (!tokens.empty()) nameTokens_.push_back(std::move(tokens));
  }
  std::vector<HudExample> all = SeedHudExamples();
  std::vector<double> weights(all.size(), 1.0);
  for (const auto& example : learned) {
    all.push_back(example);
    weights.push_back(!game.empty() && example.game == game ? kSameGameWeight : kLearnedWeight);
  }
  // The built-in examples' words are all known. A learned word is known once it's in two
  // examples, so a word a game uses for its kills is learned, but one player's name isn't.
  const size_t seeds = all.size() - learned.size();
  std::map<std::string, int> seenIn;
  for (size_t i = 0; i < all.size(); ++i) {
    std::set<std::string> distinct;
    for (auto& token : ExampleTokens(all[i].text)) {
      if (IsPlaceholder(token) || token.size() < 2 || IsNumberish(token)) continue;
      distinct.insert(token);
    }
    for (const auto& token : distinct) {
      if (i < seeds) vocabulary_.insert(token);
      else ++seenIn[token];
    }
  }
  for (const auto& [token, count] : seenIn) {
    if (count >= 2) vocabulary_.insert(token);
  }
  for (const auto& word : vocabulary_) {
    if (word.size() >= 6) fuzzyWords_.push_back(word);
  }
  std::ranges::sort(fuzzyWords_);
  Train(all, weights);
}

std::string HudModel::KnownWord(const std::string& token) const {
  if (vocabulary_.contains(token)) return token;
  if (token.size() < 6) return {};
  for (const auto& word : fuzzyWords_) {
    if (WordsMatch(token, word)) return word;
  }
  return {};
}

std::vector<std::string> HudModel::Normalize(const std::vector<std::string>& tokens) const {
  std::vector<std::string> out;
  auto push = [&](std::string token) {
    // A name read as several words is still one name.
    if (token == kNameToken && !out.empty() && out.back() == kNameToken) return;
    out.push_back(std::move(token));
  };
  for (size_t i = 0; i < tokens.size();) {
    const std::string& token = tokens[i];
    if (token == kPlayerToken || token == kNameToken) {
      push(token);
      ++i;
      continue;
    }
    size_t nameLength = 0;
    for (const auto& name : nameTokens_) {
      if (i + name.size() > tokens.size()) continue;
      bool match = true;
      for (size_t k = 0; k < name.size() && match; ++k) match = SameText(tokens[i + k], name[k]);
      if (match) nameLength = std::max(nameLength, name.size());
    }
    if (nameLength > 0) {
      push(std::string(kPlayerToken));
      i += nameLength;
      continue;
    }
    ++i;
    if (token == kFirstToken || token == "1" || token == "01") {
      // "ROUND 1" starts a match; any other round number doesn't.
      push(std::string(kFirstToken));
    } else if (token == kNumberToken || IsNumberish(token)) {
      push(std::string(kNumberToken));
    } else if (token.size() < 2) {
      // A lone letter is usually an icon read as text.
    } else if (auto known = KnownWord(token); !known.empty()) {
      push(std::move(known));
    } else {
      push(std::string(kNameToken));
    }
  }
  return out;
}

std::vector<std::string> HudModel::Features(const std::vector<std::string>& normalized, std::string_view zone) const {
  std::vector<std::string> features;
  if (normalized.empty()) return features;
  for (const auto& token : normalized) features.push_back("w:" + token);
  for (size_t i = 0; i + 1 < normalized.size(); ++i) features.push_back("b:" + normalized[i] + "_" + normalized[i + 1]);
  const size_t n = normalized.size();
  // Where the player's name is: first in a killfeed line is the killer, last the one who died.
  const auto first = std::ranges::find(normalized, kPlayerToken);
  if (first != normalized.end()) {
    const size_t from = static_cast<size_t>(first - normalized.begin());
    size_t last = from;
    for (size_t i = from; i < n; ++i) {
      if (normalized[i] == kPlayerToken) last = i;
    }
    features.push_back("pf:" + std::to_string(std::min<size_t>(from, 2)));
    features.push_back("pl:" + std::to_string(std::min<size_t>(n - 1 - last, 2)));
  } else if (std::ranges::find(normalized, kNameToken) != normalized.end()) {
    // Names without the player's: in a killfeed, someone else's business.
    features.push_back("np:" + std::string(zone));
  }
  features.push_back("n:" + std::to_string(std::min<size_t>(n, 5)));
  if (!zone.empty()) features.push_back("z:" + std::string(zone));
  return features;
}

void HudModel::Train(const std::vector<HudExample>& examples, const std::vector<double>& weights) {
  for (size_t i = 0; i < examples.size(); ++i) {
    const auto& example = examples[i];
    const auto features = Features(Normalize(ExampleTokens(example.text)), example.zone);
    if (features.empty()) continue;
    const auto c = static_cast<size_t>(example.label);
    const double weight = weights[i];
    for (const auto& feature : features) {
      auto& count = counts_.try_emplace(feature).first->second;
      count[c] += weight;
      totals_[c] += weight;
    }
    classExamples_[c] += weight;
    ++examples_;
  }
}

std::array<double, kHudLabelCount> HudModel::Probabilities(const std::vector<std::string>& tokens,
                                                           std::string_view zone) const {
  std::array<double, kHudLabelCount> probability{};
  probability[static_cast<size_t>(HudLabel::None)] = 1;
  const auto normalized = Normalize(tokens);
  // Only the player's name or a word the model knows can make a line mean something: a line of
  // names and numbers alone is someone else's business.
  const bool anchored = std::ranges::any_of(normalized, [](const std::string& t) {
    return t != kNameToken && t != kNumberToken && t != kFirstToken;
  });
  if (!anchored) return probability;
  const auto features = Features(normalized, zone);
  const double featureCount = static_cast<double>(counts_.size());
  std::array<double, kHudLabelCount> score{};
  double allExamples = 0;
  for (double n : classExamples_) allExamples += n;
  for (size_t c = 0; c < kHudLabelCount; ++c) {
    if (classExamples_[c] <= 0) {
      score[c] = -1e300;
      continue;
    }
    // How common each kind of line is: most HUD text means nothing.
    score[c] = std::log(classExamples_[c] / allExamples);
    const double denominator = totals_[c] + kSmoothing * featureCount;
    for (const auto& feature : features) {
      const auto found = counts_.find(feature);
      if (found == counts_.end()) continue;  // features never seen in training say nothing
      score[c] += std::log((found->second[c] + kSmoothing) / denominator);
    }
  }
  const double top = *std::ranges::max_element(score);
  double sum = 0;
  for (size_t c = 0; c < kHudLabelCount; ++c) sum += probability[c] = std::exp(score[c] - top);
  for (double& p : probability) p /= sum;
  return probability;
}

HudGuess HudModel::Classify(const std::vector<std::string>& tokens, std::string_view zone) const {
  const auto probability = Probabilities(tokens, zone);
  const auto best = static_cast<size_t>(std::ranges::max_element(probability) - probability.begin());
  return {static_cast<HudLabel>(best), probability[best]};
}

std::vector<HudGuess> HudModel::ClassifyRows(const std::vector<std::vector<std::string>>& rows, std::string_view zone) const {
  {
    std::lock_guard lock(lastMutex_);
    for (const auto& answer : last_) {
      if (answer.zone == zone && answer.rows == rows) return answer.guesses;
    }
  }
  std::vector<HudGuess> guesses;
  bool menu = false;
  for (const auto& row : rows) {
    HudGuess guess = Classify(row, zone);
    if (guess.confidence < kMinConfidence) guess.label = HudLabel::None;
    menu = menu || guess.label == HudLabel::Menu;
    guesses.push_back(guess);
  }
  if (menu) {
    for (auto& guess : guesses) {
      if (guess.label != HudLabel::Menu) guess.label = HudLabel::None;
    }
  }
  std::lock_guard lock(lastMutex_);
  Answer answer{rows, std::string(zone), guesses};
  if (last_.size() < kRemembered) {
    last_.push_back(std::move(answer));
  } else {
    last_[nextAnswer_] = std::move(answer);
    nextAnswer_ = (nextAnswer_ + 1) % kRemembered;
  }
  return guesses;
}

std::vector<HudExample> ReadHudExamples(std::istream& in) {
  std::vector<HudExample> examples;
  std::string line;
  while (std::getline(in, line)) {
    if (!line.empty() && line.back() == '\r') line.pop_back();
    if (line.empty() || line.front() == '#') continue;
    std::vector<std::string> parts;
    size_t start = 0;
    while (parts.size() < 3) {
      const size_t tab = line.find('\t', start);
      if (tab == std::string::npos) break;
      parts.push_back(line.substr(start, tab - start));
      start = tab + 1;
    }
    if (parts.size() != 3) continue;
    const auto label = ParseHudLabel(Trim(parts[1]));
    const std::string text = Trim(std::string_view(line).substr(start));
    if (!label || text.empty()) continue;
    const std::string game = Trim(parts[0]);
    examples.push_back({*label, Trim(parts[2]), text, game == "*" ? std::string() : game});
  }
  return examples;
}

void WriteHudExample(std::ostream& out, const HudExample& example) {
  out << (example.game.empty() ? "*" : example.game) << '\t' << HudLabelName(example.label) << '\t' << example.zone << '\t'
      << example.text << '\n';
}

std::vector<HudMark> ParseHudMarks(std::string_view text) {
  std::vector<HudMark> marks;
  size_t start = 0;
  while (start < text.size()) {
    size_t end = text.find(';', start);
    if (end == std::string_view::npos) end = text.size();
    const std::string_view part = text.substr(start, end - start);
    start = end + 1;
    const size_t at = part.find('@');
    if (at == std::string_view::npos) continue;
    const auto event = ParseEventType(Trim(part.substr(0, at)));
    const std::string seconds = Trim(part.substr(at + 1));
    double value = 0;
    const auto [ptr, ec] = std::from_chars(seconds.data(), seconds.data() + seconds.size(), value);
    if (!event || ec != std::errc() || ptr != seconds.data() + seconds.size() || value < 0) continue;
    marks.push_back({*event, static_cast<int64_t>(std::llround(value * 1000))});
  }
  return marks;
}

namespace {

// A line as it's taught: the player's name as {P}, so what's learned works for anyone, and
// every other word as read.
std::string TeachingText(const HudModel& model, const std::vector<std::string>& row) {
  const std::vector<std::string> names = model.PlayerNames();
  std::vector<std::vector<std::string>> nameTokens;
  for (const auto& name : names) {
    std::vector<std::string> tokens;
    for (auto& token : Tokenize(name)) {
      if (!WordsMatch(token, kNumberWord)) tokens.push_back(std::move(token));
    }
    if (!tokens.empty()) nameTokens.push_back(std::move(tokens));
  }
  std::string text;
  for (size_t i = 0; i < row.size();) {
    size_t length = 0;
    for (const auto& name : nameTokens) {
      if (i + name.size() > row.size()) continue;
      bool match = true;
      for (size_t k = 0; k < name.size() && match; ++k) match = SameText(row[i + k], name[k]);
      if (match) length = std::max(length, name.size());
    }
    if (!text.empty()) text += ' ';
    if (length > 0) {
      text += kPlayerToken;
      i += length;
    } else {
      text += row[i++];
    }
  }
  return text;
}

std::string RowKey(const std::string& zone, const std::vector<std::string>& row) {
  std::string key = zone + "|";
  for (const auto& token : row) {
    if (WordsMatch(token, kNumberWord)) continue;  // a timer counting down is the same line
    key += token + " ";
  }
  return key;
}

}  // namespace

std::vector<HudExample> TeachFromMarks(const HudModel& model, const std::vector<ZoneRead>& reads,
                                       const std::vector<HudMark>& marks, const std::string& game,
                                       double toleranceSeconds) {
  std::vector<HudExample> taught;
  std::set<std::string> done;  // zone|text, so each line is taught once
  auto teach = [&](HudLabel label, const std::string& zone, const std::vector<std::string>& row) {
    const std::string text = TeachingText(model, row);
    if (text.empty() || !done.insert(zone + "|" + text).second) return;
    taught.push_back({label, zone, text, game});
  };
  const auto tolerance = static_cast<int64_t>(toleranceSeconds * 1000);

  // What the model would count, line by line, and whether a mark backs it up.
  std::vector<bool> markCaught(marks.size(), false);
  for (const auto& read : reads) {
    const auto guesses = model.ClassifyRows(read.rows, read.zone);
    for (size_t r = 0; r < read.rows.size(); ++r) {
      const auto event = HudLabelEvent(guesses[r].label);
      if (!event) continue;
      bool backed = false;
      for (size_t m = 0; m < marks.size(); ++m) {
        if (marks[m].event != *event || std::llabs(marks[m].ms - read.ms) > tolerance + 3000) continue;
        backed = true;
        // A line seen around the mark's time is what caught it; it may stay up a few seconds.
        if (read.ms >= marks[m].ms - tolerance && read.ms <= marks[m].ms + tolerance + 3000) markCaught[m] = true;
      }
      if (!backed) teach(HudLabel::None, read.zone, read.rows[r]);
    }
  }

  // For each mark it missed, the new line around it that looks most like that kind of moment.
  for (size_t m = 0; m < marks.size(); ++m) {
    if (markCaught[m]) continue;
    const HudMark& mark = marks[m];
    std::set<std::string> before;
    for (const auto& read : reads) {
      if (read.ms < mark.ms - tolerance - 3000 || read.ms > mark.ms - tolerance) continue;
      for (const auto& row : read.rows) before.insert(RowKey(read.zone, row));
    }
    const HudLabel want = HudLabelFor(mark.event);
    const std::vector<std::string>* best = nullptr;
    std::string bestZone;
    double bestScore = -1;
    for (const auto& read : reads) {
      if (read.ms < mark.ms - tolerance || read.ms > mark.ms + tolerance) continue;
      for (const auto& row : read.rows) {
        if (before.contains(RowKey(read.zone, row))) continue;
        // A line needs a word to learn from: the player's name, a word the model knows, or at
        // least two words it doesn't (the game's word for a kill, and a name).
        const auto normalized = model.Normalize(row);
        const bool anchored = std::ranges::any_of(normalized, [](const std::string& t) {
          return t != "{N}" && t != "#" && t != "#1";
        });
        const auto words = std::ranges::count_if(row, [](const std::string& t) { return t.size() >= 2 && !WordsMatch(t, kNumberWord); });
        if (!anchored && words < 2) continue;
        const HudGuess guess = model.Classify(row, read.zone);
        // How much it already looks like the wanted kind: its confidence if that's its label,
        // otherwise a little for being new around the mark, closest to it first.
        double score = guess.label == want ? 1 + guess.confidence : 0.5 - std::llabs(read.ms - mark.ms) / 1e5;
        if (score > bestScore) {
          bestScore = score;
          best = &row;
          bestZone = read.zone;
        }
      }
    }
    if (best) teach(want, bestZone, *best);
  }
  return taught;
}

}  // namespace clip
