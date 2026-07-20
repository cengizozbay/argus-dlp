// Argus Web Monitor — arka plan servis çalışanı (MV3).
// Her navigasyonu (SPA/history dahil) ve aktif sekme değişimini native host'a iletir.
// Gizli mod: uzantı gizli modda ÇALIŞMASINA izin verilmişse (kullanıcı toggle'ı veya GPO
// policy'si) gizli sekmeler de yakalanır; incognito bayrağı işaretlenir.

const HOST = "com.argus.webhost";
let port = null;

function connect() {
  try {
    port = chrome.runtime.connectNative(HOST);
    port.onDisconnect.addListener(() => { port = null; });
  } catch (e) { port = null; }
}

function send(msg) {
  try {
    if (!port) connect();
    if (port) port.postMessage(msg);
  } catch (e) { port = null; }
}

function skip(url) {
  return !url ||
    url.startsWith("chrome://") || url.startsWith("edge://") ||
    url.startsWith("about:") || url.startsWith("chrome-extension://") ||
    url.startsWith("devtools://") || url.startsWith("view-source:");
}

// Tam geçmiş kaydı (her ana çerçeve navigasyonu — arka plan sekmeleri dahil)
function logNav(details) {
  if (details.frameId !== 0) return;
  if (skip(details.url)) return;
  chrome.tabs.get(details.tabId, (tab) => {
    const incog = tab ? !!tab.incognito : false;
    const title = tab ? (tab.title || "") : "";
    send({ type: "nav", url: details.url, title: title, incognito: incog, ts: Date.now() });
  });
}

// Aktif (odaktaki) sekme — agent süreyi buna yazar
function pushActive() {
  chrome.windows.getLastFocused({ populate: false }, (win) => {
    if (chrome.runtime.lastError || !win || !win.focused) return;
    chrome.tabs.query({ active: true, windowId: win.id }, (tabs) => {
      const tab = tabs && tabs[0];
      if (!tab || skip(tab.url)) return;
      send({ type: "active", url: tab.url, title: tab.title || "", incognito: !!tab.incognito, ts: Date.now() });
    });
  });
}

chrome.webNavigation.onCommitted.addListener((d) => { logNav(d); pushActive(); });
chrome.webNavigation.onHistoryStateUpdated.addListener((d) => { logNav(d); pushActive(); }); // SPA / YouTube
chrome.tabs.onActivated.addListener(pushActive);
chrome.tabs.onUpdated.addListener((id, info, tab) => { if (info.title || info.url) pushActive(); });
chrome.windows.onFocusChanged.addListener(pushActive);

// Periyodik nabız: kullanıcı aynı sayfada hareketsiz kalsa da aktif sekmeyi tazele (MV3 service
// worker uykuya dalsa chrome.alarms uyandırır) → agent hep gerçek URL'yi görür, UIA yedeğine düşmez.
chrome.alarms.create("argus-heartbeat", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((a) => { if (a.name === "argus-heartbeat") pushActive(); });

connect();
pushActive();
