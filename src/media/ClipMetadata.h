#pragma once

#include "detect/GameEvent.h"

#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace clip {

// What a clip contains, saved as <clip>.json next to the .mp4 so the desktop app can show
// event markers on the timeline and a title like "3 kills, 1 revive".
struct ClipMetadata {
  struct Marker {
    EventType type;
    double seconds;  // from the start of the clip
    std::string text;
    std::vector<Witness> witnesses = {};  // how it was found, saved as found_by
  };

  std::string game;
  std::string trigger;  // "hotkey" or "auto"
  double seconds = 0;
  std::vector<Marker> markers;

  // Which recording and match the clip came from, so the app can group clips by match.
  // session is when the recording started ("2026-10-27T20:41:05"); sessionSeconds is where the
  // clip starts within it, which lines up clips that overlap. match counts the match starts
  // detected so far in the session (0 before the first, or in games without match detection).
  std::string session;
  double sessionSeconds = -1;
  int match = 0;

  // When the clip's first frame was recorded, in Unix milliseconds (0 when unknown), so clips
  // of one moment from squadmates' PCs line up. squadMoment names the squad moment the clip
  // shows, for the squad library; empty for a clip that isn't part of one.
  int64_t utcStartMs = 0;
  std::string squadMoment;
};

std::string ToJson(const ClipMetadata& metadata);

// Reads JSON written by ToJson or by the desktop app. Unknown fields are skipped.
std::optional<ClipMetadata> ParseMetadataJson(std::string_view json);

// The part of a clip from `from` to `to` seconds: markers outside it are dropped and the rest
// move so they count from the new start.
ClipMetadata Trimmed(const ClipMetadata& metadata, double from, double to);

// "3 kills, 1 assist", or empty when there are no markers.
std::string Summarize(const std::vector<ClipMetadata::Marker>& markers);

// The same JSON is also stored inside the .mp4, in a top-level 'uuid' box after the movie
// data, so it travels with the file when the clip is shared. Players skip boxes they don't
// know. Must match ClipEngine.Desktop/Services/ClipMetadataFile.cs.
bool AppendMetadataBox(const std::filesystem::path& mp4, const std::string& json);
std::optional<std::string> ReadMetadataBox(const std::filesystem::path& mp4);

// Every clip says where it came from: an iTunes-style tag list inside the movie box
// (moov/udta/meta/ilst) with the encoding tool ("Clip Engine 0.13.0") and a comment that
// links to the download, which Windows Explorer, VLC, ffprobe and phones all show. Leaves a
// file that already has it alone, and keeps chunk offsets right wherever the movie box is.
// AppendMetadataBox does this too, so most callers never need to.
inline constexpr std::string_view kClipEngineSite = "https://mochiboy9.github.io/Clip_Engine/";
bool TagMadeWithClipEngine(const std::filesystem::path& mp4);

// The clip's metadata JSON, from the .json next to it or else from inside the file.
std::optional<std::string> ReadClipMetadataJson(const std::filesystem::path& mp4);

}  // namespace clip
