#include "app/App.h"
#include "capture/MonitorCapture.h"
#include "core/Config.h"
#include "core/Log.h"
#include "detect/DetectionRules.h"
#include "detect/EventDetector.h"

#include <mfapi.h>
#include <shlobj.h>

#include <fstream>

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

int wmain() {
  using namespace clip;

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
