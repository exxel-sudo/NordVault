"use strict";
const main = document.getElementById("main");
function text(tag, cls, value) { const e = document.createElement(tag); if (cls) e.className = cls; e.textContent = value; return e; }
function note(value) { main.replaceChildren(text("p", "m", value)); }

async function load() {
  const state = await browser.runtime.sendMessage({ type: "popupState" });
  const status = state.status || {};
  if (!status.ok) { note(status.error || "NordVault kunde inte nås."); return; }
  if (!status.unlocked) { note("Valvet är låst. Klicka Öppna NordVault och lås upp valvet."); return; }
  showSearch();
  if (!state.origin) { note("Den här sidan kan inte fyllas i."); return; }
  const host = new URL(state.origin).hostname;
  if (!state.logins.length) { note("Inga sparade inloggningar för " + host + ". Logga in som vanligt så frågar NordVault om att spara."); return; }
  main.replaceChildren(text("p", "m", "Sparade inloggningar för " + host + ". Du bekräftar ifyllningen i NordVault."));
  for (const login of state.logins) {
    const row = document.createElement("div"); row.className = "login";
    const info = document.createElement("div"); info.append(text("div", "", login.title), text("div", "u", login.username || "inget användarnamn"));
    const fill = text("button", "p", "Fyll i");
    fill.addEventListener("click", async () => {
      note("Bekräfta ifyllningen i NordVault-fönstret…");
      const result = await browser.runtime.sendMessage({ type: "popupFill", tabId: state.tabId, id: login.id });
      if (result && result.ok) window.close(); else note((result && result.error) || "Inte ifyllt.");
    });
    row.append(info, fill); main.append(row);
  }
}

// Bookmarks live only in the vault: search them here (at most 15 hits per search) and open in a new tab.
let timer = null;
function showSearch() {
  const section = document.getElementById("search"); const input = document.getElementById("q"); const list = document.getElementById("links");
  section.hidden = false; input.focus();
  input.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      const query = input.value.trim();
      if (!query) { list.replaceChildren(); return; }
      const result = await browser.runtime.sendMessage({ type: "popupLinks", query });
      if (!result || !result.ok) { list.replaceChildren(text("p", "m", (result && result.error) || "Sökningen misslyckades.")); return; }
      if (!result.links.length) { list.replaceChildren(text("p", "m", "Inga länkar hittades.")); return; }
      list.replaceChildren(...result.links.map((link) => {
        const button = document.createElement("button"); button.className = "link";
        const title = document.createElement("b"); title.textContent = link.title;
        button.append(title, text("div", "u", (link.folder ? link.folder + " · " : "") + new URL(link.url).hostname));
        button.addEventListener("click", async () => { await browser.runtime.sendMessage({ type: "popupOpenLink", url: link.url }); window.close(); });
        return button;
      }));
    }, 300);
  });
}

document.getElementById("open").addEventListener("click", async () => { await browser.runtime.sendMessage({ type: "popupOpen" }); window.close(); });
document.getElementById("forget").addEventListener("click", async () => { await browser.runtime.sendMessage({ type: "popupForget" }); note("Listan är rensad. NordVault frågar igen på alla webbplatser."); });
load().catch((e) => note("Fel: " + e.message));
