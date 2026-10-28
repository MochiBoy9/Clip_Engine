// Links the page points at. Fill these in once the accounts exist; anything left empty keeps its in-page link.
const LINKS = {
  // The Clip Engine Patreon page.
  patreon: "",
  // The latest release in the public downloads repository, e.g.
  // "https://github.com/MochiBoy9/Clip_Engine-downloads/releases/latest"
  download: "",
  support: "mailto:support@clipengine.app",
};

for (const el of document.querySelectorAll("[data-link]")) {
  const url = LINKS[el.dataset.link];
  if (url) el.href = url;
}
