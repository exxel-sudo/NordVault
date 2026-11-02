"use strict";
// NordVault for Firefox. Talks only to the native host "se.nordvault.firefox" (NordVault.exe).
// Page origins are always taken from Firefox (sender.tab / sender.url), never from page or content-script data.

const HOST = "se.nordvault.firefox";
const SAVE_TTL_MS = 3 * 60 * 1000;
let port = null;
let nextId = 1;
const pending = new Map();      // requestId -> {resolve, timer}
const pendingSaves = new Map(); // tabId -> {origin, host, username, password, time}

function connect() {
  if (port) return port;
  port = browser.runtime.connectNative(HOST);
  port.onMessage.addListener((message) => {
    const entry = pending.get(message && message.requestId);
    if (!entry) return;
    pending.delete(message.requestId);
    clearTimeout(entry.timer);
    entry.resolve(message);
  });
  port.onDisconnect.addListener(() => {
    const error = (port && port.error && port.error.message) || "";
    port = null;
    for (const [id, entry] of pending) {
      clearTimeout(entry.timer);
      entry.resolve({ ok: false, notInstalled: true, error: "NordVault kunde inte nås. Öppna NordVault → Lösenord & länkar → Koppla Firefox. " + error });
    }
    pending.clear();
  });
  return port;
}

function ask(message, timeoutMs) {
  return new Promise((resolve) => {
    const requestId = nextId++;
    const timer = setTimeout(() => {
      pending.delete(requestId);
      resolve({ ok: false, error: "NordVault svarade inte i tid." });
    }, timeoutMs);
    pending.set(requestId, { resolve, timer });
    try {
      connect().postMessage(Object.assign({}, message, { requestId }));
    } catch (e) {
      clearTimeout(timer);
      pending.delete(requestId);
      resolve({ ok: false, notInstalled: true, error: "NordVault kunde inte nås: " + e.message });
    }
  });
}

// Only encrypted pages; plain http only for the user's own machine (development).
function isLocal(host) { return host === "localhost" || host === "127.0.0.1" || host === "[::1]" || host.endsWith(".localhost"); }
function originOf(url) {
  try {
    const u = new URL(url);
    return (u.protocol === "https:" || (u.protocol === "http:" && isLocal(u.hostname))) ? u.origin : null;
  } catch (e) { return null; }
}

async function isNever(host) {
  const { never = [] } = await browser.storage.local.get("never");
  return never.includes(host);
}

function takePendingSave(tabId) {
  const save = pendingSaves.get(tabId);
  if (!save) return null;
  if (Date.now() - save.time > SAVE_TTL_MS) { pendingSaves.delete(tabId); return null; }
  return save;
}

async function fillTab(tabId, url, id) {
  const origin = originOf(url);
  if (!origin) return { ok: false, error: "Sidan kan inte fyllas i." };
  const answer = await ask({ type: "fill", url, id }, 5 * 60 * 1000);
  if (!answer.ok) return answer;
  try {
    // Only the top frame of the same origin receives the credentials.
    return await browser.tabs.sendMessage(tabId, { type: "fillCredentials", origin, username: answer.username, password: answer.password }, { frameId: 0 });
  } catch (e) {
    return { ok: false, error: "Sidan har bytts ut. Försök igen." };
  }
}

browser.runtime.onMessage.addListener(async (message, sender) => {
  // Messages from the toolbar popup (no sender.tab).
  if (!sender.tab) {
    if (message.type === "popupState") {
      const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
      const origin = tab && originOf(tab.url);
      const status = await ask({ type: "status" }, 8000);
      if (!origin || !status.ok || !status.unlocked) return { status, origin, logins: [] };
      const logins = await ask({ type: "query", url: tab.url }, 8000);
      return { status, origin, tabId: tab.id, logins: logins.ok ? logins.logins : [] };
    }
    if (message.type === "popupFill") {
      const tab = await browser.tabs.get(message.tabId);
      return fillTab(tab.id, tab.url, message.id);
    }
    if (message.type === "popupOpen") return ask({ type: "open" }, 90000);
    if (message.type === "popupLinks") return ask({ type: "links", query: String(message.query || "").slice(0, 100) }, 8000);
    if (message.type === "popupOpenLink") {
      // Only web addresses from the vault are opened, in a new tab.
      let url;
      try { url = new URL(message.url); } catch (e) { return { ok: false }; }
      if (url.protocol !== "https:" && url.protocol !== "http:") return { ok: false };
      await browser.tabs.create({ url: url.href });
      return { ok: true };
    }
    if (message.type === "popupForget") { await browser.storage.local.set({ never: [] }); return { ok: true }; }
    return { ok: false };
  }

  const tabId = sender.tab.id;
  const pageUrl = sender.url || sender.tab.url;
  const origin = originOf(pageUrl);
  if (!origin || sender.frameId !== 0) return { ok: false };
  const host = new URL(origin).hostname;

  switch (message.type) {
    case "captured": {
      if (typeof message.password !== "string" || message.password.length === 0 || message.password.length > 1024) return { ok: false };
      if (await isNever(host)) return { ok: false };
      pendingSaves.set(tabId, { origin, host, username: String(message.username || "").slice(0, 512), password: message.password, time: Date.now() });
      // A single-page login stays on the same document: offer right away; after a navigation the new page asks.
      setTimeout(() => {
        const save = takePendingSave(tabId);
        if (save) browser.tabs.sendMessage(tabId, { type: "offerSave", host: save.host, username: save.username }, { frameId: 0 }).catch(() => {});
      }, 1200);
      return { ok: true };
    }
    case "pendingSave": {
      const save = takePendingSave(tabId);
      return save ? { host: save.host, username: save.username } : null;
    }
    case "saveDecision": {
      const save = takePendingSave(tabId);
      pendingSaves.delete(tabId);
      if (!save) return { ok: false, error: "Inget lösenord väntar på att sparas." };
      if (message.decision === "never") {
        const { never = [] } = await browser.storage.local.get("never");
        if (!never.includes(save.host)) never.push(save.host);
        await browser.storage.local.set({ never });
        return { ok: true, result: "never" };
      }
      if (message.decision !== "save") return { ok: true, result: "dismissed" };
      return ask({ type: "save", url: save.origin, username: save.username, password: save.password }, 5 * 60 * 1000);
    }
    case "queryLogins": {
      const status = await ask({ type: "status" }, 5000);
      if (!status.ok || !status.unlocked) return { ok: false, locked: !!status.ok };
      return ask({ type: "query", url: pageUrl }, 8000);
    }
    case "fill":
      return fillTab(tabId, pageUrl, String(message.id || ""));
  }
  return { ok: false };
});

browser.tabs.onRemoved.addListener((tabId) => pendingSaves.delete(tabId));
