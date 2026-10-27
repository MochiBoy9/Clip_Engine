#include "detect/EventDetector.h"

#include "core/Clock.h"
#include "core/Log.h"

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.Ocr.h>
#include <winrt/Windows.Storage.Streams.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstring>

namespace clip {

using winrt::Windows::Graphics::Imaging::BitmapAlphaMode;
using winrt::Windows::Graphics::Imaging::BitmapPixelFormat;
using winrt::Windows::Graphics::Imaging::SoftwareBitmap;
using winrt::Windows::Media::Ocr::OcrEngine;

// One part of the screen, read once per sample for every rule that looks at it.
struct EventDetector::Region {
  std::string name;
  D3D11_BOX box{};
  UINT width = 0;
  UINT height = 0;
  int threshold = 0;
  // Two staging copies: the GPU fills one while the other, filled a sample ago, is read.
  std::array<winrt::com_ptr<ID3D11Texture2D>, 2> staging;
  std::vector<uint8_t> pixels;  // BGRA, width * height * 4
  bool ready = false;
  std::vector<RuleTracker> trackers;
  std::string lastText;
};

namespace {

OcrEngine CreateOcrEngine() {
  const winrt::Windows::Globalization::Language english(L"en-US");
  if (OcrEngine::IsLanguageSupported(english)) return OcrEngine::TryCreateFromLanguage(english);
  return OcrEngine::TryCreateFromUserProfileLanguages();
}

// Game HUD text is bright on a busy background. Text recognition does best with dark text
// on a light background, so keep each pixel's brightest channel and invert it.
SoftwareBitmap PrepareForOcr(const std::vector<uint8_t>& bgra, UINT width, UINT height, int threshold) {
  const UINT maxDimension = OcrEngine::MaxImageDimension();
  UINT scale = height < 120 ? 2 : 1;  // small text reads better a little larger
  if (width * scale > maxDimension || height * scale > maxDimension) scale = 1;
  const UINT outWidth = std::min(width * scale, maxDimension);
  const UINT outHeight = std::min(height * scale, maxDimension);

  winrt::Windows::Storage::Streams::Buffer buffer(outWidth * outHeight * 4);
  buffer.Length(outWidth * outHeight * 4);
  uint8_t* out = buffer.data();
  for (UINT y = 0; y < outHeight; ++y) {
    const uint8_t* row = bgra.data() + static_cast<size_t>(y / scale) * width * 4;
    for (UINT x = 0; x < outWidth; ++x) {
      const uint8_t* p = row + static_cast<size_t>(x / scale) * 4;
      const uint8_t bright = std::max({p[0], p[1], p[2]});
      const uint8_t v = threshold > 0 ? (bright >= threshold ? 0 : 255) : static_cast<uint8_t>(255 - bright);
      out[0] = out[1] = out[2] = v;
      out[3] = 255;
      out += 4;
    }
  }
  return SoftwareBitmap::CreateCopyFromBuffer(buffer, BitmapPixelFormat::Bgra8, static_cast<int32_t>(outWidth),
                                              static_cast<int32_t>(outHeight), BitmapAlphaMode::Premultiplied);
}

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
                             std::vector<DetectionRule> rules, int sampleMs, bool debug, Sink sink)
    : gpu_(std::move(gpu)),
      capture_(capture),
      rules_(std::move(rules)),
      sampleMs_(sampleMs),
      debug_(debug),
      sink_(std::move(sink)) {
  const double screenW = capture_.Width();
  const double screenH = capture_.Height();
  for (const auto& rule : rules_) {
    D3D11_BOX box{};
    box.left = static_cast<UINT>(std::floor(rule.region.left * screenW));
    box.top = static_cast<UINT>(std::floor(rule.region.top * screenH));
    box.right = std::min(capture_.Width(), static_cast<UINT>(std::ceil((rule.region.left + rule.region.width) * screenW)));
    box.bottom = std::min(capture_.Height(), static_cast<UINT>(std::ceil((rule.region.top + rule.region.height) * screenH)));
    box.back = 1;
    if (box.right < box.left + 8 || box.bottom < box.top + 8) {
      log::Warn("Detection rule {} covers too little of the screen; skipping it", rule.name);
      continue;
    }

    auto existing = std::ranges::find_if(regions_, [&](const auto& r) {
      return r->box.left == box.left && r->box.top == box.top && r->box.right == box.right &&
             r->box.bottom == box.bottom && r->threshold == rule.threshold;
    });
    Region* region = existing != regions_.end() ? existing->get() : nullptr;
    if (!region) {
      auto created = std::make_unique<Region>();
      created->name = rule.name;
      created->box = box;
      created->width = box.right - box.left;
      created->height = box.bottom - box.top;
      created->threshold = rule.threshold;
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
      region = created.get();
      regions_.push_back(std::move(created));
    }
    region->trackers.emplace_back(rule);
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

    int current = 0;
    bool havePrevious = false;
    int64_t previousTime = 0;
    auto next = std::chrono::steady_clock::now();

    while (true) {
      next += std::chrono::milliseconds(sampleMs_);
      {
        std::unique_lock lock(mutex_);
        if (wake_.wait_until(lock, next, [&] { return stop_; })) break;
      }
      // After a stall (the PC slept, a debugger paused), restart the schedule.
      if (std::chrono::steady_clock::now() - next > std::chrono::seconds(1)) next = std::chrono::steady_clock::now();

      const int64_t now = NowHns();
      bool copied = false;
      {
        std::lock_guard lock(gpu_->contextLock);
        if (ID3D11Texture2D* frame = capture_.LatestFrame()) {
          for (auto& region : regions_) {
            gpu_->context->CopySubresourceRegion(region->staging[current].get(), 0, 0, 0, 0, frame, 0, &region->box);
          }
          copied = true;
        }
        // Read last sample's copies, which the GPU has long finished. Never wait for it.
        if (havePrevious) {
          for (auto& region : regions_) {
            ID3D11Texture2D* staging = region->staging[1 - current].get();
            D3D11_MAPPED_SUBRESOURCE mapped{};
            region->ready = SUCCEEDED(gpu_->context->Map(staging, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped));
            if (!region->ready) continue;
            const size_t rowBytes = static_cast<size_t>(region->width) * 4;
            for (UINT y = 0; y < region->height; ++y) {
              std::memcpy(region->pixels.data() + y * rowBytes, static_cast<const uint8_t*>(mapped.pData) + y * mapped.RowPitch,
                          rowBytes);
            }
            gpu_->context->Unmap(staging, 0);
          }
        }
      }

      if (havePrevious) {
        const int64_t sampleMs = previousTime / kHnsPerMs;
        for (auto& region : regions_) {
          if (!region->ready) continue;
          const auto bitmap = PrepareForOcr(region->pixels, region->width, region->height, region->threshold);
          const std::string text = winrt::to_string(ocr.RecognizeAsync(bitmap).get().Text());
          if (debug_ && text != region->lastText && !text.empty()) log::Info("Read [{}]: {}", region->name, Shorten(text, 160));
          region->lastText = text;

          const auto tokens = Tokenize(text);
          for (auto& tracker : region->trackers) {
            const int fresh = tracker.Update(tokens, sampleMs);
            for (int i = 0; i < fresh; ++i) sink_(GameEvent{tracker.Rule().event, previousTime, Shorten(text, 80)});
          }
        }
      }

      if (copied) {
        havePrevious = true;
        previousTime = now;
        current = 1 - current;
      }
    }
  } catch (const winrt::hresult_error& e) {
    log::Error("Event detection stopped: {}", Describe(e));
  }
  winrt::uninit_apartment();
}

}  // namespace clip
