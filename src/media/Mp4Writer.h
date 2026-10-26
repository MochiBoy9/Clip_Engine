#pragma once

#include "media/ReplayBuffer.h"

#include <filesystem>

namespace clip {

// Writes the clip's already-encoded packets into an MP4 file without re-encoding. Each audio
// source gets its own track so game and mic audio can be balanced later.
void WriteMp4(const std::filesystem::path& file, const ReplayBuffer::Clip& clip);

}  // namespace clip
