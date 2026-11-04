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
//   POST /v1/admin/plan             set an account's plan     { account, plan } (Authorization: Bearer ADMIN_TOKEN)
//
// Public pages:
//   GET /c/<code>                   a shared clip's web player, with its moments on the timeline
//   GET /c/<code>/video|thumb|.json the clip itself
//   GET /u/<handle>                 a player's profile with their public clips
//   GET /s/<invite>                 a squad's join page, for the "Join my squad" button on Discord
//
// See README.md for the messages on the squad socket.

import { isSquadId, normalizeInvite, randomCode, sameSecret, SQUAD_ID_LENGTH } from "./codes.js";
import { Handles, normalizeHandle } from "./handles.js";
import { CLIP_CODE_LENGTH, Library } from "./library.js";
import { formatInvite } from "./codes.js";
import { clipPage, invitePage, notFoundPage, profilePage } from "./pages.js";
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
  return /^\/(create|join|admin|public)(\/|$)/.test(path);
}

// "K7QM-2X9D-PA4R-HT" (or a link ending in it): the account id and the one-time secret.
function normalizeSignIn(text) {
  if (typeof text !== "string") return null;
  const code = text.trim().split(/[/#?=]/).pop().toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
  if (code.length !== SQUAD_ID_LENGTH + 6 || !ALPHABET.test(code)) return null;
  return { account: code.slice(0, SQUAD_ID_LENGTH), secret: code.slice(SQUAD_ID_LENGTH) };
}

async function clipRoute(request, env, code, rest) {
  const account = code.slice(0, SQUAD_ID_LENGTH);
  const clip = code.slice(SQUAD_ID_LENGTH);
  const library = stub(env.LIBRARIES, account);
  if (rest === "/video" || rest === "/thumb") return forward(library, request, `/public/clip/${clip}${rest}`);
  const origin = new URL(request.url).origin;
  const viewed = rest === "" && request.method === "GET" ? "?view=1" : "";
  const answer = await library.fetch(new Request(`${origin}/public/clip/${clip}${viewed}`));
  if (!answer.ok) return rest === ".json" ? json({ error: "That clip isn't shared." }, 404) : html(notFoundPage(), 404);
  const data = await answer.json();
  if (rest === ".json") return json(data);
  return html(clipPage(data, origin));
}

async function profileRoute(request, env, handle, asJson) {
  const origin = new URL(request.url).origin;
  const found = await stub(env.HANDLES, "handles").fetch(new Request(`${origin}/lookup?handle=${encodeURIComponent(handle)}`));
  if (!found.ok) return asJson ? json({ error: "No one has that name." }, 404) : html(notFoundPage(), 404);
  const { account } = await found.json();
  const profile = await (await stub(env.LIBRARIES, account).fetch(new Request(`${origin}/public/profile`))).json();
  return asJson ? json(profile) : html(profilePage(profile, origin));
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

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
    if (path === "/v1/admin/plan" && method === "POST") {
      const token = (request.headers.get("authorization") ?? "").replace(/^Bearer /, "");
      if (!env.ADMIN_TOKEN || !sameSecret(token, env.ADMIN_TOKEN)) return json({ error: "Not allowed." }, 403);
      const body = await readBody(request);
      if (!isSquadId(body?.account)) return json({ error: "Which account?" }, 400);
      return forward(stub(env.LIBRARIES, body.account), request, "/admin/plan", { body: JSON.stringify({ plan: body.plan }) });
    }

    // ---- public pages ----
    if (method === "GET" || method === "HEAD") {
      m = path.match(/^\/c\/([0-9A-Za-z]+?)(\/video|\/thumb|\.json)?$/);
      if (m && m[1].length === SQUAD_ID_LENGTH + CLIP_CODE_LENGTH && ALPHABET.test(m[1].toUpperCase())) {
        return clipRoute(request, env, m[1].toUpperCase(), m[2] ?? "");
      }
      m = path.match(/^\/u\/([^/]+?)(\.json)?$/);
      if (m && normalizeHandle(decodeURIComponent(m[1]))) return profileRoute(request, env, normalizeHandle(decodeURIComponent(m[1])), Boolean(m[2]));
      m = path.match(/^\/s\/([^/]+)$/);
      if (m) {
        const invite = normalizeInvite(decodeURIComponent(m[1]));
        if (!invite) return html(notFoundPage(), 404);
        const answer = await stub(env.SQUADS, invite.squadId).fetch(
          new Request(`${url.origin}/public/invite?secret=${invite.secret}`, { headers: { "x-squad-id": invite.squadId } }));
        if (!answer.ok) return html(notFoundPage(), 404);
        return html(invitePage(await answer.json(), formatInvite(invite.squadId, invite.secret)));
      }
      if (path.startsWith("/c/") || path.startsWith("/u/") || path.startsWith("/s/")) return html(notFoundPage(), 404);
    }

    return json({ error: "Not found." }, 404);
  },
};
