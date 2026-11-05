// The public pages: a shared clip's web player with its moments on the timeline, and a
// player's profile with their best clips. Plain HTML with everything inline, so a link opens fast
// and embeds in Discord (og:video plays right in the chat). The hub (src/hub.js) shares their look (src/look.js).

import { badgeHtml, DOWNLOAD_URL, escapeHtml, MOMENT_COLORS as COLORS, shell, SITE_URL } from "./look.js";

export { escapeHtml, SITE_URL };

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

export function duration(seconds) {
  const s = Math.max(0, Math.round(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

const SITE_SHORT = SITE_URL.replace(/^https:\/\//, "").replace(/\/$/, "");

// The line every shared clip carries in its link preview (Discord, X, iMessage...), after the clip's own details.
export const MADE_WITH = `Clipped automatically with Clip Engine. Free for Windows: ${SITE_SHORT}`;

// A small play mark, inline so the watermark costs no request.
const MARK = `<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><defs><linearGradient id="ceg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ffc21a"/><stop offset="1" stop-color="#ff9d1a"/></linearGradient></defs><rect width="24" height="24" rx="6" fill="url(#ceg)"/><path d="M9.5 7.5v9l7-4.5z" fill="#0b0c0e"/></svg>`;

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
<meta property="og:site_name" content="Clip Engine"><meta name="twitter:card" content="player">`;
  const seconds = clip.seconds > 0 ? clip.seconds : 1;
  const marks = markers
    .map((m) => `<button class="mark" style="left:${Math.min(100, Math.max(0, (m.time / seconds) * 100)).toFixed(2)}%;--c:var(${COLORS[m.type] ?? "--accent"})" data-t="${m.time}" title="${escapeHtml(momentLabel(m.type))} at ${duration(m.time)}${m.text ? " · " + escapeHtml(m.text) : ""}" aria-label="${escapeHtml(momentLabel(m.type))} at ${duration(m.time)}"></button>`)
    .join("");
  const legend = markers
    .map((m) => `<li><button data-t="${m.time}"><span class="dot" style="--c:var(${COLORS[m.type] ?? "--accent"})"></span>${escapeHtml(momentLabel(m.type))} <span>${duration(m.time)}</span></button></li>`)
    .join("");
  const body = `
<h1>${escapeHtml(title)}</h1>
<div class="sub">${escapeHtml(clip.game)}${clip.ownerHandle ? ` · <a href="/u/${escapeHtml(clip.ownerHandle)}">@${escapeHtml(clip.ownerHandle)}</a>` : escapeHtml(by)} ${badgeHtml(clip.ownerBadge)}${summary && summary !== title ? ` · ${escapeHtml(summary)}` : ""}${clip.views ? ` · ${clip.views} ${clip.views === 1 ? "view" : "views"}` : ""}</div>
<div class="player" style="margin-top:14px"><a class="watermark" href="${SITE_URL}" target="_blank" rel="noopener" title="Made with Clip Engine. Get it free" aria-label="Made with Clip Engine. Get it free">${MARK}<span class="name">Clip Engine</span><span class="get">Get Clip Engine</span></a><video id="v" controls playsinline preload="metadata" src="${escapeHtml(base)}/video"${clip.thumb ? ` poster="${escapeHtml(base)}/thumb"` : ""}></video></div>
${markers.length ? `<div class="track" id="track"><div class="played" id="played"></div>${marks}</div><ul class="legend">${legend}</ul>` : ""}
<div class="cta"><span><b>Clipped by itself with Clip Engine.</b> Every kill, assist and revive saved for you, free on Windows.</span><a class="get-btn" href="${DOWNLOAD_URL}">Download free</a></div>
${clip.visibility === "public" ? reportBox(clip.code) : ""}
<script>
const v=document.getElementById("v"),track=document.getElementById("track"),played=document.getElementById("played");
function seek(t){v.currentTime=Math.max(0,t-1.5);v.play().catch(()=>{});}
document.querySelectorAll("[data-t]").forEach(b=>b.addEventListener("click",e=>{e.stopPropagation();seek(+b.dataset.t);}));
if(track){track.addEventListener("click",e=>{const r=track.getBoundingClientRect();v.currentTime=(e.clientX-r.left)/r.width*(v.duration||${seconds});});
v.addEventListener("timeupdate",()=>{played.style.width=(v.currentTime/(v.duration||${seconds})*100)+"%";});}
</script>`;
  return shell({ title: `${title} · Clip Engine`, description, head, body, current: "hub" });
}

// A public clip is in the hub, so anyone can report it to the operator.
function reportBox(code) {
  const reasons = [["spam", "Spam or misleading"], ["cheating", "Cheating"], ["hateful", "Hateful or harassing"], ["sexual", "Sexual content"],
    ["violent", "Graphic violence"], ["other", "Something else"]];
  return `<details class="report" style="margin-top:14px"><summary class="fine" style="cursor:pointer">Report this clip</summary>
<form id="report" class="panel" style="margin-top:8px;display:grid;gap:10px;max-width:460px"><div><label for="rr">What's wrong?</label><select id="rr">${reasons.map(([v, t]) => `<option value="${v}">${t}</option>`).join("")}</select></div>
<div><label for="rt">Anything to add (optional)</label><textarea id="rt" rows="2" maxlength="500"></textarea></div><div><button class="btn small" type="submit">Send report</button> <span class="msg" id="rm" role="status"></span></div></form></details>
<script>document.getElementById("report").addEventListener("submit",async e=>{e.preventDefault();const m=document.getElementById("rm");m.className="msg";m.textContent="Sending…";
try{const r=await fetch("/hub/api/report",{method:"POST",headers:{"content-type":"application/json","x-hub":"1"},body:JSON.stringify({code:${JSON.stringify(code)},reason:document.getElementById("rr").value,text:document.getElementById("rt").value})});
const b=await r.json().catch(()=>({}));if(!r.ok)throw new Error(b.error||"That didn't go through.");m.className="msg ok";m.textContent="Thanks. It's been sent for review.";e.target.querySelector("button").disabled=true;}catch(x){m.className="msg bad";m.textContent=x.message;}});</script>`;
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

// One clip in a grid: its thumbnail with its length and moments, title, who clipped it, game and views. The hub and
// profiles use it. clip: a feed entry (src/feed.js) or a library clip with owner set.
export function clipCard(clip, { showOwner = true } = {}) {
  const counts = clip.moments ?? countMoments(clip.markers);
  const tags = Object.entries(counts)
    .filter(([type]) => type !== "match_start" && type !== "match_end")
    .sort((a, b) => b[1] - a[1]).slice(0, 3)
    .map(([type, n]) => `<span style="--c:var(${COLORS[type] ?? "--accent"})">${n} ${escapeHtml(momentLabel(type))}${n === 1 || type === "highlight" ? "" : "s"}</span>`)
    .join("");
  const title = clip.title || summarize(Object.entries(counts).flatMap(([type, n]) => Array(Math.min(n, 50)).fill({ type }))) || `${clip.game} clip`;
  const owner = clip.owner ?? {};
  const who = showOwner && owner.name
    ? `<div class="who"><span>${escapeHtml(owner.handle ? `@${owner.handle}` : owner.name)}</span>${badgeHtml(owner.badge)}</div>` : "";
  return `<a class="card" href="/c/${escapeHtml(clip.code)}"><div class="shot">${clip.thumb ? `<img loading="lazy" decoding="async" src="/c/${escapeHtml(clip.code)}/thumb" alt="">` : ""}<div class="tags">${tags}</div><span class="len">${duration(clip.seconds)}</span></div>
<div class="body"><div class="title">${clip.featured ? `<span class="star" title="Pinned">★</span> ` : ""}${escapeHtml(title)}</div>${who}
<div class="meta">${escapeHtml(clip.game)}${clip.views ? ` · ${clip.views} ${clip.views === 1 ? "view" : "views"}` : ""}</div></div></a>`;
}

function countMoments(markers) {
  const counts = {};
  for (const m of markers ?? []) counts[m.type] = (counts[m.type] ?? 0) + 1;
  return counts;
}

// The page for /u/<handle>: featured clips first, then the newest public ones.
export function profilePage(profile, origin) {
  const cards = profile.clips.map((c) => clipCard(c, { showOwner: false })).join("");
  const body = `<h1>${escapeHtml(profile.name)} ${badgeHtml(profile.badge)}</h1><div class="sub">@${escapeHtml(profile.handle)} · ${profile.clips.length} public ${profile.clips.length === 1 ? "clip" : "clips"}</div>
${cards ? `<div class="grid">${cards}</div>` : `<p class="empty">No public clips yet.</p>`}`;
  const head = `<meta property="og:title" content="${escapeHtml(profile.name)} on Clip Engine"><meta property="og:url" content="${escapeHtml(origin)}/u/${escapeHtml(profile.handle)}">`;
  return shell({ title: `${profile.name} (@${profile.handle}) · Clip Engine`, description: `${profile.name}'s best clips`, head, body, current: "hub" });
}

export function notFoundPage() {
  return shell({ title: "Not found · Clip Engine", description: "",
    body: `<h1>That clip isn't here</h1><p class="sub">It may have been made private or removed.</p><p><a class="btn ghost" href="/hub">Browse the hub</a></p>` });
}

// The page for /s/<invite>: the "Join my squad" button on a Discord status leads here.
export function invitePage(squad, invite) {
  const app = `clipengine://squad/join/${invite}`;
  const body = `<h1>Join ${escapeHtml(squad.name)}</h1>
<div class="sub">${squad.members.length} of ${squad.max} · ${squad.members.map(escapeHtml).join(", ")}</div>
<div style="margin-top:22px;padding:18px;background:var(--surface);border:1px solid var(--line);max-width:520px">
<p style="margin:0 0 6px">Squadmates who run Clip Engine save their own view of each other's plays, and every view lands in a shared squad library.</p>
<p class="sub" style="margin:0 0 14px">Invite code</p>
<div style="font:600 22px/1.2 ui-monospace,Consolas,monospace;letter-spacing:.08em;color:var(--accent)">${escapeHtml(invite)}</div>
<p style="margin:18px 0 0"><a href="${escapeHtml(app)}" style="display:inline-block;background:var(--accent);color:var(--on-accent);padding:9px 16px;border-radius:6px;text-decoration:none;font-weight:700">Open in Clip Engine</a></p>
<p class="sub" style="margin:12px 0 0">Or paste the code on the Squad page of the app.</p></div>`;
  return shell({ title: `Join ${squad.name} · Clip Engine`, description: `Join ${squad.name} on Clip Engine`, body,
    head: `<meta property="og:title" content="Join ${escapeHtml(squad.name)} on Clip Engine">` });
}
