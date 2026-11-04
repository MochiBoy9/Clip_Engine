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

// What each plan gets. Storage is the cloud library's; lines is the highest recording resolution.
export const PLANS = {
  free: { label: "Free", price: 0, storageMb: 2048, lines: 1080 },
  pro: { label: "Pro", price: 2.99, storageMb: 102_400, lines: 1440 },
  ultra: { label: "Ultra", price: 9.99, storageMb: 256_000, lines: 2160 },
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

// Asks Polar about a key. Answers { ok: true, plan, license, display, expires } for a key that pays
// for a plan now, { ok: false, rejected: true, error } for one that doesn't, and
// { ok: false, rejected: false, error } when Polar couldn't be asked.
export async function checkLicense(env, value) {
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
  };
}

// The plan's details as the app shows them.
export function planJson(env, name) {
  const plan = PLANS[name] ?? PLANS.free;
  return { plan: name, label: plan.label, price: plan.price, storage: storageBytes(env, name), lines: plan.lines };
}
