#pragma once

#include <filesystem>

namespace clip {

struct ExportOptions {
  std::filesystem::path input;
  std::filesystem::path output;
  double maxMegabytes = 10;  // Discord's limit for free accounts
  unsigned maxFps = 60;
};

// Re-encodes a clip into a small H.264 MP4 that fits under maxMegabytes, for sharing where
// uploads are size-limited. Picks the resolution and frame rate from the bitrate that fits,
// mixes the game and mic tracks into one, and carries over the clip's event markers.
// Logs "Export progress N%" lines while working. Returns the process exit code.
int RunExport(const ExportOptions& options);

}  // namespace clip
