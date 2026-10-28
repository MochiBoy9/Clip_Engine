#include "detect/EventDetector.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "detect/Ocr.h"


#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstring>

namespace clip {

using winrt::Windows::Media::Ocr::OcrEngine;

// One part of the screen, read once per sample for every rule that looks at it, or every few
// samples for a scoreboard.
struct EventDetector::Region {
  std::string name;
  D3D11_BOX box{};
  UINT width = 0;
  UINT height = 0;
  int threshold = 0;
  int every = 1;  // read on every this many samples
  // Two staging copies: the GPU fills one while the other, filled a sample or more ago, is read.
  std::array<winrt::com_ptr<ID3D11Texture2D>, 2> staging;
  int filled = -1;          // the staging copy waiting to be read, if any
  int64_t filledTime = 0;   // when it was copied
  std::vector<uint8_t> pixels;  // BGRA, width * height * 4
  bool ready = false;
  int64_t readTime = 0;     // when the pixels were on screen
  std::vector<RuleTracker> trackers;
  const ScoreboardRule* board = nullptr;
  ScoreboardTracker boardTracker;
  std::string lastText;
};

namespace {

std::string Shorten(std::string text, size_t limit) {
  if (text.size() > limit) {
    text.resize(limit);
    text += "...";
  }
  return text;
}

}  // namespace

bool EventDetector::IsAvailable() {
  try {
    return OcrEngine::AvailableRecognizerLanguages().Size() > 0;
  } catch (...) {
    return false;
  }
}

EventDetector::EventDetector(std::shared_ptr<GpuDevice> gpu, const MonitorCapture& capture,
                             std::vector<DetectionRule> rules, std::vector<ScoreboardRule> scoreboards,
                             std::vector<std::string> playerNames, int sampleMs, bool debug, Sink sink,
                             ScoreboardSink scoreboardSink)
    : gpu_(std::move(gpu)),
      capture_(capture),
      rules_(std::move(rules)),
      scoreboards_(std::move(scoreboards)),
      playerNames_(std::move(playerNames)),
      sampleMs_(sampleMs),
      debug_(debug),
      sink_(std::move(sink)),
      scoreboardSink_(std::move(scoreboardSink)) {
  const double screenW = capture_.Width();
  const double screenH = capture_.Height();
  auto boxFor = [&](const ScreenRegion& r) {
    D3D11_BOX box{};
    box.left = static_cast<UINT>(std::floor(r.left * screenW));
    box.top = static_cast<UINT>(std::floor(r.top * screenH));
    box.right = std::min(capture_.Width(), static_cast<UINT>(std::ceil((r.left + r.width) * screenW)));
    box.bottom = std::min(capture_.Height(), static_cast<UINT>(std::ceil((r.top + r.height) * screenH)));
    box.back = 1;
    return box;
  };
  auto addRegion = [&](const std::string& name, const D3D11_BOX& box, int threshold) {
    auto created = std::make_unique<Region>();
    created->name = name;
    created->box = box;
    created->width = box.right - box.left;
    created->height = box.bottom - box.top;
    created->threshold = threshold;
    created->pixels.resize(static_cast<size_t>(created->width) * created->height * 4);

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = created->width;
    desc.Height = created->height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    for (auto& staging : created->staging) {
      winrt::check_hresult(gpu_->device->CreateTexture2D(&desc, nullptr, staging.put()));
    }
    regions_.push_back(std::move(created));
    return regions_.back().get();
  };

  for (const auto& rule : rules_) {
    const D3D11_BOX box = boxFor(rule.region);
    if (box.right < box.left + 8 || box.bottom < box.top + 8) {
      log::Warn("Detection rule {} covers too little of the screen; skipping it", rule.name);
      continue;
    }

    auto existing = std::ranges::find_if(regions_, [&](const auto& r) {
      return r->box.left == box.left && r->box.top == box.top && r->box.right == box.right &&
             r->box.bottom == box.bottom && r->threshold == rule.threshold;
    });
    Region* region = existing != regions_.end() ? existing->get() : addRegion(rule.name, box, rule.threshold);
    region->trackers.emplace_back(rule);
  }

  if (!scoreboards_.empty() && playerNames_.empty()) {
    log::Info("Add your in-game name on the Games page to have the scoreboard read for your records");
  } else {
    for (const auto& board : scoreboards_) {
      const D3D11_BOX box = boxFor(board.region);
      if (box.right < box.left + 32 || box.bottom < box.top + 32) {
        log::Warn("Scoreboard {} covers too little of the screen; skipping it", board.name);
        continue;
      }
      Region* region = addRegion(board.name, box, board.threshold);
      region->board = &board;
      region->every = std::max(1, board.sampleMs / std::max(1, sampleMs_));
    }
  }

  log::Info("Watching {} screen regions for {} detection rules", regions_.size(), rules_.size());
  thread_ = std::thread([this] { Run(); });
}

EventDetector::~EventDetector() {
  {
    std::lock_guard lock(mutex_);
    stop_ = true;
  }
  wake_.notify_all();
  if (thread_.joinable()) thread_.join();
}

void EventDetector::Run() {
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  // Reading the screen must never take time away from the game.
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);

  try {
    const OcrEngine ocr = CreateOcrEngine();
    if (!ocr) {
      log::Warn("Windows text recognition isn't installed for any of your languages, so automatic clips are off. "
                "Add English under Settings > Time & language > Language to turn them on.");
      winrt::uninit_apartment();
      return;
    }

    uint64_t sample = 0;
    auto next = std::chrono::steady_clock::now();

    while (true) {
      next += std::chrono::milliseconds(sampleMs_);
      {
        std::unique_lock lock(mutex_);
        if (wake_.wait_until(lock, next, [&] { return stop_; })) break;
      }
      // After a stall (the PC slept, a debugger paused), restart the schedule.
      if (std::chrono::steady_clock::now() - next > std::chrono::seconds(1)) next = std::chrono::steady_clock::now();
      ++sample;

      const int64_t now = NowHns();
      {
        std::lock_guard lock(gpu_->contextLock);
        ID3D11Texture2D* frame = capture_.LatestFrame();
        for (auto& region : regions_) {
          // Read the copy made a sample or more ago, which the GPU has long finished. Never wait for it.
          const int waiting = region->filled;
          region->filled = -1;
          if (frame && sample % region->every == 0) {
            const int slot = waiting == 0 ? 1 : 0;
            gpu_->context->CopySubresourceRegion(region->staging[slot].get(), 0, 0, 0, 0, frame, 0, &region->box);
            region->filled = slot;
            region->filledTime = now;
          }
          region->ready = false;
          if (waiting < 0) continue;
          ID3D11Texture2D* staging = region->staging[waiting].get();
          D3D11_MAPPED_SUBRESOURCE mapped{};
          if (FAILED(gpu_->context->Map(staging, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped))) continue;
          const size_t rowBytes = static_cast<size_t>(region->width) * 4;
          for (UINT y = 0; y < region->height; ++y) {
            std::memcpy(region->pixels.data() + y * rowBytes, static_cast<const uint8_t*>(mapped.pData) + y * mapped.RowPitch,
                        rowBytes);
          }
          gpu_->context->Unmap(staging, 0);
          region->ready = true;
          region->readTime = region->filledTime;
        }
      }

      for (auto& region : regions_) {
        if (!region->ready) continue;
        const auto bitmap = PrepareForOcr(region->pixels, region->width, region->height, region->threshold);
        const auto result = ocr.RecognizeAsync(bitmap).get();
        const std::string text = winrt::to_string(result.Text());
        if (debug_ && text != region->lastText && !text.empty()) log::Info("Read [{}]: {}", region->name, Shorten(text, 160));
        region->lastText = text;

        std::vector<WordBox> words;
        for (const auto& line : result.Lines()) {
          for (const auto& word : line.Words()) {
            const auto box = word.BoundingRect();
            words.push_back({winrt::to_string(word.Text()), box.X, box.Y, box.Width, box.Height});
          }
        }
        if (region->board) {
          std::optional<Stats> read;
          if (!text.empty()) read = ReadScoreboard(*region->board, words, playerNames_);
          if (const auto stats = region->boardTracker.Update(read)) {
            scoreboardSink_(Scoreboard{region->readTime, "screen", *stats});
          }
          continue;
        }
        const int64_t sampleMs = region->readTime / kHnsPerMs;
        const auto rows = GroupIntoRows(std::move(words));
        for (auto& tracker : region->trackers) {
          const int fresh = tracker.Update(rows, sampleMs);
          for (int i = 0; i < fresh; ++i) sink_(GameEvent{tracker.Rule().event, region->readTime, Shorten(text, 80)});
        }
      }
    }
  } catch (const winrt::hresult_error& e) {
    log::Error("Event detection stopped: {}", Describe(e));
  }
  winrt::uninit_apartment();
}

}  // namespace clip
