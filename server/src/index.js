// Clip Engine's online service, on Cloudflare Workers: squad sync and the cloud library.
//
// Squads (one Durable Object each, src/squad.js):
//   POST /v1/squads                 create a squad            { name, squad?, device }
//   POST /v1/join                   join with an invite code  { invite, name, device }
//   GET  /v1/squads/<id>/socket     the squad's live channel (WebSocket, Authorization: Bearer <token>)
//   GET  /v1/squads/<id>/squad      members, invite code and storage
//   GET  /v1/squads/<id>/library    shared moments with every view, newest first
//   PUT/GET /v1/squads/<id>/moments/<moment>/views/<member|me>/(video|thumb)
//   POST /v1/squads/<id>/moments/<moment>/star, POST /invite/rotate, PATCH|DELETE /members/me
//
// Cloud library (one Durable Object per account, src/library.js; signing up and in, src/accounts.js):
//   POST /v1/accounts               make an account           { name, email, password, device }
//   POST /v1/accounts/signin        sign a PC in              { email, password, device } or { code, device }
//   POST /v1/auth/forgot|reset|confirm, GET /v1/auth/google   password resets, email links, Sign in with Google
//   GET /security, POST /email, POST /password, POST /verify, DELETE /devices/<id>   email, password and devices
//   GET|PATCH /v1/accounts/<id>/account, POST /link, GET|POST /clips,
//   PATCH|DELETE /clips/<clip>, PUT|GET /clips/<clip>/(video|thumb)
//   POST /clips/<clip>/video/parts, PUT /parts/<n>, POST /parts/done   a clip over 100 MB, in parts (Pro and Ultra)
//   POST|DELETE /plan               put a subscription's license key on the account, or take it off { key }
//   GET|POST /friends, POST /friends/<account>/accept, DELETE /friends/<account>   { handle } or { code }
//   POST|DELETE /presence           online for friends, with the game and a squad invite { game, invite? }
//   POST /v1/admin/plan             set an account's plan     { account, plan } (Authorization: Bearer ADMIN_TOKEN)
//
// Special keys (for the operator's key maker; every route needs Authorization: Bearer ADMIN_TOKEN):
//   GET  /v1/admin/grants           every key with its status, PCs and end date
//   POST /v1/admin/grants           make keys  { count, plan, role, minutes (or days), endsAt, extraStorageGb, themes,
//                                   lines, maxDevices, perks, credits, reason, showReason, note }
//   PATCH /v1/admin/grants/<key>    change one { any of those, revoked, resetDevices, restart }
//   GET  /v1/admin/messages         messages and bug reports from special keys, open ones first
//   PATCH /v1/admin/messages/<id>   answer one or mark it done { reply?, done? }
//
// A special key's direct line (its perks, from the key maker):
//   POST /v1/contact                send the operator a message or a bug report { key, device, kind, text, name? }
//   POST /v1/contact/list           this key's messages with the operator's answers { key, device }
//
// Plans (src/plans.js):
//   GET  /v1/plans                  Free, Pro and Ultra: price, storage, cloud resolution and clip size; and what donations give
//   POST /v1/license                check a Polar or special key { key, device, credit? } and list or unlist its name in the credits.
//                                   A donation's key, or any key of someone who donated, also answers supporter and donated (cents)
//   GET  /v1/credits                the names in the credits: subscribers, supporters from $5, and special keys the operator lets in
//
// Inviting friends (one Durable Object, src/referrals.js):
//   POST /v1/referrals              this PC's invite code and its secret        { pc, device, name? }
//   GET  /v1/referrals/<code>       friends so far and the Pro keys earned      (Authorization: Bearer <secret>, ?pc=&device=)
//   POST /v1/referrals/activate     Clip Engine started on a PC                 { pc, device, fresh, code? }
//
// The hub (src/hub.js): everyone's public clips, and signing in on the web to look after your own:
//   GET /hub, /hub/signin, /hub/me and /hub/api/...  (see src/hub.js)
//   GET  /v1/admin/reports          reported clips (Authorization: Bearer ADMIN_TOKEN)
//   POST /v1/admin/hide             hide a clip from the hub, or bring it back { code, hidden, clearReports }
//
// Bug reports (one Durable Object, src/bugs.js): anyone reports a bug, a change, a design choice or an idea
//   GET /bugs, /bugs/<id>           the board and one report
//   GET|POST /v1/bugs, GET /v1/bugs/<id>, PATCH /v1/admin/bugs/<id>   (see src/bugs.js)
//
// Public pages:
//   GET /c/<code>                   a shared clip's web player, with its moments on the timeline
//   GET /c/<code>/video|thumb|.json the clip itself
//   GET /c/<code>/oembed            the clip's oEmbed, which names Clip Engine in Discord's preview
//   GET /u/<handle>                 a player's profile with their public clips
//   GET /s/<invite>                 a squad's join page, for the "Join my squad" button on Discord
//   GET /r/<code>                   a friend's invite to Clip Engine, with the download
//
// See README.md for the messages on the squad socket.

import { authRoute, passwordSignIn, signUp } from "./accounts.js";
import { BugBoard, bugsRoute } from "./bugs.js";
import { isSquadId, normalizeInvite, randomCode, sameSecret, SQUAD_ID_LENGTH } from "./codes.js";
import { Handles, normalizeHandle } from "./handles.js";
import { hubRoute } from "./hub.js";
import { pageCsp } from "./look.js";
import { CLIP_CODE_LENGTH, Library, libraryStub } from "./library.js";
import { formatInvite } from "./codes.js";
import { clipOembed, clipPage, invitePage, notFoundPage, profilePage, referralPage } from "./pages.js";
import { checkLicense, creditGroup, donationSettings, normalizeGrant, PLANS, planJson } from "./plans.js";
import { isMediaPointer, serveMedia } from "./media.js";
import { normalizeReferral, Referrals } from "./referrals.js";
import { Squad } from "./squad.js";

export { BugBoard, Handles, Library, Referrals, Squad };

const VERSION = 2;
const ALPHABET = /^[0-9A-Z]+$/;

function json(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function html(body, status = 200) {
  return new Response(body, {
    status,
    headers: {
      "content-type": "text/html; charset=utf-8",
      "cache-control": status === 200 ? "public, max-age=60" : "no-store",
      "x-content-type-options": "nosniff",
      "content-security-policy": pageCsp("*"),
    },
  });
}

function stub(namespace, name) {
  return namespace.get(namespace.idFromName(name));
}

// The request, sent on to a Durable Object with the rest of the path. Built from the request
// itself, so an upload streams through with its length and never sits in the Worker's memory.
function forward(target, request, path, { body, headers = {} } = {}) {
  const url = new URL(request.url);
  url.pathname = path;
  const sent = body === undefined
    ? new Request(url.toString(), request)
    : new Request(url.toString(), { method: request.method, headers: request.headers, body });
  for (const [name, value] of Object.entries(headers)) sent.headers.set(name, value);
  // The country Cloudflare saw, for an account's device list (src/library.js, clientOf).
  if (request.cf?.country) sent.headers.set("cf-ipcountry", request.cf.country);
  return target.fetch(sent);
}

async function readBody(request) {
  try {
    return await request.json();
  } catch {
    return null;
  }
}

// Paths a Durable Object only takes from the Worker itself, never straight from a client.
function internal(path) {
  return /^\/(create|join|admin|public|peer|web|auth)(\/|$)/.test(path);
}

// "K7QM-2X9D-PA4R-HT" (or a link ending in it): the account id and the one-time secret.
function normalizeSignIn(text) {
  if (typeof text !== "string") return null;
  const code = text.trim().split(/[/#?=]/).pop().toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
  if (code.length !== SQUAD_ID_LENGTH + 6 || !ALPHABET.test(code)) return null;
  return { account: code.slice(0, SQUAD_ID_LENGTH), secret: code.slice(SQUAD_ID_LENGTH) };
}

// The operator's routes and the key maker: Authorization: Bearer ADMIN_TOKEN (a Worker secret).
function admin(request, env) {
  const token = (request.headers.get("authorization") ?? "").replace(/^Bearer /, "");
  return Boolean(env.ADMIN_TOKEN) && token.length > 0 && sameSecret(token, env.ADMIN_TOKEN);
}

// The operator's special keys. The key maker that calls these is a file on the operator's own PC, so these answer
// any origin; without the token they do nothing.
async function grantRoute(request, env, url, path, method) {
  if (method === "OPTIONS") return new Response(null, { status: 204 });
  if (!admin(request, env)) return json({ error: "Not allowed." }, 403);
  if (path === "/v1/admin/grants" && method === "GET") {
    return stub(env.HANDLES, "handles").fetch(new Request(`${url.origin}/grant/list`, { method: "POST", body: "{}" }));
  }
  if (path === "/v1/admin/grants" && method === "POST") return directory(env, url, "/grant/create", (await readBody(request)) ?? {});
  const m = path.match(/^\/v1\/admin\/grants\/([^/]+)$/);
  if (m && method === "PATCH") {
    const code = normalizeGrant(decodeSegment(m[1]) ?? "");
    if (!code) return json({ error: "That isn't a special key." }, 400);
    return directory(env, url, "/grant/update", { ...((await readBody(request)) ?? {}), code });
  }
  if (path === "/v1/admin/messages" && method === "GET") return directory(env, url, "/grant/inbox", {});
  const n = path.match(/^\/v1\/admin\/messages\/(\d{1,12})$/);
  if (n && method === "PATCH") {
    const body = (await readBody(request)) ?? {};
    return directory(env, url, "/grant/inbox/update", { reply: body.reply, done: body.done, id: Number(n[1]) });
  }
  // The hub's reported clips: list them, and hide one (or bring it back).
  if (path === "/v1/admin/reports" && method === "GET") return directory(env, url, "/feed/reports", {});
  if (path === "/v1/admin/hide" && method === "POST") return directory(env, url, "/feed/hide", (await readBody(request)) ?? {});
  return json({ error: "Not found." }, 404);
}

function withCors(response) {
  const out = new Response(response.body, response);
  out.headers.set("access-control-allow-origin", "*");
  out.headers.set("access-control-allow-methods", "GET, POST, PATCH, OPTIONS");
  out.headers.set("access-control-allow-headers", "authorization, content-type");
  out.headers.set("access-control-max-age", "600");
  out.headers.set("cache-control", "no-store");
  return out;
}

function directory(env, url, path, body) {
  return stub(env.HANDLES, "handles").fetch(new Request(`${url.origin}${path}`, { method: "POST", body: JSON.stringify(body) }));
}

// Link-preview fetchers (Discord, Slack, X...) open a clip's page to build its embed; that isn't a view.
const PREVIEW_AGENTS = /bot|crawler|spider|preview|facebookexternalhit|embedly|whatsapp|telegram|slack|discord|skype/i;

// A path segment, decoded; null when it isn't valid percent-encoding (so "/u/%E0" is a 404, not a crash).
function decodeSegment(text) {
  try {
    return decodeURIComponent(text);
  } catch {
    return null;
  }
}

async function clipRoute(request, env, code, rest) {
  const account = code.slice(0, SQUAD_ID_LENGTH);
  const clip = code.slice(SQUAD_ID_LENGTH);
  const library = libraryStub(env, account);
  if (rest === "/video" || rest === "/thumb") return forward(library, request, `/public/clip/${clip}${rest}`);
  const origin = new URL(request.url).origin;
  const viewed = rest === "" && request.method === "GET" && !PREVIEW_AGENTS.test(request.headers.get("user-agent") ?? "") ? "?view=1" : "";
  const answer = await library.fetch(new Request(`${origin}/public/clip/${clip}${viewed}`));
  if (!answer.ok) return rest === "" ? html(notFoundPage(), 404) : json({ error: "That clip isn't shared." }, 404);
  const data = await answer.json();
  if (rest === ".json") return json(data);
  if (rest === "/oembed") {
    const response = json(clipOembed(data, origin));
    response.headers.set("cache-control", "public, max-age=300");
    return response;
  }
  return html(clipPage(data, origin));
}

async function profileRoute(request, env, handle, asJson) {
  const origin = new URL(request.url).origin;
  const found = await stub(env.HANDLES, "handles").fetch(new Request(`${origin}/lookup?handle=${encodeURIComponent(handle)}`));
  if (!found.ok) return asJson ? json({ error: "No one has that name." }, 404) : html(notFoundPage(), 404);
  const { account } = await found.json();
  const answer = await libraryStub(env, account).fetch(new Request(`${origin}/public/profile`));
  if (!answer.ok) return asJson ? json({ error: "No one has that name." }, 404) : html(notFoundPage(), 404);
  const profile = await answer.json();
  return asJson ? json(profile) : html(profilePage(profile, origin));
}

export default {
  // A clip's video or thumbnail comes back from its Durable Object as a pointer; the bytes are
  // served here (src/media.js).
  async fetch(request, env) {
    const response = await route(request, env);
    return isMediaPointer(response) ? serveMedia(request, env, response) : response;
  },
};

// Per-address limits (Workers Rate Limiting, wrangler.toml [[ratelimits]]), on top of Cloudflare's own DDoS
// protection: making accounts and squads, sign-in and invite codes, key checks and special-key messages get
// AUTH_LIMIT; every other change (uploads, friends, edits, the key maker's) gets WRITE_LIMIT. Reads aren't limited
// here. Without the bindings (tests, wrangler dev) nothing is limited.
const AUTH_ROUTES = /^\/(v1\/(accounts|accounts\/signin|auth\/(forgot|reset|confirm)|squads|join|license|contact|contact\/list|referrals|referrals\/activate|bugs)|hub\/api\/(signin|signup|report))$/;

async function overLimit(request, env, path, method) {
  const sensitive = method === "POST" && AUTH_ROUTES.test(path);
  const limiter = sensitive ? env.AUTH_LIMIT : method !== "GET" && method !== "HEAD" && method !== "OPTIONS" ? env.WRITE_LIMIT : null;
  if (!limiter?.limit) return false;
  const address = request.headers.get("cf-connecting-ip") ?? "unknown";
  try {
    const { success } = await limiter.limit({ key: `${sensitive ? "auth" : "write"}:${address}` });
    return !success;
  } catch {
    return false;
  }
}

async function route(request, env) {
  {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

    if (await overLimit(request, env, path, method)) {
      return new Response(JSON.stringify({ error: "Too many requests from this address. Wait a minute and try again." }), {
        status: 429, headers: { "content-type": "application/json", "retry-after": "60" },
      });
    }

    // Someone opening the service's address in a browser lands on the hub.
    if (path === "/" && method === "GET" && (request.headers.get("accept") ?? "").includes("text/html")) {
      return Response.redirect(`${url.origin}/hub`, 302);
    }
    if (path === "/" || path === "/v1/health") return json({ ok: true, service: "clip-engine", version: VERSION });

    // ---- the hub (src/hub.js) ----
    if (path === "/hub" || path.startsWith("/hub/")) return hubRoute(request, env, url);

    // ---- bug reports (src/bugs.js) ----
    if (path === "/bugs" || path.startsWith("/bugs/") || path === "/v1/bugs" || path.startsWith("/v1/bugs/") || path.startsWith("/v1/admin/bugs/")) {
      return bugsRoute(request, env, url, admin(request, env));
    }

    // ---- squads ----
    if (path === "/v1/squads" && method === "POST") {
      const id = randomCode(SQUAD_ID_LENGTH);
      return forward(stub(env.SQUADS, id), request, "/create", { headers: { "x-squad-id": id } });
    }
    if (path === "/v1/join" && method === "POST") {
      const body = await readBody(request);
      const invite = normalizeInvite(body?.invite);
      if (!invite) return json({ error: "That isn't a squad invite code." }, 400);
      return forward(stub(env.SQUADS, invite.squadId), request, "/join", {
        body: JSON.stringify({ ...body, secret: invite.secret }),
        headers: { "x-squad-id": invite.squadId },
      });
    }
    let m = path.match(/^\/v1\/squads\/([0-9A-Za-z]+)(\/.*)$/);
    if (m && isSquadId(m[1].toUpperCase()) && !internal(m[2])) {
      const id = m[1].toUpperCase();
      return forward(stub(env.SQUADS, id), request, m[2], { headers: { "x-squad-id": id } });
    }

    // ---- cloud library ----
    if (path === "/v1/accounts" && method === "POST") return signUp(env, url.origin, request, await readBody(request));
    if (path === "/v1/accounts/signin" && method === "POST") {
      const body = await readBody(request);
      if (typeof body?.email === "string") return passwordSignIn(env, url.origin, request, body);
      const code = normalizeSignIn(body?.code);
      if (!code) return json({ error: "That isn't a sign-in code." }, 400);
      return forward(libraryStub(env, code.account), request, "/join", {
        body: JSON.stringify({ device: body?.device, secret: code.secret }),
      });
    }
    if (path.startsWith("/v1/auth/")) return authRoute(request, env, url);
    m = path.match(/^\/v1\/accounts\/([0-9A-Za-z]+)(\/.*)$/);
    if (m && isSquadId(m[1].toUpperCase()) && !internal(m[2])) return forward(libraryStub(env, m[1].toUpperCase()), request, m[2]);
    if (/^\/v1\/admin\/(grants|messages|reports|hide)(\/|$)/.test(path)) return withCors(await grantRoute(request, env, url, path, method));
    if (path.startsWith("/v1/admin/") && !admin(request, env)) return json({ error: "Not allowed." }, 403);
    if (path === "/v1/admin/plan" && method === "POST") {
      const body = await readBody(request);
      if (!isSquadId(body?.account)) return json({ error: "Which account?" }, 400);
      return forward(libraryStub(env, body.account), request, "/admin/plan", { body: JSON.stringify({ plan: body.plan }) });
    }

    // ---- inviting friends ----
    if (path.startsWith("/v1/referrals")) {
      const ip = request.headers.get("cf-connecting-ip") ?? "";
      const referrals = (route, body) => stub(env.REFERRALS, "referrals").fetch(new Request(`${url.origin}/${route}`, {
        method: "POST", body: JSON.stringify({ ...body, ip, origin: url.origin }),
      }));
      if (path === "/v1/referrals" && method === "POST") {
        const body = (await readBody(request)) ?? {};
        return referrals("create", { pc: body.pc, device: body.device, name: body.name });
      }
      if (path === "/v1/referrals/activate" && method === "POST") {
        const body = (await readBody(request)) ?? {};
        return referrals("activate", { pc: body.pc, device: body.device, fresh: body.fresh === true, code: body.code });
      }
      m = path.match(/^\/v1\/referrals\/([^/]+)$/);
      if (m && method === "GET") {
        const secret = (request.headers.get("authorization") ?? "").replace(/^Bearer /, "");
        return referrals("status", {
          code: decodeSegment(m[1]) ?? "", secret, pc: url.searchParams.get("pc") ?? "", device: url.searchParams.get("device") ?? "",
        });
      }
      return json({ error: "Not found." }, 404);
    }

    // ---- plans ----
    if (path === "/v1/plans" && method === "GET") {
      const donations = donationSettings(env);
      return json({
        plans: Object.keys(PLANS).map((name) => planJson(env, name)),
        open: Boolean(env.POLAR_ORGANIZATION_ID),
        donations: { open: Boolean(env.POLAR_ORGANIZATION_ID && env.POLAR_DONATION_BENEFIT_ID), ...donations },
      });
    }
    if (path === "/v1/license" && method === "POST") {
      const body = await readBody(request);
      const check = await checkLicense(env, body?.key, typeof body?.device === "string" ? body.device : "");
      if (!check.ok) return json({ error: check.error, rejected: check.rejected }, check.rejected ? 403 : 503);
      // The credits thank people who pay: subscribers, and supporters whose donations come to $5 or more (under
      // their plan if they subscribe too). A special key (made in the key maker, not bought) is listed only when the
      // operator ticked "in the credits" for it, under its role.
      const credit = typeof body?.credit === "string" ? body.credit : "";
      const credited = check.grant ? Boolean(check.credits) : check.plan !== "free" || Boolean(check.donation?.credits);
      const group = check.grant ? creditGroup(check) : check.plan !== "free" ? check.plan : "supporter";
      if (check.grant && !check.credits) {
        await directory(env, url, "/credits/remove", { license: check.license });
      } else if (!credited && check.donation && check.donation.cents !== null) {
        // Refunded below $5: the name comes off. (When the amount couldn't be looked up, it's left as it was.)
        await directory(env, url, "/credits/remove", { license: check.license });
      } else if (credited && body?.credit !== undefined) {
        await directory(env, url, "/credits/set", { license: check.license, name: credit, plan: group });
      }
      return json({
        ...planJson(env, check.plan, check.features, check.role),
        display: check.display,
        expires: check.expires,
        renews: check.renewal?.renews ?? null,
        ending: Boolean(check.renewal?.ending),
        grant: Boolean(check.grant),
        perks: check.perks ?? [],
        credits: credited,
        reason: check.reason ?? "",
        supporter: Boolean(check.donation),
        donated: check.donation?.cents ?? null,
      });
    }
    if ((path === "/v1/contact" || path === "/v1/contact/list") && method === "POST") {
      const body = await readBody(request);
      const code = normalizeGrant(body?.key);
      if (!code) return json({ error: "Only special keys with a direct line or bug reports can do that." }, 403);
      const device = typeof body?.device === "string" ? body.device.slice(0, 80) : "";
      if (path === "/v1/contact/list") return directory(env, url, "/grant/message/list", { code, device });
      return directory(env, url, "/grant/message/send", { code, device, kind: body?.kind, text: body?.text, name: body?.name });
    }
    if (path === "/v1/credits" && method === "GET") {
      const answer = await stub(env.HANDLES, "handles").fetch(new Request(`${url.origin}/credits`));
      const response = new Response(answer.body, answer);
      response.headers.set("cache-control", "public, max-age=300");
      response.headers.set("access-control-allow-origin", "*");
      return response;
    }

    // ---- public pages ----
    if (method === "GET" || method === "HEAD") {
      m = path.match(/^\/c\/([0-9A-Za-z]+?)(\/video|\/thumb|\/oembed|\.json)?$/);
      if (m && m[1].length === SQUAD_ID_LENGTH + CLIP_CODE_LENGTH && ALPHABET.test(m[1].toUpperCase())) {
        return clipRoute(request, env, m[1].toUpperCase(), m[2] ?? "");
      }
      m = path.match(/^\/u\/([^/]+?)(\.json)?$/);
      const handle = m && normalizeHandle(decodeSegment(m[1]) ?? "");
      if (handle) return profileRoute(request, env, handle, Boolean(m[2]));
      m = path.match(/^\/s\/([^/]+)$/);
      if (m) {
        const invite = normalizeInvite(decodeSegment(m[1]) ?? "");
        if (!invite) return html(notFoundPage(), 404);
        const answer = await stub(env.SQUADS, invite.squadId).fetch(
          new Request(`${url.origin}/public/invite?secret=${invite.secret}`, { headers: { "x-squad-id": invite.squadId } }));
        if (!answer.ok) return html(notFoundPage(), 404);
        return html(invitePage(await answer.json(), formatInvite(invite.squadId, invite.secret)));
      }
      m = path.match(/^\/r\/([^/]+)$/);
      const referral = m && normalizeReferral(decodeSegment(m[1]) ?? "");
      if (referral) {
        const answer = await stub(env.REFERRALS, "referrals").fetch(new Request(`${url.origin}/click`, {
          method: "POST",
          body: JSON.stringify({
            code: referral, ip: request.headers.get("cf-connecting-ip") ?? "",
            record: method === "GET" && !PREVIEW_AGENTS.test(request.headers.get("user-agent") ?? ""),
          }),
        }));
        if (answer.ok) {
          const page = html(referralPage(await answer.json()));
          page.headers.set("cache-control", "no-store");
          return page;
        }
      }
      if (path.startsWith("/c/") || path.startsWith("/u/") || path.startsWith("/s/") || path.startsWith("/r/")) return html(notFoundPage(), 404);
    }

    return json({ error: "Not found." }, 404);
  }
}
