#include "detect/EventDetector.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "detect/ImageMatch.h"
#include "detect/Ocr.h"
#include "detect/RuleTester.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <climits>
#include <cmath>
#include <cstring>
#include <optional>

namespace clip {

using winrt::Windows::Media::Ocr::OcrEngine;

namespace {

// A region's brightness on a coarse grid, to tell whether it changed since it was last read.
constexpr int kGridX = 16;
constexpr int kGridY = 8;
using Signature = std::array<float, kGridX * kGridY>;

Signature MakeSignature(const std::vector<uint8_t>& bgra, UINT width, UINT height) {
  Signature sums{};
  std::array<int, kGridX * kGridY> counts{};
  // Every other pixel each way is plenty for an average.
  for (UINT y = 0; y < height; y += 2) {
    const uint8_t* row = bgra.data() + static_cast<size_t>(y) * width * 4;
    const size_t cellRow = static_cast<size_t>(y) * kGridY / height * kGridX;
    for (UINT x = 0; x < width; x += 2) {
      const uint8_t* p = row + static_cast<size_t>(x) * 4;
      const size_t cell = cellRow + static_cast<size_t>(x) * kGridX / width;
      sums[cell] += std::max({p[0], p[1], p[2]});
      ++counts[cell];
    }
  }
  for (size_t i = 0; i < sums.size(); ++i) sums[i] = counts[i] > 0 ? sums[i] / counts[i] : 0;
  return sums;
}

// Video noise and compression shift a cell by a level or so; new text in it moves it far more.
bool Unchanged(const Signature& a, const Signature& b) {
  for (size_t i = 0; i < a.size(); ++i) {
    if (std::abs(a[i] - b[i]) > 1.5f) return false;
  }
  return true;
}

std::string Shorten(std::string text, size_t limit) {
  if (text.size() > limit) {
    text.resize(limit);
    text += "...";
  }
  return text;
}

bool Contains(const D3D11_BOX& outer, const D3D11_BOX& inner) {
  return outer.left <= inner.left && outer.top <= inner.top && outer.right >= inner.right && outer.bottom >= inner.bottom;
}

double Area(const D3D11_BOX& box) { return static_cast<double>(box.right - box.left) * (box.bottom - box.top); }

}  // namespace

// One part of the screen, read once per look for every rule that looks at it.
struct EventDetector::Region {
  std::string name;
  D3D11_BOX box{};
  UINT width = 0;
  UINT height = 0;
  int threshold = 0;
  UINT shrink = 1;      // see OcrScale
  int intervalMs = 250;
  bool banner = true;   // every rule here is for a banner that stays up, so it can be read smaller

  std::vector<RuleTracker> textRules;
  struct Icon {
    RuleTracker tracker;
    ImageMatcher matcher;
    double match;
    bool found = false;
  };
  std::vector<Icon> icons;

  // A region inside this one, read from this one's words instead of on its own.
  Region* within = nullptr;
  std::vector<Region*> inside;

  winrt::com_ptr<ID3D11Texture2D> staging;
  std::vector<uint8_t> pixels;  // BGRA, width * height * 4
  bool copying = false;         // a copy is on its way from the GPU
  int64_t copyTime = 0;
  std::chrono::steady_clock::time_point due{};

  bool haveSignature = false;
  Signature signature{};
  std::vector<WordBox> words;  // from the last read
  std::string lastText;

  bool ReadsText() const { return !textRules.empty() || !inside.empty(); }
};

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

    std::optional<ImageMatcher> matcher;
    if (rule.LooksForImage()) {
      try {
        const Picture picture = LoadPicture(std::filesystem::path(winrt::to_hstring(rule.image).c_str()));
        matcher.emplace(ToGray(picture.bgra.data(), static_cast<int>(picture.width), static_cast<int>(picture.height),
                               static_cast<size_t>(picture.width) * 4, true),
                        rule.imageScreen, static_cast<int>(capture_.Height()));
        if (!matcher->Valid()) {
          log::Warn("Detection rule {}: {} is one flat color, so it would match anything; skipping it", rule.name, rule.image);
          matcher.reset();
        }
      } catch (const winrt::hresult_error& e) {
        log::Warn("Detection rule {}: couldn't open {} ({}); skipping it", rule.name, rule.image, Describe(e));
      }
      if (!matcher) continue;
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
      created->intervalMs = INT_MAX;
      region = created.get();
      regions_.push_back(std::move(created));
    }
    region->intervalMs = std::min(region->intervalMs, rule.sampleMs > 0 ? rule.sampleMs : sampleMs_);
    const bool banner = rule.position == LinePosition::Anywhere && rule.event != EventType::Kill &&
                        rule.event != EventType::Assist && rule.event != EventType::Revive;
    region->banner &= banner;
    if (rule.ReadsText()) region->textRules.emplace_back(rule);
    if (matcher) region->icons.push_back({RuleTracker(rule), std::move(*matcher), rule.imageMatch});
  }

  for (auto& region : regions_) {
    // Banners on a screen taller than 2000 pixels read as well at half size.
    if (region->banner && screenH >= 2000 && region->height >= 480) region->shrink = 2;
  }

  // Read a region as part of a bigger one around it when that's less work: the bigger one is
  // then read as often as either needs, but the smaller one not at all. Smallest first, so a
  // chain of regions ends up read from the biggest that's worth it.
  std::vector<Region*> order;
  for (auto& region : regions_) order.push_back(region.get());
  std::ranges::sort(order, [](const Region* a, const Region* b) { return Area(a->box) < Area(b->box); });
  for (Region* inner : order) {
    if (!inner->icons.empty() || inner->textRules.empty()) continue;  // icons need the region's own pixels
    Region* best = nullptr;
    for (Region* outer : order) {
      if (outer == inner || outer->within || !outer->ReadsText() || outer->threshold != inner->threshold) continue;
      if (!Contains(outer->box, inner->box) || Area(outer->box) <= Area(inner->box)) continue;
      // Text must be read at the same size either way.
      if (OcrScale(outer->width, outer->height, outer->shrink) != OcrScale(inner->width, inner->height, inner->shrink)) continue;
      const double together = Area(outer->box) / std::min(outer->intervalMs, inner->intervalMs);
      const double apart = Area(outer->box) / outer->intervalMs + Area(inner->box) / inner->intervalMs;
      if (together > apart) continue;
      if (!best || Area(outer->box) < Area(best->box)) best = outer;
    }
    if (!best) continue;
    inner->within = best;
    best->inside.push_back(inner);
    for (Region* nested : inner->inside) {
      nested->within = best;
      best->inside.push_back(nested);
    }
    inner->inside.clear();
    best->intervalMs = std::min(best->intervalMs, inner->intervalMs);
  }

  size_t read = 0;
  for (auto& region : regions_) {
    if (region->within) continue;
    ++read;
    region->pixels.resize(static_cast<size_t>(region->width) * region->height * 4);
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = region->width;
    desc.Height = region->height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    winrt::check_hresult(gpu_->device->CreateTexture2D(&desc, nullptr, region->staging.put()));
  }

  log::Info("Watching {} screen regions ({} read directly) for {} detection rules", regions_.size(), read, rules_.size());
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
    const bool readsText = std::ranges::any_of(regions_, [](const auto& r) { return r->ReadsText(); });
    const bool looksForIcons = std::ranges::any_of(regions_, [](const auto& r) { return !r->icons.empty(); });
    if (!ocr && readsText) {
      log::Warn("Windows text recognition isn't installed for any of your languages, so kills and other moments aren't "
                "read off the screen. Add English under Settings > Time & language > Language to turn them on.");
      if (!looksForIcons) {
        winrt::uninit_apartment();
        return;
      }
    }

    std::vector<Region*> direct;
    int tickMs = INT_MAX;
    for (auto& region : regions_) {
      if (region->within) continue;
      direct.push_back(region.get());
      tickMs = std::min(tickMs, region->intervalMs);
    }
    if (direct.empty()) {
      winrt::uninit_apartment();
      return;
    }

    auto next = std::chrono::steady_clock::now();
    while (true) {
      next += std::chrono::milliseconds(tickMs);
      {
        std::unique_lock lock(mutex_);
        if (wake_.wait_until(lock, next, [&] { return stop_; })) break;
      }
      auto now = std::chrono::steady_clock::now();
      // After a stall (the PC slept, a debugger paused), restart the schedule.
      if (now - next > std::chrono::seconds(1)) next = now;

      std::vector<Region*> ready;
      {
        std::lock_guard lock(gpu_->contextLock);
        // Read the copies made a look ago, which the GPU has long finished. Never wait for it;
        // one that isn't done yet is read next time.
        for (Region* region : direct) {
          if (!region->copying) continue;
          D3D11_MAPPED_SUBRESOURCE mapped{};
          if (FAILED(gpu_->context->Map(region->staging.get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped))) continue;
          const size_t rowBytes = static_cast<size_t>(region->width) * 4;
          for (UINT y = 0; y < region->height; ++y) {
            std::memcpy(region->pixels.data() + y * rowBytes, static_cast<const uint8_t*>(mapped.pData) + y * mapped.RowPitch,
                        rowBytes);
          }
          gpu_->context->Unmap(region->staging.get(), 0);
          region->copying = false;
          ready.push_back(region);
        }
        // Then copy the regions that are due a look.
        if (ID3D11Texture2D* frame = capture_.LatestFrame()) {
          const int64_t time = NowHns();
          for (Region* region : direct) {
            // Half a look early counts as on time, as the looks never land exactly.
            if (region->copying || now + std::chrono::milliseconds(tickMs / 2) < region->due) continue;
            gpu_->context->CopySubresourceRegion(region->staging.get(), 0, 0, 0, 0, frame, 0, &region->box);
            region->copying = true;
            region->copyTime = time;
            region->due = now + std::chrono::milliseconds(region->intervalMs);
          }
        }
      }

      for (Region* region : ready) Process(*region, region->copyTime, ocr);
    }
  } catch (const winrt::hresult_error& e) {
    log::Error("Event detection stopped: {}", Describe(e));
  }
  winrt::uninit_apartment();
}

void EventDetector::Process(Region& region, int64_t time, const OcrEngine& ocr) {
  const int64_t ms = time / kHnsPerMs;
  // When nothing there changed since it was last read, it would read the same again. It's
  // compared with how it looked then rather than a look ago, so a slow fade still counts.
  const Signature signature = MakeSignature(region.pixels, region.width, region.height);
  const bool changed = !region.haveSignature || !Unchanged(signature, region.signature);
  if (changed) {
    region.signature = signature;
    region.haveSignature = true;
  }

  if (region.ReadsText() && ocr && changed) {
    region.words = ReadWords(ocr, region.pixels, region.width, region.height, region.threshold, region.shrink);
  }

  // Each region's own words: all of them for this one, those within its bounds for the
  // regions inside it.
  auto feed = [&](Region& target, const std::vector<WordBox>& words) {
    std::string text;
    for (const auto& word : words) text += (text.empty() ? "" : " ") + word.text;
    if (debug_ && text != target.lastText && !text.empty()) log::Info("Read [{}]: {}", target.name, Shorten(text, 160));
    target.lastText = text;
    if (target.textRules.empty()) return;
    const auto rows = GroupIntoRows(words);
    for (auto& tracker : target.textRules) {
      const int fresh = tracker.Update(rows, ms);
      for (int i = 0; i < fresh; ++i) sink_(GameEvent{tracker.Rule().event, time, Shorten(text, 80)});
    }
  };
  if (region.ReadsText() && ocr) {
    feed(region, region.words);
    for (Region* inner : region.inside) {
      const double left = inner->box.left - region.box.left;
      const double top = inner->box.top - region.box.top;
      std::vector<WordBox> words;
      for (const auto& word : region.words) {
        const double cx = word.x + word.width / 2 - left;
        const double cy = word.y + word.height / 2 - top;
        if (cx < 0 || cy < 0 || cx >= inner->width || cy >= inner->height) continue;
        WordBox moved = word;
        moved.x -= left;
        moved.y -= top;
        words.push_back(std::move(moved));
      }
      feed(*inner, words);
    }
  }

  std::optional<GrayImage> gray;
  for (auto& icon : region.icons) {
    if (changed) {
      if (!gray) {
        gray = ToGray(region.pixels.data(), static_cast<int>(region.width), static_cast<int>(region.height),
                      static_cast<size_t>(region.width) * 4, false);
      }
      const double alike = icon.matcher.BestMatch(*gray);
      if (debug_ && (alike >= icon.match) != icon.found) log::Info("Icon [{}]: {:.2f}", icon.tracker.Rule().name, alike);
      icon.found = alike >= icon.match;
    }
    const int fresh = icon.tracker.UpdateMatches(icon.found ? std::vector<std::string>{""} : std::vector<std::string>{}, ms);
    for (int i = 0; i < fresh; ++i) sink_(GameEvent{icon.tracker.Rule().event, time, "icon: " + icon.tracker.Rule().name});
  }
}

}  // namespace clip
