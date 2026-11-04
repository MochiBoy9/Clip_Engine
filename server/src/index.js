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
// Cloud library (one Durable Object per account, src/library.js):
//   POST /v1/accounts               make an account           { name, device }
//   POST /v1/accounts/signin        sign another PC in        { code, device }
//   GET|PATCH /v1/accounts/<id>/account, POST /link, GET|POST /clips,
//   PATCH|DELETE /clips/<clip>, PUT|GET /clips/<clip>/(video|thumb)
//   POST|DELETE /plan               put a subscription's license key on the account, or take it off { key }
//   GET|POST /friends, POST /friends/<account>/accept, DELETE /friends/<account>   { handle } or { code }
//   POST|DELETE /presence           online for friends, with the game and a squad invite { game, invite? }
//   POST /v1/admin/plan             set an account's plan     { account, plan } (Authorization: Bearer ADMIN_TOKEN)
//
// Special keys (for the operator's key maker; every route needs Authorization: Bearer ADMIN_TOKEN):
//   GET  /v1/admin/grants           every key with its status, PCs and end date
//   POST /v1/admin/grants           make keys  { count, plan, days, extraStorageGb, themes, lines, maxDevices, note }
//   PATCH /v1/admin/grants/<key>    change one { any of those, revoked, resetDevices, restart }
//
// Plans (src/plans.js):
//   GET  /v1/plans                  Free, Pro and Ultra: price, storage and resolution
//   POST /v1/license                check a Polar or special key { key, device, credit? } and list or unlist its name in the credits
//   GET  /v1/credits                the names subscribers asked to have in the credits
//
// Public pages:
//   GET /c/<code>                   a shared clip's web player, with its moments on the timeline
//   GET /c/<code>/video|thumb|.json the clip itself
//   GET /c/<code>/oembed            the clip's oEmbed, which names Clip Engine in Discord's preview
//   GET /u/<handle>                 a player's profile with their public clips
//   GET /s/<invite>                 a squad's join page, for the "Join my squad" button on Discord
//
// See README.md for the messages on the squad socket.

import { isSquadId, normalizeInvite, randomCode, sameSecret, SQUAD_ID_LENGTH } from "./codes.js";
import { Handles, normalizeHandle } from "./handles.js";
import { CLIP_CODE_LENGTH, Library } from "./library.js";
import { formatInvite } from "./codes.js";
import { clipOembed, clipPage, invitePage, notFoundPage, profilePage } from "./pages.js";
import { checkLicense, normalizeGrant, PLANS, planJson } from "./plans.js";
import { isMediaPointer, serveMedia } from "./media.js";
import { Squad } from "./squad.js";

export { Handles, Library, Squad };

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
      "content-security-policy": "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self'; media-src 'self'; frame-ancestors *",
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
  return /^\/(create|join|admin|public|peer)(\/|$)/.test(path);
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
  const library = stub(env.LIBRARIES, account);
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
  const answer = await stub(env.LIBRARIES, account).fetch(new Request(`${origin}/public/profile`));
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
// protection: making accounts and squads, sign-in and invite codes, and key checks get
// AUTH_LIMIT; every other change (uploads, friends, edits, the key maker's) gets WRITE_LIMIT. Reads aren't limited
// here. Without the bindings (tests, wrangler dev) nothing is limited.
const AUTH_ROUTES = /^\/v1\/(accounts|accounts\/signin|squads|join|license)$/;

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

    if (path === "/" || path === "/v1/health") return json({ ok: true, service: "clip-engine", version: VERSION });

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
    if (path === "/v1/accounts" && method === "POST") {
      const id = randomCode(SQUAD_ID_LENGTH);
      return forward(stub(env.LIBRARIES, id), request, "/create", { headers: { "x-account-id": id } });
    }
    if (path === "/v1/accounts/signin" && method === "POST") {
      const body = await readBody(request);
      const code = normalizeSignIn(body?.code);
      if (!code) return json({ error: "That isn't a sign-in code." }, 400);
      return forward(stub(env.LIBRARIES, code.account), request, "/join", {
        body: JSON.stringify({ device: body?.device, secret: code.secret }),
      });
    }
    m = path.match(/^\/v1\/accounts\/([0-9A-Za-z]+)(\/.*)$/);
    if (m && isSquadId(m[1].toUpperCase()) && !internal(m[2])) return forward(stub(env.LIBRARIES, m[1].toUpperCase()), request, m[2]);
    if (path.startsWith("/v1/admin/grants")) return withCors(await grantRoute(request, env, url, path, method));
    if (path.startsWith("/v1/admin/") && !admin(request, env)) return json({ error: "Not allowed." }, 403);
    if (path === "/v1/admin/plan" && method === "POST") {
      const body = await readBody(request);
      if (!isSquadId(body?.account)) return json({ error: "Which account?" }, 400);
      return forward(stub(env.LIBRARIES, body.account), request, "/admin/plan", { body: JSON.stringify({ plan: body.plan }) });
    }

    // ---- plans ----
    if (path === "/v1/plans" && method === "GET") {
      return json({ plans: Object.keys(PLANS).map((name) => planJson(env, name)), open: Boolean(env.POLAR_ORGANIZATION_ID) });
    }
    if (path === "/v1/license" && method === "POST") {
      const body = await readBody(request);
      const check = await checkLicense(env, body?.key, typeof body?.device === "string" ? body.device : "");
      if (!check.ok) return json({ error: check.error, rejected: check.rejected }, check.rejected ? 403 : 503);
      // The credits thank people who pay; a special key (made in the key maker, not bought) is never listed.
      if (check.grant) {
        await directory(env, url, "/credits/remove", { license: check.license });
      } else if (body?.credit !== undefined && check.plan !== "free") {
        await directory(env, url, "/credits/set", { license: check.license, name: typeof body.credit === "string" ? body.credit : "", plan: check.plan });
      }
      return json({ ...planJson(env, check.plan, check.features), display: check.display, expires: check.expires, grant: Boolean(check.grant) });
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
      if (path.startsWith("/c/") || path.startsWith("/u/") || path.startsWith("/s/")) return html(notFoundPage(), 404);
    }

    return json({ error: "Not found." }, 404);
  }
}
