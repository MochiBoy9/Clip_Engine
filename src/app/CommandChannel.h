#pragma once

#include "core/Win.h"

#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace clip {

// Commands from the desktop app, one per line on the engine's standard input (squad sync's
// "squad-save ..."). Only listens when standard input is a pipe, which it is when the desktop
// app runs the engine; run from a console, the engine ignores what's typed. Event() is set
// whenever a line arrives, so the main loop can wait on it with its other events.
class CommandChannel {
 public:
  CommandChannel();
  ~CommandChannel();

  CommandChannel(const CommandChannel&) = delete;
  CommandChannel& operator=(const CommandChannel&) = delete;

  bool Listening() const { return listening_; }
  HANDLE Event() const { return state_->event; }
  std::vector<std::string> Take();

 private:
  struct State {
    std::mutex mutex;
    std::deque<std::string> lines;
    HANDLE event = nullptr;
  };
  // Shared with the reading thread, which may still be blocked in ReadFile when the engine
  // exits, so it can't be joined; it owns its own reference.
  std::shared_ptr<State> state_;
  bool listening_ = false;
};

}  // namespace clip
