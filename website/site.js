// Links the page points at. Anything left empty keeps its in-page link.
const LINKS = {
  // Polar checkout links for Pro and Ultra (Products > Checkout links). Empty: the buttons say "Coming soon".
  pro: "https://buy.polar.sh/polar_cl_1Od5CaTH339XapxgyZW8RjFstHRfWZbdkvncB4J3E2h",
  ultra: "https://buy.polar.sh/polar_cl_Bjr9r9ZyxK5Hca0xqYuvaTtzfJynMI1NepwwG4CrrUp",
  // The Donation product's checkout link (one-time, pay what you want). Empty: the donate buttons say it's coming soon.
  donate: "",
  // Clip Engine's online service, for the names in the credits.
  service: "https://clip-engine.clip-engine.workers.dev",
  // Where releases are published: this repository's GitHub Releases.
  repo: "MochiBoy9/Clip_Engine",
  support: "mailto:support@clipengine.app",
};

// Releases carry the installer and the zip under these fixed names (the zip from 0.11.0 on, the installer from 0.13.1),
// so these links always get the newest version.
const STABLE_ZIP = "Clip_Engine-windows-x64.zip";
const STABLE_SETUP = "Clip_Engine-Setup.exe";
const releases = `https://github.com/${LINKS.repo}/releases`;

function setLink(name, url) {
  for (const el of document.querySelectorAll(`[data-link="${name}"]`)) el.href = url;
  for (const el of document.querySelectorAll(`[data-link-text="${name}"]`)) el.value = url;
}

function setText(name, text) {
  for (const el of document.querySelectorAll(`[data-${name}]`)) el.textContent = text;
}

for (const [name, url] of Object.entries(LINKS)) {
  if (url && name !== "repo" && name !== "service" && name !== "donate") setLink(name, url);
}

// Donate buttons: Polar's page with the amount filled in (in cents), or left to the donor.
for (const el of document.querySelectorAll("[data-donate]")) {
  const cents = Math.round(Number(el.dataset.donate) * 100);
  if (!LINKS.donate) {
    el.setAttribute("aria-disabled", "true");
    continue;
  }
  el.href = cents > 0 ? `${LINKS.donate}${LINKS.donate.includes("?") ? "&" : "?"}amount=${cents}` : LINKS.donate;
  el.target = "_blank";
  el.rel = "noopener";
}
if (LINKS.donate) setText("donate-note", "Polar emails you a key for the app. Thank you!");
// The hub, where players' public clips are, is part of the online service.
if (LINKS.service) setLink("hub", `${LINKS.service}/hub`);

// The menu on phones: the button opens the section links under the bar, and picking one (or Escape) closes it.
const menu = document.querySelector(".menu-toggle");
const header = document.querySelector(".nav");
if (menu && header) {
  const close = () => {
    header.classList.remove("open");
    menu.setAttribute("aria-expanded", "false");
  };
  menu.addEventListener("click", () => {
    const open = !header.classList.contains("open");
    header.classList.toggle("open", open);
    menu.setAttribute("aria-expanded", String(open));
  });
  for (const link of header.querySelectorAll("nav a")) link.addEventListener("click", close);
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") close();
  });
}
for (const name of ["pro", "ultra"]) {
  if (!LINKS[name]) continue;
  for (const el of document.querySelectorAll(`[data-link="${name}"]`)) el.textContent = "Subscribe";
}

// Before Polar's checkout, a reminder that the license key goes to the email entered there. Continue goes on to the
// checkout; without <dialog> support the link just goes.
const checkoutDialog = document.querySelector("[data-checkout-dialog]");
let checkoutUrl = "";
function askBeforeCheckout(el) {
  el.addEventListener("click", (e) => {
    if (!checkoutDialog || typeof checkoutDialog.showModal !== "function" || !el.href || el.getAttribute("aria-disabled") === "true") return;
    e.preventDefault();
    checkoutUrl = el.href;
    checkoutDialog.showModal();
  });
}
for (const name of ["pro", "ultra"]) {
  if (!LINKS[name]) continue;
  for (const el of document.querySelectorAll(`[data-link="${name}"]`)) askBeforeCheckout(el);
}
if (LINKS.donate) for (const el of document.querySelectorAll("[data-donate]")) askBeforeCheckout(el);
if (checkoutDialog) {
  checkoutDialog.querySelector("[data-checkout-go]")?.addEventListener("click", () => {
    if (checkoutUrl) window.location.assign(checkoutUrl);
  });
  // A click on the backdrop, outside the box, closes it.
  checkoutDialog.addEventListener("click", (e) => {
    if (e.target === checkoutDialog) checkoutDialog.close("cancel");
  });
}

// Subscribers who asked to be thanked, Ultra first, then supporters who donated $5 or more, then the content
// creators and others the developer thanked with a special role.
const CREDIT_ROLES = { supporter: "Supporter", exclusive: "Exclusive", creator: "Content Creator", special: "Special" };
if (LINKS.service) {
  fetch(`${LINKS.service}/v1/credits`)
    .then((r) => (r.ok ? r.json() : null))
    .then((credits) => {
      const names = [
        ...(credits?.ultra ?? []).map((n) => [n, "ultra"]),
        ...(credits?.pro ?? []).map((n) => [n, ""]),
        ...Object.keys(CREDIT_ROLES).flatMap((role) => (credits?.[role] ?? []).map((n) => [n, role])),
      ];
      const box = document.querySelector("[data-credits]");
      const list = document.querySelector("[data-credit-list]");
      if (!box || !list || names.length === 0) return;
      for (const [name, group] of names) {
        const li = document.createElement("li");
        li.textContent = name;
        if (group === "ultra") li.className = "ultra";
        if (CREDIT_ROLES[group]) {
          li.className = "role";
          const tag = document.createElement("span");
          tag.textContent = CREDIT_ROLES[group];
          li.append(tag);
        }
        list.append(li);
      }
      box.hidden = false;
    })
    .catch(() => {});
}
setLink("releases", releases);
setLink("download", `${releases}/latest`);
setLink("setup", `${releases}/latest`);

// The newest release: its installer and zip, version, size and checksum.
fetch(`https://api.github.com/repos/${LINKS.repo}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } })
  .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
  .then((release) => {
    const assets = release.assets || [];
    const zips = assets.filter((a) => /windows-x64\.zip$/i.test(a.name));
    const zip = zips.find((a) => a.name === STABLE_ZIP) || zips[0];
    const setup = assets.find((a) => a.name === STABLE_SETUP);
    const mb = (asset) => `${Math.round(asset.size / (1024 * 1024))} MB`;
    // The fixed name when the release has it, so a copied link keeps working for later versions too.
    const zipUrl = zip && (zip.name === STABLE_ZIP ? `${releases}/latest/download/${STABLE_ZIP}` : zip.browser_download_url);
    if (zip) {
      setLink("download", zipUrl);
      setText("size", mb(zip));
    }
    const main = setup || zip;
    if (main) {
      // Before a release has the installer, the main button gets the zip and says so.
      setLink("setup", setup ? `${releases}/latest/download/${STABLE_SETUP}` : zipUrl);
      setText("setup-size", mb(main));
      if (!setup) setText("setup-label", "Download for Windows (.zip)");
      if (main.digest && main.digest.startsWith("sha256:")) setText("sha", main.digest.slice(7));
    }
    const version = (release.tag_name || "").replace(/^v/, "");
    if (version) {
      setText("version", `Version ${version}`);
      setText("version-short", `v${version}`);
      for (const el of document.querySelectorAll("dd[data-version]")) el.textContent = version;
    }
    if (release.published_at) {
      setText("date", new Date(release.published_at).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }));
    }
  })
  .catch(() => {
    // Offline or rate-limited: the links still go to the latest release page.
  });

// The live download counter: every download of every Clip Engine installer and zip on GitHub Releases, added up.
// GitHub lets a visitor ask 60 times an hour, so it's asked on load and then every five minutes while the page is
// open and showing.
let shownDownloads = 0;

async function countDownloads() {
  let total = 0;
  for (let page = 1; page <= 5; page++) {
    const r = await fetch(`https://api.github.com/repos/${LINKS.repo}/releases?per_page=100&page=${page}`, {
      headers: { Accept: "application/vnd.github+json" },
    });
    if (!r.ok) throw new Error(String(r.status));
    const list = await r.json();
    for (const release of list) {
      for (const asset of release.assets || []) {
        if (/^Clip_Engine-.*\.(zip|exe)$/i.test(asset.name)) total += asset.download_count || 0;
      }
    }
    if (list.length < 100) break;
  }
  return total;
}

function showDownloads(total) {
  if (!(total > 0)) return;
  for (const el of document.querySelectorAll("[data-downloads-box]")) el.hidden = false;
  const from = shownDownloads;
  shownDownloads = total;
  const reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const start = performance.now();
  const step = (now) => {
    const t = reduce ? 1 : Math.min(1, (now - start) / 900);
    const value = Math.round(from + (total - from) * (1 - Math.pow(1 - t, 3)));
    setText("downloads", value.toLocaleString());
    if (t < 1) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
}

function refreshDownloads() {
  countDownloads().then(showDownloads).catch(() => {
    // Rate-limited or offline: keep whatever is shown, or nothing.
  });
}

refreshDownloads();
setInterval(() => {
  if (!document.hidden) refreshDownloads();
}, 5 * 60 * 1000);

// Motion (styles.css, under html.motion): a light that follows the pointer, a scan down the page now and then and
// when you jump to a section, panels that light where the pointer is, and sections that draw in as they scroll into
// view. On unless the visitor's system asks for reduced motion; the Motion button turns it on or off, remembered.
const MOTION_KEY = "clip-engine-motion";
const root = document.documentElement;
const reducedMotion = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;
const motionToggle = document.querySelector(".motion-toggle");
const glow = Object.assign(document.createElement("div"), { className: "glow" });
const scan = Object.assign(document.createElement("div"), { className: "scan" });
glow.setAttribute("aria-hidden", "true");
scan.setAttribute("aria-hidden", "true");
document.body.prepend(glow, scan);

function savedMotion() {
  try {
    return localStorage.getItem(MOTION_KEY);
  } catch {
    return null;
  }
}

const motionOn = () => root.classList.contains("motion");

// Sections draw in once each: the observer marks a piece seen, then lets go of it so hover takes over again.
const revealTargets = [...document.querySelectorAll(".section .mark, .section .plate:not(.frame), .section .figure, .games")];
const revealer = "IntersectionObserver" in window
  ? new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        const el = entry.target;
        revealer.unobserve(el);
        el.classList.add("seen");
        setTimeout(() => el.classList.remove("reveal", "seen"), 900 + (parseInt(el.style.getPropertyValue("--d")) || 0));
      }
    }, { rootMargin: "0px 0px -8% 0px" })
  : null;

function setMotion(on, remember) {
  root.classList.toggle("motion", on);
  if (motionToggle) motionToggle.setAttribute("aria-pressed", String(on));
  if (remember) {
    try {
      localStorage.setItem(MOTION_KEY, on ? "on" : "off");
    } catch {
      // Private window or storage blocked: it holds for this visit.
    }
  }
  if (!revealer) return;
  for (const el of revealTargets) {
    revealer.unobserve(el);
    el.classList.remove("reveal", "seen");
    if (!on) continue;
    // Panels side by side rise one after another.
    const siblings = el.parentElement ? [...el.parentElement.children] : [];
    el.style.setProperty("--d", `${Math.min(siblings.indexOf(el), 5) * 70}ms`);
    el.classList.add("reveal");
    revealer.observe(el);
  }
}

if (motionToggle) {
  motionToggle.hidden = false;
  motionToggle.addEventListener("click", () => setMotion(!motionOn(), true));
}
const saved = savedMotion();
setMotion(saved ? saved === "on" : !(reducedMotion && reducedMotion.matches), false);
// Changing the system setting while the page is open follows it, unless the visitor chose here.
if (reducedMotion && reducedMotion.addEventListener) {
  reducedMotion.addEventListener("change", (e) => {
    if (!savedMotion()) setMotion(!e.matches, false);
  });
}

// The light and the panel under the pointer, moved once a frame at most.
let pointer = null;
let pointerFrame = 0;
let litPlate = null;
window.addEventListener("pointermove", (e) => {
  if (!motionOn()) return;
  pointer = e;
  if (!pointerFrame) {
    pointerFrame = requestAnimationFrame(() => {
      pointerFrame = 0;
      const p = pointer;
      glow.style.transform = `translate(${p.clientX - 320}px, ${p.clientY - 320}px)`;
      glow.classList.add("on");
      const plate = p.target instanceof Element ? p.target.closest(".plate") : null;
      if (litPlate && litPlate !== plate) {
        litPlate.style.removeProperty("--px");
        litPlate.style.removeProperty("--py");
      }
      litPlate = plate;
      if (plate) {
        const box = plate.getBoundingClientRect();
        plate.style.setProperty("--px", `${p.clientX - box.left}px`);
        plate.style.setProperty("--py", `${p.clientY - box.top}px`);
      }
    });
  }
}, { passive: true });
root.addEventListener("pointerleave", () => glow.classList.remove("on"));

// Jumping to a section runs a quick scan, like the app does when you change page.
for (const link of document.querySelectorAll('a[href^="#"]')) {
  link.addEventListener("click", () => {
    if (!motionOn()) return;
    scan.classList.remove("quick");
    void scan.offsetWidth;
    scan.classList.add("quick");
  });
}
scan.addEventListener("animationend", (e) => {
  if (e.animationName === "scan-quick") scan.classList.remove("quick");
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
