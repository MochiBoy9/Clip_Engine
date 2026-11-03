// Clip Engine's squad sync service: routes each request to its squad's Durable Object.
//
//   POST /v1/squads                 create a squad            { name, squad?, device }
//   POST /v1/join                   join with an invite code  { invite, name, device }
//   GET  /v1/squads/<id>/socket     the squad's live channel (WebSocket, Authorization: Bearer <token>)
//   GET  /v1/squads/<id>/squad      members, invite code and storage
//   GET  /v1/squads/<id>/library    shared moments with every view, newest first
//   PUT/GET /v1/squads/<id>/moments/<moment>/views/<member|me>/(video|thumb)
//   POST /v1/squads/<id>/moments/<moment>/star, POST /invite/rotate, PATCH|DELETE /members/me
//
// See README.md for the messages on the socket.

import { isSquadId, normalizeInvite, randomCode, SQUAD_ID_LENGTH } from "./codes.js";
import { Squad } from "./squad.js";

export { Squad };

const VERSION = 1;

function json(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function squad(env, id) {
  return env.SQUADS.get(env.SQUADS.idFromName(id));
}

// The request, sent on to the squad with the rest of the path. Built from the request itself,
// so an upload streams through with its length and never sits in the Worker's memory.
function forward(env, id, request, path, body) {
  const url = new URL(request.url);
  url.pathname = path;
  const sent = body === undefined
    ? new Request(url.toString(), request)
    : new Request(url.toString(), { method: request.method, headers: request.headers, body });
  sent.headers.set("x-squad-id", id);
  return squad(env, id).fetch(sent);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const path = url.pathname;

    if (path === "/" || path === "/v1/health") return json({ ok: true, service: "clip-engine-squads", version: VERSION });

    if (path === "/v1/squads" && request.method === "POST") {
      const id = randomCode(SQUAD_ID_LENGTH);
      return forward(env, id, request, "/create");
    }

    if (path === "/v1/join" && request.method === "POST") {
      let body;
      try {
        body = await request.json();
      } catch {
        return json({ error: "Expected JSON." }, 400);
      }
      const invite = normalizeInvite(body?.invite);
      if (!invite) return json({ error: "That isn't a squad invite code." }, 400);
      return forward(env, invite.squadId, request, "/join", JSON.stringify({ ...body, secret: invite.secret }));
    }

    const m = path.match(/^\/v1\/squads\/([0-9A-Za-z]+)(\/.*)$/);
    if (m && isSquadId(m[1].toUpperCase())) return forward(env, m[1].toUpperCase(), request, m[2]);

    return json({ error: "Not found." }, 404);
  },
};
