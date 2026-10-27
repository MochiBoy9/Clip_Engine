## Clip Engine 0.4.0

Automatic clips now work in 16 games, up from 3.

- **Official game events in Counter-Strike 2, Dota 2, League of Legends and Rocket League.** These games report kills, deaths, assists, multi-kills, objectives and match results themselves, so these clips are exact and don't depend on the HUD. Clip Engine turns the events on for you. If one of these games was already open when you installed this update, restart it once.
- **Valorant, Marvel Rivals, Rainbow Six Siege and Delta Force** are read from the killfeed. Put your in-game name in **Games → Your in-game name** so Clip Engine can tell your kills and deaths from everyone else's.
- **Fortnite, Apex Legends, PUBG, Call of Duty and ARC Raiders** are read from their kill, knock, revive and match messages.
- The Games page shows how each game is detected.

Screen detection in the new games is a first version. If a game misses moments or catches the wrong ones, tell us which game and what happened. Turning on `detection_debug` in the settings file logs what Clip Engine reads, which helps.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
