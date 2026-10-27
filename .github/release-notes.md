## Clip Engine 0.3.0

Clip Engine now watches your game for kills and other moments and clips them by itself.

- **Automatic clips.** While you play The Finals, Overwatch or Battlefield 6, the engine reads the kill feed and other parts of the HUD with Windows text recognition and saves a clip around every kill, assist, death, revive, match start or end and highlight you choose. Moments close together become one clip. Change which moments count, and how much is kept before and after, under **Settings → Automatic clips**.
- **Clip player.** Clips open inside the app, with every detected moment on the timeline and in a list beside the video. Click one to jump to it.
- **Share to Discord.** One click makes a copy small enough for Discord (10 MB, or 50 MB / 500 MB for Nitro) and puts it on your clipboard, ready to paste. **Copy** puts the full-quality clip on the clipboard instead.
- **Import clips friends send you.** Drop a clip on the window, use **Import**, or open it with Clip Engine, and pick the folder to keep it in. Clips made with Clip Engine carry their moments inside the file, so they show up for the person you send them to.
- **Side panel.** Alt+Shift+C (or the tray menu) shows a slim panel on the edge of the screen over your game, with recording status, a Save clip button, what was just detected and your latest clips.
- **Smoother while you play.** Capture and audio run at multimedia priority, clip saving runs in the background, and screen reading only uses frames the GPU has already finished, so saving and detection don't make the game stutter.

The detection rules live in `detection.ini` next to the app. They're a first version and will be tuned with real gameplay; a copy in `%LOCALAPPDATA%\Clip_Engine` overrides the shipped one.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
