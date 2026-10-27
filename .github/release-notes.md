## Clip Engine 0.2.1

Fixes the desktop app closing immediately on launch on Windows 11. When Windows turned on the acrylic glass effect while the window was still loading, the app crashed before it could appear. It now opens normally.

## Clip Engine desktop app

This release adds the Windows desktop app: a liquid-glass window around the recording engine.

- **Home** shows what's being recorded (game, resolution, frame rate, codec and GPU encoder), a big Save clip button, your recent clips and an activity feed.
- **Clips** is a gallery of everything you've saved, with Explorer thumbnails, per-game filters, search, play, show in folder and delete (to the Recycle Bin).
- **Games** lists the games that start recording automatically: The Finals, Overwatch and Battlefield 6.
- **Settings** edits replay buffer, clip length, frame rate, bitrate, codec, audio tracks, hotkey and clips folder, and restarts the engine for you.
- Closing the window keeps recording from the tray. The animated background pauses whenever the window isn't in front, so it costs nothing while you play.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
