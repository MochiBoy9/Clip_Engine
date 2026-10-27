#include "app/App.h"
#include "capture/MonitorCapture.h"
#include "core/Config.h"
#include "core/Log.h"
#include "detect/DetectionRules.h"
#include "detect/EventDetector.h"
#include "media/Exporter.h"
#include "media/Trimmer.h"

#include <mfapi.h>
#include <shlobj.h>

#include <algorithm>
#include <fstream>
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

// A copy in the data folder wins over the one that ships with the app, so tuning a rule
// survives updates.
clip::DetectionRules LoadDetectionRules(const std::filesystem::path& dataDir) {
  using namespace clip;
  for (const auto& file : {dataDir / L"detection.ini", ExeDir() / L"detection.ini"}) {
    std::ifstream in(file);
    if (!in) continue;
    auto rules = ParseDetectionRules(in);
    for (const auto& warning : rules.warnings) log::Warn("detection.ini {}", warning);
    log::Info("Detection rules: {} ({} rules)", winrt::to_string(file.wstring()), rules.rules.size());
    return rules;
  }
  log::Warn("detection.ini wasn't found next to the app, so only the hotkey saves clips");
  return {};
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

int wmain(int argc, wchar_t** argv) {
  using namespace clip;
  if (argc >= 4 && std::wstring_view(argv[1]) == L"--export") return RunExportCommand(argc, argv);
  if (argc >= 4 && std::wstring_view(argv[1]) == L"--trim") return RunTrimCommand(argc, argv);

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

  DetectionRules rules;
  if (config.detection) {
    if (EventDetector::IsAvailable()) {
      rules = LoadDetectionRules(dataDir);
    } else {
      log::Warn("Windows text recognition isn't installed, so automatic clips are off. Add English under "
                "Settings > Time & language > Language to turn them on.");
    }
  }

  int result = App(std::move(config), std::move(rules)).Run();

  MFShutdown();
  if (instance) CloseHandle(instance);
  return result;
}
