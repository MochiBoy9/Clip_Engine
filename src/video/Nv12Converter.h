#pragma once

#include "core/Win.h"

#include <d3d11.h>

#include <unordered_map>

namespace clip {

// Converts captured BGRA frames to the NV12 (BT.709, limited range) format hardware encoders
// take, using the GPU's fixed-function video processor.
class Nv12Converter {
 public:
  Nv12Converter(ID3D11Device* device, ID3D11DeviceContext* context, UINT inputWidth, UINT inputHeight,
                UINT outputWidth, UINT outputHeight, UINT fps);

  // The caller must hold the GpuDevice context lock.
  void Convert(ID3D11Texture2D* bgra, ID3D11Texture2D* nv12);

 private:
  winrt::com_ptr<ID3D11VideoDevice> videoDevice_;
  winrt::com_ptr<ID3D11VideoContext> videoContext_;
  winrt::com_ptr<ID3D11VideoProcessorEnumerator> enumerator_;
  winrt::com_ptr<ID3D11VideoProcessor> processor_;

  ID3D11Texture2D* lastInput_ = nullptr;
  winrt::com_ptr<ID3D11VideoProcessorInputView> inputView_;
  std::unordered_map<ID3D11Texture2D*, winrt::com_ptr<ID3D11VideoProcessorOutputView>> outputViews_;
};

}  // namespace clip
