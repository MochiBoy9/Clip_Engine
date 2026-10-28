## Clip Engine 0.5.0

This update counts your kills properly, records whole sessions and keeps your records.

- **Each kill counts once.** A kill feed line is remembered by the names on it, so it counts once however long it stays up or flickers, and quick multi-kills are no longer lost. Scores, countdowns and medals like `DOUBLE KILL` no longer count as extra kills. Older clips with repeated moments are tidied when they load.
- **Icons, not just words.** A detection rule can now look for a picture cut from a screenshot, like a kill banner or a skull, at any resolution.
- **Lighter on your PC.** Parts of the screen that haven't changed aren't read again, deaths and match banners are checked less often, and big banners on 4K screens are read at half size.
- **Long recording.** Press **F9** (or the button on Home) to record until you press it again. Nothing is re-encoded, detection keeps marking your moments, and auto clips carry on. It stops on its own when the game closes or your drive gets low.
- **Records.** A new Records page shows your lifetime kills, deaths, assists, revives, K/D and wins, your personal bests, totals per game, your recent matches and a live card for the match you're playing. Scoreboards are read in CS2, Dota 2, League and Rocket League, and from the screen in Valorant, Overwatch, Marvel Rivals, Siege, The Finals, Battlefield 6, Call of Duty and Delta Force. Add your in-game name on the Games page.
- **Detection Studio fixes.** Saving a game in Studio no longer loses settings it doesn't show, which had turned some kill feed rules into rules that counted every line with your name, or deletes the game's scoreboard.
- **New rules** for Black Ops 6 medals, Overwatch round starts and Valorant's team ace.

Everything from 0.4.1 is included: every feature free, trimming, matches, favorites and Detection Studio.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
