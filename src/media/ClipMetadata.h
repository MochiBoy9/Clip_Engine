#pragma once

#include "detect/GameEvent.h"

#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace clip {

// What a clip contains, saved as <clip>.json next to the .mp4 so the desktop app can show
// event markers on the timeline and a title like "3 kills, 1 revive".
struct ClipMetadata {
  struct Marker {
    EventType type;
    double seconds;  // from the start of the clip
    std::string text;
  };

  std::string game;
  std::string trigger;  // "hotkey" or "auto"
  double seconds = 0;
  std::vector<Marker> markers;
};

std::string ToJson(const ClipMetadata& metadata);

// "3 kills, 1 assist", or empty when there are no markers.
std::string Summarize(const std::vector<ClipMetadata::Marker>& markers);

// The same JSON is also stored inside the .mp4, in a top-level 'uuid' box after the movie
// data, so it travels with the file when the clip is shared. Players skip boxes they don't
// know. Must match ClipEngine.Desktop/Services/ClipMetadataFile.cs.
bool AppendMetadataBox(const std::filesystem::path& mp4, const std::string& json);
std::optional<std::string> ReadMetadataBox(const std::filesystem::path& mp4);

}  // namespace clip
