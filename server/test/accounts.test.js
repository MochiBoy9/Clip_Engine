import assert from "node:assert/strict";
import { test } from "node:test";
import { checkIdToken } from "../src/accounts.js";
import { Handles } from "../src/handles.js";
import worker from "../src/index.js";
import { fakeEnv, PASSWORD, request, signUpBody } from "./fakes.js";

const call = (env, path, options) => worker.fetch(request(path, options), env);
const HUB = { "x-hub": "1" };
const ADMIN = "operator-secret";
const GOOGLE = { GOOGLE_CLIENT_ID: "client-1.apps.googleusercontent.com", GOOGLE_CLIENT_SECRET: "google-secret" };

// A service that sends email through Resend and signs in with Google, with both stood in for: emails land in
// outbox, and Google's token endpoint answers with an ID token for `google.claims`.
function setup(vars = {}) {
  const env = fakeEnv({ RESEND_API_KEY: "re_test", EMAIL_FROM: "Clip Engine <accounts@clipengine.test>", ADMIN_TOKEN: ADMIN, ...GOOGLE, ...vars });
  const outbox = [];
  const google = { claims: null, sent: null };
  const real = globalThis.fetch;
  globalThis.fetch = async (url, init = {}) => {
    if (String(url) === "https://api.resend.com/emails") {
      assert.equal(new Headers(init.headers).get("authorization"), "Bearer re_test");
      outbox.push(JSON.parse(init.body));
      return Response.json({ id: "email-1" });
    }
    if (String(url) === "https://oauth2.googleapis.com/token") {
      google.sent = Object.fromEntries(new URLSearchParams(init.body));
      return Response.json({ id_token: idToken(google.claims) });
    }
    throw new Error(`unexpected fetch ${url}`);
  };
  return { env, outbox, google, done: () => { globalThis.fetch = real; } };
}

function idToken(claims) {
  const part = (o) => Buffer.from(JSON.stringify(o)).toString("base64url");
  return `${part({ alg: "RS256" })}.${part(claims)}.signature`;
}

const linkToken = (mail) => mail.text.match(/#t=([0-9A-Z]{8}\.[A-Za-z0-9_-]+)/)[1];
const signUpCall = (env, name, email, headers = {}) => call(env, "/v1/accounts", { method: "POST", body: signUpBody(name, "device-0000", email), headers });

async function signUp(env, name = "Kai", email = "kai@example.com", headers = {}) {
  const r = await signUpCall(env, name, email, headers);
  assert.equal(r.status, 200, await r.clone().text());
  const b = await r.json();
  return { token: b.token, id: b.account.id, base: `/v1/accounts/${b.account.id}`, account: b.account };
}

const signIn = (env, email, password = PASSWORD, device = "device-1111") =>
  call(env, "/v1/accounts/signin", { method: "POST", body: { email, password, device } });

function hubCookie(response) {
  return response.headers.getSetCookie().find((c) => c.startsWith("ce_hub="))?.split(";")[0];
}

test("signing up with an email and password, confirming the email and signing in with it", async () => {
  const { env, outbox, done } = setup();
  try {
    assert.equal((await call(env, "/v1/accounts", { method: "POST", body: { name: "Kai", device: "device-0000" } })).status, 400, "an email is needed");
    assert.equal((await call(env, "/v1/accounts", { method: "POST", body: { ...signUpBody("Kai"), password: "short" } })).status, 400);
    assert.equal(outbox.length, 0);

    const kai = await signUp(env, "Kai", " Kai@Example.com ");
    assert.equal(kai.account.email, "kai@example.com");
    assert.equal(kai.account.emailVerified, false);
    assert.equal(kai.account.hasPassword, true);
    assert.equal((await signUpCall(env, "Kai 2", "KAI@example.com")).status, 409, "one account per email");

    // The confirmation email, and its link working once.
    assert.equal(outbox.length, 1);
    assert.deepEqual(outbox[0].to, ["kai@example.com"]);
    assert.equal(outbox[0].from, "Clip Engine <accounts@clipengine.test>");
    assert.match(outbox[0].text, /https:\/\/squads\.test\/hub\/confirm#t=/);
    const token = linkToken(outbox[0]);
    const confirmed = await call(env, "/v1/auth/confirm", { method: "POST", body: { token } });
    assert.equal(confirmed.status, 200);
    assert.equal((await confirmed.json()).email, "kai@example.com");
    assert.equal((await call(env, "/v1/auth/confirm", { method: "POST", body: { token } })).status, 403, "a link works once");
    assert.equal((await (await call(env, `${kai.base}/account`, { token: kai.token })).json()).emailVerified, true);

    // Another PC signs in with the email and password; wrong ones and unknown emails get the same answer.
    const other = await signIn(env, "KAI@example.com");
    assert.equal(other.status, 200);
    const { token: second } = await other.json();
    assert.equal((await call(env, `${kai.base}/account`, { token: second })).status, 200);
    const wrong = await signIn(env, "kai@example.com", "not the password");
    const nobody = await signIn(env, "nobody@example.com");
    assert.equal(wrong.status, 403);
    assert.equal(nobody.status, 403);
    assert.equal((await wrong.json()).error, (await nobody.json()).error);
    assert.equal((await signIn(env, "kai@example.com", PASSWORD, "bad id")).status, 400, "the app sends its PC's id");

    // The hub signs in and up with an email too, into its cookie.
    const hub = await call(env, "/hub/api/signin", { method: "POST", headers: HUB, body: { email: "kai@example.com", password: PASSWORD } });
    assert.equal(hub.status, 200);
    assert.ok(hubCookie(hub));
    assert.equal((await hub.json()).token, undefined, "the token stays in the HttpOnly cookie");
    const up = await call(env, "/hub/api/signup", { method: "POST", headers: HUB, body: { name: "Mira", email: "mira@example.com", password: PASSWORD } });
    assert.equal(up.status, 200);
    assert.equal((await call(env, "/hub/api/me", { headers: { cookie: hubCookie(up) } })).status, 200);
    assert.equal((await call(env, "/hub/api/signup", { method: "POST", body: { name: "X", email: "x@example.com", password: PASSWORD } })).status, 403,
      "only from the hub's own pages");

    // Five wrong passwords lock it for a while, even for the right one.
    for (let i = 0; i < 5; i++) await signIn(env, "kai@example.com", "nope nope nope");
    assert.equal((await signIn(env, "kai@example.com")).status, 429);
  } finally {
    done();
  }
});

test("a forgotten password: a reset link by email that works once, for 30 minutes, and signs everything out", async () => {
  const { env, outbox, done } = setup();
  const now = Date.now;
  try {
    const kai = await signUp(env);
    const verify = linkToken(outbox.pop());

    assert.equal((await call(env, "/v1/auth/forgot", { method: "POST", body: { email: "nobody@example.com" } })).status, 200, "the same answer");
    assert.equal(outbox.length, 0);
    assert.equal((await call(env, "/v1/auth/forgot", { method: "POST", body: { email: "Kai@example.com" } })).status, 200);
    assert.equal(outbox.length, 1);
    assert.match(outbox[0].subject, /Reset/);
    assert.match(outbox[0].text, /\/hub\/reset#t=/);
    await call(env, "/v1/auth/forgot", { method: "POST", body: { email: "kai@example.com" } });
    assert.equal(outbox.length, 1, "one a minute at most");
    const token = linkToken(outbox[0]);

    const reset = (body) => call(env, "/v1/auth/reset", { method: "POST", body });
    assert.equal((await reset({ token: verify, password: "a brand new one" })).status, 403, "a confirm link can't reset");
    assert.equal((await reset({ token: "nonsense", password: "a brand new one" })).status, 403);
    assert.equal((await reset({ token, password: "short" })).status, 400);
    assert.equal((await reset({ token, password: "a brand new one" })).status, 200, "a refused password leaves the link working");
    assert.equal((await reset({ token, password: "a brand new one" })).status, 403, "once");

    assert.equal((await call(env, `${kai.base}/account`, { token: kai.token })).status, 401, "every sign-in ends");
    assert.equal((await signIn(env, "kai@example.com")).status, 403);
    const back = await signIn(env, "kai@example.com", "a brand new one");
    assert.equal(back.status, 200);
    assert.equal((await back.json()).account.emailVerified, true, "opening the link proved the email");

    // A link older than 30 minutes doesn't work.
    Date.now = () => now() + 2 * 60_000;
    await call(env, "/v1/auth/forgot", { method: "POST", body: { email: "kai@example.com" } });
    const late = linkToken(outbox.at(-1));
    Date.now = () => now() + 40 * 60_000;
    assert.equal((await reset({ token: late, password: "too late for this" })).status, 403);
  } finally {
    Date.now = now;
    done();
  }
});

test("changing the email: the password, a link to the new address and a note to the old one", async () => {
  const { env, outbox, done } = setup();
  try {
    const kai = await signUp(env);
    await signUp(env, "Mira", "mira@example.com");
    outbox.length = 0;
    const change = (body) => call(env, `${kai.base}/email`, { method: "POST", token: kai.token, body });

    assert.equal((await change({ email: "kai@new.example", password: "wrong password" })).status, 403);
    assert.equal((await change({ email: "mira@example.com", password: PASSWORD })).status, 409);
    assert.equal((await change({ email: "not an email", password: PASSWORD })).status, 400);
    const asked = await change({ email: "Kai@New.example", password: PASSWORD });
    assert.equal(asked.status, 200);
    const reply = await asked.json();
    assert.equal(reply.pendingEmail, "kai@new.example");
    assert.equal(reply.email, "kai@example.com", "nothing changes until the link is opened");
    assert.deepEqual(outbox.map((m) => m.to[0]), ["kai@new.example", "kai@example.com"]);
    assert.match(outbox[1].text, /kai@new\.example/);
    assert.doesNotMatch(outbox[1].text, /#t=/, "the old address gets no link");

    assert.equal((await call(env, "/v1/auth/confirm", { method: "POST", body: { token: linkToken(outbox[0]) } })).status, 200);
    const account = await (await call(env, `${kai.base}/account`, { token: kai.token })).json();
    assert.equal(account.email, "kai@new.example");
    assert.equal(account.emailVerified, true);
    assert.equal((await signIn(env, "kai@example.com")).status, 403);
    assert.equal((await signIn(env, "kai@new.example")).status, 200);
    await signUp(env, "Someone", "kai@example.com");
  } finally {
    done();
  }
});

test("the first device and every sign-in are logged for the owner, who can sign one out", async () => {
  const { env, done } = setup();
  try {
    const kai = await signUp(env, "Kai", "kai@example.com", { "user-agent": "ClipEngine/1.0.3 (Windows NT 10.0)", "cf-ipcountry": "NL" });
    const hub = await call(env, "/hub/api/signin", {
      method: "POST", headers: { ...HUB, "user-agent": "Mozilla/5.0 (Android 14) Firefox/131.0", "cf-ipcountry": "DE" },
      body: { email: "kai@example.com", password: PASSWORD },
    });
    const cookie = hubCookie(hub);
    const security = await (await call(env, `${kai.base}/security`, { token: kai.token })).json();
    assert.deepEqual({ ...security.firstDevice, at: 0 }, { kind: "app", agent: "ClipEngine/1.0.3 (Windows NT 10.0)", country: "NL", at: 0 });
    assert.equal(security.email, "kai@example.com");
    assert.equal(security.devices.length, 2);
    const web = security.devices.find((d) => d.kind === "web");
    assert.equal(web.country, "DE");
    assert.match(web.agent, /Firefox/);
    assert.equal(security.devices.find((d) => d.kind === "app").current, true);
    assert.equal(JSON.stringify(security).includes("token"), false, "no tokens or hashes beyond a short id");

    // From the hub too, then that sign-in is signed out from the app.
    assert.equal((await call(env, "/hub/api/security", { headers: { cookie } })).status, 200);
    const after = await call(env, `${kai.base}/devices/${web.id}`, { method: "DELETE", token: kai.token });
    assert.equal((await after.json()).devices.length, 1);
    assert.equal((await call(env, "/hub/api/me", { headers: { cookie } })).status, 401);

    // Changing the password needs the current one.
    const set = (body) => call(env, `${kai.base}/password`, { method: "POST", token: kai.token, body });
    assert.equal((await set({ password: "a new password", current: "wrong one" })).status, 403);
    assert.equal((await set({ password: null, current: PASSWORD })).status, 400, "an email account keeps a password");
    assert.equal((await set({ password: "a new password", current: PASSWORD })).status, 200);
    assert.equal((await signIn(env, "kai@example.com", "a new password")).status, 200);
  } finally {
    done();
  }
});

// Starts Sign in with Google and answers the cookie and Google's state and nonce.
async function googleStart(env, query = "") {
  const r = await call(env, `/v1/auth/google${query}`);
  assert.equal(r.status, 302);
  const to = new URL(r.headers.get("location"));
  const cookie = r.headers.getSetCookie()[0];
  assert.match(cookie, /^ce_google=.*; Path=\/v1\/auth\/google; Max-Age=600; HttpOnly; Secure; SameSite=Lax$/);
  return { to, cookie: cookie.split(";")[0], state: to.searchParams.get("state"), nonce: to.searchParams.get("nonce") };
}

const claimsFor = (nonce, more = {}) => ({
  iss: "https://accounts.google.com", aud: GOOGLE.GOOGLE_CLIENT_ID, exp: Math.floor(Date.now() / 1000) + 3600, nonce,
  sub: "google-123", email: "mira@example.com", email_verified: true, name: "Mira", ...more,
});

const callback = (env, start, state = start.state) =>
  call(env, `/v1/auth/google/callback?code=google-code&state=${encodeURIComponent(state)}`, { headers: { cookie: start.cookie } });

test("Sign in with Google on the hub makes an account, and signs in to the same one after", async () => {
  const { env, google, done } = setup();
  try {
    const start = await googleStart(env);
    assert.equal(start.to.origin + start.to.pathname, "https://accounts.google.com/o/oauth2/v2/auth");
    assert.equal(start.to.searchParams.get("client_id"), GOOGLE.GOOGLE_CLIENT_ID);
    assert.equal(start.to.searchParams.get("redirect_uri"), "https://squads.test/v1/auth/google/callback");
    assert.equal(start.to.searchParams.get("code_challenge_method"), "S256");
    assert.equal(start.to.searchParams.get("scope"), "openid email profile");

    // Someone else's state, or no cookie at all, is refused before Google is asked anything.
    assert.equal((await callback(env, start, "someone-elses-state")).status, 400);
    assert.equal((await call(env, `/v1/auth/google/callback?code=x&state=${start.state}`)).status, 400);
    assert.equal(google.sent, null);

    google.claims = claimsFor(start.nonce);
    const first = await callback(env, start);
    assert.equal(first.status, 303);
    assert.equal(first.headers.get("location"), "https://squads.test/hub/me");
    assert.ok(first.headers.getSetCookie().some((c) => c.startsWith("ce_google=;") && c.includes("Max-Age=0")));
    const verifier = start.cookie.split("=")[1].split(".")[1];
    assert.equal(google.sent.code_verifier, verifier, "PKCE");
    assert.equal(google.sent.client_secret, "google-secret");
    const me = await (await call(env, "/hub/api/me", { headers: { cookie: hubCookie(first) } })).json();
    assert.equal(me.account.name, "Mira");
    assert.equal(me.account.email, "mira@example.com");
    assert.equal(me.account.emailVerified, true);
    assert.equal(me.account.google, true);
    assert.equal(me.account.hasPassword, false);
    assert.equal((await signUpCall(env, "Copy", "mira@example.com")).status, 409, "the email is taken");

    const second = await googleStart(env);
    google.claims = claimsFor(second.nonce, { email: "mira@changed.example" });
    const again = await (await call(env, "/hub/api/me", { headers: { cookie: hubCookie(await callback(env, second)) } })).json();
    assert.equal(again.account.id, me.account.id, "found by the Google account, whatever its email is now");
  } finally {
    done();
  }
});

test("Google's ID token is checked: issuer, audience, expiry, nonce and a verified email", () => {
  const good = claimsFor("n-1");
  assert.equal(checkIdToken(idToken(good), GOOGLE, "n-1").sub, "google-123");
  assert.equal(checkIdToken(idToken(good), GOOGLE, "n-2"), null);
  assert.equal(checkIdToken(idToken({ ...good, aud: "someone-else" }), GOOGLE, "n-1"), null);
  assert.equal(checkIdToken(idToken({ ...good, iss: "https://evil.test" }), GOOGLE, "n-1"), null);
  assert.equal(checkIdToken(idToken({ ...good, exp: 1 }), GOOGLE, "n-1"), null);
  assert.equal(checkIdToken(idToken({ ...good, email_verified: false }), GOOGLE, "n-1"), null);
  assert.equal(checkIdToken("not a token", GOOGLE, "n-1"), null);
});

test("Google from the app hands back a one-time code, and takes over an account whose email was never confirmed", async () => {
  const { env, google, done } = setup();
  try {
    // Someone signs up with Mira's email before she does, and never confirms it.
    const squatter = await signUp(env, "Not Mira", "mira@example.com");
    assert.equal((await call(env, "/v1/auth/google?app=short")).status, 400);
    const appState = "app-state-0123456789abcdef";
    const start = await googleStart(env, `?app=${appState}`);
    google.claims = claimsFor(start.nonce);
    const page = await callback(env, start);
    assert.equal(page.status, 200);
    const m = (await page.text()).match(/clipengine:\/\/cloud\/signin\/([0-9A-Z-]+)\?state=([^"&]+)/);
    assert.ok(m, "a link back to the app");
    assert.equal(decodeURIComponent(m[2]), appState);

    assert.equal((await call(env, `${squatter.base}/account`, { token: squatter.token })).status, 401, "their sign-ins are gone");
    assert.equal((await signIn(env, "mira@example.com")).status, 403, "and their password");
    const app = await call(env, "/v1/accounts/signin", { method: "POST", body: { code: m[1], device: "device-mira" } });
    assert.equal(app.status, 200);
    const { account } = await app.json();
    assert.equal(account.id, squatter.id);
    assert.equal(account.google, true);
    assert.equal(account.hasPassword, false);
    assert.equal((await call(env, "/v1/accounts/signin", { method: "POST", body: { code: m[1], device: "device-mira" } })).status, 403, "once");
  } finally {
    done();
  }
});

test("without Google's keys, Google sign-in says so; without Resend's, nothing is sent", async () => {
  const env = fakeEnv();
  assert.equal((await call(env, "/v1/auth/google")).status, 503);
  assert.doesNotMatch(await (await call(env, "/hub/signin")).text(), /Sign in with Google/);
  const real = globalThis.fetch;
  globalThis.fetch = () => assert.fail("no email without RESEND_API_KEY");
  try {
    assert.equal((await call(env, "/v1/accounts", { method: "POST", body: signUpBody() })).status, 200);
  } finally {
    globalThis.fetch = real;
  }
  const google = setup();
  try {
    assert.match(await (await call(google.env, "/hub/signin")).text(), /Sign in with Google/);
  } finally {
    google.done();
  }
  for (const page of ["/hub/reset", "/hub/confirm"]) assert.equal((await call(env, page)).status, 200);
});

test("a cloud account reset: a new ACCOUNTS_EPOCH forgets every account, and keeps keys and credits", async () => {
  const { env, done } = setup();
  try {
    const { grants } = await (await call(env, "/v1/admin/grants", { method: "POST", token: ADMIN, body: { plan: "pro", credits: true } })).json();
    const key = grants[0].code;
    const kai = await signUp(env);
    await call(env, `${kai.base}/account`, { method: "PATCH", token: kai.token, body: { handle: "kai" } });
    assert.equal((await call(env, `${kai.base}/plan`, { method: "POST", token: kai.token, body: { key } })).status, 200);
    await call(env, "/v1/license", { method: "POST", body: { key, device: "device-0000", credit: "Kai" } });
    const clip = await (await call(env, `${kai.base}/clips`, { method: "POST", token: kai.token, body: { game: "Valorant", seconds: 10, visibility: "public" } })).json();
    await call(env, `${kai.base}/clips/${clip.id}/video`, { method: "PUT", token: kai.token, body: new Uint8Array(10) });
    assert.ok([...env.CLIPS.objects.keys()].some((k) => k.startsWith(`library/${kai.id}/`)));
    assert.equal((await (await call(env, "/hub/api/feed")).json()).clips.length, 1);

    // The operator sets ACCOUNTS_EPOCH and deploys: the directory starts again on the storage it has.
    env.ACCOUNTS_EPOCH = "1";
    const { sql } = env.HANDLES.instances.get("handles");
    env.HANDLES.instances.set("handles", new Handles({ storage: { sql } }, env));

    assert.equal((await call(env, `${kai.base}/account`, { token: kai.token })).status, 401, "the app hears it's signed out");
    assert.equal((await call(env, "/u/kai")).status, 404);
    assert.equal((await call(env, `/c/${clip.code}`)).status, 404);
    assert.equal((await (await call(env, "/hub/api/feed")).json()).clips.length, 0);
    const fresh = await signUp(env);
    assert.notEqual(fresh.id, kai.id);
    assert.equal((await call(env, `${fresh.base}/account`, { method: "PATCH", token: fresh.token, body: { handle: "kai" } })).status, 200);
    const plan = await call(env, `${fresh.base}/plan`, { method: "POST", token: fresh.token, body: { key } });
    assert.equal(plan.status, 200, "the key goes on the new account");
    assert.equal((await plan.json()).plan, "pro");
    const credits = await (await call(env, "/v1/credits")).json();
    assert.ok(Object.values(credits).flat().includes("Kai"), "the credits stay");
    const kept = (await (await call(env, "/v1/admin/grants", { token: ADMIN })).json()).grants;
    assert.equal(kept[0].code, key);
    assert.equal(kept[0].devices, 1, "the key's PCs stay");

    const next = await (await call(env, `${fresh.base}/clips`, { method: "POST", token: fresh.token, body: { game: "Valorant", seconds: 10 } })).json();
    await call(env, `${fresh.base}/clips/${next.id}/video`, { method: "PUT", token: fresh.token, body: new Uint8Array(10) });
    assert.ok([...env.CLIPS.objects.keys()].some((k) => k.startsWith(`library-1/${fresh.id}/`)), "new videos in a folder of their own");
  } finally {
    done();
  }
});
