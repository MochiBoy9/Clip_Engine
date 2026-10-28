#pragma once

#include "detect/DetectionRules.h"

#include <filesystem>

namespace clip {

// Commands Detection Studio in the desktop app runs to build rules from real frames.

// clip_engine.exe --frame <clip or image> <out.png> [--at seconds]
// Saves one frame of a clip (or a screenshot, as it is) as a PNG.
// Logs "Frame WxH saved to <path>". Returns the process exit code.
int RunFrameCommand(const std::filesystem::path& input, const std::filesystem::path& output, double seconds);

// clip_engine.exe --read <clip or image> [--at seconds] --region l,t,w,h [--threshold n]
//                 [--words "A | B"] [--exclude "C"]
// Reads the region the way detection does and logs "Read: <text>" and "Matches: <count>".
int RunReadCommand(const std::filesystem::path& input, double seconds, const DetectionRule& rule);

}  // namespace clip
