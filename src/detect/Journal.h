#pragma once

#include "detect/ScreenRegion.h"
#include "detect/TextMatcher.h"

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// The adaptive journal: in games built around runs (raids, missions, expeditions), what the
// player did in each one, beyond the moments that are clipped. Journal rules in detection.ini,
// [<game>.journal.<note>], read what a game shows in menus and on its HUD (the map picked, an
// enemy destroyed, an item picked up, the raid's result) and write it down as notes in the
// recording's timeline. The desktop app puts the notes of each run together into its story
// (ClipEngine.Desktop/Services/Journal.cs), so keep the note names in step with it:
//
//   map         the map or location picked before a run (value: its name)
//   condition   something about this run's map, like a night raid or a storm (value: which)
//   mission     the kind of run, like a mission type or a game mode (value: which)
//   queue       looking for a match: a map picked and the queue started, not in yet
//   deploy      the player went in: the run began (value: optional)
//   extracted   made it out alive
//   died        the run ended in death
//   failed      the run ended without success, though not in death (a mission failed)
//   left        left early: surrendered, abandoned or disconnected
//   downed      knocked down, not dead yet
//   enemy       an AI enemy destroyed or killed (value: what it was; amount: how many)
//   player_kill another player killed or downed (value: who, when shown)
//   loot        an item picked up (value: the item; amount: how many)
//   lost        an item the game says was lost
//   inventory   an item seen in the inventory or loadout (value: the item; amount: how many)
//   xp          XP gained (amount). Any note can carry XP of its own too, from {xp} in a pattern
//   objective   a quest, contract or objective done (value: which)
//
// Any other name is kept and shown in the story as it is.

// One of a rule's values: the name written down, and what it looks like on screen.
struct JournalValue {
  std::string name;              // as written in detection.ini, like "Buried City"
  std::vector<Phrase> phrases;   // the name itself, or the words in brackets after it
};

inline constexpr std::string_view kValuePlaceholder = "{value}";
// {value} in a pattern, kept in its Phrase as this word.
inline constexpr std::string_view kValueWord = "*";
// {xp} in a pattern: a number that's the XP the line gave, not how many.
inline constexpr std::string_view kXpPlaceholder = "{xp}";
inline constexpr std::string_view kXpWord = "%";

struct JournalRule {
  std::string name;  // the section name, for the log
  std::string game;
  std::string note;  // what it writes down: map, enemy, loot, ...
  ScreenRegion region;
  int threshold = 0;
  int sampleMs = 0;     // how often it's read; 0 until parsed, then a default by kind
  bool sampleMsSet = false;
  int cooldownMs = 0;   // ignore the same note again closer than this
  int forgetMs = 0;     // how long a line must be gone before it counts again; 0 for the default

  // What it looks for, one or more of:
  std::vector<Phrase> words;         // any of these means the note, with no value
  std::vector<JournalValue> values;  // which of these shows is the value (map names, outcomes)
  // With values: the screen shows the one picked, so a read showing several (a list of every
  // map) notes none. false notes each of them (a map's conditions, all shown at once).
  bool pick = true;
  std::vector<Phrase> patterns;      // lines like "DESTROYED {value} {xp} XP" or "+{number} XP"
  bool lines = false;                // every line of text is a value (an inventory list)
  std::vector<Phrase> exclude;       // a line with any of these is skipped

  // Only while one of these words shows: in when_region if it's given (a menu's title, a
  // small box read first, so the big one is only read while the menu is open), or anywhere
  // in the rule's own region otherwise.
  std::vector<Phrase> when;
  std::optional<ScreenRegion> whenRegion;

  bool Reads() const { return !words.empty() || !values.empty() || !patterns.empty() || lines; }
  // A rule for a feed that scrolls (pickups, XP lines) rather than a menu or banner.
  bool ReadsFeed() const { return !patterns.empty() || lines; }
};

// How often a journal rule is read by default: feeds like the XP log every half second,
// since lines come and go quickly, and menus, lists and banners, which stay up, every second
// and a half.
int DefaultJournalSampleMs(const JournalRule& rule);

// "Dam Battlegrounds | The Blue Gate (BLUE GATE, BLUEGATE)" as values: each name, looked for
// as itself or, with words in brackets after it, as any of those.
std::vector<JournalValue> ParseJournalValues(const std::string& text);
// "DESTROYED {value} {xp} XP | +{number} XP" as patterns, with {value} kept as kValueWord and
// {xp} as kXpWord.
std::vector<Phrase> ParseJournalPatterns(const std::string& text);

// One thing written down.
struct JournalNote {
  std::string note;   // see the list above
  std::string value;  // what, if anything: a map, an enemy, an item
  int amount = 0;     // how many or how much, when the line shows a number; 0 when not
  int xp = 0;         // the XP it gave, when the line shows it
  std::string text;   // the line it was read from
  int64_t time = 0;   // QPC time in 100 ns units, filled in by the engine
};

// The notes in one read of the rule's region, given as rows of tokens, before telling new
// from already seen. when is not checked here.
std::vector<JournalNote> FindNotes(const JournalRule& rule, const std::vector<std::vector<std::string>>& rows);

// Whether any of the phrases shows anywhere in the rows.
bool ShowsAny(const std::vector<Phrase>& phrases, const std::vector<std::vector<std::string>>& rows);

// Turns what a journal rule sees in successive reads into new notes. A line counts once
// however long it stays up; a second identical line at once (two Wasps destroyed) counts
// again; and once a line has been gone for a while it counts again when it comes back. A menu
// showing several of a rule's values at once (a list of every map) notes none of them: only a
// screen showing the one picked does.
class JournalTracker {
 public:
  explicit JournalTracker(const JournalRule& rule) : rule_(&rule) {}

  // gateOpen: whether the rule's when words show (in when_region, or in rows when it has none,
  // in which case pass true and they're checked here).
  std::vector<JournalNote> Update(const std::vector<std::vector<std::string>>& rows, int64_t nowMs, bool gateOpen = true);

  const JournalRule& Rule() const { return *rule_; }

 private:
  int64_t ForgetMs() const;

  struct Seen {
    std::string key;
    int count = 0;  // most lines like it on screen at once
    int64_t lastSeenMs = 0;
  };
  const JournalRule* rule_;
  std::vector<Seen> seen_;
  int64_t lastNoteMs_ = INT64_MIN / 2;
};

}  // namespace clip
