// Links the page points at. Anything left empty keeps its in-page link.
const LINKS = {
  // The Clip Engine Patreon page.
  patreon: "",
  // Where releases are published: this repository's GitHub Releases.
  repo: "MochiBoy9/Clip_Engine",
  support: "mailto:support@clipengine.app",
};

// Releases carry the zip under this fixed name (from 0.11.0 on), so this link always gets the newest version.
const STABLE_ZIP = "Clip_Engine-windows-x64.zip";
const releases = `https://github.com/${LINKS.repo}/releases`;

function setLink(name, url) {
  for (const el of document.querySelectorAll(`[data-link="${name}"]`)) el.href = url;
  for (const el of document.querySelectorAll(`[data-link-text="${name}"]`)) el.value = url;
}

function setText(name, text) {
  for (const el of document.querySelectorAll(`[data-${name}]`)) el.textContent = text;
}

for (const [name, url] of Object.entries(LINKS)) {
  if (url && name !== "repo") setLink(name, url);
}
setLink("releases", releases);
setLink("download", `${releases}/latest`);

// The newest release: its zip, version, size and checksum.
fetch(`https://api.github.com/repos/${LINKS.repo}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } })
  .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
  .then((release) => {
    const zips = (release.assets || []).filter((a) => /windows-x64\.zip$/i.test(a.name));
    const zip = zips.find((a) => a.name === STABLE_ZIP) || zips[0];
    if (!zip) return;
    const version = (release.tag_name || "").replace(/^v/, "");
    const mb = Math.round(zip.size / (1024 * 1024));
    // The fixed name when the release has it, so a copied link keeps working for later versions too.
    const url = zip.name === STABLE_ZIP ? `${releases}/latest/download/${STABLE_ZIP}` : zip.browser_download_url;
    setLink("download", url);
    if (version) {
      setText("version", `Version ${version}`);
      setText("version-short", `v${version}`);
      for (const el of document.querySelectorAll("dd[data-version]")) el.textContent = version;
    }
    setText("size", `${mb} MB`);
    if (release.published_at) {
      setText("date", new Date(release.published_at).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }));
    }
    if (zip.digest && zip.digest.startsWith("sha256:")) setText("sha", zip.digest.slice(7));
  })
  .catch(() => {
    // Offline or rate-limited: the links still go to the latest release page.
  });

// Copy buttons: copy the field they name, and say so.
for (const button of document.querySelectorAll("[data-copy]")) {
  button.addEventListener("click", async () => {
    const field = document.getElementById(button.dataset.copy);
    const state = button.closest(".link-box")?.querySelector(".copy-state");
    let ok = false;
    try {
      await navigator.clipboard.writeText(field.value);
      ok = true;
    } catch {
      field.select();
      ok = document.execCommand && document.execCommand("copy");
    }
    if (state) state.textContent = ok ? "Link copied" : "Select the link and copy it";
    button.textContent = ok ? "Copied" : "Copy";
    setTimeout(() => {
      button.textContent = "Copy";
      if (state) state.textContent = "";
    }, 2400);
  });
}

// "Copy download link" in the hero copies straight away and takes you to the download box.
for (const link of document.querySelectorAll('a[href="#download"].ghost')) {
  link.addEventListener("click", () => {
    const field = document.getElementById("download-link");
    if (field && navigator.clipboard) navigator.clipboard.writeText(field.value).then(() => {
      const state = document.querySelector(".link-box .copy-state");
      if (state) state.textContent = "Link copied";
    }).catch(() => {});
  });
}
