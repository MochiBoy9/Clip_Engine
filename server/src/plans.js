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
//
// Donations are a one-time, pay-what-you-want Polar product with its own License Keys benefit:
//   POLAR_DONATION_PRODUCT_ID  the Donation product
//   POLAR_DONATION_BENEFIT_ID  its License Keys benefit
// How much someone gave comes from their Polar orders, which needs an organization access token with
// orders:read, kept as a secret: npx wrangler secret put POLAR_ACCESS_TOKEN

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
// Keys the operator makes in the key maker: "CEK-XXXX-XXXX-XXXX". Each one gives a plan and, if set, a
// role (a title such as Content Creator), extra cloud storage, the colour themes it unlocks, the highest
// resolution, perks (a direct line to the developer, bug reports straight to them), a place in the
// credits, and a reason for giving it. It lasts a number of minutes from when it's first used, until a
// set date, or for good. They're kept in the directory object (src/handles.js) and work without Polar.

export const THEME_IDS = ["hazard", "aurora", "ember", "mint", "sakura", "ocean", "volt", "chrome", "arc", "redline"];
export const LINES = [480, 720, 1080, 1440, 2160];
const GRANT_LENGTH = 12;

// Titles a special key can give on top of its plan. The plan still sets storage, resolution and themes.
// Supporter is also what a donation's key gives on its own (see donations below).
export const ROLES = {
  creator: { label: "Content Creator" },
  special: { label: "Special" },
  exclusive: { label: "Exclusive" },
  supporter: { label: "Supporter" },
};

// What a special key can let its holder do besides the plan.
//   contact  a direct line: messages the operator reads and answers in the key maker
//   bugs     bug reports that go straight to the operator's inbox
export const PERKS = ["contact", "bugs"];

// The groups in the credits, in the order they're shown: subscribers first, then supporters who donated, then the
// roles the operator gives.
export const CREDIT_GROUPS = ["ultra", "pro", "supporter", "exclusive", "creator", "special"];

export function roleLabel(role) {
  return ROLES[role]?.label ?? null;
}

// The badge an account shows on its profile, its clips and the hub: its role if a special key gave one,
// otherwise Pro or Ultra. Free has none. Anyone with a badge can hide it.
export function badgeFor(plan, role) {
  if (ROLES[role]) return { kind: role, label: ROLES[role].label };
  if (plan === "pro" || plan === "ultra") return { kind: plan, label: PLANS[plan].label };
  return null;
}

const MAX_MINUTES = 36_500 * 1440;
const LATEST_END = Date.UTC(2200, 0, 1);

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

// What a special key gives, checked: the operator's form or a PATCH of one. The length is minutes (or,
// as before, days), counted from first use; endsAt is a fixed end, in ms since 1970. Either can be null.
export function cleanGrant(body, base = {}) {
  const out = { ...base };
  if (body.plan !== undefined) {
    if (!PLANS[body.plan]) return { error: "Plan must be free, pro or ultra." };
    out.plan = body.plan;
  }
  if (body.role !== undefined) {
    if (body.role === null || body.role === "") out.role = null;
    else if (ROLES[body.role]) out.role = body.role;
    else return { error: `Role is none or one of: ${Object.keys(ROLES).join(", ")}.` };
  }
  if (body.days !== undefined && body.minutes === undefined) {
    if (body.days === null || body.days === 0) out.minutes = null;
    else if (Number.isInteger(body.days) && body.days > 0 && body.days <= 36_500) out.minutes = body.days * 1440;
    else return { error: "Days must be a whole number from 1 to 36500, or none for no end." };
  }
  if (body.minutes !== undefined) {
    if (body.minutes === null || body.minutes === 0) out.minutes = null;
    else if (Number.isInteger(body.minutes) && body.minutes > 0 && body.minutes <= MAX_MINUTES) out.minutes = body.minutes;
    else return { error: "The length must be from 1 minute to 100 years, or none for no end." };
  }
  if (body.endsAt !== undefined) {
    if (body.endsAt === null || body.endsAt === 0) out.endsAt = null;
    else if (Number.isInteger(body.endsAt) && body.endsAt > 0 && body.endsAt < LATEST_END) out.endsAt = body.endsAt;
    else return { error: "The end date must be a time in ms, or none." };
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
  if (body.perks !== undefined) {
    if (Array.isArray(body.perks) && body.perks.every((p) => PERKS.includes(p))) out.perks = PERKS.filter((p) => body.perks.includes(p));
    else return { error: `Perks are a list of: ${PERKS.join(", ")}.` };
  }
  if (body.credits !== undefined) out.credits = Boolean(body.credits);
  if (body.reason !== undefined) out.reason = String(body.reason ?? "").trim().slice(0, 300);
  if (body.showReason !== undefined) out.showReason = Boolean(body.showReason);
  if (body.note !== undefined) out.note = String(body.note ?? "").slice(0, 200);
  if (body.revoked !== undefined) out.revoked = Boolean(body.revoked);
  return { grant: out };
}

// The group a credited special key's name goes in: its role, or its plan.
export function creditGroup(check) {
  if (check.role && CREDIT_GROUPS.includes(check.role)) return check.role;
  return check.plan === "ultra" || check.plan === "pro" ? check.plan : "special";
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
// features: { extraMb, themes (null = the plan's), lines (null = the plan's) }. A special key also has
// role, perks, credits (whether its name may be in the credits) and reason (only if it's shown to them).
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
  const donationKey = Boolean(info.benefit_id) && info.benefit_id === env.POLAR_DONATION_BENEFIT_ID;
  const plan = info.benefit_id && info.benefit_id === env.POLAR_ULTRA_BENEFIT_ID ? "ultra"
    : info.benefit_id && info.benefit_id === env.POLAR_PRO_BENEFIT_ID ? "pro"
    : donationKey ? "free"
    : null;
  if (!plan) return { ok: false, rejected: true, error: "This key isn't for a Clip Engine plan." };
  // Anyone who donated with the same Polar account gets the supporter perks, whether this key is the donation's
  // own or a subscription's.
  const cents = await donatedCents(env, info.customer_id ?? info.customer?.id);
  if (donationKey && cents === 0) {
    return { ok: false, rejected: true, error: "This donation hasn't gone through, or was refunded. Try again once Polar emails your receipt." };
  }
  const donation = donationKey || cents > 0 ? donationPerks(env, cents) : null;
  return {
    ok: true,
    plan,
    license: String(info.id ?? key).slice(0, 64),
    display: typeof info.display_key === "string" ? info.display_key.slice(0, 64) : "",
    expires: donationKey ? null : expires,
    features: { extraMb: donation?.extraMb ?? 0, themes: null, lines: null },
    // A subscriber keeps their plan's badge; a donation's key on its own makes its holder a Supporter.
    role: donation && plan === "free" ? "supporter" : null,
    perks: [],
    donation,
  };
}

// ---- donations ----
//
// Donations are any amount, through Polar. They're there to keep Clip Engine developed and maintained, and give a
// few small thank-yous: the Supporter badge, a little more cloud storage for each dollar given (up to a cap), and
// from $5 in all, a name in the credits. All three numbers can be changed in wrangler.toml [vars].

export const DONATION_DEFAULTS = { gbPerDollar: 1, maxGb: 25, creditsFrom: 5 };

export function donationSettings(env) {
  const number = (name, fallback) => {
    const value = Number(env?.[name]);
    return env?.[name] !== undefined && env?.[name] !== "" && Number.isFinite(value) && value >= 0 ? value : fallback;
  };
  return {
    gbPerDollar: number("DONATION_GB_PER_DOLLAR", DONATION_DEFAULTS.gbPerDollar),
    maxGb: number("DONATION_MAX_GB", DONATION_DEFAULTS.maxGb),
    creditsFrom: number("DONATION_CREDITS_FROM", DONATION_DEFAULTS.creditsFrom),
  };
}

// What a donation gives. cents is everything the person gave, after refunds and before tax, or null when it
// couldn't be looked up (no access token, or Polar didn't answer): then it's the smallest thank-you, without the
// credits, until the next check.
export function donationPerks(env, cents) {
  const { gbPerDollar, maxGb, creditsFrom } = donationSettings(env);
  const dollars = cents === null ? 0 : cents / 100;
  const gb = Math.min(maxGb, Math.max(1, Math.floor(dollars * gbPerDollar)));
  return {
    cents,
    extraMb: maxGb > 0 && gbPerDollar > 0 ? Math.round(gb * 1024) : 0,
    credits: cents !== null && dollars >= creditsFrom,
  };
}

// Everything a Polar customer gave through the Donation product, in cents: paid orders, less what was refunded,
// before tax. 0 when they haven't donated, null when it can't be told.
export async function donatedCents(env, customer) {
  const product = env?.POLAR_DONATION_PRODUCT_ID;
  const token = env?.POLAR_ACCESS_TOKEN;
  if (!product || !token || typeof customer !== "string" || !customer) return null;
  const api = (env.POLAR_API ?? "https://api.polar.sh").replace(/\/$/, "");
  const send = env.POLAR_FETCH ?? fetch;
  let total = 0;
  try {
    for (let page = 1; page <= 10; page++) {
      const query = new URLSearchParams({ customer_id: customer, product_id: product, limit: "100", page: String(page) });
      if (env.POLAR_ORGANIZATION_ID) query.set("organization_id", env.POLAR_ORGANIZATION_ID);
      const response = await send(`${api}/v1/orders/?${query}`, {
        headers: { authorization: `Bearer ${token}`, accept: "application/json" },
      });
      if (!response.ok) return null;
      const list = await response.json();
      for (const order of list?.items ?? []) {
        if (order.customer_id && order.customer_id !== customer) continue;
        if (order.product_id && order.product_id !== product) continue;
        const paid = order.paid === true || ["paid", "partially_refunded"].includes(order.status);
        if (!paid) continue;
        const net = Number(order.net_amount ?? order.subtotal_amount ?? order.amount ?? 0);
        const refunded = Number(order.refunded_amount ?? 0) - Number(order.refunded_tax_amount ?? 0);
        total += Math.max(0, (Number.isFinite(net) ? net : 0) - (Number.isFinite(refunded) ? Math.max(0, refunded) : 0));
      }
      const pages = Number(list?.pagination?.max_page ?? 1);
      if (!(page < pages)) break;
    }
  } catch {
    return null;
  }
  return Math.round(total);
}

// The plan's details as the app shows them, with a special key's changes.
export function planJson(env, name, features, role = null) {
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
    role: ROLES[role] ? role : null,
    roleLabel: roleLabel(role),
  };
}
