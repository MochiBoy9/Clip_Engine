## Clip Engine 0.7.2

Lighter on your games and your battery.

- **Records at up to 120 fps by default.** "Match monitor" now stops at 120 fps, so a 144, 165 or 240 Hz monitor no longer records every frame it shows. Clips look much the same and the game gets that GPU time back. Pick 144, 165 or 240 fps in Settings to record faster.
- **Copies only the frames it records.** Frames are taken from the screen when the recording needs one, not every time the monitor refreshes, and none at all while you're tabbed out of the game.
- **Discord status no longer burns a CPU core.** With the Discord status on and Discord closed, Clip Engine kept checking for it in a tight loop for as long as you played. It now checks every 15 seconds.
- **Background work runs in power-saving mode.** Reading the screen, writing clips and long recordings, game events and the Discord status run on Windows' efficiency mode, which keeps them off the cores your game uses on CPUs that have efficiency cores and lowers their power draw on the rest. The app does the same while its window is closed to the tray or minimized.
- **The app sits still behind your game.** The blinking recording light only blinks while Clip Engine's window is in front, so the window doesn't redraw itself every frame behind the game, and the side panel's light stays steady over it.
- **Saving a clip no longer rereads your whole library.** Each new clip used to make the app read every clip in the folder again, which meant a burst of disk reads mid-match once the library grew.
- A clip playing in the app pauses when you close the window to the tray.
- Detection keeps reading the game while the side panel is in front, as the recording already did.

Everything from 0.7.1 is included.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
