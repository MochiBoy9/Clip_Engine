// The hub: the website where anyone can watch the clips players chose to make public, newest or most watched, by
// game, and where players sign in to look after their own clips. Watching needs no account and nothing is pushed:
// a clip is only here while its owner has it public.
//
//   GET  /hub                       everyone's public clips (?sort=new|top, within=week|month, game=, q=)
//   GET  /hub/signin                sign in or up: email and password, Google, or a code from the app
//   GET  /hub/reset, /hub/confirm   the pages behind the links in account emails (the token is after the #)
//   GET  /hub/me                    your clips, profile, badge, email, password and devices (signed in only)
//   GET  /hub/api/feed              a page of clips as JSON, with the cards' HTML  (same filters, cursor=)
//   GET  /hub/api/games             the games with public clips, most first
//   POST /hub/api/signin            { code } or { email, password }; sets the session cookie
//   POST /hub/api/signup            { name, email, password }; sets the session cookie
//   POST /hub/api/signout
//   GET|PATCH /hub/api/me           the account and its clips; { name, handle, hideBadge }
//   PATCH /hub/api/clips/<clip>     { visibility, title, featured }
//   POST /hub/api/password          { password, current } changes the password
//   GET  /hub/api/security          the email, Google, the first device and every sign-in
//   POST /hub/api/email             { email, password } a new email, once its link is opened; POST /hub/api/verify resends
//   DELETE /hub/api/devices/<id>    signs one device out
//   POST /hub/api/report            { code, reason, text } reports a public clip
//
// A sign-in is a session on the account like a PC's (src/library.js), ending after 30 days. Its token is kept in an
// HttpOnly cookie; every change must also carry an "x-hub" header, which another site can't send, so a page
// elsewhere can't act for the visitor.

import { passwordSignIn, signUp } from "./accounts.js";
import { isSquadId, randomCode } from "./codes.js";
import { libraryStub, MIN_PASSWORD, WEB_SESSION_MS } from "./library.js";
import { escapeHtml, pageCsp, shell } from "./look.js";
import { clipCard } from "./pages.js";

const COOKIE = "ce_hub";
const WHO_COOKIE = "ce_hub_who";

function json(body, status = 200, headers = {}) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", "cache-control": "no-store", ...headers } });
}

function page(body, { status = 200, cache = "no-store" } = {}) {
  return new Response(body, {
    status,
    headers: {
      "content-type": "text/html; charset=utf-8",
      "cache-control": cache,
      "x-content-type-options": "nosniff",
      "referrer-policy": "same-origin",
      "content-security-policy": pageCsp("'none'"),
    },
  });
}

function stub(namespace, name) {
  return namespace.get(namespace.idFromName(name));
}

function directory(env, origin, path, body = {}) {
  return stub(env.HANDLES, "handles").fetch(new Request(`${origin}${path}`, { method: "POST", body: JSON.stringify(body) }));
}

export function readCookie(request, name) {
  const header = request.headers.get("cookie") ?? "";
  for (const part of header.split(/;\s*/)) {
    const i = part.indexOf("=");
    if (i > 0 && part.slice(0, i) === name) return part.slice(i + 1);
  }
  return null;
}

// The signed-in account and its token, from the cookie.
export function session(request) {
  const value = readCookie(request, COOKIE);
  const m = value?.match(/^([0-9A-Z]{8})\.([A-Za-z0-9_-]{16,128})$/);
  return m && isSquadId(m[1]) ? { account: m[1], token: m[2] } : null;
}

function who(account) {
  return account?.handle ? `@${account.handle}` : account?.name || "You";
}

export function sessionCookies(account, token) {
  const age = Math.floor(WEB_SESSION_MS / 1000);
  return [
    `${COOKIE}=${account.id}.${token}; Path=/; Max-Age=${age}; HttpOnly; Secure; SameSite=Lax`,
    `${WHO_COOKIE}=${encodeURIComponent(who(account))}; Path=/; Max-Age=${age}; Secure; SameSite=Lax`,
  ];
}

function clearCookies() {
  return [`${COOKIE}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax`, `${WHO_COOKIE}=; Path=/; Max-Age=0; Secure; SameSite=Lax`];
}

function withCookies(response, cookies) {
  const out = new Response(response.body, response);
  for (const cookie of cookies) out.headers.append("set-cookie", cookie);
  return out;
}

// Changes come from the hub's own pages: the x-hub header (which a form or a page on another site can't add) and,
// when the browser says where it comes from, this origin.
function sameSite(request, url) {
  if (request.headers.get("x-hub") !== "1") return false;
  const origin = request.headers.get("origin");
  return !origin || origin === url.origin;
}

async function readBody(request) {
  try {
    const text = await request.text();
    return text.length > 8192 ? null : JSON.parse(text);
  } catch {
    return null;
  }
}

// The filters the hub page and its API take.
function filters(url) {
  const p = url.searchParams;
  const sort = p.get("sort") === "top" ? "top" : "new";
  const within = sort === "top" && ["week", "month", "all"].includes(p.get("within")) ? p.get("within") : sort === "top" ? "week" : "";
  return { sort, within, game: (p.get("game") ?? "").slice(0, 80), q: (p.get("q") ?? "").trim().slice(0, 60) };
}

async function feed(env, origin, filter, cursor = "") {
  const answer = await directory(env, origin, "/feed/list", { ...filter, cursor });
  return answer.json();
}

async function games(env, origin) {
  return (await (await directory(env, origin, "/feed/games")).json()).games ?? [];
}

// The account's own library, called with the session's token.
function library(env, origin, s, method, path, body) {
  return libraryStub(env, s.account).fetch(new Request(`${origin}${path}`, {
    method,
    headers: { authorization: `Bearer ${s.token}`, "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  }));
}

export async function hubRoute(request, env, url) {
  const path = url.pathname.replace(/\/$/, "") || "/hub";
  const method = request.method;
  const origin = url.origin;

  if (method === "GET" || method === "HEAD") {
    if (path === "/hub") {
      const filter = filters(url);
      const [first, list] = await Promise.all([feed(env, origin, filter), games(env, origin)]);
      return page(hubPage(first, list, filter), { cache: "public, max-age=30" });
    }
    if (path === "/hub/signin") return page(signInPage(Boolean(env.GOOGLE_CLIENT_ID && env.GOOGLE_CLIENT_SECRET)));
    if (path === "/hub/reset") return page(linkPage("reset"));
    if (path === "/hub/confirm") return page(linkPage("confirm"));
    if (path === "/hub/api/security") {
      const s = session(request);
      if (!s) return json({ error: "Sign in first." }, 401);
      return signedIn(await library(env, origin, s, "GET", "/security"), false);
    }
    if (path === "/hub/me") {
      if (!session(request)) return Response.redirect(`${origin}/hub/signin?next=me`, 303);
      return page(mePage());
    }
    if (path === "/hub/api/feed") {
      const result = await feed(env, origin, filters(url), (url.searchParams.get("cursor") ?? "").slice(0, 60));
      return json({ ...result, html: (result.clips ?? []).map((c) => clipCard(c)).join("") }, 200, { "cache-control": "public, max-age=30" });
    }
    if (path === "/hub/api/games") return json({ games: await games(env, origin) }, 200, { "cache-control": "public, max-age=60" });
    if (path === "/hub/api/me") {
      const s = session(request);
      if (!s) return json({ error: "Sign in first." }, 401);
      return signedIn(await library(env, origin, s, "GET", "/clips"), true);
    }
    // Your own clips' thumbnails, private ones too, for your hub page.
    const t = path.match(/^\/hub\/api\/clips\/([0-9A-Z]{6})\/thumb$/);
    if (t) {
      const s = session(request);
      if (!s) return new Response(null, { status: 401 });
      return library(env, origin, s, "GET", `/clips/${t[1]}/thumb`);
    }
    return page(notHere(), { status: 404 });
  }

  if (!path.startsWith("/hub/api/")) return json({ error: "Not found." }, 404);
  if (!sameSite(request, url)) return json({ error: "Not allowed." }, 403);
  const body = (await readBody(request)) ?? {};

  if (path === "/hub/api/report" && method === "POST") {
    if (typeof body.code !== "string" || !/^[0-9A-Z]{14}$/.test(body.code)) return json({ error: "Which clip?" }, 400);
    const answer = await directory(env, origin, "/feed/report", { code: body.code, reason: body.reason, text: body.text });
    return json(await answer.json(), answer.status);
  }

  if (path === "/hub/api/signin" && method === "POST") return signIn(env, origin, request, body);
  if (path === "/hub/api/signup" && method === "POST") return withSession(await signUp(env, origin, request, body, true));

  const s = session(request);
  if (!s) return json({ error: "Sign in first." }, 401);
  if (path === "/hub/api/signout" && method === "POST") {
    await library(env, origin, s, "DELETE", "/session");
    return withCookies(json({ ok: true }), clearCookies());
  }
  if (path === "/hub/api/me" && method === "PATCH") {
    const changes = {};
    if (typeof body.hideBadge === "boolean") changes.hideBadge = body.hideBadge;
    if (typeof body.name === "string") changes.name = body.name;
    if (typeof body.handle === "string" || body.handle === null) changes.handle = body.handle;
    return signedIn(await library(env, origin, s, "PATCH", "/account", changes), false);
  }
  if (path === "/hub/api/password" && method === "POST") {
    return signedIn(await library(env, origin, s, "POST", "/password", { password: body.password ?? null, current: body.current }), false);
  }
  if (path === "/hub/api/email" && method === "POST") {
    return signedIn(await library(env, origin, s, "POST", "/email", { email: body.email, password: body.password }), false);
  }
  if (path === "/hub/api/verify" && method === "POST") return signedIn(await library(env, origin, s, "POST", "/verify", {}), false);
  const d = path.match(/^\/hub\/api\/devices\/([0-9a-f]{12})$/);
  if (d && method === "DELETE") return signedIn(await library(env, origin, s, "DELETE", `/devices/${d[1]}`), false);
  const m = path.match(/^\/hub\/api\/clips\/([0-9A-Z]{6})$/);
  if (m && method === "PATCH") {
    const changes = {};
    for (const key of ["visibility", "title", "featured"]) if (body[key] !== undefined) changes[key] = body[key];
    return signedIn(await library(env, origin, s, "PATCH", `/clips/${m[1]}`, changes), false);
  }
  return json({ error: "Not found." }, 404);
}

// A library's answer passed on. A session that has ended clears the cookies; an answer with the account keeps the
// name in the bar current.
async function signedIn(answer, hasClips) {
  if (answer.status === 401) return withCookies(json({ error: "You've been signed out. Sign in again." }, 401), clearCookies());
  const text = await answer.text();
  const response = new Response(text, { status: answer.status, headers: { "content-type": "application/json", "cache-control": "no-store" } });
  if (!answer.ok) return response;
  try {
    const body = JSON.parse(text);
    const account = hasClips ? body.account : body.id ? body : null;
    if (account) {
      response.headers.append("set-cookie",
        `${WHO_COOKIE}=${encodeURIComponent(who(account))}; Path=/; Max-Age=${Math.floor(WEB_SESSION_MS / 1000)}; Secure; SameSite=Lax`);
    }
  } catch {
    // Not JSON: passed on as it is.
  }
  return response;
}

// A code from the app (the same kind that signs another PC in) or the account's email and password.
async function signIn(env, origin, request, body) {
  if (typeof body.code !== "string") return withSession(await passwordSignIn(env, origin, request, body, true));
  const code = normalizeCode(body.code);
  if (!code) return json({ error: "That isn't a sign-in code. It looks like K7QM-2X9D-PA4R-HT." }, 400);
  const answer = await libraryStub(env, code.account).fetch(new Request(`${origin}/join`, {
    method: "POST",
    headers: { "user-agent": request.headers.get("user-agent") ?? "", "cf-ipcountry": request.cf?.country ?? request.headers.get("cf-ipcountry") ?? "" },
    body: JSON.stringify({ device: `web-${randomCode(10)}`, secret: code.secret, web: true }),
  }));
  if (answer.status === 404) return json({ error: "That sign-in code isn't right or has run out. Make a new one in the app." }, 403);
  return withSession(answer);
}

// A library's answer with a new sign-in's token: the token goes in the cookie, not the page.
async function withSession(answer) {
  const reply = await answer.json().catch(() => ({}));
  if (!answer.ok || !reply.token) return json({ error: reply.error ?? "Couldn't sign in." }, answer.ok ? 500 : answer.status);
  return withCookies(json({ account: reply.account }), sessionCookies(reply.account, reply.token));
}

// "K7QM-2X9D-PA4R-HT", or a link ending in one.
export function normalizeCode(text) {
  const code = text.trim().split(/[/#?=]/).pop().toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
  if (!/^[0-9A-Z]{14}$/.test(code) || !isSquadId(code.slice(0, 8))) return null;
  return { account: code.slice(0, 8), secret: code.slice(8) };
}

// ---- pages ----

const HUB_STYLE = `<style>
.intro{display:flex;flex-wrap:wrap;align-items:flex-end;justify-content:space-between;gap:10px 20px}
.tools{display:grid;gap:10px;margin-top:16px}
.tabs{display:flex;gap:2px;overflow-x:auto;scrollbar-width:none}.tabs::-webkit-scrollbar{display:none}
.tabs a{flex:none;white-space:nowrap;padding:8px 12px;color:var(--muted);text-decoration:none;font:700 12px var(--mono);letter-spacing:.1em;text-transform:uppercase;border:1px solid var(--line);background:var(--surface)}
.tabs a[aria-current]{color:var(--on-accent);background:var(--accent);border-color:var(--accent)}
.chips{display:flex;gap:6px;overflow-x:auto;padding-bottom:4px;scrollbar-width:thin;-webkit-overflow-scrolling:touch}
.chips a{flex:none;padding:6px 10px;border:1px solid var(--line);color:var(--muted);text-decoration:none;font-size:13px;background:var(--surface);white-space:nowrap}
.chips a b{color:var(--dim);font:500 11px var(--mono);margin-left:6px}
.chips a[aria-current]{color:var(--text);border-color:var(--accent);box-shadow:inset 0 -2px 0 var(--accent)}
.search{display:flex;gap:8px;max-width:520px}.search input{flex:1;min-width:0}
.row{display:flex;flex-wrap:wrap;align-items:center;gap:10px 14px}
.more{display:flex;justify-content:center;margin-top:22px}
</style>`;

function hubLink(filter, change) {
  const next = { ...filter, ...change };
  const p = new URLSearchParams();
  if (next.sort === "top") p.set("sort", "top");
  if (next.sort === "top" && next.within && next.within !== "week") p.set("within", next.within);
  if (next.game) p.set("game", next.game);
  if (next.q) p.set("q", next.q);
  const text = p.toString();
  return escapeHtml(`/hub${text ? `?${text}` : ""}`);
}

export function hubPage(first, gameList, filter) {
  const current = (yes) => (yes ? ` aria-current="page"` : "");
  const tabs = [
    ["New", { sort: "new", within: "" }],
    ["Top this week", { sort: "top", within: "week" }],
    ["Top this month", { sort: "top", within: "month" }],
    ["Top ever", { sort: "top", within: "all" }],
  ].map(([label, change]) => `<a href="${hubLink(filter, change)}"${current(filter.sort === change.sort && (filter.within || "") === change.within)}>${label}</a>`).join("");
  const chips = [`<a href="${hubLink(filter, { game: "" })}"${current(!filter.game)}>All games</a>`,
    ...gameList.map((g) => `<a href="${hubLink(filter, { game: g.game })}"${current(filter.game.toLowerCase() === g.game.toLowerCase())}>${escapeHtml(g.game)}<b>${g.clips}</b></a>`)].join("");
  const cards = (first.clips ?? []).map((c) => clipCard(c)).join("");
  const nothing = filter.q || filter.game
    ? `<div class="empty">Nothing matches that yet. <a href="/hub">See every clip</a></div>`
    : `<div class="empty">No public clips yet. In Clip Engine, set a clip to Public and it shows up here.</div>`;
  const body = `${HUB_STYLE}
<div class="intro"><div><h1>Clips from everyone</h1><p class="sub" style="margin:0">Only clips players chose to make public. Watching needs no account.</p></div></div>
<div class="tools">
<nav class="tabs" aria-label="Sort">${tabs}</nav>
<nav class="chips" aria-label="Games">${chips}</nav>
<form class="search" action="/hub" method="get" role="search">${filter.sort === "top" ? `<input type="hidden" name="sort" value="top"><input type="hidden" name="within" value="${escapeHtml(filter.within)}">` : ""}${filter.game ? `<input type="hidden" name="game" value="${escapeHtml(filter.game)}">` : ""}
<input type="search" name="q" value="${escapeHtml(filter.q)}" placeholder="Search titles, games or players" aria-label="Search" enterkeyhint="search"><button class="btn ghost" type="submit">Search</button></form>
</div>
${cards ? `<div class="grid" id="grid">${cards}</div>` : nothing}
${first.next ? `<div class="more"><a class="btn ghost" id="more" href="${hubLink(filter, {})}" data-next="${escapeHtml(first.next)}">Load more</a></div>` : ""}`;
  const script = `<script>(function(){var more=document.getElementById("more");if(!more)return;var grid=document.getElementById("grid"),busy=false;
var q=new URLSearchParams(location.search);
function load(){if(busy||!more.dataset.next)return;busy=true;more.textContent="Loading…";q.set("cursor",more.dataset.next);
fetch("/hub/api/feed?"+q.toString()).then(function(r){return r.json()}).then(function(b){grid.insertAdjacentHTML("beforeend",b.html||"");if(b.next){more.dataset.next=b.next;more.textContent="Load more";}else{more.parentNode.remove();io&&io.disconnect();}busy=false;})
.catch(function(){more.textContent="Couldn't load more. Try again";busy=false;});}
more.addEventListener("click",function(e){e.preventDefault();load();});
var io="IntersectionObserver" in window?new IntersectionObserver(function(es){if(es[0].isIntersecting)load();},{rootMargin:"600px"}):null;if(io)io.observe(more);})();</script>`;
  const head = `<meta property="og:title" content="Clip Engine Hub"><meta property="og:description" content="Clips players made with Clip Engine and chose to share.">`;
  return shell({ title: "Hub · Clip Engine", description: "Watch the clips Clip Engine players chose to share, newest or most watched, by game.", head, body, current: "hub", script });
}

const FORM_STYLE = `<style>.two{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,300px),1fr));gap:14px;margin-top:18px;align-items:start}
.two form,.two .panel{display:grid;gap:12px}.two h2{margin:0}.two ol{margin:0;padding-left:20px;color:var(--muted);font-size:14px}.two ol b{color:var(--text)}
.linkish{background:none;border:0;padding:0;color:var(--accent);font:inherit;cursor:pointer;justify-self:start;text-decoration:underline}
#code{font:700 18px var(--mono);letter-spacing:.08em;text-transform:uppercase}</style>`;

export function signInPage(google = false) {
  const body = `${FORM_STYLE}
<h1>Sign in</h1><p class="sub">Your Clip Engine account, the same one the app's Cloud page uses.</p>
<div class="two">
<form class="panel" id="with-password"><h2>With your email</h2>
<div><label for="email">Email</label><input id="email" type="email" autocomplete="username" autocapitalize="none" spellcheck="false" required></div>
<div><label for="password">Password</label><input id="password" type="password" autocomplete="current-password" required></div>
<button class="btn" type="submit">Sign in</button>
<button class="linkish" type="button" id="forgot">Forgot your password?</button>
${google ? `<a class="btn ghost" href="/v1/auth/google">Sign in with Google</a>` : ""}
<div class="msg" role="status"></div></form>
<form class="panel" id="sign-up"><h2>New here?</h2>
<p class="sub" style="margin:0">Make a free account for your cloud library, profile and friends.</p>
<div><label for="new-name">Name</label><input id="new-name" maxlength="32" autocomplete="nickname" required></div>
<div><label for="new-email">Email</label><input id="new-email" type="email" autocomplete="email" autocapitalize="none" spellcheck="false" required></div>
<div><label for="new-password">Password</label><input id="new-password" type="password" autocomplete="new-password" minlength="${MIN_PASSWORD}" required></div>
<button class="btn ghost" type="submit">Make my account</button><div class="msg" role="status"></div></form>
<form class="panel" id="with-code"><h2>With the app</h2>
<ol><li>In Clip Engine, open <b>Cloud</b>.</li><li>Press <b>Open the hub</b> to come here already signed in, or <b>Link another PC</b> and type its code below.</li></ol>
<div><label for="code">Sign-in code</label><input id="code" name="code" autocomplete="one-time-code" autocapitalize="characters" spellcheck="false" placeholder="K7QM-2X9D-PA4R-HT" required></div>
<button class="btn ghost" type="submit">Sign in</button><div class="msg" role="status"></div></form>
</div>`;
  const script = `<script>(function(){
var $=function(id){return document.getElementById(id)};
function go(){var n=new URLSearchParams(location.search).get("next");location.href=n==="hub"?"/hub":"/hub/me";}
function say(form,text,kind){var m=form.querySelector(".msg");m.className="msg"+(kind?" "+kind:"");m.textContent=text;}
function send(form,path,data){var b=form.querySelector("button");say(form,"One moment…");b.disabled=true;
return fetch(path,{method:"POST",headers:{"content-type":"application/json","x-hub":"1"},body:JSON.stringify(data)}).then(function(r){return r.json().then(function(j){if(!r.ok)throw new Error(j.error||"That didn't work.");return j;});})
.then(function(j){b.disabled=false;return j;},function(e){say(form,e.message,"bad");b.disabled=false;throw e;});}
var c=$("with-code"),p=$("with-password"),u=$("sign-up");
c.addEventListener("submit",function(e){e.preventDefault();send(c,"/hub/api/signin",{code:$("code").value}).then(go);});
p.addEventListener("submit",function(e){e.preventDefault();send(p,"/hub/api/signin",{email:$("email").value,password:$("password").value}).then(go);});
u.addEventListener("submit",function(e){e.preventDefault();send(u,"/hub/api/signup",{name:$("new-name").value,email:$("new-email").value,password:$("new-password").value}).then(go);});
$("forgot").addEventListener("click",function(){if(!$("email").value){say(p,"Type your email first, then press this again.","bad");$("email").focus();return;}
send(p,"/v1/auth/forgot",{email:$("email").value}).then(function(){say(p,"If that email has an account, a link to reset the password is on its way. It works for 30 minutes.","ok");});});
var h=location.hash.match(/code=([0-9A-Za-z-]+)/);if(h){history.replaceState(null,"",location.pathname+location.search);$("code").value=h[1];send(c,"/hub/api/signin",{code:h[1]}).then(go);}
})();</script>`;
  return shell({ title: "Sign in · Clip Engine Hub", description: "Sign in to Clip Engine, or make an account.", body, current: "me", script });
}

// The pages behind the links in account emails. The token is after the #, so it never reaches a server log, and
// is taken off the address as soon as the page has it.
export function linkPage(kind) {
  const reset = kind === "reset";
  const body = `${FORM_STYLE}<h1>${reset ? "Pick a new password" : "Confirming your email"}</h1>
<div class="two"><form class="panel" id="f">${reset ? `<p class="sub" style="margin:0">This signs out every PC and browser on your account. Sign in again with the new password.</p>
<div><label for="pw">New password</label><input id="pw" type="password" autocomplete="new-password" minlength="${MIN_PASSWORD}" required></div>
<button class="btn" type="submit">Save my password</button>` : ""}<div class="msg" role="status"></div>
<a class="btn ghost" href="/hub/signin" id="next" hidden>Sign in</a></form></div>`;
  const script = `<script>(function(){
var f=document.getElementById("f"),m=f.querySelector(".msg"),t=(location.hash.match(/t=([0-9A-Za-z._-]+)/)||[])[1]||"";
if(t)history.replaceState(null,"",location.pathname);
function say(text,kind){m.className="msg"+(kind?" "+kind:"");m.textContent=text;}
function post(path,data){return fetch(path,{method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify(data)}).then(function(r){return r.json().then(function(j){if(!r.ok)throw new Error(j.error||"That didn't work.");return j;});});}
function done(text){say(text,"ok");document.getElementById("next").hidden=false;var b=f.querySelector("button");if(b)b.remove();}
if(!t){say("This page needs the link from your email. Open it again from there.","bad");return;}
${reset ? `f.addEventListener("submit",function(e){e.preventDefault();say("Saving…");post("/v1/auth/reset",{token:t,password:document.getElementById("pw").value}).then(function(){done("Your password is changed. Sign in with it now.");}).catch(function(x){say(x.message,"bad");});});`
    : `say("One moment…");post("/v1/auth/confirm",{token:t}).then(function(j){done(j.email+" is confirmed for your Clip Engine account.");}).catch(function(x){say(x.message,"bad");});`}
})();</script>`;
  return shell({ title: `${reset ? "New password" : "Confirm email"} · Clip Engine`, description: "", body, current: "me", script });
}

export function mePage() {
  const body = `<style>.top{display:flex;flex-wrap:wrap;justify-content:space-between;align-items:flex-start;gap:12px}
.cols{display:grid;grid-template-columns:minmax(0,2fr) minmax(0,1fr);gap:16px;margin-top:18px;align-items:start}.side{display:grid;gap:16px}
.list{display:grid;gap:8px;margin-top:10px}.item{display:grid;grid-template-columns:120px minmax(0,1fr) auto;gap:12px;align-items:center;padding:8px;background:var(--inset);border:1px solid var(--line)}
.item img,.item .noimg{width:120px;aspect-ratio:16/9;object-fit:cover;background:#000;display:block}.item .t{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.item select{width:auto;min-height:36px;padding:6px 8px}.item .fine{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.switch{display:flex;gap:10px;align-items:flex-start;font:inherit;text-transform:none;letter-spacing:0;color:var(--text);cursor:pointer}.switch input{width:20px;height:20px;min-height:0;margin:2px 0 0;accent-color:var(--accent);flex:none}
.stack{display:grid;gap:10px}
@media (max-width:820px){.cols{grid-template-columns:1fr}}
@media (max-width:520px){.item{grid-template-columns:88px minmax(0,1fr)}.item img,.item .noimg{width:88px}.item select{grid-column:1/-1;width:100%}}</style>
<div class="top"><div><h1 id="name">Your hub</h1><div class="sub" id="line">Loading…</div></div><button class="btn ghost small" id="signout" type="button">Sign out</button></div>
<div class="cols"><section class="panel"><h2>Your clips</h2>
<p class="sub" style="margin:0"><b>Public</b> clips are on your profile and in the hub. <b>Unlisted</b> ones play for anyone with the link. <b>Private</b> ones are only yours.</p>
<div class="list" id="clips"></div></section>
<div class="side">
<section class="panel stack" id="badge-box" hidden><h2>Badge</h2><label class="switch"><input type="checkbox" id="show-badge"><span>Show my <span id="badge"></span> badge<br><span class="sub">Next to your name on your profile, your clips and the hub. Hiding it changes nothing else about your plan.</span></span></label><div class="msg" id="badge-msg" role="status"></div></section>
<form class="panel stack" id="profile"><h2>Profile</h2><div><label for="pname">Name</label><input id="pname" maxlength="32" required></div>
<div><label for="phandle">Username</label><input id="phandle" maxlength="25" autocapitalize="none" spellcheck="false" placeholder="Leave empty for no profile page"></div>
<button class="btn ghost small" type="submit">Save</button><div class="msg" role="status"></div></form>
<form class="panel stack" id="mail"><h2>Email</h2><p class="sub" style="margin:0" id="mail-line"></p>
<button class="btn ghost small" type="button" id="mail-again" hidden>Send the link again</button>
<div><label for="mail-new">New email</label><input id="mail-new" type="email" autocomplete="email" autocapitalize="none" spellcheck="false" required></div>
<div class="needs-pw"><label for="mail-pw">Your password</label><input id="mail-pw" type="password" autocomplete="current-password"></div>
<button class="btn ghost small" type="submit">Change email</button><div class="msg" role="status"></div></form>
<form class="panel stack" id="pw"><h2>Password</h2><p class="sub" style="margin:0" id="pw-line"></p>
<input type="text" autocomplete="username" id="pw-user" hidden>
<div class="needs-pw"><label for="pw-old">Current password</label><input id="pw-old" type="password" autocomplete="current-password"></div>
<div><label for="pw-new">New password</label><input id="pw-new" type="password" autocomplete="new-password" minlength="${MIN_PASSWORD}" required></div>
<button class="btn ghost small" type="submit">Save password</button><div class="msg" role="status"></div></form>
<section class="panel stack"><h2>Devices</h2><p class="sub" style="margin:0" id="first"></p><div class="list" id="devices"></div><div class="msg" id="dev-msg" role="status"></div></section>
</div></div>`;
  const script = `<script>(function(){
var me=null,$=function(id){return document.getElementById(id)};
function api(method,path,data){return fetch("/hub/api/"+path,{method:method,headers:{"content-type":"application/json","x-hub":"1"},body:data===undefined?undefined:JSON.stringify(data)})
.then(function(r){return r.json().catch(function(){return{}}).then(function(j){if(r.status===401){location.href="/hub/signin?next=me";throw new Error("Signed out");}if(!r.ok)throw new Error(j.error||"That didn't work.");return j;});});}
function say(el,text,kind){el.className="msg"+(kind?" "+kind:"");el.textContent=text;}
function esc(s){var d=document.createElement("div");d.textContent=s==null?"":String(s);return d.innerHTML;}
function len(s){s=Math.round(s);return Math.floor(s/60)+":"+String(s%60).padStart(2,"0");}
function account(a){me=a;$("name").textContent=a.name;var bits=[a.handle?'<a href="/u/'+esc(a.handle)+'">@'+esc(a.handle)+'</a>':"No profile page yet",esc(a.planLabel)+" plan"];
if(a.badge&&!a.hideBadge)bits.splice(1,0,'<span class="badge '+esc(a.badge.kind)+'">'+esc(a.badge.label)+'</span>');
$("line").innerHTML=bits.join(" · ");$("badge-box").hidden=!a.badge;if(a.badge){$("badge").textContent=a.badge.label;$("show-badge").checked=!a.hideBadge;}
$("pname").value=a.name;$("phandle").value=a.handle||"";$("pw-user").value=a.email||"";
$("mail-line").textContent=!a.email?"Add an email so you can sign in with it and get back in if you lose this PC.":a.emailVerified?a.email+" is confirmed."+(a.google?" You can also sign in with Google.":""):a.email+" isn't confirmed yet. Open the link we emailed you.";
$("mail-again").hidden=!a.email||a.emailVerified;
Array.prototype.forEach.call(document.querySelectorAll(".needs-pw"),function(el){el.hidden=!a.hasPassword;});
$("pw-line").textContent=a.hasPassword?"Change the password you sign in with.":"You sign in with Google. Add a password to sign in with your email too.";}
function when(ms){return new Date(ms).toLocaleDateString(undefined,{year:"numeric",month:"short",day:"numeric"});}
function what(d){var a=d.agent||"",b=/Edg\\//.test(a)?"Edge":/OPR\\//.test(a)?"Opera":/Firefox\\//.test(a)?"Firefox":/Chrome\\//.test(a)?"Chrome":/Safari\\//.test(a)?"Safari":"";
var os=/Windows/.test(a)?"Windows":/Android/.test(a)?"Android":/iPhone|iPad/.test(a)?"iPhone or iPad":/Mac OS/.test(a)?"Mac":/Linux/.test(a)?"Linux":"";
return d.kind==="app"?"Clip Engine on a PC":(b||"A browser")+(os?" on "+os:"");}
function devices(s){$("first").textContent=s.firstDevice?"First used from "+what(s.firstDevice)+(s.firstDevice.country?" in "+s.firstDevice.country:"")+", "+when(s.firstDevice.at)+".":"";
$("devices").innerHTML=s.devices.map(function(d){return '<div class="item" style="grid-template-columns:minmax(0,1fr) auto" data-id="'+esc(d.id)+'"><div style="min-width:0"><div class="t">'+esc(what(d))+(d.current?" (this one)":"")+'</div><div class="fine">'+(d.country?esc(d.country)+" · ":"")+"Signed in "+when(d.added)+" · last used "+when(d.seen)+'</div></div>'+(d.current?"":'<button class="btn ghost small" type="button">Sign out</button>')+'</div>';}).join("");}
$("devices").addEventListener("click",function(e){var row=e.target.closest(".item");if(!row||e.target.tagName!=="BUTTON")return;e.target.disabled=true;
api("DELETE","devices/"+row.dataset.id).then(function(s){devices(s);say($("dev-msg"),"Signed out.","ok");}).catch(function(x){e.target.disabled=false;say($("dev-msg"),x.message,"bad");});});
function clips(list){var box=$("clips");list=list.filter(function(c){return c.uploaded});if(!list.length){box.innerHTML='<div class="empty">No clips in your cloud library yet. Press the link button on a clip in the app to put it here.</div>';return;}
box.innerHTML=list.map(function(c){var link="/c/"+c.code;return '<div class="item" data-id="'+esc(c.id)+'">'+(c.thumb?'<img loading="lazy" src="/hub/api/clips/'+esc(c.id)+'/thumb" alt="" onerror="this.replaceWith(Object.assign(document.createElement(\\'div\\'),{className:\\'noimg\\'}))">':'<div class="noimg"></div>')+
'<div style="min-width:0"><a class="t" style="display:block;color:inherit"'+(c.visibility==="private"?"":' href="'+link+'"')+'>'+esc(c.title||c.game+" clip")+'</a><div class="fine">'+esc(c.game)+" · "+len(c.seconds)+(c.views?" · "+c.views+(c.views===1?" view":" views"):"")+'</div></div>'+
'<select aria-label="Who can watch">'+["public","unlisted","private"].map(function(v){return '<option value="'+v+'"'+(c.visibility===v?" selected":"")+'>'+v.charAt(0).toUpperCase()+v.slice(1)+'</option>'}).join("")+'</select></div>';}).join("");}
$("clips").addEventListener("change",function(e){var row=e.target.closest(".item");if(!row)return;e.target.disabled=true;
api("PATCH","clips/"+row.dataset.id,{visibility:e.target.value}).then(function(c){e.target.disabled=false;var t=row.querySelector(".t");if(c.visibility==="private")t.removeAttribute("href");else t.setAttribute("href","/c/"+c.code);})
.catch(function(x){e.target.disabled=false;alert(x.message);});});
$("show-badge").addEventListener("change",function(e){var m=$("badge-msg");say(m,"Saving…");api("PATCH","me",{hideBadge:!e.target.checked}).then(function(a){account(a);say(m,a.hideBadge?"Your badge is hidden.":"Your badge shows.","ok");}).catch(function(x){e.target.checked=!e.target.checked;say(m,x.message,"bad");});});
$("profile").addEventListener("submit",function(e){e.preventDefault();var m=e.target.querySelector(".msg");say(m,"Saving…");api("PATCH","me",{name:$("pname").value,handle:$("phandle").value.trim().replace(/^@/,"")}).then(function(a){account(a);say(m,"Saved.","ok");}).catch(function(x){say(m,x.message,"bad");});});
$("pw").addEventListener("submit",function(e){e.preventDefault();var m=e.target.querySelector(".msg");say(m,"Saving…");api("POST","password",{password:$("pw-new").value,current:$("pw-old").value}).then(function(a){account(a);$("pw-new").value="";$("pw-old").value="";say(m,"Password saved.","ok");}).catch(function(x){say(m,x.message,"bad");});});
$("mail").addEventListener("submit",function(e){e.preventDefault();var m=e.target.querySelector(".msg");say(m,"Sending…");api("POST","email",{email:$("mail-new").value,password:$("mail-pw").value}).then(function(a){account(a);$("mail-pw").value="";say(m,a.pendingEmail?"Open the link we sent to "+a.pendingEmail+" to switch to it.":"Sent.","ok");}).catch(function(x){say(m,x.message,"bad");});});
$("mail-again").addEventListener("click",function(){var m=$("mail").querySelector(".msg");api("POST","verify",{}).then(function(j){say(m,j.sent?"Sent. Check your inbox.":"An email went out a moment ago. Check your inbox, or try again in a minute.","ok");}).catch(function(x){say(m,x.message,"bad");});});
api("GET","security").then(devices).catch(function(){});
$("signout").addEventListener("click",function(){api("POST","signout",{}).catch(function(){}).then(function(){location.href="/hub";});});
api("GET","me").then(function(b){account(b.account);clips(b.clips);}).catch(function(x){$("line").textContent=x.message;});
})();</script>`;
  return shell({ title: "Your hub · Clip Engine", description: "Your clips on Clip Engine.", body, current: "me", script });
}

function notHere() {
  return shell({ title: "Not found · Clip Engine Hub", description: "", body: `<h1>Nothing here</h1><p><a class="btn ghost" href="/hub">Back to the hub</a></p>`, current: "hub" });
}
