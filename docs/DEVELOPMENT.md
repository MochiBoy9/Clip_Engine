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
- Timelines (what the records are built from): `%LOCALAPPDATA%\Clip_Engine\timeline\`, one JSON-lines file per recording with every moment and scoreboard reading
- Long recordings: next to clips, named `<Game>_<date>_long.mp4`. Change the hotkey with `record_hotkey` in the settings or on the Settings page.
- Clips: `Videos\Clip_Engine\<Game>\`
- Clip keys: `save_hotkey` saves the last `clip_seconds`, and `more_clip_hotkeys = Alt+F8 15, Shift+F8 120` adds keys that each save a clip of their own length (set them on the Settings page). A game can have its own set, `clip_hotkeys.<game id> = F8 20, Alt+F8 60`, used instead while it's recorded; Detection Studio sets it, and the engine takes the change at once.
- Detection rules: `detection.ini` next to the app. To tune a game, change its rules in Detection Studio (or put its sections in `%LOCALAPPDATA%\Clip_Engine\detection.ini`): a game with rules there uses them instead of the shipped ones, and every other game keeps the shipped rules, so updates still reach it. `reading_quality` (low, medium, high or ultra) in the settings trades CPU for more accurate reading. `[<game>.scoreboard]` sections say which column headers a game's scoreboard uses for kills, deaths and the rest. With `detection_debug = true` in the settings, the log shows what the engine reads from each screen region, which is how rules are tuned.
- `clip_engine.exe --export <clip.mp4> <out.mp4> --max-mb 10` makes a small copy for sharing.
- Plans: Free records up to 1080p with a 2 GB cloud library; Pro ($2.99 a month) up to 1440p with 100 GB, all seven colour themes, a badge and a name in the credits; Ultra ($4.99 a month) up to 4K with 250 GB and three more themes. Subscriptions are sold through Polar, which emails a license key; the app checks it with the online service (`POST /v1/license`), which asks Polar, about twice a day, and a paid plan keeps working for 14 days offline. The key is kept encrypted in `%LOCALAPPDATA%\Clip_Engine\license.json`. Checkout links go in `PlanConfig` in `ui/ClipEngine.Desktop/Services/Plans.cs` and `website/site.js`; the Polar ids go in `server/wrangler.toml`. Older thank-you names live in `ui/ClipEngine.Desktop/Assets/Supporters.txt`.
- Resolution: `resolution` in the settings (native, or 2160, 1440, 1080, 720 or 480) scales recordings down on the GPU and never up. The app writes the smaller of the one picked in Settings and the plan's.
- Friends: the Friends page adds people by profile name or friend code (an account's id, like `K7QM-2X9D`), shows who's online and what they're playing, and has a Join squad button when a friend shares their squad. Friends are kept with the cloud account.
- `clip_engine.exe --frame <clip.mp4 or image> <out.png> --at 12.5` saves one frame as a PNG, and `clip_engine.exe --read <clip.mp4 or image> --at 12.5 --region 0.65,0.12,0.33,0.05 --words "ELIMINATED|KNOCKED OUT"` reads that box the way detection does and prints the text and how many times the words matched; `--position start|end` reads it as a killfeed (with `{player}` filled in from your in-game names, and `--skip-tag true` for a squad tag before them), and `--image icon.png` also says how alike the box is to an icon. Detection Studio uses both.
- `clip_engine.exe --scan <clip.mp4> --game <id or name> [--quality medium] [--hud-scale 100] [--rules draft.ini]` runs a game's rules over a whole clip and prints each moment they find with its time; the player's Check detection uses it, and Detection Studio passes `--rules` to test changes before they're saved.
- `clip_engine.exe --trim <clip.mp4> <out.mp4> --start 4.5 --end 20` copies part of a clip without re-encoding, starting on the keyframe at or before the start (at most a second early).
- `clip_engine.exe --montage <out.mp4> --part "<clip.mp4>|<from>|<to>" [--part ...] [--height 1080] [--moment <id>]` puts parts of several clips together into one, with their moments. Squad highlights use it.
- Squads and the cloud library use Clip Engine's online service, a Cloudflare Worker in `server/`. See [server/README.md](server/README.md) to deploy it, which takes one `npx wrangler deploy`. Release builds name it with `-p:OnlineServer=https://...` (the `ONLINE_SERVER` Actions variable). You can also type it under **Online service** on the Squad or Cloud page. Squad settings, the cloud account and encrypted sign-ins are kept in `%LOCALAPPDATA%\Clip_Engine\desktop.json`. Your views of squad moments are listed in `squad\views.json` there, and squadmates' downloaded views are cached in `squad\cache\` for a week.

## Layout

| Folder | What's in it |
| --- | --- |
| `src/app` | App loop, game sessions, hotkey and clip saving |
| `src/capture` | GPU device and monitor capture |
| `src/video` | BGRA to NV12 conversion and the hardware encoder |
| `src/audio` | WASAPI game/mic capture and AAC encoding |
| `src/media` | Replay buffer, MP4 writing, clip metadata, trimming and the share export |
| `src/detect` | Reading the HUD for kills and other moments, and the rules for each game |
| `src/game` | Supported games and detection of running games |
| `src/core` | Settings, logging, clock, JSON |
| `src/squad` | Squad sync's engine side: wall-clock times, the app's save requests, the Discord squad line and highlight parts |
| `server` | The online service for squads, the shared library, share links and profiles (Cloudflare Worker, Durable Objects and R2) |
| `src/integrations` | Live events from games' own APIs (CS2, Dota 2, League of Legends, Rocket League) and turning them on |
| `assets/detection.ini` | Where each game shows kills and other moments, and the words to look for |
| `tests` | Tests for the detection rules, matching, clip metadata, game API events and killfeed rules |
| `ui/ClipEngine.Desktop` | The desktop app (C#, Avalonia) |
| `ui/ClipEngine.Desktop.Preview` | Renders the app's screens to PNGs with sample data |
| `ui/ClipEngine.Desktop.Tests` | Tests for the app's log parsing, settings file, MP4 reading, clip markers, import, plans and license keys, match grouping, storage cleanup and player records |
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

### Costs and protection

**R2 operations.** Each clip put in the cloud library costs two writes (Class A): the video and its thumbnail, each one plain PUT. Watching a clip reads R2 (Class B), but the bytes are served by the Worker (`src/media.js`), not the Durable Object, so nothing stays busy while a video streams. Every upload has an ETag, so a browser that already has it gets `304 Not Modified` without an R2 read. On a custom domain, the Cache API keeps each clip in the Cloudflare location that served it: the first viewer there reads R2 once, and every later viewer and seek there is free. On `*.workers.dev` the Cache API does nothing, so each request reads R2; put the Worker on your own domain (Workers & Pages > clip-engine > Settings > Domains & Routes) to turn caching on.

**Protection.** Cloudflare's DDoS protection is on for every Worker. On top of it, each address gets 10 sign-ups, sign-in or invite code tries and key checks a minute (`AUTH_LIMIT`), and 300 other changes a minute (`WRITE_LIMIT`), both in `wrangler.toml`. A sign-in code stops working after 5 wrong tries. Tokens are stored hashed and compared in constant time, every database query is parameterised, uploads have size limits, and the R2 bucket is private. With a custom domain you can also add WAF and rate limiting rules and Bot Fight Mode in the Cloudflare dashboard.

### Pro and Ultra on Polar

1. In Polar, make two monthly products: Pro at $2.99 and Ultra at $4.99.
2. Give each one a **License Keys** benefit. No activation limit is needed.
3. Copy the organization id and both benefit ids into `[vars]` above, and `npx wrangler deploy`.
4. Make a checkout link for each product and put them in `PlanConfig` (`ui/ClipEngine.Desktop/Services/Plans.cs`) and `LINKS` (`website/site.js`). The customer portal link goes in `PlanConfig.ManageUrl`.

The app sends the key to `POST /v1/license`, which checks it with Polar's public license key API (no Polar token needed) and answers with the plan. Putting the key on a cloud account (`POST /v1/accounts/<id>/plan`) gives the account the plan's storage; one subscription is on one account at a time, and the service checks it again twice a day, so a cancelled one goes back to Free. Subscribers can list a name in the credits, `GET /v1/credits`, which the app and the website show.


## Releasing

1. Set the version in `CMakeLists.txt` and `ui/ClipEngine.Desktop/ClipEngine.Desktop.csproj`, and write `.github/release-notes.md`.
2. Merge to `main`, then run the **Release** workflow from the Actions tab. It builds on Windows and publishes `Clip_Engine-windows-x64.zip` under a fixed name, so `releases/latest/download/Clip_Engine-windows-x64.zip` is always the newest.
3. The **Website** workflow publishes `website/` to GitHub Pages on every push to `main` that changes it.
4. **Prune old releases** (Actions tab) deletes releases below a version; tags are kept.
5. **Deploy online service** (Actions tab) tests and deploys `server/` with the `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` repository secrets, or run `npx wrangler deploy` in `server/`.

## Writing READMEs

`README.md`, `server/README.md` and `website/README.md` are for the community: players, and people curious how Clip Engine works. Keep them about what Clip Engine does and how to use it. Build, deploy, release and operator details go here, and nothing about operator-only tools, tokens or keys goes in any of them.
