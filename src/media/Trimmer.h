#pragma once

#include <filesystem>

namespace clip {

struct TrimOptions {
  std::filesystem::path input;
  std::filesystem::path output;
  double startSeconds = 0;
  double endSeconds = 0;
};

// Copies the part of a clip between two times into a new MP4 without re-encoding, so it's
// instant and loses no quality. Keeps every audio track and the clip's event markers. The
// copy starts on the keyframe at or before startSeconds (at most a second early).
// Logs "Trimmed Ns from Ms to <path>" when done. Returns the process exit code.
int RunTrim(const TrimOptions& options);

}  // namespace clip
