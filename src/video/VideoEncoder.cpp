#include "video/VideoEncoder.h"

#include "core/Clock.h"
#include "core/Log.h"

#include <mferror.h>
#include <strmif.h>
#include <codecapi.h>

#include <cstring>
#include <vector>

namespace clip {

// NV12 textures the encoder reads from. A texture returns to the pool when the encoder
// releases the sample wrapping it.
struct VideoEncoder::SurfacePool {
  std::mutex mutex;
  std::vector<winrt::com_ptr<ID3D11Texture2D>> textures;
  std::vector<bool> free;
};

namespace {

constexpr size_t kPoolSize = 8;

struct ReturnToPool : winrt::implements<ReturnToPool, IMFAsyncCallback> {
  ReturnToPool(std::shared_ptr<VideoEncoder::SurfacePool> pool, size_t index)
      : pool_(std::move(pool)), index_(index) {}

  HRESULT STDMETHODCALLTYPE GetParameters(DWORD*, DWORD*) noexcept final { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE Invoke(IMFAsyncResult*) noexcept final {
    std::lock_guard lock(pool_->mutex);
    pool_->free[index_] = true;
    return S_OK;
  }

  std::shared_ptr<VideoEncoder::SurfacePool> pool_;
  size_t index_;
};

void SetCodecValue(ICodecAPI* codec, const GUID& property, UINT32 value, const char* name) {
  VARIANT v{};
  v.vt = VT_UI4;
  v.ulVal = value;
  if (FAILED(codec->SetValue(&property, &v))) log::Warn("Encoder ignored setting {}", name);
}

std::string FriendlyName(IMFActivate* activate) {
  wchar_t* name = nullptr;
  UINT32 length = 0;
  if (FAILED(activate->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &name, &length))) return "Unknown encoder";
  std::string result = winrt::to_string(std::wstring_view(name, length));
  CoTaskMemFree(name);
  return result;
}

bool MatchesAdapter(IMFActivate* activate, const LUID& luid) {
  LUID encoderLuid{};
  UINT32 size = 0;
  if (SUCCEEDED(activate->GetBlob(MFT_ENUM_ADAPTER_LUID, reinterpret_cast<UINT8*>(&encoderLuid),
                                  sizeof(encoderLuid), &size))) {
    return encoderLuid.LowPart == luid.LowPart && encoderLuid.HighPart == luid.HighPart;
  }
  UINT64 packed = 0;
  if (SUCCEEDED(activate->GetUINT64(MFT_ENUM_ADAPTER_LUID, &packed))) {
    std::memcpy(&encoderLuid, &packed, sizeof(encoderLuid));
    return encoderLuid.LowPart == luid.LowPart && encoderLuid.HighPart == luid.HighPart;
  }
  return false;
}

}  // namespace

VideoEncoder::VideoEncoder(std::shared_ptr<GpuDevice> gpu, UINT captureWidth, UINT captureHeight,
                           const VideoSettings& settings, ReplayBuffer& buffer)
    : gpu_(std::move(gpu)), settings_(settings), buffer_(buffer) {
  converter_ = std::make_unique<Nv12Converter>(gpu_->device.get(), gpu_->context.get(), captureWidth,
                                               captureHeight, settings_.width, settings_.height, settings_.fps);

  pool_ = std::make_shared<SurfacePool>();
  D3D11_TEXTURE2D_DESC desc{};
  desc.Width = settings_.width;
  desc.Height = settings_.height;
  desc.MipLevels = 1;
  desc.ArraySize = 1;
  desc.Format = DXGI_FORMAT_NV12;
  desc.SampleDesc.Count = 1;
  desc.Usage = D3D11_USAGE_DEFAULT;
  desc.BindFlags = D3D11_BIND_RENDER_TARGET;
  for (size_t i = 0; i < kPoolSize; ++i) {
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::check_hresult(gpu_->device->CreateTexture2D(&desc, nullptr, texture.put()));
    pool_->textures.push_back(std::move(texture));
    pool_->free.push_back(true);
  }

  CreateTransform();
  ConfigureTransform();
  thread_ = std::thread([this] { Run(); });
}

VideoEncoder::~VideoEncoder() {
  {
    std::lock_guard lock(queueMutex_);
    stopping_ = true;
  }
  queueReady_.notify_all();
  if (thread_.joinable()) thread_.join();

  if (transform_) transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
  if (auto shutdown = transform_.try_as<IMFShutdown>()) shutdown->Shutdown();
  if (activate_) activate_->ShutdownObject();
}

void VideoEncoder::CreateTransform() {
  const MFT_REGISTER_TYPE_INFO input{MFMediaType_Video, MFVideoFormat_NV12};
  const MFT_REGISTER_TYPE_INFO output{MFMediaType_Video, settings_.hevc ? MFVideoFormat_HEVC : MFVideoFormat_H264};
  IMFActivate** found = nullptr;
  UINT32 count = 0;
  winrt::check_hresult(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                                 &input, &output, &found, &count));
  std::vector<winrt::com_ptr<IMFActivate>> candidates(count);
  for (UINT32 i = 0; i < count; ++i) candidates[i].attach(found[i]);
  CoTaskMemFree(found);

  if (candidates.empty()) {
    throw winrt::hresult_error(MF_E_TOPO_CODEC_NOT_FOUND,
                               settings_.hevc ? L"No hardware HEVC encoder found" : L"No hardware H.264 encoder found");
  }

  // On laptops with two GPUs, use the encoder on the GPU that holds our textures.
  activate_ = candidates.front();
  for (const auto& candidate : candidates) {
    if (MatchesAdapter(candidate.get(), gpu_->adapterLuid)) {
      activate_ = candidate;
      break;
    }
  }
  name_ = FriendlyName(activate_.get());
  winrt::check_hresult(activate_->ActivateObject(IID_PPV_ARGS(transform_.put())));

  winrt::com_ptr<IMFAttributes> attributes;
  winrt::check_hresult(transform_->GetAttributes(attributes.put()));
  if (MFGetAttributeUINT32(attributes.get(), MF_TRANSFORM_ASYNC, FALSE) == FALSE) {
    throw winrt::hresult_error(MF_E_UNSUPPORTED_D3D_TYPE, L"Hardware encoder is not asynchronous");
  }
  winrt::check_hresult(attributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE));
  events_ = transform_.as<IMFMediaEventGenerator>();

  UINT token = 0;
  winrt::check_hresult(MFCreateDXGIDeviceManager(&token, deviceManager_.put()));
  winrt::check_hresult(deviceManager_->ResetDevice(gpu_->device.get(), token));
  winrt::check_hresult(transform_->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                                                  reinterpret_cast<ULONG_PTR>(deviceManager_.get())));
}

void VideoEncoder::ConfigureTransform() {
  // Codec settings go in before the media types; some encoders lock them once types are set.
  if (auto codec = transform_.try_as<ICodecAPI>()) {
    SetCodecValue(codec.get(), CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR,
                  "rate control");
    SetCodecValue(codec.get(), CODECAPI_AVEncCommonMeanBitRate, settings_.bitrate, "bitrate");
    // A keyframe every second, so clips can start within a second of any moment.
    SetCodecValue(codec.get(), CODECAPI_AVEncMPVGOPSize, settings_.fps, "keyframe interval");
    // No B-frames: decode order equals display order, which keeps clip cutting simple.
    SetCodecValue(codec.get(), CODECAPI_AVEncMPVDefaultBPictureCount, 0, "B-frames");
  }

  winrt::com_ptr<IMFMediaType> outputType;
  winrt::check_hresult(MFCreateMediaType(outputType.put()));
  winrt::check_hresult(outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
  winrt::check_hresult(outputType->SetGUID(MF_MT_SUBTYPE, settings_.hevc ? MFVideoFormat_HEVC : MFVideoFormat_H264));
  winrt::check_hresult(outputType->SetUINT32(MF_MT_AVG_BITRATE, settings_.bitrate));
  winrt::check_hresult(MFSetAttributeSize(outputType.get(), MF_MT_FRAME_SIZE, settings_.width, settings_.height));
  winrt::check_hresult(MFSetAttributeRatio(outputType.get(), MF_MT_FRAME_RATE, settings_.fps, 1));
  winrt::check_hresult(MFSetAttributeRatio(outputType.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
  winrt::check_hresult(outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
  winrt::check_hresult(outputType->SetUINT32(
      MF_MT_MPEG2_PROFILE, settings_.hevc ? eAVEncH265VProfile_Main_420_8 : eAVEncH264VProfile_High));
  winrt::check_hresult(transform_->SetOutputType(0, outputType.get(), 0));

  winrt::com_ptr<IMFMediaType> inputType;
  winrt::check_hresult(MFCreateMediaType(inputType.put()));
  winrt::check_hresult(inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
  winrt::check_hresult(inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
  winrt::check_hresult(MFSetAttributeSize(inputType.get(), MF_MT_FRAME_SIZE, settings_.width, settings_.height));
  winrt::check_hresult(MFSetAttributeRatio(inputType.get(), MF_MT_FRAME_RATE, settings_.fps, 1));
  winrt::check_hresult(MFSetAttributeRatio(inputType.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
  winrt::check_hresult(inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
  winrt::check_hresult(transform_->SetInputType(0, inputType.get(), 0));

  MFT_OUTPUT_STREAM_INFO info{};
  winrt::check_hresult(transform_->GetOutputStreamInfo(0, &info));
  providesSamples_ = (info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
  outputSize_ = info.cbSize;

  winrt::check_hresult(transform_->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0));
  winrt::check_hresult(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
  winrt::check_hresult(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
}

bool VideoEncoder::Submit(ID3D11Texture2D* bgra, int64_t pts) {
  if (failed_) return false;

  size_t index = kPoolSize;
  {
    std::lock_guard lock(pool_->mutex);
    for (size_t i = 0; i < kPoolSize; ++i) {
      if (pool_->free[i]) {
        pool_->free[i] = false;
        index = i;
        break;
      }
    }
  }
  if (index == kPoolSize) {
    ++dropped_;
    return false;
  }

  try {
    ID3D11Texture2D* nv12 = pool_->textures[index].get();
    {
      std::lock_guard lock(gpu_->contextLock);
      converter_->Convert(bgra, nv12);
    }

    winrt::com_ptr<IMFMediaBuffer> buffer;
    winrt::check_hresult(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), nv12, 0, FALSE, buffer.put()));
    DWORD length = 0;
    winrt::check_hresult(buffer.as<IMF2DBuffer>()->GetContiguousLength(&length));
    winrt::check_hresult(buffer->SetCurrentLength(length));

    winrt::com_ptr<IMFTrackedSample> tracked;
    winrt::check_hresult(MFCreateTrackedSample(tracked.put()));
    auto sample = tracked.as<IMFSample>();
    winrt::check_hresult(sample->AddBuffer(buffer.get()));
    winrt::check_hresult(sample->SetSampleTime(pts));
    winrt::check_hresult(sample->SetSampleDuration(kHnsPerSecond / settings_.fps));
    auto callback = winrt::make_self<ReturnToPool>(pool_, index);
    winrt::check_hresult(tracked->SetAllocator(callback.get(), nullptr));

    {
      std::lock_guard lock(queueMutex_);
      queue_.push_back(std::move(sample));
    }
    queueReady_.notify_one();
    return true;
  } catch (const winrt::hresult_error& e) {
    log::Error("Couldn't queue a video frame: {}", Describe(e));
    std::lock_guard lock(pool_->mutex);
    pool_->free[index] = true;
    return false;
  }
}

winrt::com_ptr<IMFSample> VideoEncoder::WaitForSample() {
  std::unique_lock lock(queueMutex_);
  queueReady_.wait(lock, [&] { return stopping_ || !queue_.empty(); });
  if (stopping_) return nullptr;
  auto sample = std::move(queue_.front());
  queue_.pop_front();
  return sample;
}

void VideoEncoder::Run() {
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  bool draining = false;
  try {
    while (true) {
      winrt::com_ptr<IMFMediaEvent> event;
      winrt::check_hresult(events_->GetEvent(0, event.put()));
      MediaEventType type = MEUnknown;
      winrt::check_hresult(event->GetType(&type));

      if (type == METransformNeedInput) {
        if (draining) continue;
        auto sample = WaitForSample();
        if (!sample) {
          // Stopping: let the encoder flush what it still holds, then finish.
          draining = true;
          winrt::check_hresult(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0));
          winrt::check_hresult(transform_->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0));
          continue;
        }
        winrt::check_hresult(transform_->ProcessInput(0, sample.get(), 0));
      } else if (type == METransformHaveOutput) {
        ProcessOutput();
      } else if (type == METransformDrainComplete) {
        break;
      }
    }
  } catch (const winrt::hresult_error& e) {
    log::Error("Video encoder stopped: {}", Describe(e));
    failed_ = true;
  }
  {
    std::lock_guard lock(queueMutex_);
    queue_.clear();
  }
  winrt::uninit_apartment();
}

void VideoEncoder::ProcessOutput() {
  MFT_OUTPUT_DATA_BUFFER output{};
  winrt::com_ptr<IMFSample> ownSample;
  if (!providesSamples_) {
    ownSample = CreateMemorySample(outputSize_);
    output.pSample = ownSample.get();
  }

  DWORD status = 0;
  const HRESULT hr = transform_->ProcessOutput(0, 1, &output, &status);
  if (output.pEvents) output.pEvents->Release();

  winrt::com_ptr<IMFSample> sample;
  if (providesSamples_) sample.attach(output.pSample);
  else sample = ownSample;

  if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
    winrt::com_ptr<IMFMediaType> type;
    winrt::check_hresult(transform_->GetOutputAvailableType(0, 0, type.put()));
    winrt::check_hresult(transform_->SetOutputType(0, type.get(), 0));
    formatSent_ = false;
    return;
  }
  if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
  winrt::check_hresult(hr);
  if (!sample) return;

  EncodedPacket packet = PacketFromSample(sample.get(), kVideoStream);
  if (!packet.keyframe) packet.keyframe = ContainsKeyframe(packet.data, settings_.hevc);
  if (packet.duration == 0) packet.duration = kHnsPerSecond / settings_.fps;

  if (!formatSent_) {
    // MP4 needs the parameter sets up front. Most encoders put them on the output type;
    // otherwise take them from the first keyframe.
    if (!packet.keyframe) return;
    winrt::com_ptr<IMFMediaType> current;
    winrt::check_hresult(transform_->GetOutputCurrentType(0, current.put()));
    auto type = CopyMediaType(current.get());
    UINT32 headerSize = 0;
    if (FAILED(type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &headerSize)) || headerSize == 0) {
      const auto header = ExtractParameterSets(packet.data, settings_.hevc);
      if (header.empty()) return;
      winrt::check_hresult(type->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, header.data(), static_cast<UINT32>(header.size())));
    }
    buffer_.SetFormat(kVideoStream, type.get());
    formatSent_ = true;
  }
  buffer_.Push(std::move(packet));
}

}  // namespace clip
