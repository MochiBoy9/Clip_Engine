#include "app/App.h"
#include "capture/MonitorCapture.h"
#include "core/Config.h"
#include "core/Log.h"

#include <mfapi.h>
#include <shlobj.h>

namespace {

std::filesystem::path DataDir() {
  PWSTR local = nullptr;
  std::filesystem::path dir;
  if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) dir = local;
  CoTaskMemFree(local);
  if (dir.empty()) dir = std::filesystem::current_path();
  return dir / L"Clip_Engine";
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

  int result = App(std::move(config)).Run();

  MFShutdown();
  if (instance) CloseHandle(instance);
  return result;
}
