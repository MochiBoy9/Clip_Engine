#pragma once

#include "core/Win.h"

#include <d3d11.h>

#include <memory>
#include <mutex>

namespace clip {

// The D3D11 device shared by capture, color conversion and the hardware encoder, so frames
// never leave the GPU between the screen and the encoded video.
struct GpuDevice {
  winrt::com_ptr<ID3D11Device> device;
  winrt::com_ptr<ID3D11DeviceContext> context;
  LUID adapterLuid{};
  // Held for every use of the immediate context from Clip Engine's own threads.
  std::mutex contextLock;

  static std::shared_ptr<GpuDevice> Create();
};

}  // namespace clip
