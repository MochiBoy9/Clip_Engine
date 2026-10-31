## Clip Engine 0.9.0

Detection Studio is easier to find your way around, and it now tells you exactly how each rule works.

- **See how every rule works.** Pick a rule and Studio says in plain words what it reads and how often, when it saves a moment, how it tells a repeat from a new one, and how it works with the game's other rules: which one decides the count, which back it up, and which can't run yet. The list shows each rule's part at a glance, and warns when two rules would save the same kill twice.
- **Fine-tune any rule.** How often it reads its box, how long a message must be gone before it counts again, counting a moment once with the other rules for it, which rule decides, and squad tags before a name in a killfeed. Each shows the engine's default, so nothing is hidden.
- **Testing a killfeed rule works.** Testing a rule that looks for your in-game name now fills your name in, as the engine does in game; before, it always said "No match". You can set your name right in the rule that needs it.
- **Test your changes before saving.** Test all rules on marked clips runs your unsaved changes too, and lists every kill they missed or found extra. Click one to jump to that frame and see why.
- **A roomier, quicker Studio:**
  - The frame is bigger, with the clip picker and moments above it and testing in three tabs below: this frame, the whole clip and marked clips.
  - Step through a clip a tenth of a second or a second at a time, and jump between its moments. Frames you've seen come back instantly.
  - Copy or remove a rule from the toolbar, and give rules their own names.
  - Changes to several games are kept until you save them all together, Discard throws them away, and leaving with changes unsaved asks first.
  - Keys: Left/Right step, Shift for a second, Up/Down jump between moments, Ctrl+Enter tests, Ctrl+S saves, Ctrl+D copies, Delete removes.

Everything from 0.8.0 is included.

## Install

1. Download `Clip_Engine-…-windows-x64.zip` below and unzip it anywhere.
2. Run `ClipEngine.exe`. It starts `clip_engine.exe` (the recorder) in the background, with no console window.

Needs Windows 10 version 1903 or newer (Windows 11 recommended) and a GPU with a hardware video encoder. Windows may warn that the app is from an unknown publisher because it isn't code-signed yet; choose **More info → Run anyway**.

Proprietary software. All rights reserved; see LICENSE.txt.
