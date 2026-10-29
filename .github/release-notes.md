## Clip Engine 0.6.0

This update lets you fix detection yourself, for any game, and reads the screen better.

- **Edit any game's detection in Detection Studio.** Every game Clip Engine reads off the screen (The Finals, Overwatch, Battlefield 6, Valorant, Fortnite, Apex Legends, PUBG, Marvel Rivals, Rainbow Six Siege, Call of Duty, ARC Raiders, Delta Force) opens with its built-in rules, ready to change: move a box, change the words, the brightness filter, where on a killfeed row the words must be, then test and save. Your version replaces that game's built-in rules; **Use built-in** puts them back.
- **Updates still reach games you didn't touch.** Your changes are kept apart from the rules that ship with the app, so a fix to a game's built-in rules arrives with the update unless you've made that game's rules your own. Before, saving anything in Studio froze every game's rules at that version. Your old file is trimmed to the games you changed in Studio, and kept as `detection.before-0.6.ini` in case.
- **Use any clip.** Studio's frame picker lists all your clips and long recordings, from every game, with this game's first. **Open a video or screenshot** takes a frame from any video file too (MP4, MKV, MOV and others), with a slider to find the moment.
- **Reading quality.** Settings > Automatic clips > Reading quality goes from Low (how 0.5 read) to Medium (the new default: small text is enlarged smoothly and its contrast stretched), High (also reads every part of the screen a second way, and a quarter more often) and Ultra (three ways, twice as often). Higher catches more and misreads less, and uses more CPU. Studio's test can read at any quality, to see what each one gets.

Everything from 0.5.1 is included.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
