#include "audio/MixClock.h"

#include "core/Clock.h"
#include "core/Log.h"

#include <avrt.h>

#include <algorithm>
#include <vector>

namespace clip {

MixClock::MixClock(PcmMixer& mixer, int64_t latency, Sink sink)
    : mixer_(mixer), latency_(latency), sink_(std::move(sink)) {
  thread_ = std::thread([this] { Run(); });
}

MixClock::~MixClock() {
  stop_ = true;
  if (thread_.joinable()) thread_.join();
}

void MixClock::Run() {
  DWORD taskIndex = 0;
  HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);
  std::vector<int16_t> out;
  int64_t next = PcmMixer::HnsToFrames(NowHns() - latency_);
  while (!stop_) {
    Sleep(10);
    const int64_t until = PcmMixer::HnsToFrames(NowHns() - latency_);
    // After a long stall (the PC slept), carry on from now rather than mixing the whole gap.
    if (until - next > PcmMixer::kRate) next = until - PcmMixer::kRate / 10;
    if (until <= next) continue;
    const auto frames = static_cast<uint32_t>(until - next);
    mixer_.Mix(next, frames, out);
    try {
      sink_(out.data(), frames, PcmMixer::FramesToHns(next));
    } catch (const winrt::hresult_error& e) {
      log::Error("Audio mixing failed: {}", Describe(e));
    }
    next = until;
  }
  if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
}

}  // namespace clip
