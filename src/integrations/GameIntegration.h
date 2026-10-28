#pragma once

#include "detect/GameEvent.h"
#include "game/GameDetector.h"
#include "integrations/GameStateTrackers.h"

#include <atomic>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <optional>
#include <thread>
#include <vector>

namespace clip {

// Moments from a game's own live API, for games that have one: Counter-Strike 2 and Dota 2
// (Game State Integration), League of Legends (Live Client Data API) and Rocket League (Stats
// API). These are exact, where reading the screen is a best guess, and they work whatever the
// HUD looks like. Everything runs on a background thread at low priority.
class GameIntegration {
 public:
  virtual ~GameIntegration();

  GameIntegration(const GameIntegration&) = delete;
  GameIntegration& operator=(const GameIntegration&) = delete;

  // Moments reported since the last call, timed on the engine's clock.
  std::vector<GameEvent> TakeEvents();
  // The player's totals for the match, when they changed since the last call.
  std::optional<Scoreboard> TakeScoreboard();

 protected:
  explicit GameIntegration(std::string gameName) : gameName_(std::move(gameName)) {}

  // Starts Run() on the background thread. Called by the subclass once it's constructed.
  void Start();
  // Ends Run() and waits for it. Every subclass calls this first in its destructor, before
  // the members Run() uses go away.
  void Stop();
  virtual void Run() = 0;
  // Sleeps up to ms, waking early when stopping. False once stopping.
  bool Wait(int ms);

  void Emit(const std::vector<ApiEvent>& events);
  // The tracker's current totals; kept when they differ from the last ones.
  void Report(const Stats& board);
  // Logs once that the game is talking to us.
  void Connected();

  const std::string gameName_;
  std::atomic<bool> stop_{false};

 private:
  std::mutex mutex_;
  std::condition_variable wake_;
  std::vector<GameEvent> events_;
  Stats board_;
  std::optional<Scoreboard> newBoard_;
  bool connected_ = false;
  std::thread thread_;
};

// Starts the API reader for a running game, or returns null for games without one.
std::unique_ptr<GameIntegration> StartGameIntegration(const DetectedGame& game);

}  // namespace clip
