// The public pages: a shared clip's web player with its moments on the timeline, and a
// player's profile with their best clips. Plain HTML with everything inline, so a link opens
// fast and embeds in Discord (og:video plays right in the chat).

export function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

const MOMENT_LABELS = {
  kill: "Kill", assist: "Assist", death: "Death", revive: "Revive",
  match_start: "Match start", match_end: "Match end", highlight: "Highlight",
};

export function momentLabel(type) {
  return MOMENT_LABELS[type] ?? "Moment";
}

// "3 kills, 1 revive" from a clip's markers.
export function summarize(markers) {
  const counts = new Map();
  for (const m of markers ?? []) counts.set(m.type, (counts.get(m.type) ?? 0) + 1);
  return [...counts]
    .map(([type, n]) => {
      const label = momentLabel(type).toLowerCase();
      return `${n} ${n === 1 ? label : label === "match end" ? "match ends" : label === "match start" ? "match starts" : label + "s"}`;
    })
    .join(", ");
}

function duration(seconds) {
  const s = Math.max(0, Math.round(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

// Where people get Clip Engine. Every page, and every clip's watermark, leads here.
export const SITE_URL = "https://mochiboy9.github.io/Clip_Engine/";
const SITE_SHORT = SITE_URL.replace(/^https:\/\//, "").replace(/\/$/, "");
const DOWNLOAD_URL = `${SITE_URL}#download`;

// The line every shared clip carries in its link preview (Discord, X, iMessage...), after the clip's own details.
export const MADE_WITH = `Clipped automatically with Clip Engine. Free for Windows: ${SITE_SHORT}`;

// A small play mark, inline so the watermark costs no request.
const MARK = `<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><defs><linearGradient id="ceg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#8b5cf6"/><stop offset="1" stop-color="#3b82f6"/></linearGradient></defs><rect width="24" height="24" rx="6" fill="url(#ceg)"/><path d="M9.5 7.5v9l7-4.5z" fill="#fff"/></svg>`;

const STYLE = `
:root{--bg:#14161a;--panel:#1d2026;--line:#2c3038;--text:#e9e6df;--dim:#9a9ea8;--amber:#f2a516;--kill:#f2a516;--death:#e0524a;--assist:#6cb4ee;--revive:#5fd08a;--match:#b48cf2;--highlight:#ffd54a}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--text);font:15px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif}
a{color:var(--amber)}main{max-width:1100px;margin:0 auto;padding:20px 16px 48px}
header{display:flex;align-items:center;gap:10px;margin-bottom:16px}header .brand{font-weight:700;letter-spacing:.08em;text-transform:uppercase;font-size:13px;color:var(--amber);text-decoration:none}
h1{font-size:22px;margin:4px 0 2px}.sub{color:var(--dim);font-size:14px}
.player{position:relative;background:#000;border:1px solid var(--line);border-radius:10px;overflow:hidden}
.watermark{position:absolute;top:10px;right:12px;display:inline-flex;align-items:center;gap:6px;padding:3px 8px 3px 4px;border-radius:999px;background:rgba(0,0,0,.35);color:#fff;font-size:12px;font-weight:600;letter-spacing:.02em;text-decoration:none;opacity:.55;transition:opacity .2s;z-index:1}
.watermark:hover,.watermark:focus-visible{opacity:.95}
.watermark .get{display:none}.watermark:hover .get,.watermark:focus-visible .get{display:inline}.watermark:hover .name,.watermark:focus-visible .name{display:none}
.cta{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:10px 16px;margin-top:18px;padding:12px 14px;background:var(--panel);border:1px solid var(--line);border-radius:10px;font-size:14px;color:var(--dim)}
.cta b{color:var(--text)}.cta .get-btn{display:inline-block;background:var(--amber);color:#14161a;padding:8px 14px;border-radius:6px;text-decoration:none;font-weight:700;white-space:nowrap}
@media (max-width:480px){.watermark{top:6px;right:8px;font-size:11px}}
video{display:block;width:100%;max-height:72vh;background:#000}
.track{position:relative;height:30px;margin:10px 0 4px;background:var(--panel);border:1px solid var(--line);border-radius:6px;cursor:pointer}
.track .played{position:absolute;inset:0 auto 0 0;background:rgba(242,165,22,.15);border-radius:6px 0 0 6px;pointer-events:none}
.mark{position:absolute;top:4px;bottom:4px;width:4px;margin-left:-2px;border-radius:2px;background:var(--c,var(--amber));border:0;padding:0;cursor:pointer}
.legend{display:flex;flex-wrap:wrap;gap:6px 12px;margin:8px 0 0;padding:0;list-style:none;font-size:13px;color:var(--dim)}
.legend button{all:unset;cursor:pointer;display:inline-flex;align-items:center;gap:6px}.legend button:hover{color:var(--text)}
.dot{width:9px;height:9px;border-radius:50%;background:var(--c,var(--amber))}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:14px;margin-top:18px}
.card{display:block;background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden;text-decoration:none;color:var(--text)}
.card img,.card .noimg{display:block;width:100%;aspect-ratio:16/9;object-fit:cover;background:#0c0d10}
.card .body{padding:10px 12px}.card .title{font-weight:600}.card .meta{color:var(--dim);font-size:13px}
.star{color:var(--amber)}footer{margin-top:32px;color:var(--dim);font-size:13px}
`;

const COLORS = { kill: "--kill", death: "--death", assist: "--assist", revive: "--revive", match_start: "--match", match_end: "--match", highlight: "--highlight" };

function shell({ title, description, head = "", body }) {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}</title><meta name="description" content="${escapeHtml(description)}">${head}<style>${STYLE}</style></head>
<body><main><header><a class="brand" href="${SITE_URL}">Clip Engine</a></header>${body}
<footer>Made with <a href="${SITE_URL}">Clip Engine</a>, free clipping for PC games. <a href="${DOWNLOAD_URL}">Download Clip Engine</a></footer></main></body></html>`;
}

// The page for /c/<code>.
export function clipPage(clip, origin) {
  const base = `${origin}/c/${clip.code}`;
  const markers = (clip.markers ?? []).filter((m) => Number.isFinite(m.time));
  const summary = summarize(markers);
  const title = clip.title || summary || `${clip.game} clip`;
  const by = clip.ownerName ? ` · ${clip.ownerName}` : "";
  const details = [clip.game, summary, duration(clip.seconds)].filter(Boolean).join(" · ");
  const description = `${details}. ${MADE_WITH}`;
  const head = `
<link rel="alternate" type="application/json+oembed" href="${escapeHtml(base)}/oembed" title="${escapeHtml(title)}">
<meta property="og:type" content="video.other"><meta property="og:title" content="${escapeHtml(title)}">
<meta property="og:description" content="${escapeHtml(description)}"><meta property="og:url" content="${escapeHtml(base)}">
${clip.thumb ? `<meta property="og:image" content="${escapeHtml(base)}/thumb">` : ""}
<meta property="og:video" content="${escapeHtml(base)}/video"><meta property="og:video:secure_url" content="${escapeHtml(base)}/video">
<meta property="og:video:type" content="video/mp4">${clip.width ? `<meta property="og:video:width" content="${clip.width}"><meta property="og:video:height" content="${clip.height}">` : ""}
<meta property="og:site_name" content="Clip Engine"><meta name="twitter:card" content="player"><meta name="theme-color" content="#f2a516">`;
  const seconds = clip.seconds > 0 ? clip.seconds : 1;
  const marks = markers
    .map((m) => `<button class="mark" style="left:${Math.min(100, Math.max(0, (m.time / seconds) * 100)).toFixed(2)}%;--c:var(${COLORS[m.type] ?? "--amber"})" data-t="${m.time}" title="${escapeHtml(momentLabel(m.type))} at ${duration(m.time)}${m.text ? " · " + escapeHtml(m.text) : ""}" aria-label="${escapeHtml(momentLabel(m.type))} at ${duration(m.time)}"></button>`)
    .join("");
  const legend = markers
    .map((m) => `<li><button data-t="${m.time}"><span class="dot" style="--c:var(${COLORS[m.type] ?? "--amber"})"></span>${escapeHtml(momentLabel(m.type))} <span>${duration(m.time)}</span></button></li>`)
    .join("");
  const body = `
<h1>${escapeHtml(title)}</h1>
<div class="sub">${escapeHtml(clip.game)}${clip.ownerHandle ? ` · <a href="/u/${escapeHtml(clip.ownerHandle)}">@${escapeHtml(clip.ownerHandle)}</a>` : escapeHtml(by)}${summary && summary !== title ? ` · ${escapeHtml(summary)}` : ""}</div>
<div class="player" style="margin-top:14px"><a class="watermark" href="${SITE_URL}" target="_blank" rel="noopener" title="Made with Clip Engine. Get it free" aria-label="Made with Clip Engine. Get it free">${MARK}<span class="name">Clip Engine</span><span class="get">Get Clip Engine</span></a><video id="v" controls playsinline preload="metadata" src="${escapeHtml(base)}/video"${clip.thumb ? ` poster="${escapeHtml(base)}/thumb"` : ""}></video></div>
${markers.length ? `<div class="track" id="track"><div class="played" id="played"></div>${marks}</div><ul class="legend">${legend}</ul>` : ""}
<div class="cta"><span><b>Clipped by itself with Clip Engine.</b> Every kill, assist and revive saved for you, free on Windows.</span><a class="get-btn" href="${DOWNLOAD_URL}">Download free</a></div>
<script>
const v=document.getElementById("v"),track=document.getElementById("track"),played=document.getElementById("played");
function seek(t){v.currentTime=Math.max(0,t-1.5);v.play().catch(()=>{});}
document.querySelectorAll("[data-t]").forEach(b=>b.addEventListener("click",e=>{e.stopPropagation();seek(+b.dataset.t);}));
if(track){track.addEventListener("click",e=>{const r=track.getBoundingClientRect();v.currentTime=(e.clientX-r.left)/r.width*(v.duration||${seconds});});
v.addEventListener("timeupdate",()=>{played.style.width=(v.currentTime/(v.duration||${seconds})*100)+"%";});}
</script>`;
  return shell({ title: `${title} · Clip Engine`, description, head, body });
}

// /c/<code>/oembed: what Discord and other apps show around a shared clip's preview. The author line names
// Clip Engine with a link to get it; the provider line is the site.
export function clipOembed(clip, origin) {
  const summary = summarize((clip.markers ?? []).filter((m) => Number.isFinite(m.time)));
  const by = clip.ownerName ? `${clip.ownerName} · ` : "";
  const out = {
    version: "1.0",
    type: "video",
    title: clip.title || summary || `${clip.game} clip`,
    author_name: `${by}Clipped with Clip Engine, free for Windows`,
    author_url: SITE_URL,
    provider_name: "Clip Engine",
    provider_url: SITE_URL,
  };
  if (clip.thumb) out.thumbnail_url = `${origin}/c/${clip.code}/thumb`;
  if (clip.width && clip.height) {
    out.width = clip.width;
    out.height = clip.height;
  }
  return out;
}

// The page for /u/<handle>: featured clips first, then the newest public ones.
export function profilePage(profile, origin) {
  const cards = profile.clips
    .map((c) => `<a class="card" href="/c/${escapeHtml(c.code)}">${c.thumb ? `<img loading="lazy" src="/c/${escapeHtml(c.code)}/thumb" alt="">` : `<div class="noimg"></div>`}
<div class="body"><div class="title">${c.featured ? `<span class="star" title="Featured">★</span> ` : ""}${escapeHtml(c.title || summarize(c.markers) || `${c.game} clip`)}</div>
<div class="meta">${escapeHtml(c.game)} · ${duration(c.seconds)}${c.views ? ` · ${c.views} ${c.views === 1 ? "view" : "views"}` : ""}</div></div></a>`)
    .join("");
  const body = `<h1>${escapeHtml(profile.name)}</h1><div class="sub">@${escapeHtml(profile.handle)} · ${profile.clips.length} ${profile.clips.length === 1 ? "clip" : "clips"}</div>
${cards ? `<div class="grid">${cards}</div>` : `<p class="sub" style="margin-top:18px">No public clips yet.</p>`}`;
  const head = `<meta property="og:title" content="${escapeHtml(profile.name)} on Clip Engine"><meta property="og:url" content="${escapeHtml(origin)}/u/${escapeHtml(profile.handle)}"><meta name="theme-color" content="#f2a516">`;
  return shell({ title: `${profile.name} (@${profile.handle}) · Clip Engine`, description: `${profile.name}'s best clips`, head, body });
}

export function notFoundPage() {
  return shell({ title: "Not found · Clip Engine", description: "", body: `<h1>That clip isn't here</h1><p class="sub">It may have been made private or removed.</p>` });
}

// The page for /s/<invite>: the "Join my squad" button on a Discord status leads here.
export function invitePage(squad, invite) {
  const app = `clipengine://squad/join/${invite}`;
  const body = `<h1>Join ${escapeHtml(squad.name)}</h1>
<div class="sub">${squad.members.length} of ${squad.max} · ${squad.members.map(escapeHtml).join(", ")}</div>
<div style="margin-top:22px;padding:18px;background:var(--panel);border:1px solid var(--line);border-radius:10px;max-width:520px">
<p style="margin:0 0 6px">Squadmates who run Clip Engine save their own view of each other's plays, and every view lands in a shared squad library.</p>
<p class="sub" style="margin:0 0 14px">Invite code</p>
<div style="font:600 22px/1.2 ui-monospace,Consolas,monospace;letter-spacing:.08em;color:var(--amber)">${escapeHtml(invite)}</div>
<p style="margin:18px 0 0"><a href="${escapeHtml(app)}" style="display:inline-block;background:var(--amber);color:#14161a;padding:9px 16px;border-radius:6px;text-decoration:none;font-weight:700">Open in Clip Engine</a></p>
<p class="sub" style="margin:12px 0 0">Or paste the code on the Squad page of the app.</p></div>`;
  return shell({ title: `Join ${squad.name} · Clip Engine`, description: `Join ${squad.name} on Clip Engine`, body,
    head: `<meta property="og:title" content="Join ${escapeHtml(squad.name)} on Clip Engine"><meta name="theme-color" content="#f2a516">` });
}
