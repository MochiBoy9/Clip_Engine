// Account emails (confirming an address, resetting a password, an email change), sent through Resend
// (https://resend.com). RESEND_API_KEY is a secret and EMAIL_FROM a var with an address on a domain verified there.
// Without both, nothing is sent: with EMAIL_DEV_LOG = "true" (for wrangler dev) the email is printed instead.

const MAX_EMAIL = 254;

// Trimmed and lowercased, or null when it isn't an address.
export function normalizeEmail(value) {
  if (typeof value !== "string") return null;
  const email = value.trim().toLowerCase();
  return email.length <= MAX_EMAIL && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) ? email : null;
}

export async function sendEmail(env, { to, subject, text }) {
  if (!env.RESEND_API_KEY || !env.EMAIL_FROM) {
    if (env.EMAIL_DEV_LOG === "true") console.log(`[email to ${to}] ${subject}\n${text}`);
    return false;
  }
  try {
    const answer = await fetch("https://api.resend.com/emails", {
      method: "POST",
      headers: { authorization: `Bearer ${env.RESEND_API_KEY}`, "content-type": "application/json" },
      body: JSON.stringify({ from: env.EMAIL_FROM, to: [to], subject, text }),
    });
    return answer.ok;
  } catch {
    return false;
  }
}
