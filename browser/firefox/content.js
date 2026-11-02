"use strict";
// Runs in each top-level https/http page. Detects logins, offers to save them in NordVault,
// and fills a login only after the user clicked here AND confirmed in NordVault's own window.
(() => {
  if (window.top !== window) return;
  let lastCaptured = "";
  let bar = null;

  const visible = (el) => !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
  const passwordFields = (root) => [...(root || document).querySelectorAll("input[type=password]")].filter((el) => !el.disabled);
  const usernameCandidates = (root) => [...(root || document).querySelectorAll("input:not([type]),input[type=text],input[type=email],input[type=tel]")]
    .filter((el) => !el.disabled && !el.readOnly && visible(el));

  function usernameFor(password) {
    const scope = password.form || document;
    const all = usernameCandidates(scope);
    const before = all.filter((el) => el.compareDocumentPosition(password) & Node.DOCUMENT_POSITION_FOLLOWING);
    const pick = before.reverse().find((el) => el.value.trim()) || all.find((el) => el.value.trim());
    return pick ? pick.value.trim() : "";
  }

  function capture(scope) {
    const fields = passwordFields(scope).filter((el) => el.value);
    if (fields.length === 0) return;
    // Change-password forms: the new password appears twice; otherwise use the first filled field.
    const values = fields.map((f) => f.value);
    const repeated = values.find((v, i) => values.indexOf(v) !== i);
    const password = repeated || (fields.length >= 3 ? values[values.length - 1] : values[0]);
    const field = fields[values.indexOf(password)];
    const username = usernameFor(field);
    const key = username + "\u0000" + password;
    if (key === lastCaptured) return;
    lastCaptured = key;
    browser.runtime.sendMessage({ type: "captured", username, password }).catch(() => {});
  }

  document.addEventListener("submit", (e) => { if (e.isTrusted) capture(e.target); }, true);
  document.addEventListener("click", (e) => {
    if (!e.isTrusted) return;
    const button = e.target.closest && e.target.closest("button,input[type=submit],input[type=button],[role=button]");
    if (!button) return;
    // Ignore e.g. "show password" icons: only submit buttons or buttons that read like a login.
    const label = (button.textContent + " " + (button.value || "") + " " + (button.getAttribute("aria-label") || "")).toLowerCase();
    const submit = (button.type === "submit" && button.form) || /logga in|log ?in|sign ?in|continue|fortsätt|nästa|next|registrera|register|sign ?up|skapa konto|spara|save|byt lösenord|change password|skicka|submit/.test(label);
    if (submit) capture(button.form || document);
  }, true);
  document.addEventListener("keydown", (e) => {
    if (e.isTrusted && e.key === "Enter" && e.target instanceof HTMLInputElement) capture(e.target.form || document);
  }, true);

  // ---- Bar (closed shadow DOM: the page cannot read or restyle it) ----
  function showBar(build) {
    removeBar();
    const host = document.createElement("nordvault-bar");
    host.style.cssText = "all:initial;position:fixed;top:12px;right:12px;z-index:2147483647;";
    const shadow = host.attachShadow({ mode: "closed" });
    shadow.innerHTML = `<style>
      .b{font:14px/1.4 "Segoe UI",system-ui,sans-serif;color:#17263E;background:#fff;border:1px solid #D8E0E8;border-left:5px solid #14695B;
         border-radius:10px;box-shadow:0 8px 28px rgba(22,40,63,.22);padding:14px 16px;max-width:360px;min-width:280px}
      .t{font-weight:600;margin-bottom:4px}.m{color:#64748B;font-size:13px;margin-bottom:10px;word-break:break-word}
      .r{display:flex;gap:8px;flex-wrap:wrap}
      button{font:600 13px "Segoe UI",system-ui,sans-serif;border-radius:7px;padding:7px 12px;cursor:pointer;border:1px solid #D8E0E8;background:#fff;color:#17263E}
      button.p{background:#14695B;color:#fff;border-color:#14695B}
      select{font:13px "Segoe UI",system-ui,sans-serif;padding:6px;border-radius:7px;border:1px solid #D8E0E8;margin-bottom:10px;max-width:100%}
    </style><div class="b"></div>`;
    build(shadow.querySelector(".b"));
    (document.body || document.documentElement).appendChild(host);
    bar = host;
  }
  function removeBar() { if (bar) { bar.remove(); bar = null; } }
  function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function button(text, primary, onClick) {
    const b = el("button", primary ? "p" : "", text);
    b.addEventListener("click", (e) => { if (e.isTrusted) onClick(); });
    return b;
  }
  function message(box, title, text) {
    box.replaceChildren(el("div", "t", title), el("div", "m", text));
    const row = el("div", "r"); row.append(button("Stäng", false, removeBar)); box.append(row);
  }

  function offerSave(info) {
    showBar((box) => {
      box.append(el("div", "t", "Spara lösenordet i NordVault?"), el("div", "m", info.host + (info.username ? " · " + info.username : "")));
      const row = el("div", "r");
      const decide = async (decision) => {
        if (decision === "save") message(box, "Sparar…", "Bekräfta i NordVault om valvet är låst.");
        const result = await browser.runtime.sendMessage({ type: "saveDecision", decision }).catch(() => null);
        if (decision !== "save") { removeBar(); return; }
        if (result && result.ok) {
          message(box, "Sparat i NordVault", result.result === "updated" ? "Lösenordet är uppdaterat." : result.result === "unchanged" ? "Lösenordet fanns redan." : "Inloggningen är sparad krypterat.");
          setTimeout(removeBar, 4000);
        } else message(box, "Inte sparat", (result && result.error) || "NordVault kunde inte nås.");
      };
      row.append(button("Spara", true, () => decide("save")), button("Inte nu", false, () => decide("dismiss")), button("Aldrig för denna webbplats", false, () => decide("never")));
      box.append(row);
    });
  }

  function offerFill(logins) {
    showBar((box) => {
      box.append(el("div", "t", "NordVault har " + (logins.length === 1 ? "en sparad inloggning" : logins.length + " sparade inloggningar")),
                 el("div", "m", "Du bekräftar ifyllningen i NordVault."));
      let select = null;
      if (logins.length > 1) {
        select = el("select");
        for (const l of logins) { const o = el("option", "", l.title + (l.username ? " · " + l.username : "")); o.value = l.id; select.append(o); }
        box.append(select);
      }
      const row = el("div", "r");
      row.append(button("Fyll i", true, async () => {
        const id = select ? select.value : logins[0].id;
        message(box, "Väntar på NordVault…", "Bekräfta ifyllningen i NordVault-fönstret.");
        const result = await browser.runtime.sendMessage({ type: "fill", id }).catch(() => null);
        if (result && result.ok) removeBar();
        else message(box, "Inte ifyllt", (result && result.error) || "NordVault kunde inte nås.");
      }), button("Stäng", false, removeBar));
      box.append(row);
    });
  }

  function setValue(input, value) {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
    input.focus();
    setter.call(input, value);
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function fill(username, password) {
    const fields = passwordFields().filter(visible);
    if (fields.length === 0) return false;
    const target = fields[0];
    const scope = target.form || document;
    const users = usernameCandidates(scope).filter((u) => u.compareDocumentPosition(target) & Node.DOCUMENT_POSITION_FOLLOWING);
    if (username && users.length) setValue(users[users.length - 1], username);
    setValue(target, password);
    lastCaptured = (username || "") + "\u0000" + password; // do not offer to save what was just filled
    return true;
  }

  browser.runtime.onMessage.addListener((msg) => {
    if (msg.type === "offerSave") { offerSave(msg); return Promise.resolve(true); }
    if (msg.type === "fillCredentials") {
      if (msg.origin !== location.origin) return Promise.resolve({ ok: false, error: "Sidan har bytt adress." });
      const ok = fill(msg.username, msg.password);
      return Promise.resolve(ok ? { ok: true } : { ok: false, error: "Hittade inget lösenordsfält på sidan." });
    }
  });

  (async () => {
    const save = await browser.runtime.sendMessage({ type: "pendingSave" }).catch(() => null);
    if (save) { offerSave(save); return; }
    if (passwordFields().some(visible)) {
      const result = await browser.runtime.sendMessage({ type: "queryLogins" }).catch(() => null);
      if (result && result.ok && result.logins && result.logins.length) offerFill(result.logins);
    }
  })();
})();
