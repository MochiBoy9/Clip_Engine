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
4. Set an admin token. The key maker needs it, and it lets you move accounts between plans. Pick a long random one, such as the output of `openssl rand -hex 32`, and keep it to yourself:

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
| `PRO_STORAGE_MB` | 102400 | Cloud library space on Pro ($2.99 a month) |
| `ULTRA_STORAGE_MB` | 256000 | Cloud library space on Ultra ($9.99 a month) |
| `POLAR_ORGANIZATION_ID` | empty | Your Polar organization (Settings > General). Until it's set, license keys are refused with "Subscriptions aren't open yet." |
| `POLAR_PRO_BENEFIT_ID` | empty | The License Keys benefit on the Pro product |
| `POLAR_ULTRA_BENEFIT_ID` | empty | The License Keys benefit on the Ultra product |

## Pro and Ultra on Polar

1. In Polar, make two monthly products: Pro at $2.99 and Ultra at $9.99.
2. Give each one a **License Keys** benefit. No activation limit is needed.
3. Copy the organization id and both benefit ids into `[vars]` above, and `npx wrangler deploy`.
4. Make a checkout link for each product and put them in `PlanConfig` (`ui/ClipEngine.Desktop/Services/Plans.cs`) and `LINKS` (`website/site.js`). The customer portal link goes in `PlanConfig.ManageUrl`.

The app sends the key to `POST /v1/license`, which checks it with Polar's public license key API (no Polar token needed) and answers with the plan. Putting the key on a cloud account (`POST /v1/accounts/<id>/plan`) gives the account the plan's storage; one subscription is on one account at a time, and the service checks it again twice a day, so a cancelled one goes back to Free. Subscribers can list a name in the credits, `GET /v1/credits`, which the app and the website show.

To move an account to a plan by hand (`free`, `pro` or `ultra`), use the admin token:

```sh
curl -X POST https://clip-engine.<your-subdomain>.workers.dev/v1/admin/plan \
  -H "Authorization: Bearer <ADMIN_TOKEN>" -H "Content-Type: application/json" \
  -d '{"account":"<account id>","plan":"pro"}'
```

The account id is the first 8 characters of any of the account's share codes.

### Special keys

Special keys (`CEK-XXXX-XXXX-XXXX`) give a plan, extra cloud storage, a resolution and colour themes for a set time, and go in the same box as a subscription key (Settings, Plan). They're made and managed with the operator's own key maker, which isn't part of this repository, through `/v1/admin/grants` with the `ADMIN_TOKEN`. A special key on a cloud account gives that account its storage, one account per key.