// Stand-ins for the parts of Cloudflare's runtime the squad uses: Durable Object SQLite storage
// (on Node's built-in SQLite), R2, hibernatable WebSockets and the Durable Object namespace.

import { DatabaseSync } from "node:sqlite";
import { Squad } from "../src/squad.js";

class FakeSql {
  constructor() {
    this.db = new DatabaseSync(":memory:");
  }

  exec(query, ...bindings) {
    if (bindings.length === 0 && query.trim().split(";").filter((q) => q.trim()).length > 1) {
      this.db.exec(query);
      return { toArray: () => [] };
    }
    const statement = this.db.prepare(query);
    const isRead = /^\s*(SELECT|WITH)/i.test(query) || /RETURNING/i.test(query);
    const rows = isRead ? statement.all(...bindings).map((r) => ({ ...r })) : (statement.run(...bindings), []);
    return { toArray: () => rows, one: () => rows[0], [Symbol.iterator]: () => rows[Symbol.iterator]() };
  }
}

export class FakeSocket {
  constructor(tags) {
    this.tags = tags;
    this.sent = [];
    this.closed = false;
    this.data = null;
  }
  send(text) {
    if (this.closed) throw new Error("closed");
    this.sent.push(JSON.parse(text));
  }
  close() {
    this.closed = true;
  }
  serializeAttachment(value) {
    this.data = structuredClone(value);
  }
  deserializeAttachment() {
    return structuredClone(this.data);
  }
  take(type) {
    const found = this.sent.filter((m) => !type || m.t === type);
    this.sent = this.sent.filter((m) => type && m.t !== type);
    return found;
  }
}

export class FakeBucket {
  constructor() {
    this.objects = new Map();
  }
  async put(key, body, options) {
    const bytes = new Uint8Array(await new Response(body).arrayBuffer());
    this.objects.set(key, { bytes, options });
    return { key, size: bytes.length };
  }
  async get(key, { range } = {}) {
    const object = this.objects.get(key);
    if (!object) return null;
    let offset = 0;
    let length = object.bytes.length;
    let r;
    const header = range?.get?.("range");
    const m = header?.match(/^bytes=(\d+)-(\d*)$/);
    if (m) {
      offset = Number(m[1]);
      const end = m[2] ? Math.min(Number(m[2]), object.bytes.length - 1) : object.bytes.length - 1;
      length = end - offset + 1;
      r = { offset, length };
    }
    return { size: object.bytes.length, range: r, httpMetadata: object.options?.httpMetadata, body: object.bytes.slice(offset, offset + length) };
  }
  async delete(keys) {
    for (const key of Array.isArray(keys) ? keys : [keys]) this.objects.delete(key);
  }
}

export function fakeState() {
  const sockets = [];
  let alarm = null;
  const sql = new FakeSql();
  return {
    sockets,
    storage: {
      sql,
      async getAlarm() {
        return alarm;
      },
      async setAlarm(t) {
        alarm = t;
      },
      async deleteAlarm() {
        alarm = null;
      },
      async deleteAll() {
        sql.db.exec("DROP TABLE IF EXISTS meta; DROP TABLE IF EXISTS members; DROP TABLE IF EXISTS moments; DROP TABLE IF EXISTS views;");
      },
    },
    acceptWebSocket(ws, tags) {
      ws.tags = tags;
      sockets.push(ws);
    },
    getWebSockets(tag) {
      return sockets.filter((ws) => !ws.closed && (!tag || ws.tags.includes(tag)));
    },
  };
}

// A squad whose sockets are FakeSockets; open() connects one for a member's token.
export function makeSquad(env = {}) {
  const ctx = fakeState();
  const bucket = new FakeBucket();
  const squad = new Squad(ctx, { CLIPS: bucket, ...env });
  squad.openSocket = (member) => {
    const ws = new FakeSocket([member]);
    ctx.acceptWebSocket(ws, [member]);
    ws.serializeAttachment({ member, game: "", match: null, unlinked: [] });
    squad.lastSocket = ws;
    return new Response(null, { status: 200 });
  };
  return { squad, ctx, bucket };
}

// A Durable Object namespace with one Squad per name, as env.SQUADS.
export function fakeNamespace(env = {}) {
  const squads = new Map();
  const bucket = new FakeBucket();
  return {
    bucket,
    squads,
    idFromName: (name) => name,
    get(name) {
      if (!squads.has(name)) {
        const { squad } = makeSquadWith(bucket, env);
        squads.set(name, squad);
      }
      const squad = squads.get(name);
      return { fetch: (request) => squad.fetch(request) };
    },
  };
}

function makeSquadWith(bucket, env) {
  const ctx = fakeState();
  const squad = new Squad(ctx, { CLIPS: bucket, ...env });
  squad.openSocket = (member) => {
    const ws = new FakeSocket([member]);
    ctx.acceptWebSocket(ws, [member]);
    ws.serializeAttachment({ member, game: "", match: null, unlinked: [] });
    squad.lastSocket = ws;
    return new Response(null, { status: 200 });
  };
  return { squad, ctx };
}

export function request(path, { method = "GET", token, body, headers = {} } = {}) {
  const h = new Headers(headers);
  if (token) h.set("authorization", `Bearer ${token}`);
  const init = { method, headers: h };
  if (body !== undefined) {
    init.body = typeof body === "string" || body instanceof Uint8Array ? body : JSON.stringify(body);
    if (body instanceof Uint8Array && !h.has("content-length")) h.set("content-length", String(body.length));
  }
  return new Request(`https://squads.test${path}`, init);
}
