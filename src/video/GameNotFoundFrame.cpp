#include "video/GameNotFoundFrame.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string>

namespace clip {
namespace {

constexpr COLORREF kAccent = RGB(0xFF, 0xC2, 0x1A);
constexpr COLORREF kTitle = RGB(0xF2, 0xF3, 0xF5);
constexpr COLORREF kCaption = RGB(0x9A, 0xA1, 0xAB);

uint32_t Bgra(int r, int g, int b) {
  return 0xFF000000u | static_cast<uint32_t>(r) << 16 | static_cast<uint32_t>(g) << 8 | static_cast<uint32_t>(b);
}

void DrawLine(HDC dc, std::wstring_view text, int height, int weight, COLORREF color, RECT box, int spacing = 0) {
  HFONT font = CreateFontW(-height, 0, 0, 0, weight, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                           CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
  HGDIOBJ old = SelectObject(dc, font);
  SetTextColor(dc, color);
  SetTextCharacterExtra(dc, spacing);
  DrawTextW(dc, text.data(), static_cast<int>(text.size()), &box, DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
  SetTextCharacterExtra(dc, 0);
  SelectObject(dc, old);
  DeleteObject(font);
}

}  // namespace

winrt::com_ptr<ID3D11Texture2D> MakeGameNotFoundFrame(ID3D11Device* device, UINT width, UINT height,
                                                      std::wstring_view game) {
  const int w = static_cast<int>(width);
  const int h = static_cast<int>(height);

  BITMAPINFO info{};
  info.bmiHeader.biSize = sizeof(info.bmiHeader);
  info.bmiHeader.biWidth = w;
  info.bmiHeader.biHeight = -h;  // top-down, like a texture
  info.bmiHeader.biPlanes = 1;
  info.bmiHeader.biBitCount = 32;
  info.bmiHeader.biCompression = BI_RGB;

  HDC dc = CreateCompatibleDC(nullptr);
  void* bits = nullptr;
  HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
  if (!dc || !bitmap || !bits) {
    if (bitmap) DeleteObject(bitmap);
    if (dc) DeleteDC(dc);
    winrt::throw_hresult(E_OUTOFMEMORY);
  }
  HGDIOBJ oldBitmap = SelectObject(dc, bitmap);
  auto* pixels = static_cast<uint32_t*>(bits);

  // Gunmetal, a little lighter in the middle, like the app's background.
  const double cx = w / 2.0;
  const double cy = h / 2.0;
  const double reach = std::max(1.0, std::hypot(cx, cy));
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      const double d = std::min(1.0, std::hypot(x - cx, y - cy) / reach);
      const int shade = static_cast<int>(16 * (1 - d));
      pixels[static_cast<size_t>(y) * w + x] = Bgra(0x12 + shade, 0x14 + shade, 0x18 + shade);
    }
  }

  // An amber bar above the title, the app's mark for "attention".
  const int unit = std::max(1, h / 108);  // 10 px at 1080p
  const int barWidth = 7 * unit;
  const int barTop = h / 2 - 9 * unit;
  for (int y = barTop; y < barTop + std::max(2, unit / 2) && y < h; ++y) {
    for (int x = w / 2 - barWidth / 2; x < w / 2 + barWidth / 2; ++x) {
      if (y >= 0 && x >= 0 && x < w) pixels[static_cast<size_t>(y) * w + x] = Bgra(0xFF, 0xC2, 0x1A);
    }
  }

  SetBkMode(dc, TRANSPARENT);
  DrawLine(dc, L"CLIP ENGINE", 2 * unit, FW_BOLD, kAccent, RECT{0, h / 2 - 8 * unit, w, h / 2 - 5 * unit}, unit / 3);
  DrawLine(dc, L"GAME NOT FOUND", 7 * unit, FW_BLACK, kTitle, RECT{0, h / 2 - 5 * unit, w, h / 2 + 3 * unit}, unit / 4);
  std::wstring caption = L"Recording resumes when you're back in ";
  caption += game.empty() ? std::wstring_view(L"the game") : game;
  DrawLine(dc, caption, 2 * unit, FW_NORMAL, kCaption, RECT{0, h / 2 + 3 * unit, w, h / 2 + 7 * unit});
  GdiFlush();

  // GDI leaves the alpha channel at zero where it drew text.
  for (size_t i = 0; i < static_cast<size_t>(w) * h; ++i) pixels[i] |= 0xFF000000u;

  D3D11_TEXTURE2D_DESC desc{};
  desc.Width = width;
  desc.Height = height;
  desc.MipLevels = 1;
  desc.ArraySize = 1;
  desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
  desc.SampleDesc.Count = 1;
  desc.Usage = D3D11_USAGE_DEFAULT;
  desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
  const D3D11_SUBRESOURCE_DATA data{bits, width * 4, 0};
  winrt::com_ptr<ID3D11Texture2D> texture;
  const HRESULT hr = device->CreateTexture2D(&desc, &data, texture.put());

  SelectObject(dc, oldBitmap);
  DeleteObject(bitmap);
  DeleteDC(dc);
  winrt::check_hresult(hr);
  return texture;
}

}  // namespace clip
