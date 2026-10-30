#pragma once

#include "capture/GpuDevice.h"

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <atomic>
#include <memory>
#include <mutex>

namespace clip {

// Captures a whole monitor with Windows.Graphics.Capture. This works with borderless and
// fullscreen games and never touches the game process, so it is safe with anti-cheat.
//
// A frame Windows delivers is only held on to; it's copied out when it's asked for. So a
// 240 Hz monitor recorded at 120 fps costs 120 copies a second rather than 240, a screen that
// isn't changing costs none, and while the game isn't in front (nothing asks) nothing is
// copied at all.
class MonitorCapture {
 public:
  // fps: the rate frames are used at; Windows is asked not to deliver them faster.
  MonitorCapture(std::shared_ptr<GpuDevice> gpu, HMONITOR monitor, UINT fps);
  ~MonitorCapture();

  MonitorCapture(const MonitorCapture&) = delete;
  MonitorCapture& operator=(const MonitorCapture&) = delete;

  UINT Width() const { return width_; }
  UINT Height() const { return height_; }

  // BGRA texture holding the newest frame, or null before the first frame arrives.
  // Call it, and read the texture, only while holding the GpuDevice context lock.
  ID3D11Texture2D* LatestFrame() const;

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
  mutable bool hasFrame_ = false;  // under the context lock
  std::atomic<bool> sizeChanged_{false};

  // The newest frame Windows delivered that isn't in latest_ yet. Holding it keeps Windows
  // from drawing over it; the pool has a second buffer for the frame after.
  mutable std::mutex pendingMutex_;
  mutable winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame pending_{nullptr};

  winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice winrtDevice_{nullptr};
  winrt::Windows::Graphics::Capture::GraphicsCaptureItem item_{nullptr};
  winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool framePool_{nullptr};
  winrt::Windows::Graphics::Capture::GraphicsCaptureSession session_{nullptr};
  winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool::FrameArrived_revoker frameArrived_;
};

}  // namespace clip
