#include "app/CommandChannel.h"

#include <thread>

namespace clip {
namespace {

constexpr size_t kMaxLine = 4096;
constexpr size_t kMaxQueued = 256;

}  // namespace

CommandChannel::CommandChannel() : state_(std::make_shared<State>()) {
  state_->event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
  const HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
  if (!state_->event || input == nullptr || input == INVALID_HANDLE_VALUE || GetFileType(input) != FILE_TYPE_PIPE) return;
  listening_ = true;
  std::thread([state = state_, input] {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    std::string line;
    char buffer[1024];
    DWORD got = 0;
    // Ends when the app closes its end of the pipe.
    while (ReadFile(input, buffer, sizeof(buffer), &got, nullptr) && got > 0) {
      for (DWORD i = 0; i < got; ++i) {
        const char c = buffer[i];
        if (c != '\n') {
          if (line.size() < kMaxLine) line += c;
          continue;
        }
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (!line.empty()) {
          std::lock_guard lock(state->mutex);
          if (state->lines.size() < kMaxQueued) state->lines.push_back(std::move(line));
          SetEvent(state->event);
        }
        line.clear();
      }
    }
  }).detach();
}

CommandChannel::~CommandChannel() {
  // The event stays open while the reading thread might still set it; it goes with the process.
}

std::vector<std::string> CommandChannel::Take() {
  std::lock_guard lock(state_->mutex);
  std::vector<std::string> lines(std::make_move_iterator(state_->lines.begin()), std::make_move_iterator(state_->lines.end()));
  state_->lines.clear();
  return lines;
}

}  // namespace clip
