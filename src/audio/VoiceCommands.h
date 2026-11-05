#pragma once

#include "core/AudioSettings.h"
#include "core/Win.h"

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <thread>
#include <vector>

namespace clip {

class SpeechStream;

// Listens to the mic for the player's voice commands ("clip that"), with the speech recognition
// built into Windows, which runs on this PC: nothing is sent anywhere. It only listens for the
// phrases it's given, which keeps it light, around a percent of one CPU core while someone talks
// and nothing in silence. Heard is called on its own thread.
class VoiceCommands {
 public:
  using Heard = std::function<void(const VoicePhrase& phrase)>;

  VoiceCommands(std::vector<VoicePhrase> phrases, VoiceSensitivity sensitivity, Heard heard);
  ~VoiceCommands();

  VoiceCommands(const VoiceCommands&) = delete;
  VoiceCommands& operator=(const VoiceCommands&) = delete;

  // The mic's 48 kHz stereo audio. Called from the mic's capture thread; quick.
  void Hear(const int16_t* pcm, uint32_t frames);

 private:
  void Run();

  std::vector<VoicePhrase> phrases_;
  VoiceSensitivity sensitivity_;
  Heard heard_;
  winrt::com_ptr<SpeechStream> stream_;
  std::vector<int32_t> carry_;
  std::vector<int16_t> mono_;
  std::atomic<bool> stop_{false};
  std::thread thread_;
};

}  // namespace clip
