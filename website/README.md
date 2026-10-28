# Website

The landing page for Clip Engine: plain HTML and CSS, no build step. Open `index.html` in a browser to see it.

- Checkout, download and support links are set in `site.js`.
- `privacy.html` and `terms.html` are drafts to finish before taking payments.
- Screenshots in `assets/` come from `dotnet run --project ui/ClipEngine.Desktop.Preview -- screenshots`.

## Hosting on Cloudflare Pages

Cloudflare Pages hosts it for free and redeploys on every push to `main`.

1. In the Cloudflare dashboard, open **Workers & Pages → Create → Pages → Connect to Git** and pick this repository.
2. Leave the framework preset as **None**, the build command empty, and set the build output directory to `website`.
3. Deploy. The site is live at `<project>.pages.dev`; add your own domain under **Custom domains**.
