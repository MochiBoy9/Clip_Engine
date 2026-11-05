#pragma once

#include <filesystem>
#include <string>
#include <vector>

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

// A squad highlight or an edit from the app's editor: parts of one or more clips one after another
// in one H.264 MP4, re-encoded to one picture size, with every part's moments on its timeline.
// Logs "Montage progress N%" and "Highlight saved (N MB) to <path>". Returns the exit code.
struct MontageOptions {
  struct Part {
    std::filesystem::path input;
    double from = 0;
    double to = 0;
  };
  std::filesystem::path output;
  std::vector<Part> parts;
  unsigned maxHeight = 1080;
  unsigned maxFps = 60;
  double mbps = 16;  // 0 picks it from the picture size
  std::string squadMoment;
  std::string trigger;  // the clip's trigger in its metadata; "squad_highlight" when empty
};

int RunMontage(const MontageOptions& options);

}  // namespace clip
