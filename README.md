# Clip_Engine

Clipping software that can clip almost any game with detection, API, etc. A competitor to Medal.

Proprietary software. All rights reserved; see [LICENSE](LICENSE).

## What works today

- Detects The Finals, Overwatch and Battlefield 6 and starts recording on the monitor the game is on, stopping when it closes.
- Captures at the monitor's native resolution and refresh rate with Windows.Graphics.Capture. Nothing is injected into the game, so it is safe with anti-cheat.
- Encodes on the GPU (NVIDIA, AMD or Intel) with H.264, switching to HEVC when the resolution and frame rate need it.
- Keeps the last 60 seconds in memory; press **F8** to save the last 30 seconds as an MP4 with separate game and mic audio tracks.

Automatic clips for kills, assists, deaths and other moments come next.

## Requirements

- Windows 10 version 1903 or newer (Windows 11 recommended, so the yellow capture border can be turned off)
- A GPU with a hardware video encoder (practically any GPU from the last ten years)

## Building

Install Visual Studio 2022 or newer with the **Desktop development with C++** workload, which includes CMake and the Windows SDK. Then:

```
cmake -S . -B build -A x64
cmake --build build --config Release
```

The program is `build\Release\clip_engine.exe`. Every push to a pull request also builds it on GitHub Actions, and the `.exe` can be downloaded from the run's artifacts.

## Using it

Run `clip_engine.exe` and launch a supported game. The console shows what is being recorded.

- Settings: `%LOCALAPPDATA%\Clip_Engine\clip_engine.ini` (created on first run, with comments)
- Log: `%LOCALAPPDATA%\Clip_Engine\clip_engine.log`
- Clips: `Videos\Clip_Engine\<Game>\`

## Layout

| Folder | What's in it |
| --- | --- |
| `src/app` | App loop, game sessions, hotkey and clip saving |
| `src/capture` | GPU device and monitor capture |
| `src/video` | BGRA to NV12 conversion and the hardware encoder |
| `src/audio` | WASAPI game/mic capture and AAC encoding |
| `src/media` | Replay buffer and MP4 writing |
| `src/game` | Supported games and detection of running games |
| `src/core` | Settings, logging, clock |
