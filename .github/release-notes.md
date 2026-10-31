## Clip Engine 0.8.0

Sharper kill detection for The Finals, a way to see why each kill counted, and a stronger Detection Studio.

- **Each kill counts once.** The Finals shows a kill in the killfeed and again as a score line under your crosshair. The score line fades in and out, so one kill was sometimes counted twice. Now the killfeed, read by your in-game name, decides how many kills there were and the score line backs it up. A kill only one of them saw still counts.
- **Hears the elimination chime.** Clip Engine listens for The Finals' elimination sound in the game audio and uses it to back up each kill. The sound never counts a kill on its own.
- **See why a kill counted.** Each moment in the player shows a check when two signs agreed. Open it to see everything that saw the kill, what it read and when, with a button that opens that rule in Detection Studio.
- **Detection Studio, upgraded:**
  - **Read through the clip** shows everything a rule reads from start to end, so you can see where it misses.
  - **Listen for the sound here** turns the sound at the playhead into a rule, for any game that plays one on a kill.
  - **Test on marked clips** runs your rules over every clip you've marked and shows what they caught, missed or added.
- Deaths in The Finals are now read from the killfeed too.

**Set your in-game name** on the Games page so the killfeed can find your kills. If you saved your own rules for The Finals in Detection Studio, they're used instead of the new ones; choose **Use built-in** there and save to switch.

Everything from 0.7.2 is included.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
