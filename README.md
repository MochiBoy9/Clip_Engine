# Clip_Engine

Clipping software that can clip almost any game with detection, API, etc. A competitor to Medal.

Proprietary software. All rights reserved; see [LICENSE](LICENSE).

## What works today

- Detects 23 games (see [Supported games](#supported-games)) and starts recording on the monitor the game is on, stopping when it closes.
- Captures at the monitor's native resolution and refresh rate with Windows.Graphics.Capture. Nothing is injected into the game, so it is safe with anti-cheat.
- Encodes on the GPU (NVIDIA, AMD or Intel) with H.264, switching to HEVC when the resolution and frame rate need it.
- Records only the game: when you switch to another window, the video shows a "Game Not Found" card and game audio goes quiet until you're back, so nothing else on your PC ends up in a clip. Turn it off with **Record only the game** in Settings (`game_focus`).
- Discord Rich Presence: while Discord is open, your status shows the game you're playing, how long you've been at it, your kills and deaths in the match and the clips you've saved. Turn it off with **Discord status** in Settings (`discord_presence`).
- Keeps the last 60 seconds in memory; press **F8** to save the last 30 seconds as an MP4 with separate game and mic audio tracks.

- Long recordings: press **F9** (or the button on Home) to record for as long as you like, until you press it again. Detection keeps running, so every kill, death and other moment is marked on its timeline, and automatic clips carry on meanwhile. It streams to disk as it records, so memory use doesn't grow, and it stops by itself before the drive fills up.
- Player records: a Records page with lifetime kills, deaths, assists, revives, K/D and wins, personal bests across every game (most kills in a match, longest kill streak, biggest multi-kill, most kills without dying, best K/D, most kills in a day, longest win streak and more), totals per game and recent matches. While you play, it shows the current match against your best in that game. Every moment the engine sees counts, clipped or not.
- Reads games' own scoreboards for exact totals: from CS2, Dota 2, League of Legends and Rocket League's APIs, and in Valorant, Overwatch, Marvel Rivals, Rainbow Six Siege, The Finals, Battlefield 6, Call of Duty and Delta Force from the Tab scoreboard on screen, by finding your name and the column headers.
- Spots kills, assists, deaths, revives, match start and end, and highlights, and saves a clip around them by itself: from the game's own live events where the game has them, and otherwise by reading the HUD with Windows text recognition. Each clip carries its moments, in a `.json` next to it and inside the MP4.
- A desktop app (`ClipEngine.exe`) with a sharp, amber-on-gunmetal look that stays light on the GPU: live recording status, a Save clip button, a clip gallery with thumbnails, the supported games and all the settings. It runs the engine for you and keeps recording from the tray.
- A clip player in the app with the detected moments on the timeline.
- Share to Discord: a copy that fits the upload limit, put on the clipboard. Clips friends send can be dropped on the window to import them, moments included.
- A side panel (Alt+Shift+C) on the edge of the screen, over the game. It and the "Clip saved" notice are left out of recordings.
- Trim a clip in the player (drag the handles, or I and O) and save that part as a new clip, instantly and at full quality.
- Clips grouped by match: each match's result, kills, assists, deaths and revives, every moment on one colored ribbon, and its best clips.
- Star clips as favorites, and optionally have old clips cleaned up by age or by a size limit. Favorites are always kept.
- Works on any screen shape and HUD size: each box keeps to the edge or middle of the HUD it sits next to, so the killfeed is read in the right place on ultrawide, 16:10 and 4:3 screens, and a game's HUD size set in Detection Studio moves and resizes every box with it.
- Check detection (in the player): mark what really happened in a clip, run the game's rules over the whole clip the way the engine reads the game, and see what they caught, missed and found that wasn't there.
- Moments over the game: the in-game notice also shows each kill with the match's count, multi-kills, streaks, revives, the match's line when it ends and a new personal best. Turn it off in Settings.
- Detection Studio (Games page, or the scan button in the player): add any game, or fix how one is read. Pick a frame from a clip or a screenshot, draw a box where the game shows a kill, type the words it shows, test it on that frame exactly as the engine would read it in game, and save.

![Home screen](docs/screenshots/1-home.png)

## Supported games

| Game | How moments are found |
| --- | --- |
| Counter-Strike 2 | Official Game State Integration: kills (with headshots and weapon), assists, deaths, 3K/4K/ACE rounds, round MVPs, match start and result |
| Dota 2 | Official Game State Integration: kills, deaths, assists, multi-kills, the horn and the match result |
| League of Legends | Official Live Client Data API: kills, deaths, assists, multi-kills, aces, first blood, dragons, Baron and steals, game start and result |
| Rocket League | Official Stats API: goals, assists, demolitions, saves, kickoff and the match result |
| Valorant, Marvel Rivals, Rainbow Six Siege, Delta Force, Deadlock, Halo Infinite, Destiny 2, Team Fortress 2 | The killfeed, by your in-game name (set it on the Games page), plus round and match banners and medals |
| The Finals, Overwatch, Battlefield 6, Fortnite, Apex Legends, PUBG, Call of Duty, ARC Raiders | The HUD's kill, death, revive and match messages |
| Escape from Tarkov | Deaths and the end of each raid (Tarkov shows no kills on screen) |
| Roblox, Minecraft | Recorded, with clips on the hotkey; add rules for the experiences you play in Detection Studio |

The official events are exact and work whatever the HUD looks like. Clip Engine turns them on by itself: it writes a `gamestate_integration_clipengine.cfg` into CS2's and Dota 2's `cfg` folders and sets `PacketSendRate` in Rocket League's `TAGame\Config\DefaultStatsAPI.ini`, for installs it finds through Steam and the Epic Games launcher and for any game it sees running. A game that was already open needs one restart to pick that up. Dota 2 may also need `-gamestateintegration` in its Steam launch options. League's API is always on during a game.

Screen rules for the rest are a first pass and get tuned against real gameplay; see `detection.ini` below. For a HUD that shows a kill as an icon rather than words, a rule can look for a picture of the icon cut from a screenshot instead. Screen reading stays light: a box inside another is read with it when that is cheaper, a box that has not changed is not read again, and death screens and match banners are checked less often than kill popups. Valorant has no local events API, so it is read off the screen like the others.

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
- Timelines (what the records are built from): `%LOCALAPPDATA%\Clip_Engine\timeline\`, one JSON-lines file per recording with every moment and scoreboard reading
- Long recordings: next to clips, named `<Game>_<date>_long.mp4`. Change the hotkey with `record_hotkey` in the settings or on the Settings page.
- Clips: `Videos\Clip_Engine\<Game>\`
- Detection rules: `detection.ini` next to the app. To tune a game, change its rules in Detection Studio (or put its sections in `%LOCALAPPDATA%\Clip_Engine\detection.ini`): a game with rules there uses them instead of the shipped ones, and every other game keeps the shipped rules, so updates still reach it. `reading_quality` (low, medium, high or ultra) in the settings trades CPU for more accurate reading. `[<game>.scoreboard]` sections say which column headers a game's scoreboard uses for kills, deaths and the rest. With `detection_debug = true` in the settings, the log shows what the engine reads from each screen region, which is how rules are tuned.
- `clip_engine.exe --export <clip.mp4> <out.mp4> --max-mb 10` makes a small copy for sharing.
- Supporter key: `%LOCALAPPDATA%\Clip_Engine\supporter.key`. Patreon backers get a key that unlocks colour themes, a supporter badge and a place in the thank-you list; every recording feature is free. Keys are made with `dotnet run --project ui/ClipEngine.SupporterKeys -- make "Name"` and checked offline against the public key in `ui/ClipEngine.Desktop/Services/SupporterKey.cs`. Thank-you names live in `ui/ClipEngine.Desktop/Assets/Supporters.txt`.
- `clip_engine.exe --frame <clip.mp4 or image> <out.png> --at 12.5` saves one frame as a PNG, and `clip_engine.exe --read <clip.mp4 or image> --at 12.5 --region 0.65,0.12,0.33,0.05 --words "ELIMINATED|KNOCKED OUT"` reads that box the way detection does and prints the text and how many times the words matched; `--position start|end` reads it as a killfeed, and `--image icon.png` also says how alike the box is to an icon. Detection Studio uses both.
- `clip_engine.exe --scan <clip.mp4> --game <id or name> [--quality medium] [--hud-scale 100]` runs a game's rules over a whole clip and prints each moment they find with its time; the player's Check detection uses it.
- `clip_engine.exe --trim <clip.mp4> <out.mp4> --start 4.5 --end 20` copies part of a clip without re-encoding, starting on the keyframe at or before the start (at most a second early).

## Layout

| Folder | What's in it |
| --- | --- |
| `src/app` | App loop, game sessions, hotkey and clip saving |
| `src/capture` | GPU device and monitor capture |
| `src/video` | BGRA to NV12 conversion and the hardware encoder |
| `src/audio` | WASAPI game/mic capture and AAC encoding |
| `src/media` | Replay buffer, MP4 writing, clip metadata, trimming and the share export |
| `src/detect` | Reading the HUD for kills and other moments, and the rules for each game |
| `src/game` | Supported games and detection of running games |
| `src/core` | Settings, logging, clock, JSON |
| `src/integrations` | Live events from games' own APIs (CS2, Dota 2, League of Legends, Rocket League) and turning them on |
| `assets/detection.ini` | Where each game shows kills and other moments, and the words to look for |
| `tests` | Tests for the detection rules, matching, clip metadata, game API events and killfeed rules |
| `ui/ClipEngine.Desktop` | The desktop app (C#, Avalonia) |
| `ui/ClipEngine.Desktop.Preview` | Renders the app's screens to PNGs with sample data |
| `ui/ClipEngine.Desktop.Tests` | Tests for the app's log parsing, settings file, MP4 reading, clip markers, import, supporter keys, match grouping, storage cleanup and player records |
| `ui/ClipEngine.SupporterKeys` | Makes supporter keys for Patreon backers (the signing key stays on your PC, never in the repo) |
| `website` | The landing page (plain HTML, hosted on Cloudflare Pages; see `website/README.md`) |
