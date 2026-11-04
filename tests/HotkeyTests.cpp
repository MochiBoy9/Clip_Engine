// Tests for the clip keys: reading hotkeys, lists of keys that each save a clip of their own
// length, and a game's own set of them from the settings file.

#include "core/Hotkeys.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
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

void TestParseHotkey() {
  const auto f8 = ParseHotkey("F8");
  CHECK(f8 && f8->modifiers == 0 && f8->key == VK_F8);
  const auto altF10 = ParseHotkey("Alt+F10");
  CHECK(altF10 && altF10->modifiers == MOD_ALT && altF10->key == VK_F10);
  const auto spaced = ParseHotkey("ctrl + shift + s");
  CHECK(spaced && spaced->modifiers == (MOD_CONTROL | MOD_SHIFT) && spaced->key == 'S');
  CHECK(!ParseHotkey(""));
  CHECK(!ParseHotkey("Alt"));
  CHECK(!ParseHotkey("F8+F9"));
  CHECK(!ParseHotkey("F25"));
  CHECK(!ParseHotkey("Alt+"));
}

void TestParseClipHotkeys() {
  const auto keys = ParseClipHotkeys("Alt+F8 15, Shift+F8 120s,  Ctrl + F8 45 ");
  CHECK(keys.size() == 3);
  if (keys.size() == 3) {
    CHECK(keys[0].text == "Alt+F8" && keys[0].seconds == 15 && keys[0].hotkey.modifiers == MOD_ALT &&
          keys[0].hotkey.key == VK_F8);
    CHECK(keys[1].text == "Shift+F8" && keys[1].seconds == 120);
    CHECK(keys[2].text == "Ctrl + F8" && keys[2].seconds == 45 && keys[2].hotkey.modifiers == MOD_CONTROL);
  }

  std::vector<std::string> bad;
  const auto some = ParseClipHotkeys("F7 20, Alt+F8, Nope 10, F9 0, F10 x, ,F11 30", &bad);
  CHECK(some.size() == 2);
  if (some.size() == 2) CHECK(some[0].text == "F7" && some[1].text == "F11" && some[1].seconds == 30);
  CHECK(bad.size() == 4);
  if (bad.size() == 4) CHECK(bad[0] == "Alt+F8" && bad[1] == "Nope 10" && bad[2] == "F9 0" && bad[3] == "F10 x");

  CHECK(ParseClipHotkeys("").empty());
  CHECK(ParseClipHotkeys(" , ").empty());
}

void TestGameClipHotkeys(const std::filesystem::path& dir) {
  CHECK(GameClipHotkeysKey("Valorant") == "clip_hotkeys.valorant");

  const auto file = dir / "hotkey_test_settings.ini";
  std::ofstream(file) << "save_hotkey = F8\n"
                         "more_clip_hotkeys = Alt+F8 15\n"
                         "Clip_Hotkeys.Valorant = F8 20, Alt+F8 60 # shorter clips here\n"
                         "clip_hotkeys.fortnite =\n"
                         "clip_hotkeys.overwatch = nothing useful\n";

  const auto valorant = ReadGameClipHotkeys(file, "valorant");
  CHECK(valorant.has_value());
  if (valorant) {
    CHECK(valorant->size() == 2);
    if (valorant->size() == 2) CHECK((*valorant)[0].seconds == 20 && (*valorant)[1].text == "Alt+F8" && (*valorant)[1].seconds == 60);
  }
  // An empty or unreadable set means the game uses the usual keys.
  CHECK(!ReadGameClipHotkeys(file, "fortnite"));
  CHECK(!ReadGameClipHotkeys(file, "overwatch"));
  CHECK(!ReadGameClipHotkeys(file, "apex_legends"));
  CHECK(!ReadGameClipHotkeys(dir / "no_such_settings.ini", "valorant"));

  std::error_code ec;
  std::filesystem::remove(file, ec);
}

}  // namespace

int main(int argc, char** argv) {
  TestParseHotkey();
  TestParseClipHotkeys();
  TestGameClipHotkeys(argc > 1 ? std::filesystem::path(argv[1]) : std::filesystem::temp_directory_path());
  if (g_failures == 0) std::printf("All hotkey tests passed\n");
  return g_failures == 0 ? 0 : 1;
}
