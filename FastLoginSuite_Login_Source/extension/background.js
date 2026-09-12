// FastLogin Chrome Extension - Background Service Worker
console.log("[FastLogin Background] Service Worker active");

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
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
  if (msg.type === "CLEAR_COOKIES") {
    try {
      chrome.browsingData.remove({
        "origins": [
          "https://passport.thehof.gg",
          "https://member.thehof.gg",
          "https://thehof.gg"
        ]
      }, {
        "cache": false,
        "cookies": true,
        "localStorage": true
      }, () => {
        sendResponse({ success: true });
      });
      return true;
    } catch (e) {
      chrome.cookies.getAll({ domain: "thehof.gg" }, (cookies) => {
        for (const c of (cookies || [])) {
          const protocol = c.secure ? "https:" : "http:";
          const domain = c.domain.startsWith(".") ? c.domain.slice(1) : c.domain;
          chrome.cookies.remove({
            url: `${protocol}//${domain}${c.path}`,
            name: c.name
          });
        }
        sendResponse({ success: true });
      });
      return true;
    }
  }

  if (msg.type === "CDP_CLICK") {
    const tabId = sender.tab ? sender.tab.id : null;
    if (!tabId) {
      sendResponse({ success: false });
      return true;
    }
    const target = { tabId: tabId };
    const cx = Math.round(msg.x);
    const cy = Math.round(msg.y);

    function dispatchClickSequence() {
      // 1. Move mouse to coordinate
      chrome.debugger.sendCommand(target, "Input.dispatchMouseEvent", {
        type: "mouseMoved",
        x: cx,
        y: cy
      }, () => {
        setTimeout(() => {
          // 2. Press left mouse button
          chrome.debugger.sendCommand(target, "Input.dispatchMouseEvent", {
            type: "mousePressed",
            x: cx,
            y: cy,
            button: "left",
            buttons: 1,
            clickCount: 1
          }, () => {
            setTimeout(() => {
              // 3. Release left mouse button
              chrome.debugger.sendCommand(target, "Input.dispatchMouseEvent", {
                type: "mouseReleased",
                x: cx,
                y: cy,
                button: "left",
                buttons: 0,
                clickCount: 1
              }, () => {
                sendResponse({ success: true });
              });
            }, 80);
          });
        }, 35);
      });
    }

    chrome.debugger.attach(target, "1.3", () => {
      const err = chrome.runtime.lastError ? chrome.runtime.lastError.message : "";
      if (err && !err.includes("already attached")) {
        console.warn("[FastLogin Debugger] Attach error:", err);
        sendResponse({ success: false, error: err });
        return;
      }
      console.log(`[FastLogin Debugger] 🖱️ Sending Trusted CDP Click at (${cx}, ${cy})...`);
      dispatchClickSequence();
    });
    return true;
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

  return true;
});

