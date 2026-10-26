#pragma once

#include "capture/GpuDevice.h"
#include "media/ReplayBuffer.h"
#include "video/Nv12Converter.h"

#include <mftransform.h>

#include <atomic>
#include <condition_variable>
#include <deque>
#include <memory>
#include <string>
#include <thread>

namespace clip {

struct VideoSettings {
  UINT width = 0;   // even
  UINT height = 0;  // even
  UINT fps = 60;
  UINT bitrate = 0;  // bits per second
  bool hevc = false;
};

// Hardware H.264/HEVC encoder (NVIDIA NVENC, AMD AMF or Intel Quick Sync through Media
// Foundation). Frames stay on the GPU from capture to encoder; encoded packets go straight
// into the replay buffer.
class VideoEncoder {
 public:
  VideoEncoder(std::shared_ptr<GpuDevice> gpu, UINT captureWidth, UINT captureHeight,
               const VideoSettings& settings, ReplayBuffer& buffer);
  ~VideoEncoder();

  VideoEncoder(const VideoEncoder&) = delete;
  VideoEncoder& operator=(const VideoEncoder&) = delete;

  // Converts the frame to NV12 and queues it at the given timestamp. Returns false when the
  // frame was dropped because the encoder is behind.
  bool Submit(ID3D11Texture2D* bgra, int64_t pts);

  const std::string& Name() const { return name_; }
  bool Failed() const { return failed_; }
  uint64_t DroppedFrames() const { return dropped_; }

  struct SurfacePool;

 private:
  void CreateTransform();
  void ConfigureTransform();
  void Run();
  winrt::com_ptr<IMFSample> WaitForSample();
  void ProcessOutput();

  std::shared_ptr<GpuDevice> gpu_;
  VideoSettings settings_;
  ReplayBuffer& buffer_;
  std::string name_;

  std::unique_ptr<Nv12Converter> converter_;
  std::shared_ptr<SurfacePool> pool_;

  winrt::com_ptr<IMFActivate> activate_;
  winrt::com_ptr<IMFTransform> transform_;
  winrt::com_ptr<IMFMediaEventGenerator> events_;
  winrt::com_ptr<IMFDXGIDeviceManager> deviceManager_;
  bool providesSamples_ = false;
  DWORD outputSize_ = 0;
  bool formatSent_ = false;

  std::mutex queueMutex_;
  std::condition_variable queueReady_;
  std::deque<winrt::com_ptr<IMFSample>> queue_;
  bool stopping_ = false;

  std::atomic<bool> failed_{false};
  std::atomic<uint64_t> dropped_{0};
  std::thread thread_;
};

}  // namespace clip
