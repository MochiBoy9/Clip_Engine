#pragma once

#include "detect/DetectionRules.h"
#include "detect/GameEvent.h"

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace clip {

// Turns what each rule sees into moments, so one kill seen by two rules counts once. A HUD
// often shows a kill in more than one place: The Finals puts it in the killfeed, by name, and
// under the crosshair as a score line that stays up for seconds and is easily read twice.
//
// Sightings of the same event from rules with merge_ms set, each within merge_ms of the one
// before, are one group. Once the group has been open for a moment, so every rule has had a
// look, it's settled: the rule with the highest priority among those that saw it says how
// many there were, and the others back it up. A rule that alone saw it still counts it, so a
// kill one rule misread isn't lost. Sightings that come after it's settled count only when
// they raise that number, as a second kill in the killfeed does. Rules without merge_ms count
// everything they see, at once, as before.
//
// Every moment carries a witness for each sighting in it, which the app shows as why it counted.
class EventFuser {
 public:
  using Sink = std::function<void(const GameEvent&)>;
  // A sighting that wasn't counted, and why, for the log.
  using Note = std::function<void(const std::string&)>;

  // How long a group waits for the other rules to see what one rule saw.
  static constexpr int64_t kHoldMs = 1000;

  explicit EventFuser(Sink sink, Note note = {}) : sink_(std::move(sink)), note_(std::move(note)) {}

  // The rule saw `count` new moments at `time` (100 ns units, any clock that only goes
  // forward). where has the box it read, as fractions of the frame; its rule and text are
  // filled in here.
  void Add(const DetectionRule& rule, int count, int64_t time, const std::string& text, Witness where = {});

  // Settles the groups that have waited long enough, as of `now`. Call it every look.
  void Flush(int64_t now);
  // Settles everything, as at the end of a scan.
  void FlushAll();

 private:
  struct Sighting {
    std::string rule;
    int priority = 1;
    int count = 1;
    int64_t time = 0;
    Witness witness;
  };
  struct Group {
    EventType event = EventType::Highlight;
    int64_t first = 0;
    int64_t last = 0;
    int64_t merge = 0;  // the longest merge_ms of its rules, in 100 ns units
    std::vector<Sighting> sightings;
    int emitted = 0;
    bool settled = false;
  };

  // How many moments the group holds, and the rule whose count that is.
  static int Count(const Group& group, std::string* rule = nullptr);
  void Settle(Group& group);
  void Emit(EventType event, int64_t time, const std::string& text, std::vector<Witness> witnesses);

  Sink sink_;
  Note note_;
  std::vector<Group> groups_;
};

}  // namespace clip
