#include "detect/EventFuser.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <format>
#include <map>

namespace clip {
namespace {

constexpr int64_t kTicksPerMs = 10'000;

}  // namespace

void EventFuser::Add(const DetectionRule& rule, int count, int64_t time, const std::string& text, Witness where) {
  if (count <= 0) return;
  where.rule = rule.name;
  where.text = text;
  where.offset = 0;
  where.counted = true;
  if (rule.mergeMs <= 0 && !rule.confirmOnly) {
    for (int i = 0; i < count; ++i) Emit(rule.event, time, text, {where});
    return;
  }

  // A rule that only backs others up has to wait for them however it's set.
  const int64_t merge = int64_t{rule.mergeMs > 0 ? rule.mergeMs : 2000} * kTicksPerMs;
  auto group = std::ranges::find_if(groups_, [&](const Group& g) {
    return g.event == rule.event && time >= g.first && time - g.last <= std::max(merge, g.merge);
  });
  if (group == groups_.end()) {
    groups_.push_back({rule.event, time, time, merge, {}, 0, false});
    group = groups_.end() - 1;
  }
  group->last = std::max(group->last, time);
  group->merge = std::max(group->merge, merge);
  group->sightings.push_back({rule.name, rule.priority, rule.confirmOnly, count, time, where});
  if (!group->settled) return;
  // Only a backing-up rule had seen it: settle it again now that something can count it.
  if (group->emitted == 0) {
    Settle(*group);
    return;
  }

  // Already counted: this counts only if it shows there were more.
  const int target = Count(*group);
  if (target > group->emitted) {
    for (int i = group->emitted; i < target; ++i) Emit(rule.event, time, text, {where});
    group->emitted = target;
  } else if (note_) {
    note_(std::format("[{}] saw the same {} again {:.1f}s after it was counted; not counted again", rule.name,
                      EventName(rule.event), static_cast<double>(time - group->first) / (kTicksPerMs * 1000)));
  }
}

void EventFuser::Flush(int64_t now) {
  for (auto& group : groups_) {
    if (!group.settled && now - group.first >= kHoldMs * kTicksPerMs) Settle(group);
  }
  std::erase_if(groups_, [&](const Group& g) { return g.settled && now - g.last > g.merge; });
}

void EventFuser::FlushAll() {
  for (auto& group : groups_) {
    if (!group.settled) Settle(group);
  }
  groups_.clear();
}

int EventFuser::Count(const Group& group, std::string* rule) {
  int best = -1;
  for (const auto& s : group.sightings) {
    if (!s.confirmOnly) best = std::max(best, s.priority);
  }
  // Of the rules with the highest priority, the one that saw the most.
  std::map<std::string, int> counts;
  for (const auto& s : group.sightings) {
    if (!s.confirmOnly && s.priority == best) counts[s.rule] += s.count;
  }
  int most = 0;
  for (const auto& [name, count] : counts) {
    if (count > most) {
      most = count;
      if (rule) *rule = name;
    }
  }
  return most;
}

void EventFuser::Settle(Group& group) {
  group.settled = true;
  std::string counting;
  group.emitted = Count(group, &counting);
  if (group.emitted == 0) {
    if (note_) {
      note_(std::format("[{}] noticed a {} nothing else saw; not counted", group.sightings.front().rule,
                        EventName(group.event)));
    }
    return;
  }

  // One moment for each of the counting rule's sightings, at its time.
  struct Moment {
    int64_t time;
    std::string text;
    std::vector<std::pair<int64_t, Witness>> witnesses;  // with the time of each sighting
  };
  std::vector<Moment> moments;
  auto byTime = group.sightings;
  std::ranges::stable_sort(byTime, {}, &Sighting::time);
  for (const auto& s : byTime) {
    if (s.rule != counting) continue;
    for (int i = 0; i < s.count; ++i) moments.push_back({s.time, s.witness.text, {{s.time, s.witness}}});
  }
  // The other rules' sightings back up the moment nearest them.
  std::map<std::string, int> otherCounts;
  for (const auto& s : byTime) {
    if (s.rule == counting) continue;
    otherCounts[s.rule] += s.count;
    auto nearest = std::ranges::min_element(moments, {}, [&](const Moment& m) { return std::llabs(m.time - s.time); });
    Witness w = s.witness;
    w.counted = false;
    nearest->witnesses.emplace_back(s.time, std::move(w));
  }
  if (note_) {
    for (const auto& [rule, count] : otherCounts) {
      if (count == group.emitted) continue;
      note_(std::format("[{}] saw {} {} where [{}] saw {}; counted {}", rule, count, EventName(group.event), counting,
                        group.emitted, group.emitted));
    }
  }

  for (auto& m : moments) {
    std::vector<Witness> witnesses;
    for (auto& [time, w] : m.witnesses) {
      w.offset = static_cast<double>(time - m.time) / (kTicksPerMs * 1000);
      witnesses.push_back(std::move(w));
    }
    Emit(group.event, m.time, m.text, std::move(witnesses));
  }
}

void EventFuser::Emit(EventType event, int64_t time, const std::string& text, std::vector<Witness> witnesses) {
  sink_(GameEvent{event, time, text, std::move(witnesses)});
}

}  // namespace clip
