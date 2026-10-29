#pragma once

#include "core/Win.h"

#include <d3d11.h>

#include <string_view>

namespace clip {

// The frame recorded in place of the screen while another window is in front of the game: a
// "Game Not Found" card in Clip Engine's colors, the size of the captured monitor, in the same
// BGRA format as captured frames so it goes through the encoder the same way.
winrt::com_ptr<ID3D11Texture2D> MakeGameNotFoundFrame(ID3D11Device* device, UINT width, UINT height,
                                                      std::wstring_view game);

}  // namespace clip
