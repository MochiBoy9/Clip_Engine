#pragma once

#include "capture/GpuDevice.h"

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <atomic>
#include <memory>

namespace clip {

// Captures a whole monitor with Windows.Graphics.Capture. This works with borderless and
// fullscreen games and never touches the game process, so it is safe with anti-cheat.
class MonitorCapture {
 public:
  MonitorCapture(std::shared_ptr<GpuDevice> gpu, HMONITOR monitor);
  ~MonitorCapture();

  MonitorCapture(const MonitorCapture&) = delete;
  MonitorCapture& operator=(const MonitorCapture&) = delete;

  UINT Width() const { return width_; }
  UINT Height() const { return height_; }

  // BGRA texture holding the newest frame, or null before the first frame arrives.
  // Read it only while holding the GpuDevice context lock.
  ID3D11Texture2D* LatestFrame() const { return hasFrame_ ? latest_.get() : nullptr; }

  // True once the monitor's resolution changed; the session must be restarted.
  bool SizeChanged() const { return sizeChanged_; }

  static bool IsSupported();

 private:
  void OnFrameArrived(const winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool& pool,
                      const winrt::Windows::Foundation::IInspectable&);

  std::shared_ptr<GpuDevice> gpu_;
  UINT width_ = 0;
  UINT height_ = 0;
  winrt::com_ptr<ID3D11Texture2D> latest_;
  std::atomic<bool> hasFrame_{false};
  std::atomic<bool> sizeChanged_{false};

  winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice winrtDevice_{nullptr};
  winrt::Windows::Graphics::Capture::GraphicsCaptureItem item_{nullptr};
  winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool framePool_{nullptr};
  winrt::Windows::Graphics::Capture::GraphicsCaptureSession session_{nullptr};
  winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool::FrameArrived_revoker frameArrived_;
};

}  // namespace clip
