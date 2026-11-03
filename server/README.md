# Clip Engine online service

Squad sync and the cloud library run on one Cloudflare Worker:

- **Squads.** Friends join with an invite. The service links squadmates who are playing the same game, and tells each one's app about the others' moments. It also keeps the shared library of everyone's views.
- **Cloud library.** Share links (`/c/<code>`) play in any browser, with the clip's moments marked on the timeline. There are public profiles (`/u/<handle>`) and storage plans.
- **Invite pages.** `/s/<invite>` opens Clip Engine and joins the squad, and shows a short how-to for friends who don't have the app yet.

Each squad and each cloud account is its own [Durable Object](https://developers.cloudflare.com/durable-objects/) with a small SQLite database. Video files are stored in one [R2](https://developers.cloudflare.com/r2/) bucket. The free Workers plan covers a group of friends. If use grows past its daily limits, Workers Paid costs $5 a month. R2 is free up to 10 GB, and downloads cost nothing.

The service doesn't need your own website or domain. It gets an address like `https://clip-engine.<your-subdomain>.workers.dev`.

## Deploying it (once)

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
4. Optional: set an admin token, so you can move accounts to the Plus plan:

   ```sh
   npx wrangler secret put ADMIN_TOKEN
   ```

5. Point the app at the service. Choose either way:
   - **For release builds:** in the GitHub repository, open **Settings > Secrets and variables > Actions > Variables** and add `ONLINE_SERVER` with the address. CI builds pick it up with `-p:OnlineServer=...`. For a local build, run `dotnet publish ui/ClipEngine.Desktop -p:OnlineServer=https://clip-engine.<your-subdomain>.workers.dev`.
   - **For one PC:** paste the address into **Online service** on the Squad or Cloud page.

To ship changes later, run `npx wrangler deploy` again. Squads, accounts and clips are kept.

## Settings

These live in `wrangler.toml` under `[vars]`:

| Name | Default | What it does |
| --- | --- | --- |
| `MAX_MEMBERS` | 8 | Players in one squad |
| `SQUAD_STORAGE_MB` | 5120 | Uploaded views each squad keeps. When the library is full, the oldest unstarred views go first. |
| `KEEP_DAYS` | 30 | Days before unstarred squad views are removed |
| `FREE_STORAGE_MB` | 2048 | Cloud library space on the Free plan |
| `PLUS_STORAGE_MB` | 102400 | Cloud library space on the Plus plan |

To move an account to a plan (`free` or `plus`), use the admin token:

```sh
curl -X POST https://clip-engine.<your-subdomain>.workers.dev/v1/admin/plan \
  -H "Authorization: Bearer <ADMIN_TOKEN>" -H "Content-Type: application/json" \
  -d '{"account":"<account id>","plan":"plus"}'
```

The account id is the first 8 characters of any of the account's share codes.

## Working on it

```sh
npm install
npm test            # unit tests, no network
npx wrangler dev    # the service on http://localhost:8787, with local storage
node test/e2e.mjs   # while wrangler dev runs: squads, uploads, share pages and profiles
```

The desktop app's online tests run against `wrangler dev` too:

```sh
CLIPENGINE_TEST_SERVER=http://localhost:8787 dotnet test ui/ClipEngine.Desktop.Tests --filter OnlineServiceTests
```

## What's stored

- **Squads:** each member's name and device id, a hash of each member's token, and the moments shared (type, game, time, killfeed text). It also keeps which views exist and the view files squadmates uploaded.
- **Cloud library:** the account name, its handle, a hash of each PC's token, and each clip's title, game, length, moments and file.

Tokens never leave the PC that holds them except to sign requests, and the app keeps them encrypted for the Windows account. When a player leaves a squad, the views they uploaded are deleted with them. When an account removes a clip, its file is deleted too.
