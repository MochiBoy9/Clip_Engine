#pragma once

#include "core/Win.h"

namespace clip {

// While one of these is alive, Windows runs the thread that made it in its power-saving mode
// (EcoQoS): on efficiency cores where the CPU has them, and at a lower clock elsewhere, so
// background work like reading the screen or writing a clip leaves the fast cores to the
// game and draws less power. Not for anything that keeps time, like the frame pacer or
// audio capture. Does nothing on Windows versions without it.
class EfficiencyMode {
 public:
  EfficiencyMode() { Set(true); }
  // Hands the thread back to Windows' own judgment, as a pooled thread may run other work next.
  ~EfficiencyMode() { Set(false); }

  EfficiencyMode(const EfficiencyMode&) = delete;
  EfficiencyMode& operator=(const EfficiencyMode&) = delete;

 private:
  static void Set(bool on) {
    THREAD_POWER_THROTTLING_STATE state{};
    state.Version = THREAD_POWER_THROTTLING_CURRENT_VERSION;
    state.ControlMask = on ? THREAD_POWER_THROTTLING_EXECUTION_SPEED : 0;
    state.StateMask = on ? THREAD_POWER_THROTTLING_EXECUTION_SPEED : 0;
    SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, &state, sizeof(state));
  }
};

}  // namespace clip
