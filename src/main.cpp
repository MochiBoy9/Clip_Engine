#include "app/App.h"
#include "capture/MonitorCapture.h"
#include "core/Config.h"
#include "core/Log.h"
#include "detect/DetectionRules.h"
#include "detect/EventDetector.h"
#include "detect/RuleTester.h"
#include "detect/UniversalHud.h"
#include "game/GameDetector.h"
#include "media/Exporter.h"
#include "media/Trimmer.h"

#include <mfapi.h>
#include <shlobj.h>

#include <algorithm>
#include <fstream>
#include <optional>
#include <string_view>

namespace {

std::filesystem::path DataDir() {
  PWSTR local = nullptr;
  std::filesystem::path dir;
  if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) dir = local;
  CoTaskMemFree(local);
  if (dir.empty()) dir = std::filesystem::current_path();
  return dir / L"Clip_Engine";
}

std::filesystem::path ExeDir() {
  std::wstring path(MAX_PATH, L'\0');
  while (true) {
    const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length < path.size()) {
      path.resize(length);
      break;
    }
    path.resize(path.size() * 2);
  }
  return std::filesystem::path(path).parent_path();
}

// The rules that ship with the app, with the user's own detection.ini (written by Detection
// Studio, or by hand) laid over them: a game the user's file has rules for uses those, and
// every other game the shipped ones, so fixes in an update still reach it. userFile stands in
// for the user's file, as when Detection Studio tests rules that aren't saved yet.
clip::DetectionRules LoadDetectionRules(const std::filesystem::path& dataDir, std::filesystem::path userFile = {}) {
  using namespace clip;
  auto load = [&](const std::filesystem::path& file) -> std::optional<DetectionRules> {
    std::ifstream in(file);
    if (!in) return std::nullopt;
    auto rules = ParseDetectionRules(in);
    for (const auto& warning : rules.warnings) log::Warn("{} {}", winrt::to_string(file.wstring()), warning);
    rules.warnings.clear();
    // Icons and sounds are found next to the detection.ini that names them, then in the other
    // places they can be.
    auto resolve = [&](std::string& name) {
      const std::filesystem::path relative(winrt::to_hstring(name).c_str());
      if (relative.is_absolute()) return;
      for (const auto& folder : {file.parent_path(), dataDir, ExeDir()}) {
        std::error_code ec;
        if (std::filesystem::exists(folder / relative, ec)) {
          name = winrt::to_string((folder / relative).wstring());
          return;
        }
      }
    };
    for (auto& rule : rules.rules) {
      if (rule.LooksForImage()) resolve(rule.image);
      for (auto& sound : rule.sounds) resolve(sound);
    }
    log::Info("Detection rules: {} ({} rules)", winrt::to_string(file.wstring()), rules.rules.size());
    return rules;
  };
  auto shipped = load(ExeDir() / L"detection.ini");
  auto user = load(userFile.empty() ? dataDir / L"detection.ini" : userFile);
  if (!shipped && !user) {
    log::Warn("detection.ini wasn't found next to the app, so only the hotkey saves clips");
    return {};
  }
  if (!user) return std::move(*shipped);
  if (!shipped) return std::move(*user);
  if (!user->editedGames.empty()) {
    std::string games;
    for (const auto& game : user->editedGames) games += (games.empty() ? "" : ", ") + game;
    log::Info("Your own rules replace the built-in ones for: {}", games);
  }
  return MergeDetectionRules(std::move(*shipped), std::move(*user));
}

}  // namespace

// clip_engine.exe --export <clip.mp4> <out.mp4> [--max-mb 10]
// Makes a small copy for sharing and exits. Runs alongside a recording engine.
int RunExportCommand(int argc, wchar_t** argv) {
  using namespace clip;
  ExportOptions options;
  options.input = argv[2];
  options.output = argv[3];
  for (int i = 4; i + 1 < argc; i += 2) {
    if (std::wstring_view(argv[i]) == L"--max-mb") options.maxMegabytes = std::clamp(_wtof(argv[i + 1]), 1.0, 4000.0);
  }
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  if (FAILED(MFStartup(MF_VERSION))) {
    log::Error("Export failed: Media Foundation is unavailable");
    return 1;
  }
  const int result = RunExport(options);
  MFShutdown();
  return result;
}

// clip_engine.exe --trim <clip.mp4> <out.mp4> --start <seconds> --end <seconds>
// Copies part of a clip without re-encoding and exits.
int RunTrimCommand(int argc, wchar_t** argv) {
  using namespace clip;
  TrimOptions options;
  options.input = argv[2];
  options.output = argv[3];
  for (int i = 4; i + 1 < argc; i += 2) {
    const std::wstring_view flag = argv[i];
    if (flag == L"--start") options.startSeconds = _wtof(argv[i + 1]);
    if (flag == L"--end") options.endSeconds = _wtof(argv[i + 1]);
  }
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  if (FAILED(MFStartup(MF_VERSION))) {
    log::Error("Trim failed: Media Foundation is unavailable");
    return 1;
  }
  const int result = RunTrim(options);
  MFShutdown();
  return result;
}

// clip_engine.exe --frame <clip or image> <out.png> [--at seconds]
// clip_engine.exe --read <clip or image> [--at seconds] --region l,t,w,h [--threshold n] [--words "A|B"] [--exclude "C"]
//                 [--position start|end] [--skip-tag true] [--image icon.png] [--image-match 0.85] [--image-screen 1440]
//                 [--quality low|medium|high|ultra] [--event kill] [--anchor top-right] [--aspect 16:9] [--hud-scale 100]
// clip_engine.exe --track <clip> [the --read flags] [--every ms]
// clip_engine.exe --cut-sound <clip> <out.wav> --at seconds
// Detection Studio's helpers: grab a frame to draw on, read a box of it the way detection does,
// read it all through a clip, and cut a sound out of a clip for a rule to listen for. {player}
// in the words is filled in with the player's names, as it is in game.
int RunStudioCommand(int argc, wchar_t** argv) {
  using namespace clip;
  const std::wstring_view command = argv[1];
  // --frame and --cut-sound name a file to write after the input.
  const bool frame = command == L"--frame" || command == L"--cut-sound";
  int everyMs = 250;
  double seconds = 0;
  ReadQuality quality = ReadQuality::Medium;
  double hudScale = 1;
  DetectionRule rule;
  rule.name = "studio";
  std::string words, exclude;
  for (int i = frame ? 4 : 3; i + 1 < argc; i += 2) {
    const std::wstring_view flag = argv[i];
    const std::string value = winrt::to_string(argv[i + 1]);
    if (flag == L"--at") seconds = _wtof(argv[i + 1]);
    if (flag == L"--every") everyMs = _wtoi(argv[i + 1]);
    if (flag == L"--threshold") rule.threshold = std::clamp(_wtoi(argv[i + 1]), 0, 255);
    if (flag == L"--words") words = value;
    if (flag == L"--exclude") exclude = value;
    if (flag == L"--skip-tag") rule.skipTag = value == "true";
    if (flag == L"--position") rule.position = value == "start" ? LinePosition::Start : value == "end" ? LinePosition::End : LinePosition::Anywhere;
    if (flag == L"--image") rule.image = value;
    if (flag == L"--image-match") rule.imageMatch = std::clamp(_wtof(argv[i + 1]), 0.05, 1.0);
    if (flag == L"--image-screen") rule.imageScreen = std::max(0, _wtoi(argv[i + 1]));
    if (flag == L"--event") {
      if (const auto e = ParseEventType(value)) rule.event = *e;
    }
    if (flag == L"--quality") {
      if (const auto q = ParseReadQuality(value)) quality = *q;
    }
    if (flag == L"--region") {
      double l = 0, t = 0, w = 0, h = 0;
      if (swscanf_s(argv[i + 1], L"%lf,%lf,%lf,%lf", &l, &t, &w, &h) == 4) {
        rule.region.left = l;
        rule.region.top = t;
        rule.region.width = w;
        rule.region.height = h;
      }
    }
    if (flag == L"--anchor") ParseAnchor(value, rule.region.anchorX, rule.region.anchorY);
    if (flag == L"--aspect") ParseAspect(value, rule.region.aspect);
    if (flag == L"--hud-scale") hudScale = std::clamp(_wtof(argv[i + 1]), 25.0, 400.0) / 100;
    if (flag == L"--classify") rule.classify = value == "true";
    if (flag == L"--zone") rule.hudZone = value;
    if (flag == L"--game") rule.game = value;
  }
  const auto playerNames = ReadPlayerNames(DataDir() / L"clip_engine.ini");
  SetPhrases(rule, words, exclude, playerNames);
  if (rule.classify) {
    std::vector<DetectionRule> one{rule};
    AttachHudModel(one, LoadHudModel(DataDir() / std::string(kHudLearnedFile), rule.game, playerNames));
    rule = one.front();
  }
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  if (FAILED(MFStartup(MF_VERSION))) {
    log::Error("{} failed: Media Foundation is unavailable", command == L"--frame" ? "Frame" : "Read");
    return 1;
  }
  int result = 1;
  if (command == L"--frame") result = RunFrameCommand(argv[2], argv[3], seconds);
  else if (command == L"--cut-sound") result = RunCutSoundCommand(argv[2], argv[3], seconds);
  else if (command == L"--track") result = RunTrackCommand(argv[2], rule, quality, hudScale, everyMs);
  else result = RunReadCommand(argv[2], seconds, rule, quality, hudScale);
  MFShutdown();
  return result;
}

// clip_engine.exe --scan <clip> --game <id or name> [--quality low|medium|high|ultra] [--hud-scale 100]
//                 [--rules detection.ini] [--universal true]
// clip_engine.exe --learn <clip> --game <id or name> --marks "kill@12.3;death@40.1" [--quality ...] [--hud-scale 100]
// Runs a game's rules (yours where you've changed them) over a whole clip, for checking
// detection against what really happened in it. --rules reads your rules from that file instead
// of your detection.ini, so Detection Studio can test changes before they're saved. A game with no
// rules that read the screen is read by the universal HUD reader, as in game; --universal true
// reads any game with it, to try it. --learn teaches the universal reader from the player's marks.
int RunScanCommand(int argc, wchar_t** argv) {
  using namespace clip;
  const bool learn = std::wstring_view(argv[1]) == L"--learn";
  bool universal = false;
  std::string marks;
  std::string game;
  ReadQuality quality = ReadQuality::Medium;
  std::optional<double> hudScale;
  std::filesystem::path userRules;
  for (int i = 3; i + 1 < argc; i += 2) {
    const std::wstring_view flag = argv[i];
    const std::string value = winrt::to_string(argv[i + 1]);
    if (flag == L"--game") game = value;
    if (flag == L"--quality") {
      if (const auto q = ParseReadQuality(value)) quality = *q;
    }
    if (flag == L"--hud-scale") hudScale = std::clamp(_wtof(argv[i + 1]), 25.0, 400.0) / 100;
    if (flag == L"--rules") userRules = argv[i + 1];
    if (flag == L"--universal") universal = value == "true";
    if (flag == L"--marks") marks = value;
  }
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  const auto dataDir = DataDir();
  const DetectionRules rules = LoadDetectionRules(dataDir, userRules);
  // The game by its id, or by the name its clips are filed under.
  std::vector<GameProfile> custom;
  for (const auto& g : rules.games) custom.push_back({g.id, g.name, {}, GameApi::None});
  for (const auto& profile : GameDetector(custom).Games()) {
    if (_stricmp(profile.name.c_str(), game.c_str()) == 0) game = profile.id;
  }
  const auto settings = dataDir / L"clip_engine.ini";
  const auto playerNames = ReadPlayerNames(settings);
  const auto learnedFile = dataDir / std::string(kHudLearnedFile);
  auto forGame = rules.ForGame(game, playerNames);
  const auto model = LoadHudModel(learnedFile, game, playerNames);
  AttachHudModel(forGame, model);
  // The universal reader reads a game without rules, as it does in game (src/app/App.cpp).
  const bool readsScreen = std::ranges::any_of(forGame, [](const DetectionRule& r) { return !r.ListensForSound(); });
  const bool hasApi = std::ranges::any_of(GameDetector(custom).Games(), [&](const GameProfile& p) {
    return p.id == game && p.api != GameApi::None;
  });
  if (universal || (!readsScreen && !hasApi && !rules.rulesRemoved.contains(game) &&
                    Config::Load(settings).universalHud)) {
    if (universal) forGame.clear();
    const auto added = UniversalRules(game.empty() ? "game" : game, model);
    forGame.insert(forGame.end(), added.begin(), added.end());
    log::Info("Reading with the universal HUD reader ({} examples learned from)", model->ExampleCount());
  }
  if (FAILED(MFStartup(MF_VERSION))) {
    log::Error("{} failed: Media Foundation is unavailable", learn ? "Learn" : "Scan");
    return 1;
  }
  const double scale = hudScale.value_or(ReadHudScale(settings, game));
  const int result = learn ? RunLearnCommand(argv[2], *model, ParseHudMarks(marks), game, quality, scale, learnedFile)
                           : RunScanCommand(argv[2], forGame, quality, scale);
  MFShutdown();
  return result;
}

int wmain(int argc, wchar_t** argv) {
  using namespace clip;
  if (argc >= 4 && std::wstring_view(argv[1]) == L"--export") return RunExportCommand(argc, argv);
  if (argc >= 4 && std::wstring_view(argv[1]) == L"--trim") return RunTrimCommand(argc, argv);
  if (argc >= 4 && std::wstring_view(argv[1]) == L"--frame") return RunStudioCommand(argc, argv);
  if (argc >= 3 && std::wstring_view(argv[1]) == L"--read") return RunStudioCommand(argc, argv);
  if (argc >= 3 && std::wstring_view(argv[1]) == L"--track") return RunStudioCommand(argc, argv);
  if (argc >= 4 && std::wstring_view(argv[1]) == L"--cut-sound") return RunStudioCommand(argc, argv);
  if (argc >= 3 && std::wstring_view(argv[1]) == L"--scan") return RunScanCommand(argc, argv);
  if (argc >= 3 && std::wstring_view(argv[1]) == L"--learn") return RunScanCommand(argc, argv);

  // One copy at a time; two would fight over the hotkey and the GPU encoder.
  HANDLE instance = CreateMutexW(nullptr, TRUE, L"Local\\ClipEngine.SingleInstance");
  if (GetLastError() == ERROR_ALREADY_EXISTS) {
    std::fputs("Clip Engine is already running.\n", stderr);
    return 1;
  }

  SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
  winrt::init_apartment(winrt::apartment_type::multi_threaded);

  const auto dataDir = DataDir();
  log::Init(dataDir / L"clip_engine.log");
  log::Info("Clip Engine {} starting", CLIP_ENGINE_VERSION);

  if (!MonitorCapture::IsSupported()) {
    log::Error("Screen capture needs Windows 10 version 1903 or newer");
    return 1;
  }

  const auto configFile = dataDir / L"clip_engine.ini";
  Config config = Config::Load(configFile);
  log::Info("Settings: {}", winrt::to_string(configFile.wstring()));
  log::Info("Clips go to: {}", winrt::to_string(config.outputDir.wstring()));

  if (FAILED(MFStartup(MF_VERSION))) {
    log::Error("Media Foundation is unavailable. On Windows N editions, install the Media Feature Pack.");
    return 1;
  }

  // Always read, for the games added there; the rules themselves only matter with detection on.
  DetectionRules rules = LoadDetectionRules(dataDir);
  if (config.detection && !EventDetector::IsAvailable()) {
    log::Warn("Windows text recognition isn't installed, so the screen is only checked for icons. Add English under "
              "Settings > Time & language > Language to read kills and other moments off the screen too.");
    std::erase_if(rules.rules, [](const DetectionRule& rule) { return !rule.LooksForImage(); });
    config.universalHud = false;
  } else if (!config.detection) {
    rules.rules.clear();
  }

  int result = App(std::move(config), std::move(rules)).Run();

  MFShutdown();
  if (instance) CloseHandle(instance);
  return result;
}
