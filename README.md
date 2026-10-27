# Clip_Engine

Clipping software that can clip almost any game with detection, API, etc. A competitor to Medal.

Proprietary software. All rights reserved; see [LICENSE](LICENSE).

## What works today

- Detects The Finals, Overwatch and Battlefield 6 and starts recording on the monitor the game is on, stopping when it closes.
- Captures at the monitor's native resolution and refresh rate with Windows.Graphics.Capture. Nothing is injected into the game, so it is safe with anti-cheat.
- Encodes on the GPU (NVIDIA, AMD or Intel) with H.264, switching to HEVC when the resolution and frame rate need it.
- Keeps the last 60 seconds in memory; press **F8** to save the last 30 seconds as an MP4 with separate game and mic audio tracks.

- Reads the HUD with Windows text recognition to spot kills, assists, deaths, revives, match start and end, and highlights, and saves a clip around them by itself. Each clip carries its moments, in a `.json` next to it and inside the MP4.
- A desktop app (`ClipEngine.exe`) with a liquid-glass look: live recording status, a Save clip button, a clip gallery with thumbnails, the supported games and all the settings. It runs the engine for you and keeps recording from the tray.
- A clip player in the app with the detected moments on the timeline.
- Share to Discord: a copy that fits the upload limit, put on the clipboard. Clips friends send can be dropped on the window to import them, moments included.
- A side panel (Alt+Shift+C) on the edge of the screen, over the game.

![Home screen](docs/screenshots/1-home.png)

## Requirements

- Windows 10 version 1903 or newer (Windows 11 recommended, so the yellow capture border can be turned off)
- A GPU with a hardware video encoder (practically any GPU from the last ten years)

## Installing

Download the zip from the [latest release](../../releases/latest), unzip it anywhere and run `ClipEngine.exe`.

## Building

Install Visual Studio 2022 or newer with the **Desktop development with C++** workload, which includes CMake and the Windows SDK, and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Then:

```
cmake -S . -B build -A x64
cmake --build build --config Release
dotnet publish ui/ClipEngine.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
copy build\Release\clip_engine.exe dist\
```

`dist\ClipEngine.exe` is the app, and it expects `clip_engine.exe` next to it. Every push to a pull request also builds both on GitHub Actions; the run's artifacts have the ready-to-run folder and screenshots of the app.

To see the app without Windows, `dotnet run --project ui/ClipEngine.Desktop.Preview -- screenshots` renders every screen with sample data to PNGs. `dotnet test ui/ClipEngine.Desktop.Tests` runs the app's tests.

To publish a release, set the version in `CMakeLists.txt` and run the **Release** workflow from the Actions tab.

## Using it

Run `ClipEngine.exe` and launch a supported game. `clip_engine.exe` can also run on its own, in which case its console shows what is being recorded.

- Settings: `%LOCALAPPDATA%\Clip_Engine\clip_engine.ini` (created on first run, with comments)
- Log: `%LOCALAPPDATA%\Clip_Engine\clip_engine.log`
- Clips: `Videos\Clip_Engine\<Game>\`
- Detection rules: `detection.ini` next to the app. Copy it to `%LOCALAPPDATA%\Clip_Engine\` to tune it; that copy wins. With `detection_debug = true` in the settings, the log shows what the engine reads from each screen region, which is how rules are tuned.
- `clip_engine.exe --export <clip.mp4> <out.mp4> --max-mb 10` makes a small copy for sharing.
- License: `%LOCALAPPDATA%\Clip_Engine\license.json`. Keys are sold and checked through [Polar](https://polar.sh); the organization and benefit ids go in `ui/ClipEngine.Desktop/Services/Licensing.cs`. Until they're filled in, Settings shows paid plans as coming soon.

## Layout

| Folder | What's in it |
| --- | --- |
| `src/app` | App loop, game sessions, hotkey and clip saving |
| `src/capture` | GPU device and monitor capture |
| `src/video` | BGRA to NV12 conversion and the hardware encoder |
| `src/audio` | WASAPI game/mic capture and AAC encoding |
| `src/media` | Replay buffer, MP4 writing, clip metadata and the share export |
| `src/detect` | Reading the HUD for kills and other moments, and the rules for each game |
| `src/game` | Supported games and detection of running games |
| `src/core` | Settings, logging, clock |
| `assets/detection.ini` | Where each game shows kills and other moments, and the words to look for |
| `tests` | Tests for the detection rules, matching and clip metadata |
| `ui/ClipEngine.Desktop` | The desktop app (C#, Avalonia) |
| `ui/ClipEngine.Desktop.Preview` | Renders the app's screens to PNGs with sample data |
| `ui/ClipEngine.Desktop.Tests` | Tests for the app's log parsing, settings file, MP4 reading, clip markers, import and licensing |
| `website` | The landing page (plain HTML, hosted on Cloudflare Pages; see `website/README.md`) |
