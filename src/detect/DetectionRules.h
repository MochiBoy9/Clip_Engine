#pragma once

#include "detect/GameEvent.h"
#include "detect/Scoreboard.h"
#include "detect/ScreenRegion.h"
#include "detect/TextMatcher.h"

#include <cstdint>
#include <istream>
#include <set>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// Where on a row of text the words must be.
enum class LinePosition {
  Anywhere,  // anywhere in the region (the default)
  Start,     // first on a row: in a killfeed, the player who got the kill
  End,       // last on a row: in a killfeed, the player who died
  Middle,    // between two names on a row: in a killfeed that names them, an assister
};

// Stands for the player's own in-game names (player_names in clip_engine.ini) in words and exclude.
inline constexpr std::string_view kPlayerPlaceholder = "{player}";

// One "this text in this part of the screen means this happened" rule from detection.ini.
struct DetectionRule {
  std::string name;  // the section name, for the log
  std::string game;  // GameProfile id
  EventType event = EventType::Highlight;
  ScreenRegion region;
  std::vector<Phrase> words;    // any of these means the event
  std::vector<Phrase> exclude;  // but not while any of these is on screen
  int cooldownMs = 1000;        // ignore repeats closer together than this
  int threshold = 0;            // 1-255 keeps only pixels at least this bright; 0 reads the region as is
  LinePosition position = LinePosition::Anywhere;
  // How often to look, in ms. 0 until parsed, then the [detection] sample_ms for quick moments
  // (kills, assists, revives, highlights) and a slower default for banners that stay up.
  int sampleMs = 0;
  bool sampleMsSet = false;  // sample_ms was given, rather than the default

  // Moments seen by more than one rule. Matches of the same event from rules with merge_ms set,
  // each within merge_ms of the one before, are the same moment or moments, and the rule
  // with the highest priority among those that saw them says how many there were (see
  // EventFuser). So the killfeed and the score line under the crosshair both see a kill, and
  // it counts once.
  int mergeMs = 0;
  int priority = 1;
  // How long a message must be gone before it counts again; 0 for the defaults in RuleTracker.
  int forgetMs = 0;
  // With position = start, the row may begin with a squad or club tag before the name, as
  // The Finals shows [SMEDC] before each player in its killfeed.
  bool skipTag = false;

  // An icon to look for instead of (or as well as) words: a picture cut from a screenshot,
  // where fully transparent pixels are ignored. image is as written in detection.ini until
  // the engine resolves it to a full path.
  std::string image;
  double imageMatch = 0.85;  // how alike, 0-1, the region must be to the picture somewhere
  int imageScreen = 0;      // the height of the screen the picture was cut from; 0 means this one

  // A sound to listen for instead of reading the screen: one or more WAV cuts of it, each
  // starting kSoundLeadIn before the sound (see SoundMatch.h), resolved to full paths by the
  // engine like image. soundMatch is how alike, 0-1, the game's audio must be to them.
  std::vector<std::string> sounds;
  double soundMatch = 0.44;
  // Its sightings never count a moment on their own, only back up another rule's.
  bool confirmOnly = false;

  // Phrases as written, kept when they mention {player} so the names can be filled in later.
  std::vector<std::string> playerWords;
  std::vector<std::string> playerExclude;
  bool NeedsPlayer() const { return !playerWords.empty() || !playerExclude.empty(); }
  bool ReadsText() const { return !words.empty() || !playerWords.empty(); }
  bool LooksForImage() const { return !image.empty(); }
  bool ListensForSound() const { return !sounds.empty(); }
};

// How often a rule for this event is checked by default, given the [detection] sample_ms.
// Death screens and match banners stay up for seconds, so they needn't be read as often.
int DefaultSampleMs(EventType event, int sampleMs);

// A game added in detection.ini as [game.<id>] with a name and its executables, so games
// Clip Engine doesn't know yet can be recorded and given rules.
struct CustomGame {
  std::string id;
  std::string name;
  std::vector<std::string> executables;  // file names like "VALORANT-Win64-Shipping.exe"
};

struct DetectionRules {
  int sampleMs = 250;  // how often the screen is read
  bool sampleMsSet = false;  // [detection] sample_ms was in the file
  // Games this file gives its own rules for: those with a [<game>.<event>] section, or an
  // [<game>.edited] one, which Detection Studio writes so a game can have every rule removed.
  std::set<std::string> editedGames;
  std::vector<CustomGame> games;
  std::vector<DetectionRule> rules;
  std::vector<ScoreboardRule> scoreboards;
  std::vector<std::string> warnings;

  // The game's rules, with {player} filled in from playerNames. Rules that need a name are left
  // out when there are none, and their names added to needingName.
  std::vector<DetectionRule> ForGame(const std::string& game, const std::vector<std::string>& playerNames = {},
                                     std::vector<std::string>* needingName = nullptr) const;
  std::vector<ScoreboardRule> ScoreboardsForGame(const std::string& game) const;
};

// Reads detection.ini. Sections are named [<game id>.<event>] or [<game id>.<event>.<anything>],
// [<game id>.scoreboard] says where the game's scoreboard is, and [game.<id>] adds a game.
DetectionRules ParseDetectionRules(std::istream& in);

// The rules that ship with the app, with the user's own detection.ini laid over them. A game
// the user's file has rules for (editedGames) uses those instead of the shipped ones, and a
// game whose scoreboard it describes uses that scoreboard; every other game keeps the shipped
// rules, so fixes in an update still reach it. Games added in either file are all kept, the
// user's winning, and so is the user's [detection] sample_ms.
DetectionRules MergeDetectionRules(DetectionRules shipped, DetectionRules user);

// "A | B" as phrases, as the words and exclude settings are read. Phrases mentioning {player} are left out.
std::vector<Phrase> ParsePhrases(const std::string& value);

// Sets the rule's words and exclude from "A | B" as detection.ini has them, with {player} filled
// in from playerNames, as ForGame does. Detection Studio tests a rule this way, so a killfeed
// rule that looks for your name reads the frame as it would in game.
void SetPhrases(DetectionRule& rule, const std::string& words, const std::string& exclude,
                const std::vector<std::string>& playerNames);

// What the rule matches in one read of its region, given as rows of tokens: one entry per
// match, holding the other words around it (in a killfeed, the player names) so the same
// message can be recognized in the next read. Entries are empty when the words stand alone.
std::vector<std::string> FindMatches(const DetectionRule& rule, const std::vector<std::vector<std::string>>& rows);

// How many times the rule's words show in one read of its region, given as rows of tokens.
int CountMatches(const DetectionRule& rule, const std::vector<std::vector<std::string>>& rows);
// The same for one run of tokens, read as a single row.
inline int CountMatches(const DetectionRule& rule, const std::vector<std::string>& tokens) {
  return CountMatches(rule, std::vector<std::vector<std::string>>{tokens});
}

// With a region read more than one way (at the High and Ultra reading qualities), which read
// the rule goes by, given each read's rows of tokens: the one where it finds the most. But
// when the reads disagree and one of them shows the rule's exclude words, the one where it
// finds the fewest, so a misread "BY" can't turn "ELIMINATED BY" into a kill. Ties go to
// the first read.
size_t ChooseRead(const DetectionRule& rule, const std::vector<std::vector<std::vector<std::string>>>& reads);

// Turns what one rule sees in successive screen reads into new events. Each message is
// remembered by the names in it, so a killfeed line counts once however long it stays, even
// when OCR misses it for a few reads or the feed scrolls. A message with new names counts,
// and so does a second line with the same names (the same player killed twice); once a name
// has been gone from the screen for a few seconds, it can count again.
class RuleTracker {
 public:
  explicit RuleTracker(const DetectionRule& rule) : rule_(&rule) {}

  // Returns how many new events this read shows.
  int Update(const std::vector<std::vector<std::string>>& rows, int64_t nowMs);
  // The same for what was found some other way than reading words, such as an icon: one
  // entry per match, each holding what tells it apart from the others (or nothing).
  int UpdateMatches(std::vector<std::string> matches, int64_t nowMs);
  // The same for a read that is a single row.
  int Update(const std::vector<std::string>& tokens, int64_t nowMs) {
    return Update(std::vector<std::vector<std::string>>{tokens}, nowMs);
  }

  const DetectionRule& Rule() const { return *rule_; }

 private:
  // How long a message must be gone before the same names count as a new event. Messages
  // without names (a lone "KILL" popup, an icon) are told apart only by going away, so they
  // are forgotten sooner, though never before a couple of missed reads.
  static constexpr int64_t kForgetMs = 3000;
  static constexpr int64_t kForgetUnnamedMs = 1200;
  // forget_ms in detection.ini replaces both.
  int64_t ForgetMs(const std::string& key) const;

  struct Seen {
    std::string key;
    int count = 0;  // most lines with these names on screen at once
    int64_t lastSeenMs = 0;
  };

  const DetectionRule* rule_;
  std::vector<Seen> seen_;
  int64_t lastFiredMs_ = INT64_MIN / 2;
  // Kills, assists and revives found during the cooldown, counted once it's over rather than
  // lost, so a quick double kill is still two kills.
  int held_ = 0;
};

}  // namespace clip
