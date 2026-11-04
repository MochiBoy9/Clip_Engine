// The key maker: the operator's page at /admin for making and managing special keys. The page
// itself is public but does nothing on its own: every call it makes needs the ADMIN_TOKEN secret,
// which the operator types in and which stays in that browser tab (sessionStorage) only.

const THEMES = [
  ["hazard", "Hazard", "#FFC21A"], ["aurora", "Aurora", "#9B87FF"], ["ember", "Ember", "#FF6A3D"],
  ["mint", "Mint", "#2FD6A8"], ["sakura", "Sakura", "#FF6FAE"], ["ocean", "Ocean", "#3AA8FF"],
  ["volt", "Volt", "#C5F135"], ["chrome", "Chrome", "#D3DAE3"], ["arc", "Arc", "#22E1F0"], ["redline", "Redline", "#FF3D5A"],
];

export function adminPage() {
  return new Response(PAGE, {
    headers: {
      "content-type": "text/html; charset=utf-8",
      "cache-control": "no-store",
      "x-robots-tag": "noindex, nofollow",
      "referrer-policy": "no-referrer",
      "x-content-type-options": "nosniff",
      "content-security-policy":
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
    },
  });
}

const PAGE = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>Clip Engine key maker</title>
<style>
:root {
  --bg: #0b0c0e; --surface: #131418; --surface-2: #191a1f; --inset: #0d0e11; --line: #25272d; --line-strong: #363942;
  --text: #eeece6; --muted: #a7a59d; --dim: #706f69; --accent: #ffc21a; --on-accent: #0b0c0e; --bad: #ff3d52; --ok: #34d17d;
  --mono: "JetBrains Mono", ui-monospace, Consolas, monospace; --body: Inter, "Segoe UI", system-ui, sans-serif;
  color-scheme: dark;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.5 var(--body); }
main { max-width: 1100px; margin: 0 auto; padding: 28px 16px 64px; }
h1 { font-size: 22px; letter-spacing: .04em; text-transform: uppercase; margin: 0 0 4px; }
h2 { font-size: 13px; letter-spacing: .12em; text-transform: uppercase; color: var(--muted); margin: 0 0 14px; }
p.lead { color: var(--muted); margin: 0 0 24px; }
.card { background: var(--surface); border: 1px solid var(--line); padding: 20px; margin-bottom: 20px; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px 18px; }
label.field { display: flex; flex-direction: column; gap: 6px; font-size: 12px; color: var(--muted); text-transform: uppercase; letter-spacing: .08em; }
input, select, textarea { background: var(--inset); color: var(--text); border: 1px solid var(--line-strong); padding: 8px 10px; font: inherit; font-size: 14px; text-transform: none; letter-spacing: 0; width: 100%; }
input:focus, select:focus { outline: 2px solid var(--accent); outline-offset: -1px; }
button { background: var(--surface-2); color: var(--text); border: 1px solid var(--line-strong); padding: 8px 14px; font: inherit; cursor: pointer; }
button:hover { border-color: var(--accent); }
button.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); font-weight: 600; }
button.small { padding: 4px 9px; font-size: 12px; }
button.danger:hover { border-color: var(--bad); color: var(--bad); }
.row { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
.themes { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 4px; }
.theme { display: inline-flex; align-items: center; gap: 6px; border: 1px solid var(--line-strong); padding: 5px 9px; font-size: 13px; cursor: pointer; text-transform: none; letter-spacing: 0; color: var(--text); }
.theme input { width: auto; margin: 0; accent-color: var(--accent); }
.swatch { width: 12px; height: 12px; display: inline-block; }
.check { display: inline-flex; gap: 8px; align-items: center; color: var(--text); font-size: 14px; text-transform: none; letter-spacing: 0; }
.check input { width: auto; accent-color: var(--accent); }
.msg { margin-top: 12px; min-height: 1.5em; }
.msg.bad { color: var(--bad); }
.msg.ok { color: var(--ok); }
.made { margin-top: 14px; display: none; }
.made textarea { font-family: var(--mono); min-height: 90px; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th { text-align: left; color: var(--dim); font-weight: 500; text-transform: uppercase; letter-spacing: .08em; font-size: 11px; padding: 6px 8px; border-bottom: 1px solid var(--line-strong); }
td { padding: 9px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
td.code { font-family: var(--mono); white-space: nowrap; }
.pill { display: inline-block; padding: 1px 7px; font-size: 11px; text-transform: uppercase; letter-spacing: .08em; border: 1px solid var(--line-strong); }
.pill.active { color: var(--ok); border-color: var(--ok); }
.pill.unused { color: var(--accent); border-color: var(--accent); }
.pill.expired, .pill.revoked { color: var(--dim); }
.actions { display: flex; gap: 6px; flex-wrap: wrap; }
.sub { color: var(--dim); font-size: 12px; }
.hidden { display: none !important; }
.tablewrap { overflow-x: auto; }
.editing { border-color: var(--accent); }
</style>
</head>
<body>
<main>
  <h1>Key maker</h1>
  <p class="lead">Special keys give a plan or extra features for a set time. Type one into Settings, Plan in the app, like a subscription key.</p>

  <section class="card" id="signin">
    <h2>Sign in</h2>
    <div class="row">
      <input id="token" type="password" autocomplete="current-password" placeholder="Your ADMIN_TOKEN" style="max-width:360px">
      <button class="primary" id="go">Open</button>
    </div>
    <div class="msg" id="signin-msg"></div>
  </section>

  <div id="app" class="hidden">
    <section class="card" id="form-card">
      <div class="row" style="justify-content:space-between"><h2 id="form-title">Make keys</h2><button class="small" id="signout">Sign out</button></div>
      <div class="grid">
        <label class="field">Plan
          <select id="plan"><option value="pro">Pro</option><option value="ultra">Ultra</option><option value="free">Free (only the extras below)</option></select>
        </label>
        <label class="field">How long
          <select id="length">
            <option value="7">1 week</option><option value="30" selected>1 month</option><option value="90">3 months</option>
            <option value="180">6 months</option><option value="365">1 year</option><option value="0">Unlimited</option><option value="custom">Custom days</option>
          </select>
        </label>
        <label class="field hidden" id="days-field">Days
          <input id="days" type="number" min="1" max="36500" value="14">
        </label>
        <label class="field">Extra cloud storage (GB)
          <input id="extra" type="number" min="0" max="10000" step="1" value="0">
        </label>
        <label class="field">Highest resolution
          <select id="lines">
            <option value="">The plan's</option><option value="2160">4K</option><option value="1440">1440p</option>
            <option value="1080">1080p</option><option value="720">720p</option><option value="480">480p</option>
          </select>
        </label>
        <label class="field">PCs per key
          <input id="devices" type="number" min="1" max="100" value="3">
        </label>
        <label class="field" id="count-field">How many keys
          <input id="count" type="number" min="1" max="100" value="1">
        </label>
        <label class="field">Note (only you see it)
          <input id="note" maxlength="200" placeholder="Who it's for">
        </label>
      </div>
      <div style="margin-top:16px">
        <label class="check"><input type="checkbox" id="plan-themes" checked> Colour themes: the plan's</label>
        <div class="themes hidden" id="themes"></div>
      </div>
      <p class="sub">The time starts the first time the key is used, not when it's made.</p>
      <div class="row" style="margin-top:8px">
        <button class="primary" id="save">Make keys</button>
        <button class="hidden" id="cancel">Cancel</button>
      </div>
      <div class="msg" id="form-msg"></div>
      <div class="made" id="made">
        <div class="row" style="justify-content:space-between;margin-bottom:6px"><span class="sub">New keys. Copy them now or from the list below.</span><button class="small" id="copy-made">Copy all</button></div>
        <textarea id="made-keys" readonly></textarea>
      </div>
    </section>

    <section class="card">
      <div class="row" style="justify-content:space-between;margin-bottom:10px">
        <h2 style="margin:0">Keys</h2>
        <div class="row">
          <select id="filter" style="width:auto"><option value="">All</option><option value="unused">Unused</option><option value="active">Active</option><option value="expired">Run out</option><option value="revoked">Turned off</option></select>
          <input id="search" placeholder="Search keys or notes" style="width:200px">
          <button class="small" id="refresh">Refresh</button>
        </div>
      </div>
      <div class="tablewrap">
        <table>
          <thead><tr><th>Key</th><th>Gives</th><th>Length</th><th>Status</th><th>PCs</th><th>Note</th><th></th></tr></thead>
          <tbody id="list"></tbody>
        </table>
      </div>
      <p class="sub" id="empty">No keys yet.</p>
    </section>
  </div>
</main>
<script>
(() => {
  const THEMES = ${JSON.stringify(THEMES)};
  const RES = { 2160: "4K", 1440: "1440p", 1080: "1080p", 720: "720p", 480: "480p" };
  const PLAN = { free: "Free", pro: "Pro", ultra: "Ultra" };
  const $ = (id) => document.getElementById(id);
  let token = "";
  let keys = [];
  let editing = null;

  try { token = sessionStorage.getItem("ce-admin") || ""; } catch {}

  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  const date = (ms) => ms ? new Date(ms).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" }) : "";

  function message(id, text, ok) {
    const el = $(id);
    el.textContent = text || "";
    el.className = "msg" + (text ? (ok ? " ok" : " bad") : "");
  }

  async function call(method, path, body) {
    const response = await fetch(path, {
      method,
      headers: { authorization: "Bearer " + token, "content-type": "application/json" },
      body: body ? JSON.stringify(body) : undefined,
    });
    const data = await response.json().catch(() => ({}));
    if (response.status === 403) { signOut("That token wasn't accepted."); throw new Error("Not allowed."); }
    if (!response.ok) throw new Error(data.error || ("The service answered " + response.status + "."));
    return data;
  }

  function signOut(text) {
    token = "";
    try { sessionStorage.removeItem("ce-admin"); } catch {}
    $("app").classList.add("hidden");
    $("signin").classList.remove("hidden");
    message("signin-msg", text || "");
  }

  async function open() {
    try {
      await load();
      try { sessionStorage.setItem("ce-admin", token); } catch {}
      $("signin").classList.add("hidden");
      $("app").classList.remove("hidden");
      message("signin-msg", "");
    } catch (e) {
      if (token) message("signin-msg", e.message);
    }
  }

  async function load() {
    keys = (await call("GET", "/v1/admin/grants")).grants || [];
    render();
  }

  function gives(k) {
    const parts = [PLAN[k.plan] || k.plan];
    if (k.extraStorageGb > 0) parts.push("+" + k.extraStorageGb + " GB");
    if (k.lines) parts.push(RES[k.lines] || k.lines + "p");
    if (k.themes) parts.push(k.themes.length + (k.themes.length === 1 ? " theme" : " themes"));
    return parts.join(" · ");
  }

  function length(k) {
    const span = !k.days ? "Unlimited" : k.days % 365 === 0 ? (k.days / 365) + (k.days === 365 ? " year" : " years")
      : k.days === 30 ? "1 month" : k.days % 30 === 0 ? (k.days / 30) + " months" : k.days + " days";
    const end = k.expires ? '<div class="sub">' + (k.status === "expired" ? "Ended " : "Ends ") + esc(date(k.expires)) + "</div>"
      : k.days && !k.firstUsed ? '<div class="sub">Starts when used</div>' : "";
    return esc(span) + end;
  }

  const STATUS = { unused: "Unused", active: "Active", expired: "Run out", revoked: "Turned off" };

  function render() {
    const filter = $("filter").value;
    const search = $("search").value.trim().toLowerCase();
    const shown = keys.filter((k) => (!filter || k.status === filter) &&
      (!search || k.code.toLowerCase().includes(search) || (k.note || "").toLowerCase().includes(search)));
    $("empty").textContent = keys.length ? "No keys match." : "No keys yet.";
    $("empty").classList.toggle("hidden", shown.length > 0);
    $("list").innerHTML = shown.map((k) =>
      "<tr>" +
      '<td class="code">' + esc(k.code) + '<div class="sub">Made ' + esc(date(k.created)) + (k.lastUsed ? " · used " + esc(date(k.lastUsed)) : "") + "</div></td>" +
      "<td>" + esc(gives(k)) + "</td>" +
      "<td>" + length(k) + "</td>" +
      '<td><span class="pill ' + esc(k.status) + '">' + esc(STATUS[k.status] || k.status) + "</span></td>" +
      "<td>" + esc(k.devices + " / " + k.maxDevices) + "</td>" +
      "<td>" + esc(k.note) + "</td>" +
      '<td><div class="actions">' +
        '<button class="small" data-act="copy" data-code="' + esc(k.code) + '">Copy</button>' +
        '<button class="small" data-act="edit" data-code="' + esc(k.code) + '">Edit</button>' +
        (k.devices > 0 ? '<button class="small" data-act="reset" data-code="' + esc(k.code) + '">Free PCs</button>' : "") +
        (k.firstUsed ? '<button class="small" data-act="restart" data-code="' + esc(k.code) + '">Restart time</button>' : "") +
        '<button class="small danger" data-act="' + (k.status === "revoked" ? "on" : "off") + '" data-code="' + esc(k.code) + '">' +
          (k.status === "revoked" ? "Turn on" : "Turn off") + "</button>" +
      "</div></td></tr>").join("");
  }

  // ---- the form ----

  $("themes").innerHTML = THEMES.map(([id, name, color]) =>
    '<label class="theme"><input type="checkbox" value="' + id + '"' + (id === "hazard" ? " checked" : "") + '><span class="swatch" style="background:' + color + '"></span>' + name + "</label>").join("");

  $("plan-themes").addEventListener("change", () => $("themes").classList.toggle("hidden", $("plan-themes").checked));
  $("length").addEventListener("change", () => $("days-field").classList.toggle("hidden", $("length").value !== "custom"));

  function readForm() {
    const choice = $("length").value;
    const days = choice === "custom" ? Number($("days").value) : Number(choice);
    return {
      plan: $("plan").value,
      days: days > 0 ? days : null,
      extraStorageGb: Number($("extra").value) || 0,
      lines: $("lines").value ? Number($("lines").value) : null,
      themes: $("plan-themes").checked ? null : [...$("themes").querySelectorAll("input:checked")].map((i) => i.value),
      maxDevices: Number($("devices").value),
      note: $("note").value,
    };
  }

  function fillForm(k) {
    $("plan").value = k.plan;
    const preset = ["7", "30", "90", "180", "365"].includes(String(k.days)) ? String(k.days) : !k.days ? "0" : "custom";
    $("length").value = preset;
    $("days").value = k.days || 14;
    $("days-field").classList.toggle("hidden", preset !== "custom");
    $("extra").value = k.extraStorageGb;
    $("lines").value = k.lines ? String(k.lines) : "";
    $("plan-themes").checked = !k.themes;
    $("themes").classList.toggle("hidden", !k.themes);
    for (const box of $("themes").querySelectorAll("input")) box.checked = k.themes ? k.themes.includes(box.value) : box.value === "hazard";
    $("devices").value = k.maxDevices;
    $("note").value = k.note || "";
  }

  function startEdit(code) {
    const k = keys.find((x) => x.code === code);
    if (!k) return;
    editing = code;
    fillForm(k);
    $("form-title").textContent = "Change " + code;
    $("save").textContent = "Save changes";
    $("cancel").classList.remove("hidden");
    $("count-field").classList.add("hidden");
    $("form-card").classList.add("editing");
    $("made").style.display = "none";
    message("form-msg", "Changes apply the next time the app checks the key (within 12 hours, or right away when it's typed in again).", true);
    $("form-card").scrollIntoView({ behavior: "smooth" });
  }

  function stopEdit() {
    editing = null;
    $("form-title").textContent = "Make keys";
    $("save").textContent = "Make keys";
    $("cancel").classList.add("hidden");
    $("count-field").classList.remove("hidden");
    $("form-card").classList.remove("editing");
    message("form-msg", "");
  }

  $("save").addEventListener("click", async () => {
    const body = readForm();
    if (body.themes && body.themes.length === 0) return message("form-msg", "Pick at least one colour theme, or tick the plan's.");
    $("save").disabled = true;
    try {
      if (editing) {
        await call("PATCH", "/v1/admin/grants/" + encodeURIComponent(editing), body);
        stopEdit();
        message("form-msg", "Saved.", true);
      } else {
        const made = (await call("POST", "/v1/admin/grants", { ...body, count: Number($("count").value) || 1 })).grants || [];
        $("made-keys").value = made.map((k) => k.code).join("\\n");
        $("made").style.display = "block";
        message("form-msg", made.length === 1 ? "Made 1 key." : "Made " + made.length + " keys.", true);
      }
      await load();
    } catch (e) {
      message("form-msg", e.message);
    } finally {
      $("save").disabled = false;
    }
  });
  $("cancel").addEventListener("click", stopEdit);
  $("copy-made").addEventListener("click", () => copy($("made-keys").value, "form-msg"));

  async function copy(text, where) {
    try { await navigator.clipboard.writeText(text); message(where, "Copied.", true); }
    catch { message(where, "Couldn't copy. Select the text and copy it yourself."); }
  }

  // ---- the list ----

  $("list").addEventListener("click", async (event) => {
    const button = event.target.closest("button[data-act]");
    if (!button) return;
    const code = button.dataset.code;
    const act = button.dataset.act;
    if (act === "copy") return copy(code, "form-msg");
    if (act === "edit") return startEdit(code);
    const change = { off: { revoked: true }, on: { revoked: false }, reset: { resetDevices: true }, restart: { restart: true } }[act];
    const ask = { off: "Turn off " + code + "? Whoever uses it goes back to their own plan.",
      reset: "Free the PCs on " + code + "? Any PC can then use it again, up to its limit.",
      restart: "Restart the time on " + code + "? Its days count again from the next time it's used." }[act];
    if (ask && !confirm(ask)) return;
    try { await call("PATCH", "/v1/admin/grants/" + encodeURIComponent(code), change); await load(); }
    catch (e) { message("form-msg", e.message); }
  });
  $("filter").addEventListener("change", render);
  $("search").addEventListener("input", render);
  $("refresh").addEventListener("click", () => load().catch((e) => message("form-msg", e.message)));

  $("go").addEventListener("click", () => { token = $("token").value.trim(); $("token").value = ""; open(); });
  $("token").addEventListener("keydown", (e) => { if (e.key === "Enter") $("go").click(); });
  $("signout").addEventListener("click", () => signOut());
  if (token) open();
})();
</script>
</body>
</html>`;
