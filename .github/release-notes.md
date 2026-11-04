## Clip Engine 0.14.0

More games, and kills that count once and only once.

- **Seven new games.** Battlefield 2042, Battlefield V, Battlefield 1, Battlefield 4, Halo: The Master Chief Collection, Paladins and Splitgate now clip themselves. That's 25 games with automatic clips.
- **Team Fortress 2 is exact.** Clip Engine now reads Team Fortress 2's console log, which names every kill, so your kills (with the weapon and crits) and deaths are caught however busy the screen is. It turns this on by itself; restart the game once if it was open. If a custom HUD has its own `autoexec.cfg`, add `-condebug` to the game's launch options in Steam.
- **Battlefield kills count once.** The KILL line under the crosshair and the killfeed are now checked against each other, so a kill isn't missed when one is hard to read and isn't counted twice when both are.
- **Games with vague kill feeds are gone.** Escape from Tarkov, ARC Raiders and Destiny 2 keep kills off the screen, so they couldn't be clipped dependably. They're still recorded with the hotkey if you add them in Detection Studio.

New here? Download `Clip_Engine-Setup.exe` and run it. If you already have Clip Engine, update from Settings.
