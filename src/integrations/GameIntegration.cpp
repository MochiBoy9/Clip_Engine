#include "integrations/GameIntegration.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "integrations/IntegrationSetup.h"
#include "integrations/StreamReaders.h"

#include <winhttp.h>
#include <winsock2.h>

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cstdlib>
#include <format>
#include <functional>
#include <random>
#include <utility>

namespace clip {

// ---------------------------------------------------------------------- Common

GameIntegration::~GameIntegration() { Stop(); }

void GameIntegration::Start() {
  thread_ = std::thread([this] {
    // Game events are never urgent next to the game itself.
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    try {
      Run();
    } catch (const std::exception& e) {
      log::Error("{} game events stopped: {}", gameName_, e.what());
    }
  });
}

void GameIntegration::Stop() {
  {
    std::lock_guard lock(mutex_);
    stop_ = true;
  }
  wake_.notify_all();
  if (thread_.joinable()) thread_.join();
}

bool GameIntegration::Wait(int ms) {
  std::unique_lock lock(mutex_);
  return !wake_.wait_for(lock, std::chrono::milliseconds(ms), [&] { return stop_.load(); });
}

std::vector<GameEvent> GameIntegration::TakeEvents() {
  std::lock_guard lock(mutex_);
  return std::exchange(events_, {});
}

void GameIntegration::Emit(const std::vector<ApiEvent>& events) {
  if (events.empty()) return;
  const int64_t now = NowHns();
  std::lock_guard lock(mutex_);
  for (const auto& e : events) {
    events_.push_back(GameEvent{e.type, now - static_cast<int64_t>(e.secondsAgo * kHnsPerSecond), e.text});
  }
}

void GameIntegration::Connected() {
  if (connected_) return;
  connected_ = true;
  log::Info("Receiving game events from {}", gameName_);
}

namespace {

class WinsockSession {
 public:
  WinsockSession() {
    WSADATA data{};
    ok_ = WSAStartup(MAKEWORD(2, 2), &data) == 0;
  }
  ~WinsockSession() {
    if (ok_) WSACleanup();
  }
  bool Ok() const { return ok_; }

 private:
  bool ok_ = false;
};

// Waits up to ms for the socket to have data (or a connection to accept). False on timeout.
bool Readable(SOCKET socket, int ms) {
  fd_set set;
  FD_ZERO(&set);
  FD_SET(socket, &set);
  timeval timeout{ms / 1000, (ms % 1000) * 1000};
  return select(0, &set, nullptr, nullptr, &timeout) > 0;
}

std::string Lower(std::string s) {
  std::ranges::transform(s, s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  return s;
}

// ---------------------------------------------------------------------- Valve Game State Integration

// A tiny HTTP server on 127.0.0.1 that takes the game's state posts. CS2 and Dota 2 post the
// whole state as JSON whenever something changes.
class GsiIntegration final : public GameIntegration {
 public:
  using Handler = std::function<std::vector<ApiEvent>(const Json&, double)>;

  GsiIntegration(std::string gameName, std::string hint, Handler handler)
      : GameIntegration(std::move(gameName)), hint_(std::move(hint)), handler_(std::move(handler)) {
    Start();
  }
  ~GsiIntegration() override { Stop(); }

 private:
  void Run() override {
    WinsockSession winsock;
    if (!winsock.Ok()) return;
    const SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) return;
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(kGsiPort);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    BOOL exclusive = TRUE;
    setsockopt(listener, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));
    if (bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0 || listen(listener, 4) != 0) {
      log::Error("Couldn't listen for {} game events on port {}; another program is using it", gameName_, kGsiPort);
      closesocket(listener);
      return;
    }

    const auto started = std::chrono::steady_clock::now();
    bool hinted = false;
    while (!stop_) {
      if (!hinted && !received_ && std::chrono::steady_clock::now() - started > std::chrono::seconds(90)) {
        log::Warn("{} hasn't sent any game events yet. {}", gameName_, hint_);
        hinted = true;
      }
      if (!Readable(listener, 250)) continue;
      const SOCKET client = accept(listener, nullptr, nullptr);
      if (client == INVALID_SOCKET) continue;
      HandleRequest(client);
      closesocket(client);
    }
    closesocket(listener);
  }

  void HandleRequest(SOCKET client) {
    std::string request;
    size_t headerEnd = std::string::npos;
    size_t contentLength = 0;
    char buffer[16384];
    while (!stop_) {
      if (!Readable(client, 2000)) return;
      const int n = recv(client, buffer, sizeof(buffer), 0);
      if (n <= 0) return;
      request.append(buffer, n);
      if (request.size() > (8u << 20)) return;
      if (headerEnd == std::string::npos) {
        headerEnd = request.find("\r\n\r\n");
        if (headerEnd == std::string::npos) continue;
        const std::string headers = Lower(request.substr(0, headerEnd));
        if (const auto at = headers.find("content-length:"); at != std::string::npos) {
          contentLength = std::strtoul(headers.c_str() + at + 15, nullptr, 10);
        }
      }
      if (request.size() >= headerEnd + 4 + contentLength) break;
    }
    if (headerEnd == std::string::npos) return;

    static constexpr char kOk[] = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
    send(client, kOk, static_cast<int>(sizeof(kOk) - 1), 0);

    const auto state = Json::Parse(std::string_view(request).substr(headerEnd + 4, contentLength));
    if (!state || (*state)["auth"]["token"].Str() != kGsiToken) return;
    received_ = true;
    Connected();
    const double now = std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
    Emit(handler_(*state, now));
  }

  std::string hint_;
  Handler handler_;
  bool received_ = false;
};

// ---------------------------------------------------------------------- League of Legends

// Polls the Live Client Data API that League serves on https://127.0.0.1:2999 during a game.
class LeagueIntegration final : public GameIntegration {
 public:
  LeagueIntegration() : GameIntegration("League of Legends") { Start(); }
  ~LeagueIntegration() override { Stop(); }

 private:
  void Run() override {
    HINTERNET session = WinHttpOpen(L"ClipEngine", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME,
                                    WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return;
    WinHttpSetTimeouts(session, 2000, 2000, 2000, 3000);
    HINTERNET connection = WinHttpConnect(session, L"127.0.0.1", 2999, 0);
    while (connection && Wait(1000)) {
      const std::string body = Get(connection, L"/liveclientdata/allgamedata");
      if (body.empty()) continue;  // still loading, or between games
      const auto data = Json::Parse(body);
      if (!data || !data->Has("activePlayer")) continue;
      Connected();
      Emit(tracker_.Update(*data));
    }
    if (connection) WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
  }

  static std::string Get(HINTERNET connection, const wchar_t* path) {
    std::string body;
    HINTERNET request = WinHttpOpenRequest(connection, L"GET", path, nullptr, WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    if (!request) return body;
    // The game serves this on 127.0.0.1 only, with a certificate signed by Riot's own root
    // that Windows doesn't know.
    DWORD flags = SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_CN_INVALID |
                  SECURITY_FLAG_IGNORE_CERT_DATE_INVALID | SECURITY_FLAG_IGNORE_CERT_WRONG_USAGE;
    WinHttpSetOption(request, WINHTTP_OPTION_SECURITY_FLAGS, &flags, sizeof(flags));
    DWORD status = 0;
    DWORD size = sizeof(status);
    if (WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
        WinHttpReceiveResponse(request, nullptr) &&
        WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX,
                            &status, &size, WINHTTP_NO_HEADER_INDEX) &&
        status == 200) {
      char buffer[16384];
      DWORD read = 0;
      while (WinHttpReadData(request, buffer, sizeof(buffer), &read) && read > 0) body.append(buffer, read);
    }
    WinHttpCloseHandle(request);
    return body;
  }

  LeagueTracker tracker_;
};

// ---------------------------------------------------------------------- Rocket League

// Reads the Stats API that Rocket League serves on a local port once it's turned on in
// DefaultStatsAPI.ini. Psyonix calls it a web socket; the stream may be plain JSON objects,
// so both are handled: if the game says nothing after connecting, a WebSocket handshake is
// sent, and the first bytes back decide which it is.
class RocketLeagueIntegration final : public GameIntegration {
 public:
  explicit RocketLeagueIntegration(unsigned short port) : GameIntegration("Rocket League"), port_(port) { Start(); }
  ~RocketLeagueIntegration() override { Stop(); }

 private:
  void Run() override {
    WinsockSession winsock;
    if (!winsock.Ok()) return;
    const auto started = std::chrono::steady_clock::now();
    bool hinted = false;
    while (!stop_) {
      ReadOnce();
      if (!hinted && !received_ && std::chrono::steady_clock::now() - started > std::chrono::seconds(90)) {
        log::Warn("Rocket League hasn't sent any game events yet. Restart it once so it picks up the Stats API "
                  "setting Clip Engine turned on.");
        hinted = true;
      }
      if (!Wait(3000)) break;
    }
  }

  // One connection, until it closes.
  void ReadOnce() {
    const SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return;
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(port_);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(s, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0) {
      closesocket(s);
      return;
    }

    enum class Mode { Unknown, Raw, Handshake, WebSocket } mode = Mode::Unknown;
    std::string pending;
    JsonObjectStream objects;
    WebSocketReader frames;
    std::mt19937 random(std::random_device{}());
    const auto connectedAt = std::chrono::steady_clock::now();
    char buffer[16384];

    while (!stop_) {
      if (!Readable(s, 250)) {
        if (mode == Mode::Unknown && std::chrono::steady_clock::now() - connectedAt > std::chrono::milliseconds(1500)) {
          const std::string handshake = std::format(
              "GET / HTTP/1.1\r\nHost: 127.0.0.1:{}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
              "Sec-WebSocket-Key: Q2xpcEVuZ2luZVN0YXRzQQ==\r\nSec-WebSocket-Version: 13\r\n\r\n",
              port_);
          send(s, handshake.data(), static_cast<int>(handshake.size()), 0);
          mode = Mode::Handshake;
        }
        continue;
      }
      const int n = recv(s, buffer, sizeof(buffer), 0);
      if (n <= 0) break;
      std::string_view bytes(buffer, n);

      if (mode == Mode::Unknown || mode == Mode::Handshake) {
        pending.append(bytes);
        const auto first = pending.find_first_not_of(" \r\n\t");
        if (first == std::string::npos) continue;
        if (pending[first] == '{') {
          mode = Mode::Raw;
          bytes = pending;
        } else if (pending.compare(first, 5, "HTTP/") == 0) {
          const auto end = pending.find("\r\n\r\n");
          if (end == std::string::npos) continue;
          if (pending.find(" 101 ") == std::string::npos) break;  // not a WebSocket after all
          mode = Mode::WebSocket;
          frames.Feed(std::string_view(pending).substr(end + 4));
          bytes = {};
        } else {
          break;
        }
      }

      if (mode == Mode::Raw) {
        objects.Feed(bytes);
        while (auto object = objects.Next()) Handle(*object);
        pending.clear();
      } else if (mode == Mode::WebSocket) {
        frames.Feed(bytes);
        bool closed = false;
        while (auto message = frames.Next()) {
          if (message->kind == WebSocketReader::Kind::Text) {
            objects.Feed(message->payload);
            while (auto object = objects.Next()) Handle(*object);
          } else if (message->kind == WebSocketReader::Kind::Ping) {
            const auto pong = WebSocketReader::ClientFrame(0xA, message->payload, static_cast<uint32_t>(random()));
            send(s, pong.data(), static_cast<int>(pong.size()), 0);
          } else if (message->kind == WebSocketReader::Kind::Close) {
            closed = true;
          }
        }
        pending.clear();
        if (closed) break;
      }
    }
    closesocket(s);
  }

  void Handle(const std::string& object) {
    const auto message = Json::Parse(object);
    if (!message || !message->Has("Event")) return;
    received_ = true;
    Connected();
    Emit(tracker_.Update(*message));
  }

  unsigned short port_;
  bool received_ = false;
  RocketLeagueTracker tracker_;
};

}  // namespace

std::unique_ptr<GameIntegration> StartGameIntegration(const DetectedGame& game) {
  const GameProfile& profile = *game.profile;
  const auto exe = ProcessImagePath(game.processId);
  switch (profile.api) {
    case GameApi::Cs2Gsi: {
      if (!exe.empty()) InstallCs2Config(exe);
      auto tracker = std::make_shared<Cs2Tracker>();
      return std::make_unique<GsiIntegration>(
          profile.name, "If Clip Engine just turned them on, restart Counter-Strike 2 once.",
          [tracker](const Json& state, double) { return tracker->Update(state); });
    }
    case GameApi::Dota2Gsi: {
      if (!exe.empty()) InstallDota2Config(exe);
      auto tracker = std::make_shared<Dota2Tracker>();
      return std::make_unique<GsiIntegration>(
          profile.name,
          "Restart Dota 2 once, and if that doesn't help add -gamestateintegration to its launch options in Steam "
          "(Properties > General).",
          [tracker](const Json& state, double now) { return tracker->Update(state, now); });
    }
    case GameApi::LeagueLiveClient:
      return std::make_unique<LeagueIntegration>();
    case GameApi::RocketLeagueStats: {
      unsigned short port = kRocketLeagueDefaultPort;
      if (!exe.empty()) EnableRocketLeagueStats(exe, port);
      return std::make_unique<RocketLeagueIntegration>(port);
    }
    default:
      return nullptr;
  }
}

}  // namespace clip
