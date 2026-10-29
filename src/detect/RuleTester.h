#pragma once

#include "detect/DetectionRules.h"
#include "detect/OcrImage.h"

#include <cstdint>
#include <filesystem>
#include <vector>

namespace clip {

// A picture's pixels: BGRA, top row first.
struct Picture {
  uint32_t width = 0;
  uint32_t height = 0;
  std::vector<uint8_t> bgra;
};

// Decodes a PNG, JPEG or BMP, keeping its transparency. Throws winrt::hresult_error.
Picture LoadPicture(const std::filesystem::path& path);

// Commands Detection Studio in the desktop app runs to build rules from real frames.

// clip_engine.exe --frame <clip or image> <out.png> [--at seconds]
// Saves one frame of a clip (or a screenshot, as it is) as a PNG.
// Logs "Frame WxH saved to <path>", and for a video "Video length: <seconds>". Returns the process exit code.
int RunFrameCommand(const std::filesystem::path& input, const std::filesystem::path& output, double seconds);

// clip_engine.exe --read <clip or image> [--at seconds] --region l,t,w,h [--threshold n]
//                 [--words "A | B"] [--exclude "C"] [--position start|end]
//                 [--image icon.png [--image-match 0.85] [--image-screen 1440]] [--quality low|medium|high|ultra]
// Reads the region the way detection does at that reading quality and logs "Read: <text>" (the
// read the rule goes by, when it's read more than one way) and "Matches: <count>".
// With an image, also logs "Image: <how alike, 0-1>" and counts it as a match when it's alike enough.
int RunReadCommand(const std::filesystem::path& input, double seconds, const DetectionRule& rule, ReadQuality quality);

}  // namespace clip
