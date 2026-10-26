#include "capture/GpuDevice.h"

#include <d3d11_4.h>
#include <dxgi.h>

namespace clip {

std::shared_ptr<GpuDevice> GpuDevice::Create() {
  auto gpu = std::make_shared<GpuDevice>();

  const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
  winrt::check_hresult(D3D11CreateDevice(
      nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
      D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT, levels,
      static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION, gpu->device.put(), nullptr,
      gpu->context.put()));

  // The capture callback, the frame pacer and the encoder all touch this device from
  // different threads.
  gpu->device.as<ID3D11Multithread>()->SetMultithreadProtected(TRUE);

  winrt::com_ptr<IDXGIAdapter> adapter;
  winrt::check_hresult(gpu->device.as<IDXGIDevice>()->GetAdapter(adapter.put()));
  DXGI_ADAPTER_DESC desc{};
  winrt::check_hresult(adapter->GetDesc(&desc));
  gpu->adapterLuid = desc.AdapterLuid;
  return gpu;
}

}  // namespace clip
