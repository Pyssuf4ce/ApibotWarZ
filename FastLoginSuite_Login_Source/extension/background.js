// FastLogin Chrome Extension - Background Service Worker
console.log("[FastLogin Background] Service Worker active");

// Load config for event_id
let extensionConfig = { wid: 1, port: 5000, event_id: "a297cbd7-c1c4-448f-9e9f-0f2a35d28ac3" };
try {
  fetch(chrome.runtime.getURL("config.json"))
    .then(r => r.json())
    .then(cfg => {
      if (cfg && typeof cfg === "object") extensionConfig = { ...extensionConfig, ...cfg };
      console.log("[FastLogin Background] Config loaded:", extensionConfig);
    })
    .catch(() => {});
} catch (e) {}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === "GET_CONFIG") {
    sendResponse({ config: extensionConfig });
    return true;
  }

  if (msg.type === "GET_ALL_COOKIES") {
    chrome.cookies.getAll({ domain: "thehof.gg" }, (dCookies) => {
      chrome.cookies.getAll({ url: "https://member.thehof.gg" }, (mCookies) => {
        chrome.cookies.getAll({ url: "https://passport.thehof.gg" }, (pCookies) => {
          const combined = [...(dCookies || []), ...(mCookies || []), ...(pCookies || [])];
          const unique = [];
          const seen = new Set();
          for (const c of combined) {
            if (!seen.has(c.name)) {
              seen.add(c.name);
              unique.push(c);
            }
          }
          sendResponse({ cookies: unique });
        });
      });
    });
    return true;
  }

  if (msg.type === "GET_COOKIE_TOKEN") {
    chrome.cookies.get({ url: "https://member.thehof.gg", name: "access_token" }, (cookie) => {
      sendResponse({ token: cookie ? cookie.value : "" });
    });
    return true;
  }
  if (msg.type === "CLEAR_COOKIES" || msg.type === "HARD_PURGE_SESSION") {
    try {
      // 1. Purge all cookies, caches, Service Workers, IndexedDB, and Turnstile challenge data
      chrome.browsingData.remove({}, {
        "cache": true,
        "cookies": true,
        "localStorage": true,
        "indexedDB": true,
        "serviceWorkers": true,
        "cacheStorage": true,
        "webSQL": true
      }, () => {
        // 2. Explicitly remove all cookies from thehof & cloudflare domains
        const targetDomains = [
          "thehof.gg",
          ".thehof.gg",
          "passport.thehof.gg",
          "member.thehof.gg",
          "core-api.thehof.gg",
          "challenges.cloudflare.com",
          "cloudflare.com",
          ".cloudflare.com"
        ];
        let pending = targetDomains.length;
        targetDomains.forEach(domain => {
          chrome.cookies.getAll({ domain: domain }, (cookies) => {
            if (cookies && cookies.length > 0) {
              cookies.forEach(c => {
                const protocol = c.secure ? "https:" : "http:";
                const d = c.domain.startsWith(".") ? c.domain.slice(1) : c.domain;
                try {
                  chrome.cookies.remove({
                    url: `${protocol}//${d}${c.path}`,
                    name: c.name
                  });
                } catch (err) {}
              });
            }
            pending--;
            if (pending <= 0) {
              sendResponse({ success: true });
            }
          });
        });
      });
      return true;
    } catch (e) {
      sendResponse({ success: true, error: e.toString() });
      return true;
    }
  }


  if (msg.type === "FETCH_TASK") {
    fetch(`http://127.0.0.1:5000/worker/${msg.wid}/task`, {
      cache: "no-store",
      headers: { "Accept": "application/json" }
    })
      .then(r => r.json())
      .then(data => sendResponse({ success: true, data: data }))
      .catch(err => sendResponse({ success: false, error: err.toString() }));
    return true;
  }

  if (msg.type === "REPORT_DONE") {
    fetch(`http://127.0.0.1:5000/worker/${msg.wid}/done`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(msg.payload)
    })
      .then(r => r.json())
      .then(data => sendResponse({ success: true, data: data }))
      .catch(err => sendResponse({ success: false, error: err.toString() }));
    return true;
  }

  if (msg.type === "ACQUIRE_SUBMIT") {
    fetch(`http://127.0.0.1:5000/worker/${msg.wid}/acquire_submit`, {
      cache: "no-store"
    })
      .then(r => r.json())
      .then(data => sendResponse({ success: true, data: data }))
      .catch(err => sendResponse({ success: false, error: err.toString() }));
    return true;
  }

  if (msg.type === "CHECK_SERVER_HEALTH") {
    fetch("http://127.0.0.1:5000/health", { cache: "no-store" })
      .then(r => r.json())
      .then(data => sendResponse({ success: true, data: data }))
      .catch(err => sendResponse({ success: false, error: err.toString() }));
    return true;
  }

  return true;
});
