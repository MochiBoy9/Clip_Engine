#pragma once

// Common Windows + C++/WinRT include. unknwn.h must come before winrt/base.h so that
// winrt::com_ptr and winrt::implements work with classic COM interfaces (D3D, Media Foundation).
#include <unknwn.h>
#include <windows.h>

#include <winrt/base.h>

#include <format>
#include <string>

namespace clip {

// "message (0x80004005)" for logging a failed call.
inline std::string Describe(const winrt::hresult_error& e) {
  return std::format("{} (0x{:08X})", winrt::to_string(e.message()),
                     static_cast<uint32_t>(e.code().value));
}

}  // namespace clip
