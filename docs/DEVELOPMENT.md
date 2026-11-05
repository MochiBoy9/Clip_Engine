# Developing Clip Engine

Notes for building, releasing and running Clip Engine and its online service. The READMEs are written for players; this is the technical side.

## Building

Install Visual Studio 2022 or newer with the **Desktop development with C++** workload, which includes CMake and the Windows SDK, and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Then:

```
cmake -S . -B build -A x64
cmake --build build --config Release
dotnet publish ui/ClipEngine.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
copy build\Release\clip_engine.exe dist\
```

`dist\ClipEngine.exe` is the app, and it expects `clip_engine.exe` next to it. Every push to a pull request also builds both on GitHub Actions; the run's artifacts have the ready-to-run folder and screenshots of the app.

To see the app without Windows, `dotnet run --project ui/ClipEngine.Desktop.Preview -- screenshots` renders every screen with sample data to PNGs. `dotnet test ui/ClipEngine.Desktop.Tests` runs the app's tests.

To publish a release, set the version in `CMakeLists.txt` and run the **Release** workflow from the Actions tab.

## Files, settings and the command line

Run `ClipEngine.exe` and launch a supported game. `clip_engine.exe` can also run on its own, in which case its console shows what is being recorded.

- Settings: `%LOCALAPPDATA%\Clip_Engine\clip_engine.ini` (created on first run, with comments)
- Log: `%LOCALAPPDATA%\Clip_Engine\clip_engine.log`
- Timelines (what the records and the journal are built from): `%LOCALAPPDATA%\Clip_Engine\timeline\`, one JSON-lines file per recording with every moment, scoreboard reading and journal note
- Long recordings: next to clips, named `<Game>_<date>_long.mp4`. Change the hotkey with `record_hotkey` in the settings or on the Settings page.
- Clips: `Videos\Clip_Engine\<Game>\`
- Audio tracks: with more than one source on, clips have a first track of every source mixed (what players and the share export play), then one per source in the order `game`, `mic`, `discord`, `apps`; one source gives just its track. The clip's `audio_tracks` in its metadata names them. `game_audio`, `mic_audio`, `discord_audio` (Discord, TeamSpeak, Mumble) and `apps_audio` turn each on (a restart); `game_volume` and the others (0 to 200) and `muted_audio = mic, discord` apply at once, since the app sends the engine `reload-audio` after writing them. The game, Discord and apps tracks use process loopback (`ActivateAudioInterfaceAsync`, Windows 10 2004 and later), found from the audio sessions on every output device every two seconds (`src/audio/AppAudioCapture.cpp`); on older Windows the game track is the whole loopback, named `system`. Sources are put on one timeline by their timestamps in `PcmMixer` (`src/audio/PcmMixer.cpp`), 60 ms behind real time for each app track and 150 ms for the mix.
- Voice clips: `voice_clips = true` listens to the mic with SAPI's in-process recognizer and a grammar of just `voice_phrases` (plus a wildcard rule that takes other speech, so it isn't forced to match), fed 16 kHz mono from the engine's own mic capture (`src/audio/VoiceCommands.cpp`). A phrase can end in its own length (`save the last minute 60`); the rest save `voice_clip_seconds`, or `clip_seconds` when it's 0. `voice_sensitivity` (low, medium, high) is the SAPI confidence a phrase needs. Clips saved this way have `trigger: "voice"`. It needs a Windows speech recognizer for the user's language; the log says so when there isn't one.
- Replay buffer: `buffer_seconds` goes up to 1800. The buffer is kept to 40% of physical memory by dropping its oldest group of pictures (`ReplayBuffer::SetMaxBytes`), and the log says how much fits when the setting asks for more. Settings estimates the memory from resolution, frame rate and bitrate (`Services/ReplayMemory.cs`) and warns past 10 minutes on PCs with under 16 GB or fewer than 8 logical cores.
- Clip keys: `save_hotkey` saves the last `clip_seconds`, and `more_clip_hotkeys = Alt+F8 15, Shift+F8 120` adds keys that each save a clip of their own length (set them on the Settings page). A game can have its own set, `clip_hotkeys.<game id> = F8 20, Alt+F8 60`, used instead while it's recorded; Detection Studio sets it, and the engine takes the change at once.
- Detection rules: `detection.ini` next to the app. To tune a game, change its rules in Detection Studio (or put its sections in `%LOCALAPPDATA%\Clip_Engine\detection.ini`): a game with rules there uses them instead of the shipped ones, and every other game keeps the shipped rules, so updates still reach it. `reading_quality` (low, medium, high or ultra) in the settings trades CPU for more accurate reading. `[<game>.scoreboard]` sections say which column headers a game's scoreboard uses for kills, deaths and the rest. With `detection_debug = true` in the settings, the log shows what the engine reads from each screen region, which is how rules are tuned.
- Journal: `journal = true` in the settings (Settings' "Keep a journal of each run") turns on `[<game>.journal.<note>]` sections in `detection.ini` for the game being recorded. Each section reads a box like an event rule (`region`, `anchor`, `aspect`, `threshold`) and notes what it finds as a `note` line in the recording's timeline: `{"t":..,"match":..,"note":"enemy","value":"Wasp","amount":1,"xp":50,"text":"WASP DESTROYED"}`. `words` notes the note itself, `values` (`Name (ALIAS, ALIAS)`) picks a named value such as a map, `pattern` reads one off the line (`{value}`, `{number}`, `{xp}`, and a literal word is required), and `lines = true` notes every line in the box (inventories). `pick = true` notes nothing when a read shows several values, so a menu listing every map doesn't note them all. `exclude` drops lines, `when`/`when_region` only copies and reads the box while the gate's words show (an inventory isn't read until its header is on screen), and `sample_ms` (500 for feeds, 1500 for menus), `cooldown_ms` and `forget_ms` (4 s for feeds, 30 s otherwise) say how often it's read and when the same line counts again. Journal boxes share copies with event rules, and a box that hasn't changed isn't read again. The note names the app understands are `map condition mission queue deploy extracted died failed left downed enemy player_kill loot lost inventory xp objective`; any other is shown as is. `JournalTracker` and `FindNotes` in `src/detect/Journal.cpp` have the parsing, with tests in `tests/JournalTests.cpp`. The log has a line `Journal <note> in <game>: <value> (<amount>)` for each, which the app's activity feed shows. A user's journal sections for a game replace that game's shipped ones, like event rules, but don't stop the game's shipped event rules from updating.
- The Journal page (`Services/Journal.cs`) turns the notes into runs: a map, conditions or inventory read within 15 minutes before a `deploy` belong to it; `extracted`, `died`, `failed` or `left` end it; `xp` and `lost` notes up to 3 minutes after count for the run that just ended; a `queue` that never deployed is shown as backed out; and a run whose `deploy` wasn't seen is timed from its first note and marked as at least that long. Items lost are the `lost` notes when the game lists them, otherwise everything taken in, carried and picked up. Player kills added by hand are kept in `desktop.json` (`JournalPlayerKills`, by `game|session|start`). `JournalWords` has each game's words for the story (raid, ARC, Destroyed...).
- Hunt: Showdown 1896: the engine reads `user\profiles\default\attributes.xml` under the game's folder every 3 seconds while it runs (`HuntMissionIntegration`), and when the `MissionBag*`/`MissionAccolade*` values change after a match, `HuntMissionTracker` in `src/integrations/GameStateTrackers.cpp` notes the mode, monsters and bosses, hunters killed, bounties, XP and whether the hunter extracted. The first read after starting is only remembered, so an old match isn't noted again.
- The journal's screen rules for ARC Raiders, Escape from Tarkov, Helldivers 2 and Deep Rock Galactic are a first pass written from the games' known wording; check them on real clips with `--scan`, which prints `Scan journal <note> at <s>: <value>` for each note, and tune them in `assets/detection.ini`.
- Team Fortress 2: the engine appends `con_logfile "clip_engine_console.log"` and `name` to `tf\cfg\autoexec.cfg`, then follows `tf\clip_engine_console.log` (or `tf\console.log` from `-condebug`, whichever grows first) twice a second for "X killed Y with Z." lines, matched to `player_names` or the name the game prints. Until the log grows, the killfeed rules count kills; once it does, the screen's kills and deaths are dropped so nothing counts twice. A log over 64 MB is emptied when the engine starts. `Tf2ConsoleTracker` in `src/integrations/GameStateTrackers.cpp` has the parsing and its tests.
- `clip_engine.exe --export <clip.mp4> <out.mp4> --max-mb 10` makes a small copy for sharing.
- Plans: Free records up to 1080p with a 2 GB cloud library; Pro ($2.99 a month) up to 1440p with 100 GB, all seven colour themes, a badge and a name in the credits; Ultra ($4.99 a month) up to 4K with 250 GB and three more themes. Subscriptions are sold through Polar, which emails a license key; the app checks it with the online service (`POST /v1/license`), which asks Polar, about twice a day, and a paid plan keeps working for 14 days offline. The key is kept encrypted in `%LOCALAPPDATA%\Clip_Engine\license.json`. Checkout links go in `PlanConfig` in `ui/ClipEngine.Desktop/Services/Plans.cs` and `website/site.js`; the Polar ids go in `server/wrangler.toml`. Older thank-you names live in `ui/ClipEngine.Desktop/Assets/Supporters.txt`.
- Donations: any amount, once, through Polar. A donation's key on its own is the Free plan with the Supporter role; anyone who donated (with that key, or a subscription's key from the same Polar customer) gets 1 GB more cloud storage per dollar, up to 25 GB, and from $5 in all, a name in the credits. See "Donations on Polar" below.
- Resolution: `resolution` in the settings (native, or 2160, 1440, 1080, 720 or 480) scales recordings down on the GPU and never up. The app writes the smaller of the one picked in Settings and the plan's.
- Stats overlay: `StatsOverlay` in `desktop.json` (on, corner, hotkey, which readings), off until turned on in Settings or with its hotkey (Alt+Shift+F). It runs in the desktop app, only while a game is recorded and in front, and stops all sampling when it's hidden. `Services/Stats/` has the pieces: `PresentCounter` starts a real-time ETW session (`ClipEngine-FrameTimes`) for the Present_Start events of Microsoft-Windows-DXGI (42, 55) and Microsoft-Windows-D3D9 (1), filtered by event id at the source and flushed once a second, and keeps the foreground window's process's frame times (the app inside it for Store apps); `FrameTimes` turns them into FPS (the last second) and the 1% low (the average of the slowest 1% of frame times over the last 10 seconds). `HardwareMonitor` reads CPU from `GetSystemTimes`, RAM from `GlobalMemoryStatusEx`, GPU load from PDH `\GPU Engine(*engtype_3D)\Utilization Percentage` (busiest 3D engine, every process added up, as Task Manager does; the video engines are the recording's own work and left out), video memory from `\GPU Adapter Memory(*)\Dedicated Usage`, and the temperature from `D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)`, all for the adapter with the most dedicated memory. The sampler and the trace's reader run on their own below-normal, EcoQoS threads. The overlay is a click-through Avalonia window kept out of captures (`WDA_EXCLUDEFROMCAPTURE`), like the notices; where Windows can't do that (before 10 2004) it doesn't show. Starting the ETW session needs an administrator or the Performance Log Users group (S-1-5-32-559); Settings' Allow button adds the account with an elevated `Add-LocalGroupMember`, which counts from the next sign-in. Vulkan and OpenGL games don't raise these events, so FPS shows `--` there; the other readings still work. CPU temperature isn't shown, since Windows has no API for it without a driver.
- Friends: the Friends page adds people by profile name or friend code (an account's id, like `K7QM-2X9D`), shows who's online and what they're playing, and has a Join squad button when a friend shares their squad. Friends are kept with the cloud account.
- `clip_engine.exe --frame <clip.mp4 or image> <out.png> --at 12.5` saves one frame as a PNG, and `clip_engine.exe --read <clip.mp4 or image> --at 12.5 --region 0.65,0.12,0.33,0.05 --words "ELIMINATED|KNOCKED OUT"` reads that box the way detection does and prints the text and how many times the words matched; `--position start|end` reads it as a killfeed (with `{player}` filled in from your in-game names, and `--skip-tag true` for a squad tag before them), and `--image icon.png` also says how alike the box is to an icon. Detection Studio uses both.
- `clip_engine.exe --scan <clip.mp4> --game <id or name> [--quality medium] [--hud-scale 100] [--rules draft.ini]` runs a game's rules over a whole clip and prints each moment they find with its time, and each journal note; the player's Check detection uses it, and Detection Studio passes `--rules` to test changes before they're saved.
- `clip_engine.exe --trim <clip.mp4> <out.mp4> --start 4.5 --end 20` copies part of a clip without re-encoding, starting on the keyframe at or before the start (at most a second early).
- `clip_engine.exe --montage <out.mp4> --part "<clip.mp4>|<from>|<to>" [--part ...] [--height 1080] [--mbps 16|auto] [--moment <id>] [--kind edit]` puts parts of several clips together into one, re-encoded, with their moments. Squad highlights use it, and so does the editor's export (`--kind edit` marks the clip's trigger as `edit`; `--mbps auto` picks 8, 16, 28 or 45 Mbps from the picture height).
- The editor (Edit in the player) keeps each clip's edit in `%LOCALAPPDATA%\Clip_Engine\edits\<id>.json`: the parts as source times, so reopening the clip picks it up. The model is `EditTimeline` in `ui/ClipEngine.Desktop/Services/Editing/`; every change (split, delete, trim, move, duplicate, undo) goes through it, and new tools are added there first.
- Squads and the cloud library use Clip Engine's online service, a Cloudflare Worker in `server/`. See [server/README.md](server/README.md) to deploy it, which takes one `npx wrangler deploy`. Release builds name it with `-p:OnlineServer=https://...` (the `ONLINE_SERVER` Actions variable). You can also type it under **Online service** on the Squad or Cloud page. Squad settings, the cloud account and encrypted sign-ins are kept in `%LOCALAPPDATA%\Clip_Engine\desktop.json`. Your views of squad moments are listed in `squad\views.json` there, and squadmates' downloaded views are cached in `squad\cache\` for a week.

## Layout

| Folder | What's in it |
| --- | --- |
| `src/app` | App loop, game sessions, hotkey and clip saving |
| `src/capture` | GPU device and monitor capture |
| `src/video` | BGRA to NV12 conversion and the hardware encoder |
| `src/audio` | WASAPI mic and per-app capture, mixing the tracks, voice commands and AAC encoding |
| `src/media` | Replay buffer, MP4 writing, clip metadata, trimming and the share export |
| `src/detect` | Reading the HUD for kills and other moments, the journal's notes, and the rules for each game |
| `src/game` | Supported games and detection of running games |
| `src/core` | Settings, logging, clock, JSON |
| `src/squad` | Squad sync's engine side: wall-clock times, the app's save requests, the Discord squad line and highlight parts |
| `server` | The online service for squads, the shared library, share links, profiles and the hub (Cloudflare Worker, Durable Objects and R2) |
| `src/integrations` | Live events from games' own APIs (CS2, Dota 2, League of Legends, Rocket League), Team Fortress 2's console log and Hunt: Showdown 1896's match summaries, and turning them on |
| `assets/detection.ini` | Where each game shows kills and other moments, and the words to look for |
| `tests` | Tests for the detection rules, matching, clip metadata, game API events, killfeed rules, journal rules and audio mixing |
| `ui/ClipEngine.Desktop` | The desktop app (C#, Avalonia) |
| `ui/ClipEngine.Desktop.Preview` | Renders the app's screens to PNGs with sample data |
| `ui/ClipEngine.Desktop.Tests` | Tests for the editor's timeline and export, the app's log parsing, settings file, MP4 reading, clip markers, import, plans and license keys, match grouping, storage cleanup, player records, the journal's runs and stories, the stats overlay's readings, replay buffer memory and voice phrases |
| `website` | The landing page (plain HTML, published to GitHub Pages; see `website/README.md`) |

## The online service

Squads, the cloud library, friends and license checks run on one Cloudflare Worker in `server/`, with a Durable Object (SQLite) per squad and per account and one private R2 bucket for videos. `cd server && npm test` runs its tests.

### Deploying it (once)

You need [Node.js](https://nodejs.org) 20 or newer and a Cloudflare account.

1. Sign up at [dash.cloudflare.com](https://dash.cloudflare.com/sign-up). Open **Workers & Pages** once, so you get a `workers.dev` subdomain.
2. Open **R2** and turn it on. Cloudflare asks for a payment method even for the free amount.
3. From this folder, run:

   ```sh
   npm install
   npx wrangler login
   npx wrangler r2 bucket create clip-engine-clips
   npx wrangler deploy
   ```

   `wrangler login` opens your browser to allow access. `wrangler deploy` ends by printing the service's address, `https://clip-engine.<your-subdomain>.workers.dev`.
4. Set an admin token for the operator's tools. Pick a long random one, such as the output of `openssl rand -hex 32`, and keep it to yourself:

   ```sh
   npx wrangler secret put ADMIN_TOKEN
   ```

5. Point the app at the service. Choose either way:
   - **For release builds:** in the GitHub repository, open **Settings > Secrets and variables > Actions > Variables** and add `ONLINE_SERVER` with the address. CI builds pick it up with `-p:OnlineServer=...`. For a local build, run `dotnet publish ui/ClipEngine.Desktop -p:OnlineServer=https://clip-engine.<your-subdomain>.workers.dev`.
   - **For one PC:** paste the address into **Online service** on the Squad or Cloud page.

To ship changes later, run `npx wrangler deploy` again. Squads, accounts and clips are kept.

### Settings

These live in `wrangler.toml` under `[vars]`:

| Name | Default | What it does |
| --- | --- | --- |
| `MAX_MEMBERS` | 8 | Players in one squad |
| `SQUAD_STORAGE_MB` | 5120 | Uploaded views each squad keeps. When the library is full, the oldest unstarred views go first. |
| `KEEP_DAYS` | 30 | Days before unstarred squad views are removed |
| `FREE_STORAGE_MB` | 2048 | Cloud library space on the Free plan |
| `PRO_STORAGE_MB` | 102400 | Cloud library space on Pro ($2.99 a month) |
| `ULTRA_STORAGE_MB` | 256000 | Cloud library space on Ultra ($4.99 a month) |
| `POLAR_ORGANIZATION_ID` | empty | Your Polar organization (Settings > General). Until it's set, license keys are refused with "Subscriptions aren't open yet." |
| `POLAR_PRO_BENEFIT_ID` | empty | The License Keys benefit on the Pro product |
| `POLAR_ULTRA_BENEFIT_ID` | empty | The License Keys benefit on the Ultra product |
| `POLAR_DONATION_PRODUCT_ID` | empty | The Donation product (one-time, pay what you want) |
| `POLAR_DONATION_BENEFIT_ID` | empty | The License Keys benefit on the Donation product. Until it's set, donation keys are refused like any other unknown key. |
| `DONATION_GB_PER_DOLLAR` | 1 | Extra cloud storage for each dollar donated, in GB (at least 1 GB for any donation; 0 turns it off) |
| `DONATION_MAX_GB` | 25 | The most extra storage donations give, in GB |
| `DONATION_CREDITS_FROM` | 5 | Dollars donated in all before a supporter's name can go in the credits |
| `REFERRAL_FRIENDS` | 3 | Friends who must install Clip Engine from someone's invite for one reward |
| `REFERRAL_REWARD_DAYS` | 30 | Days of Pro each reward gives, counted from when its key is first used |
| `REFERRAL_MAX_REWARDS` | 1 | Rewards one person can earn; with 2, six friends give two months |
| `REFERRAL_CLAIM_DAYS` | 7 | How long after installing a friend can still add an invite code, and how long an opened invite link is remembered |
| `REFERRAL_IP_CHECK` | true | `false` lets friends on the inviter's own network, or several on one network, count |

`POLAR_ACCESS_TOKEN` is a secret (`npx wrangler secret put POLAR_ACCESS_TOKEN`): a Polar organization access token with `orders:read`, used only to add up how much a customer donated.

### Costs and protection

**R2 operations.** Each clip put in the cloud library costs two writes (Class A): the video and its thumbnail, each one plain PUT. Watching a clip reads R2 (Class B), but the bytes are served by the Worker (`src/media.js`), not the Durable Object, so nothing stays busy while a video streams. Every upload has an ETag, so a browser that already has it gets `304 Not Modified` without an R2 read. On a custom domain, the Cache API keeps each clip in the Cloudflare location that served it: the first viewer there reads R2 once, and every later viewer and seek there is free. On `*.workers.dev` the Cache API does nothing, so each request reads R2; put the Worker on your own domain (Workers & Pages > clip-engine > Settings > Domains & Routes) to turn caching on.

**Protection.** Cloudflare's DDoS protection is on for every Worker. On top of it, each address gets 10 sign-ups, sign-in or invite code tries and key checks a minute (`AUTH_LIMIT`), and 300 other changes a minute (`WRITE_LIMIT`), both in `wrangler.toml`. A sign-in code stops working after 5 wrong tries. Tokens are stored hashed and compared in constant time, every database query is parameterised, uploads have size limits, and the R2 bucket is private. With a custom domain you can also add WAF and rate limiting rules and Bot Fight Mode in the Cloudflare dashboard.

### The hub

`/hub` on the service (`src/hub.js`) lists every public, uploaded clip. Each library tells the directory object when one of its clips becomes public or stops being (`src/feed.js`, the `feed` table), and when the owner's name, username or badge changes, so the hub reads one table instead of every library. Views follow every view for the first 20, then every tenth. The pages are server-rendered and work without scripts; scripts add "load more" and the signed-in pages. `/` in a browser redirects to `/hub`.

Signing in: a code from the app (the same one-time code as **Link another PC**; **Open the hub** opens `/hub/signin#code=...`, and the code stays in the fragment so it isn't logged) or a username and password set on the hub (PBKDF2-SHA256, 100,000 rounds; 5 wrong tries lock it for 15 minutes). Either makes a device token on the account that expires after 30 days, kept in an HttpOnly, Secure, SameSite=Lax cookie (`ce_hub`); `ce_hub_who` holds only the name shown in the bar. Every change also needs an `x-hub: 1` header and, when the browser sends one, this origin, so other sites can't act with the cookie. `/hub/api/signin` and `/hub/api/report` are under `AUTH_LIMIT`.

Badges: `badgeFor` in `src/plans.js` (a special key's role, else Pro or Ultra). `PATCH /v1/accounts/<id>/account { hideBadge }` hides it on the profile, shared clips and the hub; the app's Cloud page and the hub both have the switch.

Reports land in the directory. `GET /v1/admin/reports` lists reported clips and `POST /v1/admin/hide { code, hidden, clearReports }` takes one out of the hub (its link still plays), both with the admin token.

To try it locally: put `ADMIN_TOKEN=<anything>` in `server/.dev.vars` (ignored by git) and run `npx wrangler dev`; the hub is at `http://localhost:8787/hub`.

### Pro and Ultra on Polar

1. In Polar, make two monthly products: Pro at $2.99 and Ultra at $4.99.
2. Give each one a **License Keys** benefit. No activation limit is needed.
3. Copy the organization id and both benefit ids into `[vars]` above, and `npx wrangler deploy`.
4. Make a checkout link for each product and put them in `PlanConfig` (`ui/ClipEngine.Desktop/Services/Plans.cs`) and `LINKS` (`website/site.js`). The customer portal link goes in `PlanConfig.ManageUrl`.

The app sends the key to `POST /v1/license`, which checks it with Polar's public license key API (no Polar token needed) and answers with the plan. Putting the key on a cloud account (`POST /v1/accounts/<id>/plan`) gives the account the plan's storage; one subscription is on one account at a time, and the service checks it again twice a day, so a cancelled one goes back to Free. Subscribers can list a name in the credits, `GET /v1/credits`, which the app and the website show.

### Donations on Polar

1. In Polar, make a one-time product called Donation with a **Pay what you want** price. Set a minimum of $1 or more, so a key always comes from a paid order.
2. Give it a **License Keys** benefit, with no expiry and no activation limit.
3. Make an organization access token with the `orders:read` scope (Settings > Developers) and run `npx wrangler secret put POLAR_ACCESS_TOKEN`.
4. Copy the product id and the benefit id into `POLAR_DONATION_PRODUCT_ID` and `POLAR_DONATION_BENEFIT_ID` in `[vars]`, and `npx wrangler deploy`.
5. Make a checkout link for the product and put it in `PlanConfig.DonateUrl` (`ui/ClipEngine.Desktop/Services/Plans.cs`) and `LINKS.donate` (`website/site.js`). The $3, $5, $10 and $25 buttons add `?amount=<cents>` to it, which Polar fills in on its page; Any amount leaves it to the donor.

How it works: Polar emails the donor a license key. `POST /v1/license` checks it with Polar's public API like a subscription's key, then lists that customer's paid Donation orders (`GET /v1/orders/?customer_id=...&product_id=...` with the access token) and adds them up, less refunds, before tax. The answer has `supporter`, `donated` (cents) and the storage bonus in `extraStorage`; a donation's own key also has `role: "supporter"`. Because it's by Polar customer, a subscriber who donates with the same email gets the bonus on their subscription's key, and keeps their plan's badge. A refund takes the bonus and credits down at the next check (twice a day). If the orders can't be read (no token, or Polar is down), a donation's key still works with 1 GB and no change to the credits. Supporters are listed under `supporter` in `GET /v1/credits`, after Pro; unlike subscribers', their names stay after their app stops checking in, since a donation doesn't run out. Donation keys go on a cloud account like any other (`POST /v1/accounts/<id>/plan`), which adds the bonus to its storage and shows Supporter. The key maker can also give the `supporter` role to a special key.

The perks the app lists are in `Donations` (`Plans.cs`) and the website's donate box; change them with the `DONATION_*` vars.

### Special keys

The operator's key maker (kept privately, never in this repository) makes special keys, `CEK-XXXX-XXXX-XXXX`, through `/v1/admin/grants` with `ADMIN_TOKEN` (see `server/src/index.js`). A key gives:

- a plan (Free, Pro or Ultra), which sets storage, resolution and themes, and optionally a role on top of it: `creator` (Content Creator), `special`, `exclusive` or `supporter`. The app shows the role as the badge, and a cloud account shows it next to the plan.
- a length in minutes counted from first use (`minutes`, or `days` as before), a fixed end (`endsAt`, ms), both (whichever comes first), or neither.
- extra cloud storage, a resolution cap, its own colour themes and a PC limit, as before.
- perks: `contact`, a direct line to the developer, and `bugs`, bug reports that go straight to them. The app's plan card gets a box for them (`POST /v1/contact`, `/v1/contact/list`), checked against the key and a PC it was turned on on, 20 a day per key. The operator reads and answers them in the key maker's inbox (`GET /v1/admin/messages`, `PATCH /v1/admin/messages/<id>`).
- `credits`: whether the holder may put a name in the credits. Their name is listed under their role (`supporter`, `exclusive`, `creator`, `special` in `GET /v1/credits`), after Ultra and Pro subscribers.
- `reason`: why it was given. With `showReason` the holder sees it in the app ("Given for: ..."); otherwise only the operator does. `note` is always private.

Keys made before roles keep working: their days become minutes the first time the directory starts.


### Inviting friends

`src/referrals.js`, one `Referrals` Durable Object (migration `v2`). The app's side is `Services/Referrals.cs` and the Invite friends card on the Friends page (`ViewModels/InviteViewModel.cs`).

- **Invite code.** **Get my invite link** calls `POST /v1/referrals` with the PC's id and gets an 8-character code, its link (`/r/<code>`) and a secret, kept encrypted in `desktop.json`. Asking again from the same PC gives the same code and a new secret. The app looks at its friends every 3 hours (`GET /v1/referrals/<code>`, Bearer secret).
- **Counting a friend.** On its first start on a PC the app calls `POST /v1/referrals/activate` once, with `fresh: true` when nothing of Clip Engine (`clip_engine.ini`, `desktop.json`, `license.json`) was there yet. A friend who opened an invite link in the last 7 days from the same address is credited without typing anything; otherwise they can type the code on the Friends page for 7 days.
- **Checks.** The PC's id is a SHA-256 of Windows' `MachineGuid` (the app's own random id when it can't be read), so wiping Clip Engine's files doesn't make a PC new. A PC is recorded the first time it's seen and only counts once, ever, for one code, and only when the app said it was a new install. It doesn't count from a PC the inviter used, from an address the inviter used (hashed with a salt kept in the object), or from an address another of the inviter's friends already counted from. A typed code is under `AUTH_LIMIT` like sign-ins.
- **Reward.** Each `REFERRAL_FRIENDS` counted friends, up to `REFERRAL_MAX_REWARDS`, the service makes a special key in the directory (`/grant/create`: Pro, `REFERRAL_REWARD_DAYS`, 2 PCs, noted "Invited N friends (invite XXXX-XXXX)"), so it shows in the key maker with the others and can be turned off there. The app puts it on at once when the PC has no working key; with a subscription it stays on the card to use later.
- **Limits.** A determined cheat with VPNs and virtual machines can still fake friends; the checks hold that to one PC per network per invite.

## Releasing

1. Set the version in `CMakeLists.txt` and `ui/ClipEngine.Desktop/ClipEngine.Desktop.csproj`, and write `.github/release-notes.md`.
2. Merge to `main`, then run the **Release** workflow from the Actions tab. It builds on Windows and publishes `Clip_Engine-Setup.exe` (the installer, the website's main button) and `Clip_Engine-windows-x64.zip` (portable, and what the in-app updater downloads) under fixed names, so `releases/latest/download/<name>` is always the newest.

The installer is `installer/ClipEngine.iss` (Inno Setup 6), compiled by the Build workflow from `dist\` with `iscc /DAppVersion=<version> /DSourceDir=..\dist installer\ClipEngine.iss`. It installs per user (no admin) into `%LOCALAPPDATA%\Programs\Clip Engine`, so the in-app updater can still swap files there, adds a Start menu entry, an optional desktop shortcut and the `clipengine://` links, and its uninstaller leaves `%LOCALAPPDATA%\Clip_Engine` and the clips alone. Never change its `AppId`; the app (`Services/InstallInfo.cs`) uses it to keep the version shown in Apps & features current after in-app updates.

The website's download counter adds up `download_count` for every `Clip_Engine-*.zip` and `.exe` on this repository's releases, from GitHub's API in the visitor's browser. Deleting releases (Prune old releases) removes their downloads from the total.
3. The **Website** workflow publishes `website/` to GitHub Pages on every push to `main` that changes it.
4. **Prune old releases** (Actions tab) deletes releases below a version; tags are kept.
5. **Deploy online service** (Actions tab) tests and deploys `server/` with the `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` repository secrets, or run `npx wrangler deploy` in `server/`.

## Writing READMEs

`README.md`, `server/README.md` and `website/README.md` are for the community: players, and people curious how Clip Engine works. Keep them about what Clip Engine does and how to use it. Build, deploy, release and operator details go here, and nothing about operator-only tools, tokens or keys goes in any of them.
