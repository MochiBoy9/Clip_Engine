// Squad ids, invite codes, member tokens and moment ids.

// Crockford's base32: no I, L, O or U, so a code read aloud or typed from a screenshot survives.
const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

export const SQUAD_ID_LENGTH = 8;
export const INVITE_SECRET_LENGTH = 12;

export function randomCode(length) {
  const bytes = crypto.getRandomValues(new Uint8Array(length));
  let out = "";
  for (const b of bytes) out += ALPHABET[b & 31];
  return out;
}

export function isSquadId(id) {
  return typeof id === "string" && id.length === SQUAD_ID_LENGTH && [...id].every((c) => ALPHABET.includes(c));
}

// What a person typed or pasted, as the 20 characters of a code: case, spaces and dashes don't
// matter, O reads as 0 and I or L as 1. Null when it isn't an invite code.
export function normalizeInvite(text) {
  if (typeof text !== "string") return null;
  // A link carries the code at its end: clipengine://squad/join/<code> or .../squad#<code>.
  const tail = text.trim().split(/[/#?=]/).pop();
  const code = tail
    .toUpperCase()
    .replace(/[\s-]/g, "")
    .replace(/O/g, "0")
    .replace(/[IL]/g, "1");
  if (code.length !== SQUAD_ID_LENGTH + INVITE_SECRET_LENGTH) return null;
  if (![...code].every((c) => ALPHABET.includes(c))) return null;
  return { squadId: code.slice(0, SQUAD_ID_LENGTH), secret: code.slice(SQUAD_ID_LENGTH) };
}

// "K7QM-2X9D-PA4R-HT6W-ZC3N": the squad id, then the secret, in groups of four.
export function formatInvite(squadId, secret) {
  return (squadId + secret).match(/.{1,4}/g).join("-");
}

export function randomToken() {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return base64url(bytes);
}

export function base64url(bytes) {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

// Comparing secrets without returning early, so timing doesn't give away how much matched.
export function sameSecret(a, b) {
  if (typeof a !== "string" || typeof b !== "string" || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

// Moment ids sort by time: "m" + milliseconds in base32 + 6 random characters. Always matches
// the engine's IsSquadMomentId (letters and digits, at most 64).
export function newMomentId(nowMs) {
  let time = "";
  let n = Math.max(0, Math.floor(nowMs));
  for (let i = 0; i < 9; i++) {
    time = ALPHABET[n % 32] + time;
    n = Math.floor(n / 32);
  }
  return "m" + time + randomCode(6);
}

export function isMomentId(id) {
  return typeof id === "string" && /^[A-Za-z0-9_-]{1,64}$/.test(id);
}

export function newMemberId() {
  return "p" + randomCode(10);
}

export function isMemberId(id) {
  return typeof id === "string" && /^p[0-9A-Z]{10}$/.test(id);
}
