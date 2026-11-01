## Clip Engine 0.10.0

Detection counts more accurately, makes highlights in every game, and does less work while it's at it.

- **Counts kills more accurately.**
  - The Finals' elimination chime backs up a kill even when it's heard after the killfeed already counted it, instead of being ignored.
  - A killfeed row that's just your name (a fall, the zone, your own grenade) is a death, not a kill, in every game that reads the killfeed by name.
  - Words run together by text recognition ("ELIMINATEDBOB") are split, and plurals like ELIMINATIONS on a scoreboard no longer count as a kill.
  - Names must match more closely: a longer name that starts with yours isn't you, and accented names are read right.
  - Halo Infinite tells suicides and betrayals from kills.
- **Killcams and spectating don't count.** Kills shown while a killcam is up or you're watching a teammate after you're out are theirs, not yours. Kills found by your name in a killfeed always count. First-pass rules ship for Call of Duty, Fortnite, Apex Legends, PUBG, ARC Raiders, Overwatch, Battlefield 6 and The Finals.
- **Assists from killfeeds** in Team Fortress 2, and from the assist popup in Rainbow Six Siege and Delta Force.
- **Matches in every game.** Records, the Matches view and your Discord status now split matches from start and end banners, scoreboards that start again from zero, and long quiet spells, not only in games with their own API.
- **Multi-kills and streaks in every game.** Double, triple, quad and penta kills and every fifth kill without dying become highlights, unless the game called the multi-kill out itself. Turn it off in Settings.
- **Smarter clip lengths.** A match end keeps more of the fight that won it, a match start more of the start, and a death less after it. Choose whether a death on its own gets a clip.
- **Your scoreboard, checked.** Records count kills and deaths as they happen; the scoreboard checks them. Each game on the Records page shows how many of the kills its scoreboard showed were caught, and opens Detection Studio when it keeps missing some.
- **Less work while you play.**
  - The scoreboard is read only while you hold Tab (or the key you set) and for 30 seconds after a match, not all the time.
  - A box is read again only when HUD text in it came or went, not every time the scenery behind it moves.
  - After 30 seconds with nothing happening, quick popups are looked for half as often, until anything shows.

Everything from 0.9.0 is included.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
