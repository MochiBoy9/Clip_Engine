// Cloud accounts: signing up with an email and password, signing in with them, the links in account emails
// (confirming an address, a new one, a password reset) and signing in with Google. The account itself lives in its
// library (src/library.js); the directory (src/handles.js) knows which account each email and Google account is on.
//
//   POST /v1/accounts               sign up              { name, email, password, device }
//   POST /v1/accounts/signin        sign in              { email, password, device } (or { code, device }, src/index.js)
//   POST /v1/auth/forgot            a password reset link to the email, if it has an account { email }
//   POST /v1/auth/reset             a new password with that link's token { token, password }
//   POST /v1/auth/confirm           confirm an email with its link's token { token }
//   GET  /v1/auth/google            Sign in with Google (?app=<state> from the desktop app)
//   GET  /v1/auth/google/callback   where Google sends the browser back

import { base64url, isSquadId, randomCode, randomToken, sameSecret, SQUAD_ID_LENGTH } from "./codes.js";
import { normalizeEmail } from "./email.js";
import { sessionCookies } from "./hub.js";
import { libraryStub } from "./library.js";
import { escapeHtml, pageCsp, shell } from "./look.js";

const GOOGLE_AUTH = "https://accounts.google.com/o/oauth2/v2/auth";
const GOOGLE_TOKEN = "https://oauth2.googleapis.com/token";
const GOOGLE_ISSUERS = ["https://accounts.google.com", "accounts.google.com"];
const GOOGLE_COOKIE = "ce_google";
const GOOGLE_COOKIE_SECONDS = 600;

function json(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });
}

function directory(env, origin, path, body) {
  return env.HANDLES.get(env.HANDLES.idFromName("handles")).fetch(new Request(`${origin}${path}`, { method: "POST", body: JSON.stringify(body) }));
}

// Who's signing in, passed to the library for its device list: the user agent and the country Cloudflare saw.
function clientHeaders(request, extra = {}) {
  const headers = { "user-agent": request.headers.get("user-agent") ?? "", ...extra };
  const country = request.cf?.country ?? request.headers.get("cf-ipcountry");
  if (country) headers["cf-ipcountry"] = country;
  return headers;
}

function library(env, origin, account, path, body, request, headers = {}) {
  return libraryStub(env, account).fetch(new Request(`${origin}${path}`, {
    method: "POST", headers: clientHeaders(request, headers), body: JSON.stringify(body),
  }));
}

async function accountFor(env, origin, kind, key) {
  const found = await directory(env, origin, `/${kind}/lookup`, kind === "email" ? { email: key } : { sub: key });
  return found.ok ? (await found.json()).account : null;
}

// Sign up: a new account with its email claimed. `web` is the hub, whose sign-in ends after 30 days. Only these
// fields reach the library, so a client can't sign itself up as a Google account.
export function signUp(env, origin, request, body, web = false) {
  const id = randomCode(SQUAD_ID_LENGTH);
  return library(env, origin, id, "/create", {
    name: body?.name, email: body?.email, password: body?.password, device: web ? `web-${randomCode(10)}` : body?.device, web,
  }, request, { "x-account-id": id });
}

// Sign in with an email and password. An email with no account gets the same answer as a wrong password.
export async function passwordSignIn(env, origin, request, body, web = false) {
  const email = normalizeEmail(body?.email);
  if (!email || typeof body?.password !== "string") return json({ error: "Type your email and password." }, 400);
  const account = await accountFor(env, origin, "email", email);
  if (!account) return json({ error: "That email and password don't match." }, 403);
  return library(env, origin, account, "/auth/login", { password: body.password, device: body.device, web }, request);
}

// "K7QM2X9D.<secret>": the account and the token from an email's link.
function splitToken(text) {
  const m = typeof text === "string" ? text.trim().match(/^([0-9A-Z]{8})\.([A-Za-z0-9_-]{20,128})$/) : null;
  return m && isSquadId(m[1]) ? { account: m[1], secret: m[2] } : null;
}

export async function authRoute(request, env, url) {
  const path = url.pathname;
  const origin = url.origin;
  if (path === "/v1/auth/google" && request.method === "GET") return googleStart(env, url);
  if (path === "/v1/auth/google/callback" && request.method === "GET") return googleCallback(request, env, url);
  if (request.method !== "POST") return json({ error: "Not found." }, 404);
  const body = await request.json().catch(() => null);

  if (path === "/v1/auth/forgot") {
    const email = normalizeEmail(body?.email);
    if (!email) return json({ error: "Type the email on your account." }, 400);
    const account = await accountFor(env, origin, "email", email);
    if (account) await library(env, origin, account, "/auth/forgot", {}, request);
    return json({ ok: true });
  }
  if (path === "/v1/auth/reset" || path === "/v1/auth/confirm") {
    const wrong = () => json({ error: "That link isn't right or has run out. Ask for a new one." }, 403);
    const token = splitToken(body?.token);
    if (!token) return wrong();
    const reset = path === "/v1/auth/reset";
    const answer = await library(env, origin, token.account, "/auth/use",
      { secret: token.secret, kinds: reset ? ["reset"] : ["verify", "email"], password: body?.password }, request);
    return answer.status === 404 ? wrong() : answer;
  }
  return json({ error: "Not found." }, 404);
}

// ---- Sign in with Google (OpenID Connect, authorization code with PKCE) ----
//
// The start sets a short HttpOnly cookie with the state, the PKCE verifier and the nonce (and the app's own state
// when the desktop app opened it), so only the browser that started can finish. The callback trades Google's code
// for an ID token straight from Google's token endpoint over TLS, which OpenID Connect Core 3.1.3.7 lets stand in
// for checking its signature; the issuer, audience, expiry, nonce and a verified email are still checked.

function googleReady(env) {
  return Boolean(env.GOOGLE_CLIENT_ID && env.GOOGLE_CLIENT_SECRET);
}

export function redirectUri(origin) {
  return `${origin}/v1/auth/google/callback`;
}

async function googleStart(env, url) {
  if (!googleReady(env)) return page("Google sign-in isn't set up", "This service doesn't have Google sign-in turned on. Sign in with your email instead.", 503);
  const app = url.searchParams.get("app") ?? "";
  if (app && !/^[A-Za-z0-9_-]{16,64}$/.test(app)) return page("Couldn't start", "That sign-in link isn't right. Try again from Clip Engine.", 400);
  const state = randomToken();
  const verifier = randomToken();
  const nonce = randomToken();
  const challenge = base64url(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier))));
  const to = new URL(GOOGLE_AUTH);
  to.search = new URLSearchParams({
    client_id: env.GOOGLE_CLIENT_ID, redirect_uri: redirectUri(url.origin), response_type: "code", scope: "openid email profile",
    state, nonce, code_challenge: challenge, code_challenge_method: "S256", prompt: "select_account",
  }).toString();
  return new Response(null, {
    status: 302,
    headers: {
      location: to.toString(), "cache-control": "no-store",
      "set-cookie": `${GOOGLE_COOKIE}=${state}.${verifier}.${nonce}.${app || "-"}; Path=/v1/auth/google; Max-Age=${GOOGLE_COOKIE_SECONDS}; HttpOnly; Secure; SameSite=Lax`,
    },
  });
}

function readGoogleCookie(request) {
  for (const part of (request.headers.get("cookie") ?? "").split(/;\s*/)) {
    if (!part.startsWith(`${GOOGLE_COOKIE}=`)) continue;
    const [state, verifier, nonce, app] = part.slice(GOOGLE_COOKIE.length + 1).split(".");
    if (state && verifier && nonce && app) return { state, verifier, nonce, app: app === "-" ? "" : app };
  }
  return null;
}

// The claims of Google's ID token once they check out, or null.
export function checkIdToken(idToken, env, nonce, now = Date.now()) {
  try {
    const part = idToken.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    const claims = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(part), (c) => c.charCodeAt(0))));
    const audience = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
    if (!GOOGLE_ISSUERS.includes(claims.iss) || !audience.includes(env.GOOGLE_CLIENT_ID)) return null;
    if (!(claims.exp * 1000 > now) || typeof claims.nonce !== "string" || !sameSecret(claims.nonce, nonce)) return null;
    if (typeof claims.sub !== "string" || claims.email_verified !== true || !normalizeEmail(claims.email)) return null;
    return claims;
  } catch {
    return null;
  }
}

async function googleCallback(request, env, url) {
  const response = await googleFinish(request, env, url);
  response.headers.append("set-cookie", `${GOOGLE_COOKIE}=; Path=/v1/auth/google; Max-Age=0; HttpOnly; Secure; SameSite=Lax`);
  return response;
}

async function googleFinish(request, env, url) {
  const saved = readGoogleCookie(request);
  const state = url.searchParams.get("state") ?? "";
  if (!googleReady(env)) return page("Google sign-in isn't set up", "Sign in with your email instead.", 503);
  if (url.searchParams.get("error")) return page("Not signed in", "Google sign-in was cancelled. Nothing changed.", 400);
  if (!saved || !state || !sameSecret(state, saved.state)) {
    return page("Couldn't sign in", "That sign-in ran out or was started in another browser. Start it again.", 400);
  }
  let claims = null;
  try {
    const answer = await fetch(GOOGLE_TOKEN, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        code: url.searchParams.get("code") ?? "", client_id: env.GOOGLE_CLIENT_ID, client_secret: env.GOOGLE_CLIENT_SECRET,
        redirect_uri: redirectUri(url.origin), grant_type: "authorization_code", code_verifier: saved.verifier,
      }).toString(),
    });
    const reply = await answer.json().catch(() => ({}));
    if (answer.ok && typeof reply.id_token === "string") claims = checkIdToken(reply.id_token, env, saved.nonce);
  } catch {
    // Google couldn't be reached: the page below says so.
  }
  if (!claims) return page("Couldn't sign in", "Google didn't confirm who you are. Try again in a minute.", 502);

  const origin = url.origin;
  const email = normalizeEmail(claims.email);
  const app = Boolean(saved.app);
  const hello = { sub: claims.sub, email, app };
  let account = (await accountFor(env, origin, "google", claims.sub)) ?? (await accountFor(env, origin, "email", email));
  let answer;
  if (account) {
    answer = await library(env, origin, account, "/auth/google", hello, request);
  } else {
    // No account yet: one is made with Google's name and verified email, and no password.
    account = randomCode(SQUAD_ID_LENGTH);
    const name = String(claims.name || claims.given_name || email.split("@")[0]).slice(0, 32);
    answer = await library(env, origin, account, "/create",
      { name, email, google: claims.sub, device: `web-${randomCode(10)}`, web: true }, request, { "x-account-id": account });
    if (answer.ok && app) answer = await library(env, origin, account, "/auth/google", hello, request);
  }
  const reply = await answer.json().catch(() => ({}));
  if (!answer.ok) return page("Couldn't sign in", reply.error ?? "Something went wrong. Try again.", answer.status);
  if (app) return appPage(reply.code, saved.app);
  const signedIn = new Response(null, { status: 303, headers: { location: `${origin}/hub/me`, "cache-control": "no-store" } });
  for (const cookie of sessionCookies(reply.account, reply.token)) signedIn.headers.append("set-cookie", cookie);
  return signedIn;
}

function page(title, text, status = 200, extra = "") {
  const body = `<h1>${escapeHtml(title)}</h1><p class="sub">${escapeHtml(text)}</p>${extra}<p><a class="btn ghost" href="/hub/signin">Back to sign in</a></p>`;
  return new Response(shell({ title: `${title} · Clip Engine`, description: "", body, current: "me" }), {
    status,
    headers: {
      "content-type": "text/html; charset=utf-8", "cache-control": "no-store", "x-content-type-options": "nosniff",
      "referrer-policy": "no-referrer", "content-security-policy": pageCsp("'none'"),
    },
  });
}

// Back to the desktop app: a clipengine:// link with a one-time code and the app's own state, so the app only takes
// a code from a sign-in it started. The code is shown too, for typing on the Cloud page by hand.
function appPage(code, state) {
  const link = `clipengine://cloud/signin/${encodeURIComponent(code)}?state=${encodeURIComponent(state)}`;
  const extra = `<p><a class="btn" id="open" href="${escapeHtml(link)}">Open Clip Engine</a></p>
<p class="sub">Or type this code on Clip Engine's Cloud page under Link another PC: <b style="font-family:var(--mono)">${escapeHtml(code)}</b>. It works once, for 10 minutes.</p>
<script>location.href=document.getElementById("open").href;</script>`;
  return page("Signed in with Google", "Clip Engine opens and finishes signing in.", 200, extra);
}
