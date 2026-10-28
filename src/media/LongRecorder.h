#pragma once

#include "core/Clock.h"
#include "media/ReplayBuffer.h"

#include <atomic>
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>

namespace clip {

// A recording that runs until it's stopped, however long that is: from the moment it starts,
// every packet the encoders put in the replay buffer is also written to an MP4 on a background
// thread, so memory use stays the same as without it. Nothing is re-encoded.
class LongRecorder {
 public:
  // Starts with what the buffer holds from its latest keyframe, so the file opens on a picture.
  // Writes to file + ".part" until Finish. Throws when the buffer has no video yet or the file
  // can't be created.
  LongRecorder(ReplayBuffer& buffer, std::filesystem::path file);
  ~LongRecorder();

  LongRecorder(const LongRecorder&) = delete;
  LongRecorder& operator=(const LongRecorder&) = delete;

  // Timestamp of the first frame, on the engine's clock.
  int64_t Start() const { return start_; }
  // Seconds written so far, and bytes queued but not yet written (when the disk can't keep up).
  double Seconds() const { return static_cast<double>(lastPts_.load() - start_) / kHnsPerSecond; }
  size_t Backlog() const;
  // A write failed (disk full, folder gone); what's been written so far is still finished.
  bool Failed() const { return failed_; }

  // Stops taking packets from the buffer. Call before the buffer goes away; Finish can then run
  // on another thread.
  void StopTaking();
  // Writes what's left, closes the MP4 and renames it into place. Returns its length in seconds.
  // Throws when nothing could be written.
  double Finish();

  const std::filesystem::path& File() const { return file_; }

 private:
  void Run();

  ReplayBuffer* buffer_;
  std::filesystem::path file_;
  std::filesystem::path partial_;
  ReplayBuffer::Clip opening_;
  int64_t start_ = 0;
  std::atomic<int64_t> lastPts_{0};

  mutable std::mutex mutex_;
  std::condition_variable wake_;
  std::deque<ReplayBuffer::PacketPtr> queue_;
  size_t queuedBytes_ = 0;
  bool stopping_ = false;
  bool taking_ = false;
  std::atomic<bool> failed_{false};
  std::string error_;
  bool finished_ = false;
  std::thread thread_;
};

}  // namespace clip
