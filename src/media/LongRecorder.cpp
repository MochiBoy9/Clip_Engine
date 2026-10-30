#include "media/LongRecorder.h"

#include "core/Log.h"
#include "core/Power.h"

#include <mfreadwrite.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <stdexcept>

namespace clip {
namespace {

// Past this much waiting to be written, the disk is clearly too slow; say so once.
constexpr size_t kBacklogWarning = 256u << 20;

}  // namespace

LongRecorder::LongRecorder(ReplayBuffer& buffer, std::filesystem::path file)
    : buffer_(&buffer), file_(std::move(file)) {
  partial_ = file_;
  partial_ += L".part";
  std::filesystem::create_directories(file_.parent_path());

  // The buffer hands over what it holds and starts passing on new packets in one step, so
  // nothing is missed or written twice in between.
  taking_ = true;
  auto opening = buffer_->StartTap([this](const ReplayBuffer::PacketPtr& packet) {
    std::lock_guard lock(mutex_);
    if (!taking_) return;
    queuedBytes_ += packet->data.size();
    queue_.push_back(packet);
    wake_.notify_one();
  });
  if (!opening) throw std::runtime_error("the recording has no video yet");
  opening_ = std::move(*opening);
  start_ = opening_.start;
  lastPts_ = start_;
  thread_ = std::thread([this] { Run(); });
}

LongRecorder::~LongRecorder() {
  StopTaking();
  if (!finished_) {
    try {
      Finish();
    } catch (const std::exception& e) {
      log::Error("Couldn't finish the long recording: {}", e.what());
    }
  }
}

size_t LongRecorder::Backlog() const {
  std::lock_guard lock(mutex_);
  return queuedBytes_;
}

void LongRecorder::StopTaking() {
  if (!buffer_) return;
  buffer_->StopTap();
  buffer_ = nullptr;
  std::lock_guard lock(mutex_);
  taking_ = false;
}

double LongRecorder::Finish() {
  StopTaking();
  {
    std::lock_guard lock(mutex_);
    stopping_ = true;
  }
  wake_.notify_all();
  if (thread_.joinable()) thread_.join();
  finished_ = true;

  std::error_code ec;
  if (!std::filesystem::exists(partial_, ec)) throw std::runtime_error(error_.empty() ? "nothing was written" : error_);
  std::filesystem::rename(partial_, file_);
  return Seconds();
}

void LongRecorder::Run() {
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  // Steady, but behind the game and the encoder.
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
  const EfficiencyMode efficiency;

  winrt::com_ptr<IMFSinkWriter> writer;
  std::array<DWORD, kStreamCount> trackOf{};
  std::array<bool, kStreamCount> hasTrack{};
  std::array<int64_t, kStreamCount> lastPts{};
  lastPts.fill(INT64_MIN);
  bool warned = false;

  auto write = [&](const ReplayBuffer::PacketPtr& packet) {
    const int s = packet->stream;
    // Audio from just before the first frame, and anything out of order, is left out.
    if (!hasTrack[s] || packet->pts < start_ || packet->pts <= lastPts[s]) return;
    lastPts[s] = packet->pts;
    const auto size = static_cast<DWORD>(packet->data.size());
    auto sample = CreateMemorySample(size);
    winrt::com_ptr<IMFMediaBuffer> buffer;
    winrt::check_hresult(sample->GetBufferByIndex(0, buffer.put()));
    BYTE* bytes = nullptr;
    winrt::check_hresult(buffer->Lock(&bytes, nullptr, nullptr));
    std::memcpy(bytes, packet->data.data(), size);
    buffer->Unlock();
    winrt::check_hresult(buffer->SetCurrentLength(size));
    winrt::check_hresult(sample->SetSampleTime(packet->pts - start_));
    winrt::check_hresult(sample->SetSampleDuration(packet->duration));
    if (packet->keyframe) winrt::check_hresult(sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE));
    winrt::check_hresult(writer->WriteSample(trackOf[s], sample.get()));
    if (s == kVideoStream) lastPts_ = packet->pts;
  };

  try {
    winrt::com_ptr<IMFAttributes> attributes;
    winrt::check_hresult(MFCreateAttributes(attributes.put(), 3));
    winrt::check_hresult(attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4));
    winrt::check_hresult(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
    winrt::check_hresult(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE));
    winrt::check_hresult(MFCreateSinkWriterFromURL(partial_.c_str(), nullptr, attributes.get(), writer.put()));

    // A track for each stream that's already flowing; a source that has sent nothing (a mic
    // that's off) would leave an empty track the MP4 can't be finished with.
    std::array<bool, kStreamCount> flowing{};
    for (const auto& packet : opening_.packets) flowing[packet->stream] = true;
    for (int s = 0; s < kStreamCount; ++s) {
      if (!opening_.formats[s] || !flowing[s]) continue;
      winrt::check_hresult(writer->AddStream(opening_.formats[s].get(), &trackOf[s]));
      winrt::check_hresult(writer->SetInputMediaType(trackOf[s], opening_.formats[s].get(), nullptr));
      hasTrack[s] = true;
    }
    winrt::check_hresult(writer->BeginWriting());

    for (const auto& packet : opening_.packets) write(packet);
    opening_.packets.clear();

    while (true) {
      std::deque<ReplayBuffer::PacketPtr> batch;
      {
        std::unique_lock lock(mutex_);
        wake_.wait(lock, [&] { return stopping_ || !queue_.empty(); });
        if (queue_.empty() && stopping_) break;
        batch.swap(queue_);
        if (!warned && queuedBytes_ > kBacklogWarning) {
          log::Warn("The long recording is writing slower than it records; the drive may be too slow or busy");
          warned = true;
        }
        queuedBytes_ = 0;
      }
      for (const auto& packet : batch) write(packet);
    }
    winrt::check_hresult(writer->Finalize());
  } catch (const winrt::hresult_error& e) {
    failed_ = true;
    error_ = Describe(e);
    log::Error("The long recording stopped writing: {}", error_);
    // Keep whatever made it to disk playable.
    if (writer) writer->Finalize();
    // Stop queuing packets for a writer that's gone.
    std::unique_lock lock(mutex_);
    taking_ = false;
    queue_.clear();
    queuedBytes_ = 0;
    wake_.wait(lock, [&] { return stopping_; });
  }
  winrt::uninit_apartment();
}

}  // namespace clip
