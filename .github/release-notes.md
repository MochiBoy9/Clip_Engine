## Clip Engine 0.5.1

This update fixes kills, assists and revives being counted too many times, and lets Clip Engine update itself.

- **Updates from inside the app.** Settings > Updates looks for a new version when Clip Engine starts, and **Install and restart** downloads it, swaps the files and opens the new version, with your settings and clips untouched. You can also install a release zip you already downloaded with **Choose zip**. While the repository is private, paste a GitHub token that can read it under Release source.
- **Each kill counts once in The Finals.** A kill shows both "ELIMINATED <name>" and "ELIMINATION +250", and both used to count, often several times over as the name read differently each time. Only the score line counts now. In one recorded match the app logged 46 kills, 35 assists and 30 revives where the game's summary shows 12, 8 and 1.
- **Revives are real revives.** A downed teammate's REVIVE marker and the hold-E REVIVE prompt no longer count; only the "REVIVE +200" you get for picking someone up does.
- **The end-of-match summary isn't counted.** Its ELIMINATIONS, REVIVES and ASSISTS headings no longer add kills, revives or assists, in any game.
- **Only the game is read.** While another window is in front of the game, like Clip Engine's own clip list or a browser, the screen isn't read, so text there can't become a moment.
- **Detection rules** can use `{number}` for any number on the same row, as in `ELIMINATION {number}`.

Everything from 0.5.0 is included.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
