// Stand-ins for the parts of Cloudflare's runtime the squad uses: Durable Object SQLite storage
// (on Node's built-in SQLite), R2, hibernatable WebSockets and the Durable Object namespace.

import { DatabaseSync } from "node:sqlite";
import { Handles } from "../src/handles.js";
import { Library } from "../src/library.js";
import { Referrals } from "../src/referrals.js";
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
    this.reads = 0;
    this.writes = 0;
    this.uploads = new Map();
    this.uploadCount = 0;
  }
  async put(key, body, options) {
    this.writes++;
    const bytes = new Uint8Array(await new Response(body).arrayBuffer());
    this.objects.set(key, { bytes, options });
    return { key, size: bytes.length };
  }
  async get(key, { range } = {}) {
    this.reads++;
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
  // R2's multipart uploads: parts kept by upload until complete() joins them, in the order given.
  async createMultipartUpload(key, options) {
    const uploadId = `upload-${++this.uploadCount}`;
    this.uploads.set(uploadId, { key, options, parts: new Map() });
    return this.resumeMultipartUpload(key, uploadId);
  }
  resumeMultipartUpload(key, uploadId) {
    const bucket = this;
    const find = () => {
      const upload = bucket.uploads.get(uploadId);
      if (!upload || upload.key !== key) throw new Error("NoSuchUpload");
      return upload;
    };
    return {
      key,
      uploadId,
      async uploadPart(partNumber, body) {
        const upload = find();
        const bytes = new Uint8Array(await new Response(body).arrayBuffer());
        const etag = `etag-${partNumber}-${bytes.length}`;
        upload.parts.set(partNumber, { bytes, etag });
        return { partNumber, etag };
      },
      async complete(parts) {
        const upload = find();
        const chunks = parts.map((p) => {
          const part = upload.parts.get(p.partNumber);
          if (!part || part.etag !== p.etag) throw new Error("InvalidPart");
          return part.bytes;
        });
        const bytes = new Uint8Array(chunks.reduce((n, c) => n + c.length, 0));
        let at = 0;
        for (const c of chunks) {
          bytes.set(c, at);
          at += c.length;
        }
        bucket.uploads.delete(uploadId);
        bucket.writes++;
        bucket.objects.set(key, { bytes, options: upload.options });
        return { key, size: bytes.length };
      },
      async abort() {
        find();
        bucket.uploads.delete(uploadId);
      },
    };
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

// A Durable Object namespace with one instance of Class per name, as env.SQUADS and the rest.
// All of them share one bucket and one env, like the deployed Worker.
export function fakeNamespace(Class = Squad, shared = {}) {
  const instances = new Map();
  shared.CLIPS ??= new FakeBucket();
  return {
    bucket: shared.CLIPS,
    instances,
    idFromName: (name) => name,
    get(name) {
      if (!instances.has(name)) {
        const ctx = fakeState();
        const instance = new Class(ctx, shared);
        instance.openSocket = (member) => {
          const ws = new FakeSocket([member]);
          ctx.acceptWebSocket(ws, [member]);
          ws.serializeAttachment({ member, game: "", match: null, unlinked: [] });
          instance.lastSocket = ws;
          return new Response(null, { status: 200 });
        };
        instances.set(name, instance);
      }
      const instance = instances.get(name);
      return { fetch: (request) => instance.fetch(request) };
    },
  };
}

// The whole service's env, for calling the Worker in tests.
export function fakeEnv(vars = {}) {
  const env = { ...vars, CLIPS: new FakeBucket() };
  env.SQUADS = fakeNamespace(Squad, env);
  env.LIBRARIES = fakeNamespace(Library, env);
  env.HANDLES = fakeNamespace(Handles, env);
  env.REFERRALS = fakeNamespace(Referrals, env);
  return env;
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
