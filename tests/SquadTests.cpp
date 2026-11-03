// Tests for squad sync's engine side: lining clips up by wall-clock time, the desktop app's
// save command, the queue of squadmates' moments, and the squad fields in clip metadata.

#include "media/ClipMetadata.h"
#include "squad/SquadClips.h"

#include <cstdio>
#include <string>

namespace {

int g_failures = 0;

void Check(bool ok, const char* expression, int line) {
  if (ok) return;
  ++g_failures;
  std::printf("FAILED line %d: %s\n", line, expression);
}

#define CHECK(x) Check((x), #x, __LINE__)

using namespace clip;

void TestClockMath() {
  const ClockPair now{5'000'000'000, 1'762'142'400'000};
  CHECK(HnsAtUnixMs(now, now.unixMs) == now.hns);
  CHECK(HnsAtUnixMs(now, now.unixMs - 1500) == now.hns - 15'000'000);
  CHECK(HnsAtUnixMs(now, now.unixMs + 20) == now.hns + 200'000);
  CHECK(UnixMsAtHns(now, now.hns - 15'000'000) == now.unixMs - 1500);
  CHECK(UnixMsAtHns(now, now.hns + 4'999) == now.unixMs);       // rounds to the nearest ms
  CHECK(UnixMsAtHns(now, now.hns + 5'000) == now.unixMs + 1);
  CHECK(UnixMsAtHns(now, now.hns - 4'999) == now.unixMs);
  CHECK(UnixMsAtHns(now, now.hns - 5'000) == now.unixMs - 1);
  // There and back is exact on whole milliseconds.
  for (int64_t ms : {-60'000LL, -1LL, 0LL, 7LL, 123'456LL}) CHECK(UnixMsAtHns(now, HnsAtUnixMs(now, now.unixMs + ms)) == now.unixMs + ms);
}

void TestParseSquadSave() {
  const auto r = ParseSquadSave("squad-save m-01HZX_9 1762142400000 1762142415000 The Finals\r\n");
  CHECK(r.has_value());
  if (r) {
    CHECK(r->moment == "m-01HZX_9");
    CHECK(r->startUnixMs == 1762142400000);
    CHECK(r->endUnixMs == 1762142415000);
    CHECK(r->game == "The Finals");
  }
  const auto anyGame = ParseSquadSave("  squad-save abc 10 20");
  CHECK(anyGame && anyGame->game.empty());

  CHECK(!ParseSquadSave("squad-save"));
  CHECK(!ParseSquadSave("squad-save abc 20 10 Valorant"));            // backwards
  CHECK(!ParseSquadSave("squad-save abc 10 10 Valorant"));            // empty
  CHECK(!ParseSquadSave("squad-save abc 0 120001 Valorant"));         // longer than two minutes
  CHECK(ParseSquadSave("squad-save abc 0 120000 Valorant").has_value());
  CHECK(!ParseSquadSave("squad-save ../x 10 20 Valorant"));           // not a safe id
  CHECK(!ParseSquadSave("squad-save a\"b 10 20"));
  CHECK(!ParseSquadSave("squad-save abc 10x 20"));
  CHECK(!ParseSquadSave("squad-saves abc 10 20"));
  CHECK(!ParseSquadSave("save abc 10 20"));
  CHECK(!ParseSquadSave(std::string("squad-save ") + std::string(65, 'a') + " 10 20"));
  CHECK(IsSquadMomentId(std::string(64, 'a')));
  CHECK(!IsSquadMomentId(""));
}

void TestSquadQueue() {
  SquadSaveQueue q;
  CHECK(q.Add({"a", 100, 200}));
  CHECK(q.Add({"b", 150, 260}));
  CHECK(!q.Add({"a", 90, 210}));  // the same moment again widens it, never saved twice
  CHECK(q.TakeReady(199).empty());
  auto ready = q.TakeReady(220);
  CHECK(ready.size() == 1 && ready[0].moment == "a" && ready[0].start == 90 && ready[0].end == 210);
  CHECK(!q.Add({"a", 100, 200}));  // already saved
  CHECK(!q.Empty());
  auto all = q.TakeAll();
  CHECK(all.size() == 1 && all[0].moment == "b");
  CHECK(q.Empty());
  CHECK(q.TakeReady(1'000'000).empty());
}

void TestSquadMetadata() {
  ClipMetadata m;
  m.game = "Valorant";
  m.trigger = "squad";
  m.seconds = 15;
  m.utcStartMs = 1762142400123;
  m.squadMoment = "m-42";
  m.markers = {{EventType::Kill, 10, "ENTERRO ELIMINATED X"}};
  const auto json = ToJson(m);
  CHECK(json.find(R"("utc_start": 1762142400123)") != std::string::npos);
  CHECK(json.find(R"("squad_moment": "m-42")") != std::string::npos);
  const auto back = ParseMetadataJson(json);
  CHECK(back && back->utcStartMs == 1762142400123 && back->squadMoment == "m-42" && back->trigger == "squad");

  // A clip without them leaves them out, and older clips read back as unknown.
  const auto plain = ToJson(ClipMetadata{});
  CHECK(plain.find("utc_start") == std::string::npos && plain.find("squad_moment") == std::string::npos);
  const auto old = ParseMetadataJson(R"({"game":"X","seconds":3,"events":[]})");
  CHECK(old && old->utcStartMs == 0 && old->squadMoment.empty());

  // Trimming moves the start time with the new start.
  const auto t = Trimmed(m, 2.5, 12);
  CHECK(t.utcStartMs == 1762142402623);
  CHECK(t.squadMoment == "m-42");
}

}  // namespace

int main() {
  TestClockMath();
  TestParseSquadSave();
  TestSquadQueue();
  TestSquadMetadata();
  if (g_failures == 0) std::printf("All squad tests passed\n");
  return g_failures == 0 ? 0 : 1;
}
