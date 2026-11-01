#include "detect/EventDetector.h"

#include "core/Clock.h"
#include "core/Log.h"
#include "core/Power.h"
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

// A region checked only for HUD text coming or going is read this often whatever it looks like,
// so text dimmer than the HUD usually is is still read within a second.
constexpr int64_t kHudRefreshMs = 1000;

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

// One part of the screen, read once per look for every rule that looks at it, or a game's
// scoreboard, read every few seconds.
struct EventDetector::Region {
  std::string name;
  D3D11_BOX box{};
  UINT width = 0;
  UINT height = 0;
  int threshold = 0;
  UINT shrink = 1;      // see OcrScale
  int intervalMs = 250;
  bool banner = true;   // every rule here is for a banner that stays up, so it can be read smaller
  bool hudOnly = true;  // read again only when HUD text came or went (RegionChanged)
  int bright = kHudBright;  // how bright its text is at least

  std::vector<RuleTracker> textRules;
  struct Icon {
    RuleTracker tracker;
    ImageMatcher matcher;
    double match;
    bool found = false;
    double alike = 0;  // how alike it was at the last look
  };
  std::vector<Icon> icons;

  const ScoreboardRule* board = nullptr;
  ScoreboardTracker boardTracker;

  // A region inside this one, read from this one's words instead of on its own.
  Region* within = nullptr;
  std::vector<Region*> inside;

  winrt::com_ptr<ID3D11Texture2D> staging;
  std::vector<uint8_t> pixels;  // BGRA, width * height * 4
  bool copying = false;         // a copy is on its way from the GPU
  int64_t copyTime = 0;
  std::chrono::steady_clock::time_point copiedAt{};  // when it was last copied, to pace the next
  bool keyDown = false;  // a scoreboard's key was held at the last look
  bool boardUp = false;  // a scoreboard was being read at the last look

  bool haveLook = false;
  RegionLook look;   // how it looked when it was last read
  int64_t readMs = 0;  // and when
  std::vector<std::vector<WordBox>> reads;  // the last read, one for each way it's read (ReadPasses)
  std::string lastText;

  bool ReadsText() const { return !textRules.empty() || !inside.empty() || board; }
};

namespace {

// SetWindowDisplayAffinity's "leave out of captures", as Clip Engine's own side panel uses.
constexpr DWORD kExcludedFromCapture = 0x11;

bool InFront(DWORD processId) {
  if (processId == 0) return true;
  DWORD front = 0;
  HWND window = GetForegroundWindow();
  if (window) GetWindowThreadProcessId(window, &front);
  if (front == processId) return true;
  // A window left out of captures, like the side panel, isn't in the frames that are read, so
  // the game's HUD is still there to read around it; the recording treats it the same way.
  DWORD affinity = 0;
  return window && GetWindowDisplayAffinity(window, &affinity) && affinity == kExcludedFromCapture;
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
                             std::vector<std::string> playerNames, DetectionSettings settings, ReadQuality quality,
                             double hudScale,
                             bool debug, Sink sink, ScoreboardSink scoreboardSink)
    : gpu_(std::move(gpu)),
      capture_(capture),
      rules_(std::move(rules)),
      scoreboards_(std::move(scoreboards)),
      playerNames_(std::move(playerNames)),
      settings_(settings),
      quality_(quality),
      debug_(debug),
      sink_(std::move(sink)),
      scoreboardSink_(std::move(scoreboardSink)),
      calm_(settings.calmMs),
      fuser_(
          [this](const GameEvent& e) {
            // Games show the scoreboard by themselves once a match is over.
            if (e.type == EventType::MatchEnd) boardGate_.MatchEnded(e.time / kHnsPerMs);
            calm_.Saw(e.time / kHnsPerMs);
            sink_(e);
          },
          [](const std::string& note) { log::Info("{}", note); }) {
  const double screenW = capture_.Width();
  const double screenH = capture_.Height();
  // Where the HUD draws each region on this screen, at the HUD size the player set.
  auto boxFor = [&](const ScreenRegion& rule) {
    const ScreenRegion r = PlaceRegion(rule, screenW, screenH, hudScale);
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
    created->bright = threshold > 0 ? threshold : kHudBright;
    created->intervalMs = INT_MAX;
    regions_.push_back(std::move(created));
    return regions_.back().get();
  };

  for (const auto& rule : rules_) {
    if (rule.ListensForSound()) continue;  // see sounds_ below
    const D3D11_BOX box = boxFor(rule.region);
    if (box.right < box.left + 8 || box.bottom < box.top + 8) {
      log::Warn("Detection rule {} covers too little of the screen; skipping it", rule.name);
      continue;
    }

    std::optional<ImageMatcher> matcher;
    if (rule.LooksForImage()) {
      try {
        const Picture picture = LoadPicture(std::filesystem::path(winrt::to_hstring(rule.image).c_str()));
        // An icon is drawn as big as the rest of the HUD: scaled with the height of its 16:9
        // area, and with the HUD size.
        const int pictureScreen = rule.imageScreen > 0 ? rule.imageScreen : static_cast<int>(capture_.Height());
        const double drawnAt = rule.imageScreen > 0 ? HudHeight(rule.region, screenW, screenH, hudScale) : screenH * hudScale;
        matcher.emplace(ToGray(picture.bgra.data(), static_cast<int>(picture.width), static_cast<int>(picture.height),
                               static_cast<size_t>(picture.width) * 4, true),
                        pictureScreen, static_cast<int>(std::lround(drawnAt)));
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
    Region* region = existing != regions_.end() ? existing->get() : addRegion(rule.name, box, rule.threshold);
    region->intervalMs = std::min(region->intervalMs, ScaledSampleMs(rule.sampleMs > 0 ? rule.sampleMs : settings_.sampleMs, quality_));
    const bool banner = rule.position == LinePosition::Anywhere && rule.event != EventType::Kill &&
                        rule.event != EventType::Assist && rule.event != EventType::Revive;
    region->banner &= banner;
    // An icon can be any color, so its region goes by any change; so does a rule set to.
    const ChangeCheck check = rule.changeCheck == ChangeCheck::Default ? settings_.changeCheck : rule.changeCheck;
    region->hudOnly &= check == ChangeCheck::Hud && !matcher;
    if (rule.pauses) fuser_.UsePauses();
    if (rule.ReadsText()) region->textRules.emplace_back(rule);
    if (matcher) region->icons.push_back({RuleTracker(rule), std::move(*matcher), rule.imageMatch});
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
      // A scoreboard always gets a region of its own: it's read seldom, and in full.
      Region* region = addRegion(board.name, box, board.threshold);
      region->board = &board;
      region->banner = false;
      region->hudOnly = false;  // its numbers can be dimmer than the HUD
      region->intervalMs = std::max(settings_.sampleMs, board.sampleMs);
    }
  }

  for (auto& region : regions_) {
    // Banners on a screen taller than 2000 pixels read as well at half size.
    if (ShrinksBanners(quality_) && region->banner && screenH >= 2000 && region->height >= 480) region->shrink = 2;
  }

  // Read a region as part of a bigger one around it when that's less work: the bigger one is
  // then read as often as either needs, but the smaller one not at all. Smallest first, so a
  // chain of regions ends up read from the biggest that's worth it.
  std::vector<Region*> order;
  for (auto& region : regions_) order.push_back(region.get());
  std::ranges::sort(order, [](const Region* a, const Region* b) { return Area(a->box) < Area(b->box); });
  for (Region* inner : order) {
    // Icons need the region's own pixels; a scoreboard is read on its own schedule.
    if (!inner->icons.empty() || inner->board || inner->textRules.empty()) continue;
    Region* best = nullptr;
    for (Region* outer : order) {
      if (outer == inner || outer->within || outer->board || !outer->ReadsText() || outer->threshold != inner->threshold) continue;
      if (!Contains(outer->box, inner->box) || Area(outer->box) <= Area(inner->box)) continue;
      // Text must be read at the same size either way.
      if (OcrScale(outer->width, outer->height, outer->shrink, quality_) !=
          OcrScale(inner->width, inner->height, inner->shrink, quality_)) {
        continue;
      }
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
    best->hudOnly &= inner->hudOnly;
  }

  sounds_ = std::make_unique<SoundWatch>(rules_, [](const std::string& warning) { log::Warn("{}", warning); });
  if (sounds_->Empty()) sounds_.reset();
  // A sound is heard only once it has played out, so moments wait for it a little longer.
  if (sounds_) fuser_.SetHoldMs(EventFuser::kSoundHoldMs);

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

  log::Info("Watching {} screen regions ({} read directly) for {} detection rules, reading quality {}", regions_.size(),
            read, rules_.size(), ToString(quality_));
  if (sounds_) log::Info("Listening to the game's audio for its sounds");
  if (const auto pauses = std::ranges::count_if(rules_, &DetectionRule::pauses); pauses > 0) {
    log::Info("Not counting kills during killcams or while spectating ({} pause rules)", pauses);
  }
  for (const auto& region : regions_) {
    if (!region->board) continue;
    switch (region->board->read) {
      case ScoreboardRead::WhileHeld: log::Info("Reading {} while its key is held and after a match", region->name); break;
      case ScoreboardRead::AfterMatch: log::Info("Reading {} after a match", region->name); break;
      case ScoreboardRead::Always: log::Info("Reading {} all the time", region->name); break;
    }
  }
  if (std::abs(screenW / screenH - kDefaultAspect) > 0.02 || hudScale != 1) {
    log::Info("Regions placed for a {}x{} screen with the HUD at {:.0f}%", capture_.Width(), capture_.Height(), hudScale * 100);
  }
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
  const EfficiencyMode efficiency;

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
      if (region->board && region->board->read == ScoreboardRead::WhileHeld) tickMs = std::min(tickMs, ScoreboardGate::kHeldMs);
    }
    calm_.Saw(NowHns() / kHnsPerMs);
    if (direct.empty() && !sounds_) {
      winrt::uninit_apartment();
      return;
    }
    if (direct.empty()) tickMs = 250;

    auto next = std::chrono::steady_clock::now();
    bool wasInFront = true;
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
        // Then copy the regions that are due a look, unless the game is behind another window.
        const bool inFront = InFront(gameProcess_);
        if (debug_ && inFront != wasInFront) {
          if (inFront) log::Info("The game is in front again; reading the screen");
          else log::Info("Another window is in front of the game; not reading the screen");
        }
        wasInFront = inFront;
        ID3D11Texture2D* frame = inFront ? capture_.LatestFrame() : nullptr;
        if (frame) {
          const int64_t time = NowHns();
          const int64_t nowMs = time / kHnsPerMs;
          for (Region* region : direct) {
            int intervalMs = calm_.IntervalMs(region->intervalMs, nowMs);
            if (region->board) {
              // Only while the scoreboard can be up: its key is held, or a match just ended.
              const ScoreboardRule& board = *region->board;
              const bool keyDown = board.read == ScoreboardRead::WhileHeld && (GetAsyncKeyState(board.key) & 0x8000) != 0;
              const bool wanted = boardGate_.Wanted(board, keyDown, nowMs);
              if (!wanted && region->boardUp) {
                // Put away: the next time it's up starts afresh.
                region->boardTracker.Update(std::nullopt);
                region->haveLook = false;
              }
              if (debug_ && wanted != region->boardUp) log::Info("{} {}", wanted ? "Reading" : "Stopped reading", region->name);
              region->boardUp = wanted;
              // Read it at once when its key goes down.
              if (keyDown && !region->keyDown) region->copiedAt = {};
              region->keyDown = keyDown;
              if (!wanted) continue;
              intervalMs = ScoreboardGate::IntervalMs(board, keyDown);
            }
            // Half a look early counts as on time, as the looks never land exactly.
            if (region->copying ||
                now + std::chrono::milliseconds(tickMs / 2) < region->copiedAt + std::chrono::milliseconds(intervalMs)) {
              continue;
            }
            gpu_->context->CopySubresourceRegion(region->staging.get(), 0, 0, 0, 0, frame, 0, &region->box);
            region->copying = true;
            region->copyTime = time;
            region->copiedAt = now;
          }
        }
      }

      for (Region* region : ready) Process(*region, region->copyTime, ocr);
      std::vector<Heard> heard;
      {
        std::lock_guard lock(heardMutex_);
        heard.swap(heard_);
      }
      for (const auto& h : heard) {
        calm_.Saw(h.time / kHnsPerMs);
        fuser_.Add(*h.rule, h.count, h.time, h.text);
      }
      fuser_.Flush(NowHns());
    }
  } catch (const winrt::hresult_error& e) {
    log::Error("Event detection stopped: {}", Describe(e));
  }
  winrt::uninit_apartment();
}

void EventDetector::Hear(const int16_t* pcm, uint32_t frames, int64_t pts) {
  if (!sounds_) return;
  sounds_->Push(pcm, frames, 48000, 2, pts, [&](const DetectionRule& rule, int count, int64_t time, const std::string& text) {
    if (debug_) log::Info("Heard [{}]: {}", rule.name, text);
    std::lock_guard lock(heardMutex_);
    heard_.push_back({&rule, count, time, text});
  });
}

Witness EventDetector::Where(const Region& region) const {
  const double w = capture_.Width();
  const double h = capture_.Height();
  Witness where;
  where.left = region.box.left / w;
  where.top = region.box.top / h;
  where.width = (region.box.right - region.box.left) / w;
  where.height = (region.box.bottom - region.box.top) / h;
  return where;
}

void EventDetector::Process(Region& region, int64_t time, const OcrEngine& ocr) {
  const int64_t ms = time / kHnsPerMs;
  // When nothing there changed since it was last read, it would read the same again. It's
  // compared with how it looked then rather than a look ago, so a slow fade still counts.
  const RegionLook look = LookAt(region.pixels, region.width, region.height, region.bright);
  const bool changed = !region.haveLook || RegionChanged(region.look, look, region.hudOnly) ||
                       (region.hudOnly && ms - region.readMs >= kHudRefreshMs);
  if (changed) {
    region.look = look;
    region.haveLook = true;
    region.readMs = ms;
  }

  if (region.ReadsText() && ocr && changed) {
    region.reads = ReadWords(ocr, region.pixels, region.width, region.height, region.threshold, region.shrink, quality_);
  }

  auto textOf = [](const std::vector<std::vector<WordBox>>& reads) {
    std::string text;
    if (reads.empty()) return text;
    for (const auto& word : reads.front()) text += (text.empty() ? "" : " ") + word.text;
    return text;
  };
  // Each region's own words: all of them for this one, those within its bounds for the
  // regions inside it. Each rule goes by whichever read of the region suits it (ChooseRead).
  auto feed = [&](Region& target, const std::vector<std::vector<WordBox>>& reads) {
    const std::string text = textOf(reads);
    if (debug_ && text != target.lastText && !text.empty()) log::Info("Read [{}]: {}", target.name, Shorten(text, 160));
    target.lastText = text;
    if (target.textRules.empty()) return;
    std::vector<std::vector<std::vector<std::string>>> rows;
    for (const auto& words : reads) rows.push_back(GroupIntoRows(words));
    if (rows.empty()) rows.emplace_back();
    for (auto& tracker : target.textRules) {
      const size_t chosen = ChooseRead(tracker.Rule(), rows);
      if (tracker.Rule().pauses) {
        // A killcam or spectating: what shows from a look ago until a while after it's gone isn't
        // the player's own.
        if (!FindMatches(tracker.Rule(), rows[chosen]).empty()) {
          const int64_t lead = int64_t{target.intervalMs} * kHnsPerMs;
          const int64_t hold = int64_t{tracker.Rule().holdMs} * kHnsPerMs;
          if (debug_ && !fuser_.Paused(time)) log::Info("[{}] is up; not counting kills", tracker.Rule().name);
          fuser_.Pause(time - lead, time + hold);
        }
        continue;
      }
      const int fresh = tracker.Update(rows[chosen], ms);
      if (fresh > 0) calm_.Saw(ms);
      if (debug_ && fresh > 0 && chosen > 0) log::Info("[{}] found in read {} of the region", tracker.Rule().name, chosen + 1);
      fuser_.Add(tracker.Rule(), fresh, time, Shorten(text, 120), Where(target));
    }
  };
  if (region.board) {
    if (!ocr) return;
    const std::string text = textOf(region.reads);
    if (debug_ && text != region.lastText && !text.empty()) log::Info("Read [{}]: {}", region.name, Shorten(text, 160));
    region.lastText = text;
    // The first read that finds the player's row.
    std::optional<Stats> read;
    for (const auto& words : region.reads) {
      if (!words.empty()) read = ReadScoreboard(*region.board, words, playerNames_);
      if (read) break;
    }
    if (const auto stats = region.boardTracker.Update(read)) scoreboardSink_(Scoreboard{time, "screen", *stats});
    return;
  }

  if (region.ReadsText() && ocr) {
    feed(region, region.reads);
    for (Region* inner : region.inside) {
      const double left = inner->box.left - region.box.left;
      const double top = inner->box.top - region.box.top;
      std::vector<std::vector<WordBox>> reads;
      for (const auto& all : region.reads) {
        auto& words = reads.emplace_back();
        for (const auto& word : all) {
          const double cx = word.x + word.width / 2 - left;
          const double cy = word.y + word.height / 2 - top;
          if (cx < 0 || cy < 0 || cx >= inner->width || cy >= inner->height) continue;
          WordBox moved = word;
          moved.x -= left;
          moved.y -= top;
          words.push_back(std::move(moved));
        }
      }
      feed(*inner, reads);
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
      icon.alike = alike;
    }
    const int fresh = icon.tracker.UpdateMatches(icon.found ? std::vector<std::string>{""} : std::vector<std::string>{}, ms);
    if (fresh > 0) calm_.Saw(ms);
    fuser_.Add(icon.tracker.Rule(), fresh, time, std::format("icon: {} ({:.2f} alike)", icon.tracker.Rule().name, icon.alike),
               Where(region));
  }
}

}  // namespace clip
