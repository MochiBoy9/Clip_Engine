#pragma once

#include <algorithm>
#include <cstdint>
#include <deque>
#include <vector>

namespace clip {

// Decides which stretches of gameplay to save as automatic clips. Each event asks for a
// window from a little before it to a little after; events close together share one clip,
// so a triple kill is one clip instead of three overlapping ones.
class AutoClipPlanner {
 public:
  struct Window {
    int64_t start = 0;
    int64_t end = 0;
  };

  AutoClipPlanner(int64_t before, int64_t after, int64_t maxLength)
      : before_(before), after_(after), maxLength_(std::max(maxLength, before + after)) {}

  void Add(int64_t eventTime) {
    const int64_t start = eventTime - before_;
    const int64_t end = eventTime + after_;
    if (!pending_.empty() && start <= pending_.back().end) {
      Window& last = pending_.back();
      if (std::max(end, last.end) - last.start <= maxLength_) {
        last.end = std::max(end, last.end);
        return;
      }
      // Too long for one clip: carry on in a new one that starts where the last ends.
      pending_.push_back({last.end, end});
      return;
    }
    pending_.push_back({start, end});
  }

  // Windows that have fully happened by now.
  std::vector<Window> TakeReady(int64_t now) {
    std::vector<Window> ready;
    while (!pending_.empty() && pending_.front().end <= now) {
      ready.push_back(pending_.front());
      pending_.pop_front();
    }
    return ready;
  }

  // Everything still waiting, for when recording stops early.
  std::vector<Window> TakeAll() {
    std::vector<Window> all(pending_.begin(), pending_.end());
    pending_.clear();
    return all;
  }

  bool Empty() const { return pending_.empty(); }

 private:
  int64_t before_;
  int64_t after_;
  int64_t maxLength_;
  std::deque<Window> pending_;
};

}  // namespace clip
