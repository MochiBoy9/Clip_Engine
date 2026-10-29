#include "integrations/DiscordPresence.h"

#include "core/Json.h"
#include "core/Log.h"

#include <chrono>
#include <format>

namespace clip {
namespace {

using Clock = std::chrono::steady_clock;

// Discord's pipe messages.
constexpr uint32_t kHandshake = 0;
constexpr uint32_t kFrame = 1;
constexpr uint32_t kClose = 2;
constexpr uint32_t kPing = 3;
constexpr uint32_t kPong = 4;

// Discord takes 5 status changes per 20 seconds, so changes closer together are combined.
constexpr auto kMinGap = std::chrono::seconds(4);
constexpr auto kRetryConnect = std::chrono::seconds(15);
// After Discord turns the app down (an unknown application id), don't keep knocking.
constexpr auto kRetryRejected = std::chrono::minutes(5);
constexpr int kReplyTimeoutMs = 3000;
constexpr uint32_t kMaxMessage = 64 * 1024;

#ifndef CLIP_ENGINE_VERSION
#define CLIP_ENGINE_VERSION "dev"
#endif

}  // namespace

DiscordPresence::DiscordPresence(std::string appId) : appId_(std::move(appId)) {
  thread_ = std::thread([this] {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    try {
      Run();
    } catch (const std::exception& e) {
      log::Warn("Discord status stopped: {}", e.what());
    }
  });
}

DiscordPresence::~DiscordPresence() {
  {
    std::lock_guard lock(mutex_);
    stop_ = true;
  }
  wake_.notify_all();
  if (thread_.joinable()) thread_.join();
}

void DiscordPresence::Set(std::optional<PresenceState> state) {
  {
    std::lock_guard lock(mutex_);
    if (state == wanted_) return;
    wanted_ = std::move(state);
    dirty_ = true;
  }
  wake_.notify_all();
}

void DiscordPresence::Run() {
  auto nextConnect = Clock::now();
  auto nextSend = Clock::now();
  bool shown = false;  // Discord is showing a status of ours

  while (true) {
    std::optional<PresenceState> wanted;
    {
      std::unique_lock lock(mutex_);
      wake_.wait_for(lock, std::chrono::seconds(1), [&] { return stop_ || (dirty_ && Clock::now() >= nextSend); });
      if (stop_) break;
      if (!dirty_ || Clock::now() < nextSend) continue;
      wanted = wanted_;
    }
    // Nothing to show and nothing showing: no need to find Discord.
    if (!wanted && !shown) {
      std::lock_guard lock(mutex_);
      dirty_ = false;
      continue;
    }

    if (pipe_ == INVALID_HANDLE_VALUE) {
      if (Clock::now() < nextConnect) continue;
      if (!Connect()) {
        nextConnect = Clock::now() + (rejected_ ? kRetryRejected : kRetryConnect);
        continue;
      }
    }

    {
      // Taken before sending, so a change made meanwhile is sent next time round.
      std::lock_guard lock(mutex_);
      dirty_ = false;
      wanted = wanted_;
    }
    if (Publish(wanted)) {
      shown = wanted.has_value();
    } else {
      Disconnect();
      shown = false;
      std::lock_guard lock(mutex_);
      dirty_ = true;  // try again once Discord is back
      nextConnect = Clock::now() + kRetryConnect;
    }
    nextSend = Clock::now() + kMinGap;
  }

  // Clear the status on the way out. Discord also clears it when the pipe closes, but not
  // always straight away.
  if (pipe_ != INVALID_HANDLE_VALUE && shown) Publish(std::nullopt);
  Disconnect();
}

bool DiscordPresence::Connect() {
  rejected_ = false;
  // Discord listens on the first free one of discord-ipc-0 to 9.
  for (int i = 0; i < 10 && pipe_ == INVALID_HANDLE_VALUE; ++i) {
    const std::wstring name = std::format(L"\\\\.\\pipe\\discord-ipc-{}", i);
    pipe_ = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
  }
  if (pipe_ == INVALID_HANDLE_VALUE) return false;

  if (!Send(kHandshake, std::format(R"({{"v":1,"client_id":{}}})", JsonString(appId_)))) {
    Disconnect();
    return false;
  }
  const auto reply = Receive(kReplyTimeoutMs);
  if (!reply || reply->first != kFrame) {
    if (reply && reply->first == kClose) {
      rejected_ = true;
      const auto message = Json::Parse(reply->second);
      log::Warn("Discord didn't accept Clip Engine's status: {}", message ? (*message)["message"].Str() : reply->second);
    }
    Disconnect();
    return false;
  }
  if (!announced_) {
    log::Info("Showing what you play as your Discord status");
    announced_ = true;
  }
  return true;
}

void DiscordPresence::Disconnect() {
  if (pipe_ == INVALID_HANDLE_VALUE) return;
  CloseHandle(pipe_);
  pipe_ = INVALID_HANDLE_VALUE;
}

bool DiscordPresence::Send(uint32_t opcode, const std::string& json) {
  const std::string frame = DiscordFrame(opcode, json);
  DWORD written = 0;
  return WriteFile(pipe_, frame.data(), static_cast<DWORD>(frame.size()), &written, nullptr) &&
         written == frame.size();
}

std::optional<std::pair<uint32_t, std::string>> DiscordPresence::Receive(int timeoutMs) {
  const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
  auto readExactly = [&](char* into, DWORD size) {
    while (size > 0) {
      DWORD got = 0;
      if (!ReadFile(pipe_, into, size, &got, nullptr) || got == 0) return false;
      into += got;
      size -= got;
    }
    return true;
  };

  while (true) {
    // Wait for a whole header without blocking, so a Discord that never answers can't hang
    // this thread.
    DWORD available = 0;
    if (!PeekNamedPipe(pipe_, nullptr, 0, nullptr, &available, nullptr)) return std::nullopt;
    if (available < 8) {
      if (Clock::now() >= deadline) return std::nullopt;
      Sleep(20);
      continue;
    }
    unsigned char header[8];
    if (!readExactly(reinterpret_cast<char*>(header), sizeof(header))) return std::nullopt;
    auto word = [&](int at) {
      return uint32_t{header[at]} | uint32_t{header[at + 1]} << 8 | uint32_t{header[at + 2]} << 16 |
             uint32_t{header[at + 3]} << 24;
    };
    const uint32_t opcode = word(0);
    const uint32_t length = word(4);
    if (length > kMaxMessage) return std::nullopt;
    std::string body(length, '\0');
    if (length > 0 && !readExactly(body.data(), length)) return std::nullopt;
    if (opcode == kPing) {
      if (!Send(kPong, body)) return std::nullopt;
      continue;
    }
    return std::pair{opcode, std::move(body)};
  }
}

bool DiscordPresence::Publish(const std::optional<PresenceState>& state) {
  for (int attempt = 0; attempt < 2; ++attempt) {
    const std::string activity = state ? PresenceActivityJson(*state, CLIP_ENGINE_VERSION, showGameName_) : "";
    const std::string nonce = std::to_string(++nonce_);
    if (!Send(kFrame, SetActivityCommand(GetCurrentProcessId(), activity, nonce))) return false;

    // Skip anything else Discord sends until the answer to this command.
    std::optional<Json> answer;
    while (!answer) {
      const auto reply = Receive(kReplyTimeoutMs);
      if (!reply || reply->first != kFrame) return false;
      auto message = Json::Parse(reply->second);
      if (message && (*message)["nonce"].Str() == nonce) answer = std::move(message);
    }
    if ((*answer)["evt"].Str() != "ERROR") return true;
    // An older Discord that doesn't know the field naming the game in the member list; send
    // the status without it.
    if (state && showGameName_) {
      showGameName_ = false;
      continue;
    }
    log::Warn("Discord didn't take the status: {}", (*answer)["data"]["message"].Str());
    return true;
  }
  return true;
}

}  // namespace clip
