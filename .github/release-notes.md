## Clip Engine 0.7.0

This update fits detection to any screen, lets you check how well it reads your own footage, tells you about your moments while you play, records only the game, adds seven games, and gives the app more room.

- **Any screen shape and HUD size.** Detection boxes now keep to the edges of the game's HUD, so ultrawide, 16:10 and 4:3 screens read correctly. Detection Studio has a **HUD size** slider for each game: set it to match the game's own HUD scale setting and every box moves with it.
- **Check detection.** A new panel in the player lets you mark what really happened in a clip (M marks at the playhead, 1 to 7 picks the kind of moment), then **Run detection** to see what was caught, what was missed and what was found that isn't there. Each one is a click away on the timeline.
- **Moments over the game.** The in-game "Clip saved" notice now also shows each kill with the match's count, double, triple and quad kills and 5-kill streaks, assists and revives, the match's line when it ends, and a new personal best. Turn it off in Settings > **Moments over the game**.
- **Record only the game.** When you tab out, the recording shows a "Game Not Found" card and game audio goes quiet (your mic stays on) until you're back. Home shows when this is happening. Turn it off in Settings > **Record only the game**.
- **Seven more games.** Deadlock, Halo Infinite, Destiny 2 and Team Fortress 2 (kills by killfeed, medals and match end), Escape from Tarkov (deaths and raid end), and Roblox and Minecraft (recorded, with clips on the hotkey). All are in Detection Studio. Their rules are a first pass, so tell us where they miss.
- **More room and some motion.** Pages, cards and settings are spaced out, pages rise into place, clip cards lift under the pointer, and the nav rail's accent slides to the page you pick. Grids now line up with their headers, scrollbars sit at the window's edge, and the Settings subtitle shows again.
- **Discord status is on its way.** The Settings toggle is there, but your Discord profile won't show anything yet; it switches on in a small update shortly.

Everything from 0.6.0 is included.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
