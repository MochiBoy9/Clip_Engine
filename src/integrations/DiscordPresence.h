#pragma once

#include "core/Win.h"
#include "integrations/DiscordActivity.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <utility>

namespace clip {

// Shows what's being played and recorded as the user's Discord status (Rich Presence), over
// the local pipe the Discord app listens on. Nothing happens while Discord isn't running; it's
// found when it starts. Talking to Discord runs on its own thread, so a slow or hung Discord
// never holds up recording.
class DiscordPresence {
 public:
  explicit DiscordPresence(std::string appId);
  ~DiscordPresence();

  DiscordPresence(const DiscordPresence&) = delete;
  DiscordPresence& operator=(const DiscordPresence&) = delete;

  // The status to show; nothing clears it. Cheap to call often: only changes go to Discord.
  void Set(std::optional<PresenceState> state);

 private:
  void Run();
  bool Connect();
  void Disconnect();
  bool Send(uint32_t opcode, const std::string& json);
  // The next message from Discord, answering its pings on the way.
  std::optional<std::pair<uint32_t, std::string>> Receive(int timeoutMs);
  // Sends the status and checks Discord took it. False when the pipe broke.
  bool Publish(const std::optional<PresenceState>& state);

  const std::string appId_;
  HANDLE pipe_ = INVALID_HANDLE_VALUE;
  bool showGameName_ = true;  // newer Discord can show the game's name in the member list
  uint64_t nonce_ = 0;
  bool announced_ = false;
  bool rejected_ = false;  // Discord turned the last connection down

  std::mutex mutex_;
  std::condition_variable wake_;
  std::optional<PresenceState> wanted_;
  bool dirty_ = false;
  std::atomic<bool> stop_{false};
  std::thread thread_;
};

}  // namespace clip
