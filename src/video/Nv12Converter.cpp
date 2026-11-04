#include "video/Nv12Converter.h"

namespace clip {

Nv12Converter::Nv12Converter(ID3D11Device* device, ID3D11DeviceContext* context, UINT inputWidth,
                             UINT inputHeight, UINT outputWidth, UINT outputHeight, UINT fps) {
  winrt::check_hresult(device->QueryInterface(IID_PPV_ARGS(videoDevice_.put())));
  winrt::check_hresult(context->QueryInterface(IID_PPV_ARGS(videoContext_.put())));

  D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
  content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
  content.InputFrameRate = {fps, 1};
  content.InputWidth = inputWidth;
  content.InputHeight = inputHeight;
  content.OutputFrameRate = {fps, 1};
  content.OutputWidth = outputWidth;
  content.OutputHeight = outputHeight;
  content.Usage = D3D11_VIDEO_USAGE_OPTIMAL_QUALITY;
  winrt::check_hresult(videoDevice_->CreateVideoProcessorEnumerator(&content, enumerator_.put()));
  winrt::check_hresult(videoDevice_->CreateVideoProcessor(enumerator_.get(), 0, processor_.put()));

  D3D11_VIDEO_PROCESSOR_COLOR_SPACE input{};
  input.RGB_Range = 0;  // full range RGB from the desktop
  D3D11_VIDEO_PROCESSOR_COLOR_SPACE output{};
  output.YCbCr_Matrix = 1;  // BT.709
  output.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
  videoContext_->VideoProcessorSetStreamColorSpace(processor_.get(), 0, &input);
  videoContext_->VideoProcessorSetOutputColorSpace(processor_.get(), &output);
  videoContext_->VideoProcessorSetStreamAutoProcessingMode(processor_.get(), 0, FALSE);

  // Encoders need even dimensions, so an odd-sized monitor loses its last row or column. The rest
  // of the frame is scaled to the output, which is smaller when the resolution setting asks.
  const RECT source{0, 0, static_cast<LONG>(inputWidth & ~1u), static_cast<LONG>(inputHeight & ~1u)};
  const RECT dest{0, 0, static_cast<LONG>(outputWidth), static_cast<LONG>(outputHeight)};
  videoContext_->VideoProcessorSetStreamSourceRect(processor_.get(), 0, TRUE, &source);
  videoContext_->VideoProcessorSetStreamDestRect(processor_.get(), 0, TRUE, &dest);
  const RECT target = dest;
  videoContext_->VideoProcessorSetOutputTargetRect(processor_.get(), TRUE, &target);
}

void Nv12Converter::Convert(ID3D11Texture2D* bgra, ID3D11Texture2D* nv12) {
  if (bgra != lastInput_) {
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC desc{};
    desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    inputView_ = nullptr;
    winrt::check_hresult(videoDevice_->CreateVideoProcessorInputView(bgra, enumerator_.get(), &desc, inputView_.put()));
    lastInput_ = bgra;
  }

  auto& outputView = outputViews_[nv12];
  if (!outputView) {
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC desc{};
    desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    winrt::check_hresult(videoDevice_->CreateVideoProcessorOutputView(nv12, enumerator_.get(), &desc, outputView.put()));
  }

  D3D11_VIDEO_PROCESSOR_STREAM stream{};
  stream.Enable = TRUE;
  stream.pInputSurface = inputView_.get();
  winrt::check_hresult(videoContext_->VideoProcessorBlt(processor_.get(), outputView.get(), 0, 1, &stream));
}

}  // namespace clip
