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

const STYLE = `
:root{--bg:#14161a;--panel:#1d2026;--line:#2c3038;--text:#e9e6df;--dim:#9a9ea8;--amber:#f2a516;--kill:#f2a516;--death:#e0524a;--assist:#6cb4ee;--revive:#5fd08a;--match:#b48cf2;--highlight:#ffd54a}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--text);font:15px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif}
a{color:var(--amber)}main{max-width:1100px;margin:0 auto;padding:20px 16px 48px}
header{display:flex;align-items:center;gap:10px;margin-bottom:16px}header .brand{font-weight:700;letter-spacing:.08em;text-transform:uppercase;font-size:13px;color:var(--amber);text-decoration:none}
h1{font-size:22px;margin:4px 0 2px}.sub{color:var(--dim);font-size:14px}
.player{background:#000;border:1px solid var(--line);border-radius:10px;overflow:hidden}
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
<body><main><header><a class="brand" href="/">Clip Engine</a></header>${body}
<footer>Recorded and clipped with Clip Engine.</footer></main></body></html>`;
}

// The page for /c/<code>.
export function clipPage(clip, origin) {
  const base = `${origin}/c/${clip.code}`;
  const markers = (clip.markers ?? []).filter((m) => Number.isFinite(m.time));
  const summary = summarize(markers);
  const title = clip.title || summary || `${clip.game} clip`;
  const by = clip.ownerName ? ` · ${clip.ownerName}` : "";
  const description = [clip.game, summary, duration(clip.seconds)].filter(Boolean).join(" · ");
  const head = `
<meta property="og:type" content="video.other"><meta property="og:title" content="${escapeHtml(title)}">
<meta property="og:description" content="${escapeHtml(description)}"><meta property="og:url" content="${escapeHtml(base)}">
${clip.thumb ? `<meta property="og:image" content="${escapeHtml(base)}/thumb">` : ""}
<meta property="og:video" content="${escapeHtml(base)}/video"><meta property="og:video:secure_url" content="${escapeHtml(base)}/video">
<meta property="og:video:type" content="video/mp4">${clip.width ? `<meta property="og:video:width" content="${clip.width}"><meta property="og:video:height" content="${clip.height}">` : ""}
<meta name="twitter:card" content="player"><meta name="theme-color" content="#f2a516">`;
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
<div class="player" style="margin-top:14px"><video id="v" controls playsinline preload="metadata" src="${escapeHtml(base)}/video"${clip.thumb ? ` poster="${escapeHtml(base)}/thumb"` : ""}></video></div>
${markers.length ? `<div class="track" id="track"><div class="played" id="played"></div>${marks}</div><ul class="legend">${legend}</ul>` : ""}
<script>
const v=document.getElementById("v"),track=document.getElementById("track"),played=document.getElementById("played");
function seek(t){v.currentTime=Math.max(0,t-1.5);v.play().catch(()=>{});}
document.querySelectorAll("[data-t]").forEach(b=>b.addEventListener("click",e=>{e.stopPropagation();seek(+b.dataset.t);}));
if(track){track.addEventListener("click",e=>{const r=track.getBoundingClientRect();v.currentTime=(e.clientX-r.left)/r.width*(v.duration||${seconds});});
v.addEventListener("timeupdate",()=>{played.style.width=(v.currentTime/(v.duration||${seconds})*100)+"%";});}
</script>`;
  return shell({ title: `${title} · Clip Engine`, description, head, body });
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
