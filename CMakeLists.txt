cmake_minimum_required(VERSION 3.24)
project(ClipEngine VERSION 0.2.1 LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC)
  message(FATAL_ERROR "Clip Engine builds on Windows with MSVC (Visual Studio 2022 or newer).")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_executable(clip_engine
  src/main.cpp
  src/app/App.cpp
  src/app/RecordingSession.cpp
  src/audio/AacEncoder.cpp
  src/audio/AudioCapture.cpp
  src/capture/GpuDevice.cpp
  src/capture/MonitorCapture.cpp
  src/core/Config.cpp
  src/core/Log.cpp
  src/game/GameDetector.cpp
  src/media/MediaUtil.cpp
  src/media/Mp4Writer.cpp
  src/media/ReplayBuffer.cpp
  src/video/Nv12Converter.cpp
  src/video/VideoEncoder.cpp
)

target_include_directories(clip_engine PRIVATE src)

target_compile_definitions(clip_engine PRIVATE
  UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN
  _WIN32_WINNT=0x0A00
  CLIP_ENGINE_VERSION="${PROJECT_VERSION}"
)

target_compile_options(clip_engine PRIVATE /W4 /permissive- /utf-8 /EHsc /bigobj /Zc:__cplusplus)

target_link_libraries(clip_engine PRIVATE
  d3d11 dxgi
  mfplat mfuuid mfreadwrite mf wmcodecdspuuid
  windowsapp ole32 winmm
)
