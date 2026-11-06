// The look every page the service serves shares (a shared clip, a profile, the hub): the website's gunmetal
// panels, amber accent, angular display face and monospace readouts, laid out for phones first.

export function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

// Where people get Clip Engine. Every page, and every clip's watermark, leads here.
export const SITE_URL = "https://mochiboy9.github.io/Clip_Engine/";
export const DOWNLOAD_URL = `${SITE_URL}#download`;

export const FONTS = `<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin><link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Chakra+Petch:wght@600;700&family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@500;700&display=swap">`;

// The service's pages load their fonts from Google Fonts and nothing else from outside.
export function pageCsp(frames = "*") {
  return `default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src 'self' data:; media-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors ${frames}`;
}

export const STYLE = `
:root{--bg:#0b0c0e;--surface:#131418;--surface-2:#191a1f;--inset:#0d0e11;--line:#25272d;--line-strong:#363942;--text:#eeece6;--muted:#a7a59d;--dim:#706f69;--accent:#ffc21a;--accent-hover:#ffd04d;--accent-soft:rgba(255,194,26,.12);--on-accent:#0b0c0e;--bad:#ff5d6c;--ok:#34d17d;
--kill:#ffc21a;--death:#ff5d6c;--assist:#6cb4ee;--revive:#34d17d;--match:#b48cf2;--highlight:#ffe27a;
--display:"Chakra Petch","Segoe UI",system-ui,sans-serif;--mono:"JetBrains Mono",ui-monospace,Consolas,monospace;--body:Inter,"Segoe UI",system-ui,-apple-system,sans-serif;--cut:10px}
*{box-sizing:border-box}html{-webkit-text-size-adjust:100%}[hidden]{display:none!important}
html,body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 var(--body);-webkit-font-smoothing:antialiased}
body{background-image:radial-gradient(circle at 1px 1px,rgba(255,255,255,.06) 1px,transparent 1.2px);background-size:24px 24px;min-height:100vh}
a{color:var(--accent)}img,video{max-width:100%}
main{max-width:1180px;margin:0 auto;padding:18px 16px 56px}
h1,h2,h3{font-family:var(--display);font-weight:700;letter-spacing:.01em;line-height:1.15}
h1{font-size:clamp(24px,5vw,34px);margin:6px 0 4px;overflow-wrap:anywhere}h2{font-size:20px;margin:0 0 10px}
.sub{color:var(--muted);font-size:14px;overflow-wrap:anywhere}.fine{color:var(--dim);font:500 12px/1.5 var(--mono)}
.bar{position:sticky;top:0;z-index:5;background:rgba(11,12,14,.94);border-bottom:1px solid var(--line)}
.bar-in{max-width:1180px;margin:0 auto;padding:0 16px;height:56px;display:flex;align-items:center;gap:14px}
.brand{display:inline-flex;align-items:center;gap:9px;text-decoration:none;color:var(--text);font:700 14px var(--display);letter-spacing:.14em;text-transform:uppercase;white-space:nowrap}
.brand i{width:22px;height:22px;background:var(--accent);clip-path:polygon(0 0,100% 0,100% 70%,70% 100%,0 100%);position:relative;flex:none}
.brand i::after{content:"";position:absolute;left:8px;top:6px;border-left:8px solid var(--on-accent);border-top:5px solid transparent;border-bottom:5px solid transparent}
.brand b{color:var(--accent);font-weight:700}.brand .sep{color:var(--dim);font-family:var(--mono);letter-spacing:0}
.bar nav{margin-left:auto;display:flex;align-items:center;gap:6px}
.bar nav a{color:var(--muted);text-decoration:none;font:700 12px var(--mono);letter-spacing:.12em;text-transform:uppercase;padding:8px 10px;border-radius:4px;white-space:nowrap}
.bar nav a:hover,.bar nav a[aria-current]{color:var(--text)}
.bar nav a.me{color:var(--text);border:1px solid var(--line-strong);max-width:150px;overflow:hidden;text-overflow:ellipsis}
.btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;min-height:40px;padding:8px 16px;border:0;background:var(--accent);color:var(--on-accent);font:700 13px var(--display);letter-spacing:.08em;text-transform:uppercase;text-decoration:none;cursor:pointer;clip-path:polygon(0 0,100% 0,100% calc(100% - 8px),calc(100% - 8px) 100%,0 100%);white-space:nowrap}
.btn:hover{background:var(--accent-hover)}.btn:disabled{opacity:.5;cursor:default}
.btn.ghost{background:var(--surface-2);color:var(--text);box-shadow:inset 0 0 0 1px var(--line-strong)}.btn.ghost:hover{background:var(--line)}
.btn.small{min-height:32px;padding:6px 12px;font-size:12px}.btn.bad{background:transparent;color:var(--bad);box-shadow:inset 0 0 0 1px rgba(255,93,108,.4)}
.panel{background:var(--surface);border:1px solid var(--line);clip-path:polygon(0 0,calc(100% - var(--cut)) 0,100% var(--cut),100% 100%,0 100%);padding:16px}
.badge{display:inline-flex;align-items:center;gap:4px;padding:1px 7px;border-radius:3px;font:700 10px/16px var(--mono);letter-spacing:.1em;text-transform:uppercase;vertical-align:middle;white-space:nowrap;background:var(--accent-soft);color:var(--accent);box-shadow:inset 0 0 0 1px rgba(255,194,26,.35)}
.badge.ultra{background:linear-gradient(90deg,#ffc21a,#ff8a3d);color:var(--on-accent);box-shadow:none}
.badge.creator{background:rgba(255,93,108,.14);color:#ff8f9a;box-shadow:inset 0 0 0 1px rgba(255,93,108,.4)}
.badge.exclusive{background:rgba(180,140,242,.15);color:#cdb2ff;box-shadow:inset 0 0 0 1px rgba(180,140,242,.45)}
.badge.special{background:rgba(52,209,125,.13);color:#7fe6ae;box-shadow:inset 0 0 0 1px rgba(52,209,125,.4)}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:14px;margin-top:16px}
.card{display:flex;flex-direction:column;background:var(--surface);border:1px solid var(--line);text-decoration:none;color:var(--text);min-width:0;transition:border-color .15s}
.card:hover,.card:focus-visible{border-color:var(--line-strong)}
.card .shot{position:relative;aspect-ratio:16/9;background:var(--inset);overflow:hidden}
.card .shot img{display:block;width:100%;height:100%;object-fit:cover}
.card .len{position:absolute;right:6px;bottom:6px;background:rgba(0,0,0,.72);color:#fff;font:700 11px var(--mono);padding:2px 6px;border-radius:3px}
.card .tags{position:absolute;left:6px;top:6px;right:6px;display:flex;gap:4px;flex-wrap:wrap}
.card .tags span{background:rgba(0,0,0,.72);color:#fff;font:700 10px var(--mono);letter-spacing:.06em;text-transform:uppercase;padding:2px 6px;border-radius:3px;border-left:2px solid var(--c,var(--accent))}
.card .body{padding:10px 12px 12px;display:grid;gap:3px;min-width:0}
.card .title{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.card .who{color:var(--muted);font-size:13px;display:flex;align-items:center;gap:6px;min-width:0}.card .who span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.card .meta{color:var(--dim);font:500 12px var(--mono)}
.star{color:var(--accent)}
.player{position:relative;background:#000;border:1px solid var(--line);overflow:hidden}
video{display:block;width:100%;max-height:74vh;background:#000}
.watermark{position:absolute;top:10px;right:12px;display:inline-flex;align-items:center;gap:6px;padding:3px 8px 3px 4px;border-radius:999px;background:rgba(0,0,0,.35);color:#fff;font-size:12px;font-weight:600;letter-spacing:.02em;text-decoration:none;opacity:.55;transition:opacity .2s;z-index:1}
.watermark:hover,.watermark:focus-visible{opacity:.95}
.watermark .get{display:none}.watermark:hover .get,.watermark:focus-visible .get{display:inline}.watermark:hover .name,.watermark:focus-visible .name{display:none}
.track{position:relative;height:30px;margin:10px 0 4px;background:var(--surface);border:1px solid var(--line);cursor:pointer;touch-action:manipulation}
.track .played{position:absolute;inset:0 auto 0 0;background:var(--accent-soft);pointer-events:none}
.mark{position:absolute;top:4px;bottom:4px;width:4px;margin-left:-2px;border-radius:2px;background:var(--c,var(--accent));border:0;padding:0;cursor:pointer}
.mark::before{content:"";position:absolute;inset:-6px -8px}
.legend{display:flex;flex-wrap:wrap;gap:4px 6px;margin:8px 0 0;padding:0;list-style:none;font-size:13px;color:var(--muted)}
.legend button{all:unset;cursor:pointer;display:inline-flex;align-items:center;gap:6px;padding:4px 8px;border:1px solid var(--line);border-radius:3px}.legend button:hover{color:var(--text);border-color:var(--line-strong)}
.dot{width:9px;height:9px;border-radius:50%;background:var(--c,var(--accent))}
.cta{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:10px 16px;margin-top:18px;padding:12px 14px;background:var(--surface);border:1px solid var(--line);font-size:14px;color:var(--muted)}
.cta b{color:var(--text)}.cta .get-btn{display:inline-block;background:var(--accent);color:var(--on-accent);padding:8px 14px;text-decoration:none;font:700 13px var(--display);letter-spacing:.06em;text-transform:uppercase;white-space:nowrap}
footer.foot{max-width:1180px;margin:0 auto;padding:20px 16px 36px;border-top:1px solid var(--line);color:var(--dim);font:500 12px/1.7 var(--mono);display:flex;flex-wrap:wrap;gap:6px 18px;justify-content:space-between}
footer.foot a{color:var(--muted)}
input,select,textarea{font:inherit;color:var(--text);background:var(--inset);border:1px solid var(--line-strong);border-radius:0;padding:9px 11px;min-height:40px;width:100%;max-width:100%}
input:focus,select:focus,textarea:focus{outline:2px solid var(--accent);outline-offset:-1px}
label{display:block;font:700 11px var(--mono);letter-spacing:.12em;text-transform:uppercase;color:var(--muted);margin:0 0 6px}
.msg{font-size:14px;color:var(--muted)}.msg:empty{display:none}.msg.bad{color:var(--bad)}.msg.ok{color:var(--ok)}
.empty{padding:40px 16px;text-align:center;color:var(--muted)}
@media (max-width:600px){main{padding:14px 12px 48px}.bar-in{padding:0 12px;gap:8px;height:52px}.brand .full,.brand .sep{display:none}.bar nav a{padding:8px 6px;letter-spacing:.06em}
.grid{grid-template-columns:1fr 1fr;gap:10px}.card .tags span:nth-child(n+2){display:none}.card .body{padding:8px 9px 10px}.card .who,.card .meta{font-size:11px}.card .title{font-size:14px}
.watermark{top:6px;right:8px;font-size:11px}.panel{padding:14px}}
@media (max-width:360px){.grid{grid-template-columns:1fr}}
@media (prefers-reduced-motion:reduce){*{transition:none!important}}
`;

export const MOMENT_COLORS = { kill: "--kill", death: "--death", assist: "--assist", revive: "--revive", match_start: "--match", match_end: "--match", highlight: "--highlight" };

export function badgeHtml(badge) {
  if (!badge?.label) return "";
  const kind = /^[a-z]+$/.test(badge.kind ?? "") ? badge.kind : "";
  return `<span class="badge ${kind}" title="${escapeHtml(badge.label)}">${escapeHtml(badge.label)}</span>`;
}

// The bar at the top of every page: the brand, the hub, and the visitor's own page or a way to sign in. Who's
// signed in is read from a cookie in the page, so the pages themselves are the same for everyone and cache well.
function bar(current) {
  const here = (name) => (current === name ? ` aria-current="page"` : "");
  return `<header class="bar"><div class="bar-in"><a class="brand" href="/hub"><i></i><span class="full">Clip Engine</span><span class="sep">//</span><b>Hub</b></a>
<nav><a href="/hub"${here("hub")}>Clips</a><a href="/bugs"${here("bugs")}>Bugs</a><a href="/hub/signin" id="who"${here("me")}>Sign in</a></nav></div></header>
<script>(function(){var m=document.cookie.match(/(?:^|; )ce_hub_who=([^;]*)/);if(!m)return;var a=document.getElementById("who");a.className="me";a.href="/hub/me";try{a.textContent=decodeURIComponent(m[1])||"You"}catch(e){a.textContent="You"}})();</script>`;
}

export function shell({ title, description, head = "", body, current = "", script = "" }) {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>${escapeHtml(title)}</title><meta name="description" content="${escapeHtml(description)}"><meta name="theme-color" content="#0b0c0e"><link rel="icon" href="${SITE_URL}favicon.ico">${FONTS}${head}<style>${STYLE}</style></head>
<body>${bar(current)}<main>${body}</main>
<footer class="foot"><span>Made with <a href="${SITE_URL}">Clip Engine</a>, free clipping for PC games.</span><span><a href="/hub">Hub</a> · <a href="/bugs">Report a bug</a> · <a href="${DOWNLOAD_URL}">Download Clip Engine</a> · <a href="${SITE_URL}privacy.html">Privacy</a></span></footer>${script}</body></html>`;
}
