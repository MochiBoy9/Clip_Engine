cmake_minimum_required(VERSION 3.24)
project(ClipEngine VERSION 0.3.1 LANGUAGES CXX)

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
  src/detect/DetectionRules.cpp
  src/detect/EventDetector.cpp
  src/detect/Ocr.cpp
  src/detect/RuleTester.cpp
  src/detect/TextMatcher.cpp
  src/game/GameDetector.cpp
  src/media/ClipMetadata.cpp
  src/media/Exporter.cpp
  src/media/MediaUtil.cpp
  src/media/Mp4Writer.cpp
  src/media/ReplayBuffer.cpp
  src/media/Trimmer.cpp
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
  windowsapp ole32 winmm avrt windowscodecs
)

# The detection rules ship next to the executable.
add_custom_command(TARGET clip_engine POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different ${CMAKE_SOURCE_DIR}/assets/detection.ini $<TARGET_FILE_DIR:clip_engine>)

# Tests for the parts of detection that don't need Windows APIs.
enable_testing()
add_executable(clip_engine_tests
  tests/DetectTests.cpp
  src/detect/DetectionRules.cpp
  src/detect/TextMatcher.cpp
  src/media/ClipMetadata.cpp
)
target_include_directories(clip_engine_tests PRIVATE src)
target_compile_options(clip_engine_tests PRIVATE /W4 /permissive- /utf-8 /EHsc)
add_test(NAME detection COMMAND clip_engine_tests ${CMAKE_SOURCE_DIR}/assets/detection.ini ${CMAKE_CURRENT_BINARY_DIR})
