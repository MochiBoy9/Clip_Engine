cmake_minimum_required(VERSION 3.24)
project(ClipEngine VERSION 0.12.2 LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC)
  message(FATAL_ERROR "Clip Engine builds on Windows with MSVC (Visual Studio 2022 or newer).")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_executable(clip_engine
  src/main.cpp
  src/app/App.cpp
  src/app/CommandChannel.cpp
  src/app/RecordingSession.cpp
  src/app/Timeline.cpp
  src/audio/AacEncoder.cpp
  src/audio/AudioCapture.cpp
  src/capture/GpuDevice.cpp
  src/capture/MonitorCapture.cpp
  src/core/Config.cpp
  src/core/Hotkeys.cpp
  src/core/Json.cpp
  src/core/Log.cpp
  src/detect/DetectionRules.cpp
  src/detect/EventDetector.cpp
  src/detect/EventFuser.cpp
  src/detect/ImageMatch.cpp
  src/detect/Ocr.cpp
  src/detect/OcrImage.cpp
  src/detect/RuleTester.cpp
  src/detect/Scoreboard.cpp
  src/detect/ScreenRegion.cpp
  src/detect/SoundMatch.cpp
  src/detect/SoundWatch.cpp
  src/detect/TextMatcher.cpp
  src/game/GameDetector.cpp
  src/integrations/DiscordActivity.cpp
  src/integrations/DiscordPresence.cpp
  src/integrations/GameIntegration.cpp
  src/integrations/GameStateTrackers.cpp
  src/integrations/IntegrationSetup.cpp
  src/integrations/StreamReaders.cpp
  src/media/ClipMetadata.cpp
  src/media/Exporter.cpp
  src/media/LongRecorder.cpp
  src/media/MediaUtil.cpp
  src/media/Mp4Writer.cpp
  src/media/ReplayBuffer.cpp
  src/media/Trimmer.cpp
  src/squad/SquadClips.cpp
  src/video/GameNotFoundFrame.cpp
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
  d3d11 dxgi gdi32 user32
  mfplat mfuuid mfreadwrite mf wmcodecdspuuid
  windowsapp ole32 winmm avrt windowscodecs
  ws2_32 winhttp advapi32
)

# The detection rules ship next to the executable, with the sounds they listen for.
add_custom_command(TARGET clip_engine POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different ${CMAKE_SOURCE_DIR}/assets/detection.ini $<TARGET_FILE_DIR:clip_engine>
  COMMAND ${CMAKE_COMMAND} -E copy_directory ${CMAKE_SOURCE_DIR}/assets/sounds $<TARGET_FILE_DIR:clip_engine>/sounds)

# Tests for the parts of detection that don't need Windows APIs.
enable_testing()
add_executable(clip_engine_tests
  tests/DetectTests.cpp
  src/detect/DetectionRules.cpp
  src/detect/EventFuser.cpp
  src/detect/SoundMatch.cpp
  src/detect/SoundWatch.cpp
  src/detect/ImageMatch.cpp
  src/detect/OcrImage.cpp
  src/detect/Scoreboard.cpp
  src/detect/ScreenRegion.cpp
  src/detect/TextMatcher.cpp
  src/media/ClipMetadata.cpp
)
target_include_directories(clip_engine_tests PRIVATE src)
target_compile_options(clip_engine_tests PRIVATE /W4 /permissive- /utf-8 /EHsc)
add_test(NAME detection COMMAND clip_engine_tests ${CMAKE_SOURCE_DIR}/assets/detection.ini ${CMAKE_CURRENT_BINARY_DIR})

# Tests for the games added in 0.4: their own APIs (CS2, Dota 2, League of Legends, Rocket
# League), killfeed rules and the shipped rules for every screen-read game, plus scoreboard
# reading and the timeline the player's records come from.
add_executable(clip_engine_game_tests
  tests/GameSupportTests.cpp
  src/app/Timeline.cpp
  src/core/Json.cpp
  src/detect/DetectionRules.cpp
  src/detect/Scoreboard.cpp
  src/detect/ScreenRegion.cpp
  src/detect/TextMatcher.cpp
  src/integrations/DiscordActivity.cpp
  src/integrations/GameStateTrackers.cpp
  src/integrations/StreamReaders.cpp
)
target_include_directories(clip_engine_game_tests PRIVATE src)
target_compile_options(clip_engine_game_tests PRIVATE /W4 /permissive- /utf-8 /EHsc)
add_test(NAME games COMMAND clip_engine_game_tests ${CMAKE_SOURCE_DIR}/assets/detection.ini)

# Tests for squad sync's engine side: lining clips up by wall-clock time, the app's save
# command and the squad fields in clip metadata.
add_executable(clip_engine_squad_tests
  tests/SquadTests.cpp
  src/media/ClipMetadata.cpp
  src/squad/SquadClips.cpp
)
target_include_directories(clip_engine_squad_tests PRIVATE src)
target_compile_options(clip_engine_squad_tests PRIVATE /W4 /permissive- /utf-8 /EHsc)
add_test(NAME squad COMMAND clip_engine_squad_tests)

# Tests for the clip keys: several keys that each save a clip of their own length, and a
# game's own set of them from the settings file.
add_executable(clip_engine_hotkey_tests
  tests/HotkeyTests.cpp
  src/core/Hotkeys.cpp
)
target_include_directories(clip_engine_hotkey_tests PRIVATE src)
target_compile_definitions(clip_engine_hotkey_tests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(clip_engine_hotkey_tests PRIVATE /W4 /permissive- /utf-8 /EHsc)
add_test(NAME hotkeys COMMAND clip_engine_hotkey_tests ${CMAKE_CURRENT_BINARY_DIR})
