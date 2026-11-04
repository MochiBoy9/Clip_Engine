// Clip Engine's plans and the Polar license keys that pay for them.
//
// Pro and Ultra are monthly subscriptions sold through Polar (polar.sh), the merchant of record:
// it takes the payment, handles sales tax and VAT, and emails a license key. Each product has a
// License Keys benefit; its id says which plan a key is for. The app sends the key here, and the
// service checks it with Polar's public license key API, which needs no secret.
//
// Set these in wrangler.toml [vars] from the Polar dashboard:
//   POLAR_ORGANIZATION_ID   Settings > General
//   POLAR_PRO_BENEFIT_ID    the Pro product's License Keys benefit
//   POLAR_ULTRA_BENEFIT_ID  the Ultra product's License Keys benefit

import { randomCode } from "./codes.js";

// What each plan gets. Storage is the cloud library's; lines is the highest recording resolution.
export const PLANS = {
  free: { label: "Free", price: 0, storageMb: 2048, lines: 1080 },
  pro: { label: "Pro", price: 2.99, storageMb: 102_400, lines: 1440 },
  ultra: { label: "Ultra", price: 4.99, storageMb: 256_000, lines: 2160 },
};

// Accounts made before Pro and Ultra had "plus", which was Pro's storage.
export function planName(name) {
  if (name === "plus") return "pro";
  return PLANS[name] ? name : "free";
}

export function storageBytes(env, name) {
  const plan = PLANS[name] ?? PLANS.free;
  const override = Number(env?.[`${name.toUpperCase()}_STORAGE_MB`]);
  return (Number.isFinite(override) && override > 0 ? override : plan.storageMb) * 1024 * 1024;
}

// How long a paid plan keeps working while Polar can't be reached, and how often it's checked again.
export const OFFLINE_GRACE_MS = 14 * 24 * 3600_000;
export const RECHECK_MS = 12 * 3600_000;

export function cleanKey(value) {
  if (typeof value !== "string") return null;
  const key = value.trim();
  return key.length >= 8 && key.length <= 128 && /^[\x21-\x7e]+$/.test(key) ? key : null;
}

// ---- special keys ----
//
// Keys the operator makes in the key maker (/admin): "CEK-XXXX-XXXX-XXXX". Each one gives a plan
// and, if set, extra cloud storage, the colour themes it unlocks and the highest resolution, for a
// number of days from when it's first used, or for good. They're kept in the directory object
// (src/handles.js) and work without Polar.

export const THEME_IDS = ["hazard", "aurora", "ember", "mint", "sakura", "ocean", "volt", "chrome", "arc", "redline"];
export const LINES = [480, 720, 1080, 1440, 2160];
const GRANT_LENGTH = 12;

export function normalizeGrant(value) {
  if (typeof value !== "string") return null;
  const code = value.trim().toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
  if (!code.startsWith("CEK")) return null;
  const rest = code.slice(3);
  return rest.length === GRANT_LENGTH && /^[0-9A-HJKMNP-TV-Z]+$/.test(rest) ? rest : null;
}

export function formatGrant(code) {
  return `CEK-${code.match(/.{1,4}/g).join("-")}`;
}

export function newGrantCode() {
  return randomCode(GRANT_LENGTH);
}

// What a special key gives, checked: the operator's form or a PATCH of one.
export function cleanGrant(body, base = {}) {
  const out = { ...base };
  if (body.plan !== undefined) {
    if (!PLANS[body.plan]) return { error: "Plan must be free, pro or ultra." };
    out.plan = body.plan;
  }
  if (body.days !== undefined) {
    if (body.days === null || body.days === 0) out.days = null;
    else if (Number.isInteger(body.days) && body.days > 0 && body.days <= 36_500) out.days = body.days;
    else return { error: "Days must be a whole number from 1 to 36500, or none for no end." };
  }
  if (body.extraStorageGb !== undefined) {
    const gb = Number(body.extraStorageGb);
    if (!Number.isFinite(gb) || gb < 0 || gb > 10_000) return { error: "Extra storage is 0 to 10000 GB." };
    out.extraMb = Math.round(gb * 1024);
  }
  if (body.themes !== undefined) {
    if (body.themes === null) out.themes = null;
    else if (Array.isArray(body.themes) && body.themes.every((t) => THEME_IDS.includes(t))) out.themes = [...new Set(body.themes)];
    else return { error: `Themes are null (the plan's) or a list of: ${THEME_IDS.join(", ")}.` };
  }
  if (body.lines !== undefined) {
    if (body.lines === null || LINES.includes(body.lines)) out.lines = body.lines;
    else return { error: `Resolution is null (the plan's) or one of ${LINES.join(", ")}.` };
  }
  if (body.maxDevices !== undefined) {
    if (Number.isInteger(body.maxDevices) && body.maxDevices >= 1 && body.maxDevices <= 100) out.maxDevices = body.maxDevices;
    else return { error: "PCs per key is 1 to 100." };
  }
  if (body.note !== undefined) out.note = String(body.note ?? "").slice(0, 200);
  if (body.revoked !== undefined) out.revoked = Boolean(body.revoked);
  return { grant: out };
}

async function checkGrant(env, code, device) {
  if (!env?.HANDLES) return { ok: false, rejected: false, error: "Special keys can't be checked right now." };
  const stub = env.HANDLES.get(env.HANDLES.idFromName("handles"));
  const answer = await stub.fetch(new Request("https://handles/grant/check", {
    method: "POST",
    body: JSON.stringify({ code, device: typeof device === "string" ? device.slice(0, 80) : "" }),
  }));
  return answer.json();
}

// Checks a key: a special key from the key maker, or a Polar license key. Answers { ok: true, plan,
// license, display, expires, features } for a key that gives a plan now, { ok: false, rejected: true,
// error } for one that doesn't, and { ok: false, rejected: false, error } when it couldn't be checked.
// features: { extraMb, themes (null = the plan's), lines (null = the plan's) }.
export async function checkLicense(env, value, device) {
  const grant = normalizeGrant(value);
  if (grant) return checkGrant(env, grant, device);
  const key = cleanKey(value);
  if (!key) return { ok: false, rejected: true, error: "That doesn't look like a license key. Copy it from your receipt email." };
  const organization = env?.POLAR_ORGANIZATION_ID;
  if (!organization) return { ok: false, rejected: false, error: "Subscriptions aren't open yet." };

  const api = (env.POLAR_API ?? "https://api.polar.sh").replace(/\/$/, "");
  const send = env.POLAR_FETCH ?? fetch;
  let response;
  try {
    response = await send(`${api}/v1/customer-portal/license-keys/validate`, {
      method: "POST",
      headers: { "content-type": "application/json", accept: "application/json" },
      body: JSON.stringify({ key, organization_id: organization }),
    });
  } catch {
    return { ok: false, rejected: false, error: "Couldn't reach Polar to check the key. Try again in a minute." };
  }
  if (response.status === 404) return { ok: false, rejected: true, error: "That key wasn't found. Check it matches the one in your receipt email." };
  if (response.status >= 400 && response.status < 500 && response.status !== 429) {
    return { ok: false, rejected: true, error: "Polar didn't accept that key." };
  }
  if (!response.ok) return { ok: false, rejected: false, error: `Polar couldn't check the key right now (${response.status}).` };

  let info;
  try {
    info = await response.json();
  } catch {
    return { ok: false, rejected: false, error: "Polar sent an answer that couldn't be read." };
  }
  if (info?.status !== "granted") {
    return {
      ok: false,
      rejected: true,
      error: info?.status === "revoked"
        ? "This key was revoked, usually because the subscription ended or was refunded."
        : "This key is turned off. Contact support if that's a mistake.",
    };
  }
  const expires = info.expires_at ? Date.parse(info.expires_at) : null;
  if (expires && expires <= Date.now()) return { ok: false, rejected: true, error: "This key has run out. Renew the subscription to keep your plan." };
  const plan = info.benefit_id && info.benefit_id === env.POLAR_ULTRA_BENEFIT_ID ? "ultra"
    : info.benefit_id && info.benefit_id === env.POLAR_PRO_BENEFIT_ID ? "pro"
    : null;
  if (!plan) return { ok: false, rejected: true, error: "This key isn't for a Clip Engine plan." };
  return {
    ok: true,
    plan,
    license: String(info.id ?? key).slice(0, 64),
    display: typeof info.display_key === "string" ? info.display_key.slice(0, 64) : "",
    expires,
    features: { extraMb: 0, themes: null, lines: null },
  };
}

// The plan's details as the app shows them, with a special key's changes.
export function planJson(env, name, features) {
  const plan = PLANS[name] ?? PLANS.free;
  const extra = (features?.extraMb ?? 0) * 1024 * 1024;
  return {
    plan: name,
    label: plan.label,
    price: plan.price,
    storage: storageBytes(env, name) + extra,
    extraStorage: extra,
    lines: features?.lines ?? plan.lines,
    themes: features?.themes ?? null,
  };
}
