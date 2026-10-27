#include "capture/MonitorCapture.h"

#include "core/Clock.h"
#include "core/Log.h"

#include <winrt/Windows.Foundation.Metadata.h>

#include <dxgi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <algorithm>

namespace clip {

using namespace winrt::Windows::Graphics::Capture;
using winrt::Windows::Foundation::Metadata::ApiInformation;
using winrt::Windows::Graphics::DirectX::DirectXPixelFormat;

namespace {

constexpr wchar_t kSessionClass[] = L"Windows.Graphics.Capture.GraphicsCaptureSession";

// Windows 11 lets an app capture without the yellow border once the user allows it.
void RequestBorderlessAccess() {
  static bool requested = false;
  if (requested) return;
  requested = true;
  try {
    if (ApiInformation::IsTypePresent(L"Windows.Graphics.Capture.GraphicsCaptureAccess")) {
      GraphicsCaptureAccess::RequestAccessAsync(GraphicsCaptureAccessKind::Borderless).get();
    }
  } catch (const winrt::hresult_error& e) {
    log::Warn("Couldn't request borderless capture: {}", Describe(e));
  }
}

}  // namespace

bool MonitorCapture::IsSupported() {
  try {
    return GraphicsCaptureSession::IsSupported();
  } catch (...) {
    return false;
  }
}

MonitorCapture::MonitorCapture(std::shared_ptr<GpuDevice> gpu, HMONITOR monitor, UINT fps) : gpu_(std::move(gpu)) {
  RequestBorderlessAccess();

  winrt::com_ptr<::IInspectable> inspectable;
  winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(gpu_->device.as<IDXGIDevice>().get(),
                                                            inspectable.put()));
  winrtDevice_ = inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();

  const auto interop = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
  winrt::check_hresult(interop->CreateForMonitor(monitor, winrt::guid_of<GraphicsCaptureItem>(),
                                                 winrt::put_abi(item_)));

  const auto size = item_.Size();
  width_ = static_cast<UINT>(size.Width);
  height_ = static_cast<UINT>(size.Height);

  D3D11_TEXTURE2D_DESC desc{};
  desc.Width = width_;
  desc.Height = height_;
  desc.MipLevels = 1;
  desc.ArraySize = 1;
  desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
  desc.SampleDesc.Count = 1;
  desc.Usage = D3D11_USAGE_DEFAULT;
  desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
  winrt::check_hresult(gpu_->device->CreateTexture2D(&desc, nullptr, latest_.put()));

  framePool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(
      winrtDevice_, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
  frameArrived_ = framePool_.FrameArrived(winrt::auto_revoke, {this, &MonitorCapture::OnFrameArrived});
  session_ = framePool_.CreateCaptureSession(item_);

  // Games draw their own cursor, so the system one would show up twice.
  if (ApiInformation::IsPropertyPresent(kSessionClass, L"IsCursorCaptureEnabled")) {
    session_.IsCursorCaptureEnabled(false);
  }
  if (ApiInformation::IsPropertyPresent(kSessionClass, L"IsBorderRequired")) {
    try {
      session_.IsBorderRequired(false);
    } catch (const winrt::hresult_error&) {
      // The user hasn't allowed borderless capture; Windows shows the border instead.
    }
  }
  // On a 240 Hz monitor recording at 60 fps, skip copying most of the frames that would be
  // thrown away (Windows 11 24H2 and newer). The limit sits a little under the frame interval
  // so a frame is always ready when the pacer wants one.
  if (ApiInformation::IsPropertyPresent(kSessionClass, L"MinUpdateInterval") && fps > 0) {
    session_.MinUpdateInterval(winrt::Windows::Foundation::TimeSpan{kHnsPerSecond * 3 / 4 / fps});
  }
  session_.StartCapture();
}

MonitorCapture::~MonitorCapture() {
  frameArrived_.revoke();
  if (session_) session_.Close();
  if (framePool_) framePool_.Close();
}

void MonitorCapture::OnFrameArrived(const Direct3D11CaptureFramePool& pool,
                                    const winrt::Windows::Foundation::IInspectable&) {
  try {
    const auto frame = pool.TryGetNextFrame();
    if (!frame) return;

    const auto size = frame.ContentSize();
    if (size.Width <= 0 || size.Height <= 0) return;
    if (static_cast<UINT>(size.Width) != width_ || static_cast<UINT>(size.Height) != height_) {
      sizeChanged_ = true;
    }

    const auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
    winrt::com_ptr<ID3D11Texture2D> surface;
    winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(surface.put())));

    const D3D11_BOX box{0, 0, 0, std::min(width_, static_cast<UINT>(size.Width)),
                        std::min(height_, static_cast<UINT>(size.Height)), 1};
    {
      std::lock_guard lock(gpu_->contextLock);
      gpu_->context->CopySubresourceRegion(latest_.get(), 0, 0, 0, 0, surface.get(), 0, &box);
    }
    hasFrame_ = true;
  } catch (const winrt::hresult_error& e) {
    log::Error("Capture frame failed: {}", Describe(e));
  }
}

}  // namespace clip
