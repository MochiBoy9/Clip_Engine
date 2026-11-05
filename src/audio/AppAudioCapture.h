#pragma once

#include "audio/MixClock.h"
#include "audio/PcmMixer.h"
#include "core/Win.h"

#include <atomic>
#include <functional>
#include <memory>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace clip {

// A process with sound on the PC's speakers or headphones, from Windows' audio sessions.
struct AudioApp {
  DWORD processId = 0;
  std::string exe;  // file name, like "Discord.exe"
};

// The apps with an audio session on any speakers or headphones right now.
std::vector<AudioApp> AppsWithAudio();
// Each running process's parent, for telling which processes belong to the game.
std::unordered_map<uint32_t, uint32_t> ParentProcesses();

// Records what some apps play, and nothing else, with Windows' process loopback (Windows 10
// version 2004 and later): each app with its child processes, all added together into one 48 kHz
// stereo stream on a steady clock. Which apps can change while it runs, so the game's track picks
// the game up once it's found and the Discord track picks Discord up once a call starts.
class AppAudioCapture {
 public:
  using Sink = MixClock::Sink;
  // Called on the capture thread every two seconds: the processes to record now. exe is only for
  // the log, and may be empty.
  using Targets = std::function<std::vector<AudioApp>()>;

  AppAudioCapture(std::string name, Targets targets, Sink sink);
  ~AppAudioCapture();

  AppAudioCapture(const AppAudioCapture&) = delete;
  AppAudioCapture& operator=(const AppAudioCapture&) = delete;

  // Asks for the targets again now instead of at the next two-second check, as when the game's
  // process is found.
  void Refresh() { refresh_ = true; }

  // Whether this Windows can record a single app's audio. Checked once.
  static bool Supported();

 private:
  void Run();

  std::string name_;
  Targets targets_;
  PcmMixer mixer_;
  std::unique_ptr<MixClock> clock_;
  std::atomic<bool> refresh_{false};
  std::atomic<bool> stop_{false};
  std::thread thread_;
};

}  // namespace clip
