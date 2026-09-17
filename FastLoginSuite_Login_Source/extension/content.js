// FastLogin Chrome Extension - Content Script (Direct Sync Engine)
// Fixes: Session isolation, Promise race condition, Rate limit detection,
//        Turnstile timeout (15s), Poll max retry, Event ID configurable
(async function() {
  console.log("[FastLogin] Content script loaded on:", window.location.href);

  // ── Load config from background (event_id etc.) ──
  let EXT_CONFIG = { event_id: "a297cbd7-c1c4-448f-9e9f-0f2a35d28ac3" };
  try {
    const cfgRes = await new Promise((resolve) => {
      let resolved = false;
      const done = (val) => { if (!resolved) { resolved = true; resolve(val); } };
      chrome.runtime.sendMessage({ type: "GET_CONFIG" }, (response) => done(response));
      setTimeout(() => done(null), 1000);
    });
    if (cfgRes && cfgRes.config) {
      EXT_CONFIG = { ...EXT_CONFIG, ...cfgRes.config };
    }
  } catch (e) {}

  // Floating Visual HUD for user status visibility
  function showHUD(text, color = "#6366f1") {
    try {
      let hud = document.getElementById("fastlogin-hud");
      if (!hud) {
        hud = document.createElement("div");
        hud.id = "fastlogin-hud";
        hud.style.cssText = "position:fixed;top:10px;right:10px;z-index:2147483647;padding:8px 14px;background:#0f172a;color:#f8fafc;font-family:Segoe UI,sans-serif;font-size:12px;font-weight:600;border:1.5px solid " + color + ";border-radius:8px;box-shadow:0 4px 16px rgba(0,0,0,0.6);pointer-events:none;display:flex;align-items:center;gap:8px;";
        if (document.body) document.body.appendChild(hud);
        else document.documentElement.appendChild(hud);
      }
      hud.style.borderColor = color;
      hud.innerHTML = `<span style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${color};box-shadow:0 0 8px ${color};"></span>` + text;
    } catch(e) {}
  }

  // Robust React / Vue / Standard Input Value Setter
  function setInputValue(el, value) {
    if (!el) return false;
    try {
      try { el.focus({ preventScroll: true }); } catch (e) { el.focus(); }
      const lastValue = el.value;
      el.value = value;
      const tracker = el._valueTracker;
      if (tracker) {
        tracker.setValue(lastValue);
      }
      const protoSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")?.set;
      if (protoSetter) {
        protoSetter.call(el, value);
      }
      el.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
      el.dispatchEvent(new Event("change", { bubbles: true, composed: true }));
      el.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, data: value }));
      el.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true }));
      el.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true }));
      el.dispatchEvent(new Event("blur", { bubbles: true, composed: true }));
      return true;
    } catch (e) {
      try { el.value = value; } catch(ex) {}
      return true;
    }
  }

  // Universal JWT & Token Extractor
  async function extractToken() {
    // True JWT has 3 parts: header.payload.signature, starting with eyJ...eyJ...
    const jwtRegex = /(eyJ[a-zA-Z0-9_-]{5,}\.eyJ[a-zA-Z0-9_-]{5,}\.[a-zA-Z0-9_-]+)/;

    // 1. Search in URL (query parameters or hash)
    try {
      const urlMatch = (window.location.href).match(jwtRegex);
      if (urlMatch && urlMatch[1]) return urlMatch[1];
    } catch (e) {}

    // 2. Search in localStorage (all keys)
    try {
      for (let i = 0; i < localStorage.length; i++) {
        const k = localStorage.key(i);
        const v = localStorage.getItem(k);
        if (v) {
          const match = v.match(jwtRegex);
          if (match && match[1]) return match[1];
        }
      }
    } catch (e) {}

    // 3. Search in sessionStorage (all keys)
    try {
      for (let i = 0; i < sessionStorage.length; i++) {
        const k = sessionStorage.key(i);
        const v = sessionStorage.getItem(k);
        if (v) {
          const match = v.match(jwtRegex);
          if (match && match[1]) return match[1];
        }
      }
    } catch (e) {}

    // 4. Search in document.cookie
    try {
      const match = document.cookie.match(jwtRegex);
      if (match && match[1]) return match[1];
    } catch (e) {}

    // 5. Request cookies from background service worker (all domains)
    try {
      const cookieRes = await new Promise((resolve) => {
        let resolved = false;
        const done = (val) => { if (!resolved) { resolved = true; resolve(val); } };
        chrome.runtime.sendMessage({ type: "GET_ALL_COOKIES" }, (response) => done(response));
        setTimeout(() => done(null), 1000);
      });

      if (cookieRes && cookieRes.cookies) {
        for (const c of cookieRes.cookies) {
          const match = (c.value || "").match(jwtRegex);
          if (match && match[1]) return match[1];
          if (c.name === "access_token" || c.name === "token" || c.name === "auth_token") {
            if (c.value && c.value.startsWith("eyJ") && c.value.includes(".")) return c.value;
          }
        }
      }
    } catch (e) {}

    return "";
  }

  // Universal JWT Payload Decoder
  function decodeJwt(tok) {
    try {
      if (!tok || typeof tok !== "string") return null;
      const p = tok.split(".");
      if (p.length !== 3) return null;
      const b64 = p[1].replace(/-/g, '+').replace(/_/g, '/');
      const pad = b64 + "=".repeat((4 - b64.length % 4) % 4);
      return JSON.parse(atob(pad));
    } catch (e) {
      return null;
    }
  }

  // ── FIX #3: Safe Promise wrapper (no double-resolve) ──
  function safeSendMessage(msg, timeoutMs = 2500) {
    return new Promise((resolve) => {
      let resolved = false;
      const done = (val) => { if (!resolved) { resolved = true; resolve(val); } };
      try {
        chrome.runtime.sendMessage(msg, (response) => done(response));
      } catch (e) {
        done(null);
      }
      setTimeout(() => done(null), timeoutMs);
    });
  }

  // Robust Server Communication via Extension Service Worker (CORS & PNA Bypassed)
  async function sendWorkerDone(wid, payload) {
    try {
      const res = await safeSendMessage({ type: "REPORT_DONE", wid: wid, payload: payload }, 3000);
      if (res && res.success) {
        console.log("[FastLogin] ✅ รายงานผลลัพธ์ผ่าน Service Worker สำเร็จ");
        return true;
      }
    } catch (e) {
      console.warn("[FastLogin] Service Worker report_done error:", e);
    }
    return false;
  }

  async function fetchWorkerTask(wid) {
    try {
      const res = await safeSendMessage({ type: "FETCH_TASK", wid: wid }, 2000);
      if (res && res.success && res.data && res.data.task) {
        return res.data.task;
      }
    } catch (e) {}
    return null;
  }

  // ── FIX #10: Server health check ──
  async function checkServerAlive() {
    try {
      const res = await safeSendMessage({ type: "CHECK_SERVER_HEALTH" }, 1500);
      if (res && res.success) return true;
    } catch (e) {}
    return false;
  }

  // ── FIX #1 & #2: Full session cleanup ──
  async function cleanupAndReturnToLogin(wid) {
    // Clear ALL extension storage (fixes session isolation across accounts)
    try {
      await chrome.storage.local.clear();
    } catch (e) {}

    try {
      localStorage.clear();
      sessionStorage.clear();
    } catch (e) {}

    try {
      await safeSendMessage({ type: "HARD_PURGE_SESSION" }, 1500);
    } catch (e) {}

    // Re-store only the wid for this worker (after clearing everything)
    try {
      await chrome.storage.local.set({ fastlogin_wid: wid });
    } catch (e) {}

    setTimeout(() => {
      window.location.replace(`https://passport.thehof.gg/hall-of-fame-web/login?_t=${Date.now()}#wid=${wid}`);
    }, 1000);
  }

  // ── FIX #5: Enhanced Rate Limit & Hard Block Detection (No False Positives) ──
  function checkIfRateLimited() {
    const title = (document.title || "").toLowerCase();
    const text = (document.body ? document.body.innerText : "").toLowerCase();

    // Standard 429 / 1015 / WAF Block checks only
    if (title.includes("too many requests") || title.includes("error 1015") || title.includes("rate limited") || title.includes("429")) {
      return true;
    }
    if (text.includes("too many requests") || text.includes("error 1015") || text.includes("you are being rate limited") || text.includes("429 too many requests") || text.includes("rate limit exceeded")) {
      return true;
    }

    // Hard Cloudflare WAF block (Access Denied 1020/1006)
    if (text.includes("access denied") && (text.includes("cloudflare") || text.includes("ray id"))) {
      return true;
    }

    return false;
  }

  // ══════════════════════════════════════════════════════════════════
  //  SCENARIO A: On member.thehof.gg or after successful login
  // ══════════════════════════════════════════════════════════════════
  const isMemberPage = window.location.hostname.includes("member.thehof.gg") || 
                       (window.location.hostname.includes("thehof.gg") && !window.location.pathname.includes("/login"));

  if (isMemberPage) {
    // อ่านข้อมูล Worker & Username จาก chrome.storage.local
    let wid = "1";
    let username = "";
    try {
      const stored = await chrome.storage.local.get(["fastlogin_wid", "fastlogin_user"]);
      if (stored.fastlogin_wid) wid = stored.fastlogin_wid.toString();
      if (stored.fastlogin_user) username = stored.fastlogin_user;
    } catch (e) {}

    // ป้องกัน Infinite Extraction Loop: หากไม่มีบัญชีที่กำลังประมวลผล ให้ล้างเซสชันและกลับหน้า Login ทันที
    if (!username) {
      console.warn("[FastLogin] ⚠️ อยู่บนหน้า Member Portal แต่ไม่มีคิวงานที่กำลังล็อกอิน — ล้างเซสชันและกลับหน้า Login");
      await cleanupAndReturnToLogin(wid);
      return;
    }

    console.log(`[FastLogin] 🎉 ตรวจพบหน้า Member Portal ของ '${username}'! เริ่มการสกัดโทเคนและรับรางวัล...`);
    showHUD(`🎉 ล็อกอิน '${username}' สำเร็จ! กำลังสกัด Token และรับรางวัล...`, "#10b981");

    // Helper: ถอดรหัส JWT Payload เพื่อตรวจสอบความถูกต้องและป้องกัน Token ปนกันข้ามไอดี
    function decodeJwt(tok) {
      try {
        const p = tok.split(".");
        if (p.length !== 3) return null;
        const b64 = p[1].replace(/-/g, '+').replace(/_/g, '/');
        const pad = b64 + "=".repeat((4 - b64.length % 4) % 4);
        return JSON.parse(atob(pad));
      } catch (e) {
        return null;
      }
    }

    // ดึง Token จากทุกแหล่ง พร้อมตรวจความถูกต้อง 100%
    let token = "";
    for (let attempt = 0; attempt < 25; attempt++) {
      const candidate = await extractToken();
      if (candidate && candidate.length > 50) {
        const payload = decodeJwt(candidate);
        if (payload) {
          // 1. ตรวจสอบว่า Token ยังไม่หมดอายุ
          const nowSec = Math.floor(Date.now() / 1000);
          if (payload.exp && nowSec > payload.exp) {
            console.warn("[FastLogin] ⚠️ ตรวจพบ Token หมดอายุใน Cookie — ข้ามเพื่อรอ Token ใหม่");
          } else {
            // 2. ตรวจสอบว่าชื่อผู้ใช้ตรงกับไอดีปัจจุบัน (ป้องกัน Token ปนกันข้ามจอ)
            const tokenUser = (payload.username || payload.account_name || payload.account || "").toString().toLowerCase();
            const curUser = (username || "").toLowerCase();
            if (tokenUser && curUser && tokenUser !== curUser) {
              console.warn(`[FastLogin] ⚠️ ตรวจพบ Token ของไอดีเก่า '${tokenUser}' ปนมาใน Cookie (กำลังล็อกอิน '${curUser}') — รอคัดกรอง Token ใหม่`);
            } else {
              token = candidate;
              break;
            }
          }
        } else {
          token = candidate;
          break;
        }
      }
      await new Promise(r => setTimeout(r, 200));
    }

    console.log(`[FastLogin] Worker-${wid} | บัญชี: '${username}' | Token Length: ${token ? token.length : 0}`);

    // ส่ง Token กลับไปยัง Python Server ทันที (Python Server จะเป็นผู้ยิงรับของเองที่เดียว)
    await sendWorkerDone(wid, {
      status: "success",
      worker_id: parseInt(wid, 10),
      username: username,
      token: token,
      detail: "🔑 สกัด Token สำเร็จ",
      time: "0s"
    });

    // ล้างเซสชันและนำหน้าต่างกลับไปรอคิวล็อกอินบัญชีถัดไปทันที
    await cleanupAndReturnToLogin(wid);
    return;
  }

  // ══════════════════════════════════════════════════════════════════
  //  SCENARIO B: On passport.thehof.gg (Login Page)
  // ══════════════════════════════════════════════════════════════════
  let wid = null;
  const hashMatch = window.location.hash.match(/wid=(\d+)/);
  if (hashMatch) {
    wid = hashMatch[1];
    try { await chrome.storage.local.set({ fastlogin_wid: wid }); } catch (e) {}
  } else {
    try {
      const res = await chrome.storage.local.get("fastlogin_wid");
      wid = res.fastlogin_wid || "1";
    } catch (e) {
      wid = "1";
    }
  }

  console.log(`[FastLogin] 🚀 Worker ${wid} พร้อมทำงานบนหน้าล็อกอิน กำลังดึงงานจากเซิร์ฟเวอร์...`);
  showHUD(`⚡ HOF Bot พร้อมทำงาน (Worker #${wid}) | กำลังรอรับคิวงาน...`, "#6366f1");



  // If already rate limited upon page load
  if (checkIfRateLimited()) {
    console.warn("[FastLogin] ⚠️ ตรวจพบหน้า Too Many Requests (429) ตั้งแต่เริ่มต้น!");
    await sendWorkerDone(wid, {
      status: "failed",
      worker_id: parseInt(wid, 10),
      username: "",
      reason: "ติด Limit IP (Too Many Requests 429)",
      is_limit: true,
      time: "0.5s"
    });
    return;
  }

  // ── FIX #10: Poll with max retry limit & server health check ──
  let currentTask = null;
  let pollAttempts = 0;
  const MAX_POLL_ATTEMPTS = 120; // 120 * 500ms = 60 seconds max

  while (!currentTask && pollAttempts < MAX_POLL_ATTEMPTS) {
    if (checkIfRateLimited()) {
      await sendWorkerDone(wid, {
        status: "failed",
        worker_id: parseInt(wid, 10),
        username: "",
        reason: "ติด Limit IP (Too Many Requests 429)",
        is_limit: true,
        time: "0.5s"
      });
      return;
    }

    currentTask = await fetchWorkerTask(wid);
    if (currentTask && currentTask.username) {
      break;
    }

    // Check server health every 20 attempts (10 seconds)
    if (pollAttempts > 0 && pollAttempts % 20 === 0) {
      const alive = await checkServerAlive();
      if (!alive) {
        console.warn(`[FastLogin] ⚠️ Server ไม่ตอบสนองหลังจาก ${pollAttempts} รอบ กำลังรอต่อ...`);
        showHUD(`⚠️ Server ไม่ตอบสนอง กำลังรอต่อ... (${pollAttempts}/${MAX_POLL_ATTEMPTS})`, "#f59e0b");
      }
    }

    pollAttempts++;
    await new Promise(r => setTimeout(r, 500));
  }

  if (!currentTask || !currentTask.username) {
    console.warn(`[FastLogin] ⚠️ Worker ${wid} ไม่ได้รับงานหลังจาก ${MAX_POLL_ATTEMPTS} รอบ`);
    showHUD(`⚠️ ไม่ได้รับงานหลังรอ 60 วินาที`, "#ef4444");
    return;
  }

  console.log(`[FastLogin] 📥 ได้รับงาน: บัญชี '${currentTask.username}'. กำลังรอ Turnstile...`);
  showHUD(`🔑 กำลังทำงาน [Worker #${wid}]: บัญชี ${currentTask.username}`, "#f59e0b");
  try {
    await chrome.storage.local.set({
      fastlogin_wid: wid,
      fastlogin_user: currentTask.username,
      fastlogin_task: currentTask
    });
  } catch (e) {}

  const tStart = Date.now();

  // ── FIX #6: Robust Turnstile Detection, Cursor Simulation & Anti-Stuck Recovery ──
  function getTurnstileToken() {
    const cfInput = document.querySelector('[name="cf-turnstile-response"]') ||
                    document.querySelector('input[name*="turnstile"]') ||
                    document.querySelector('textarea[name*="turnstile"]') ||
                    document.querySelector('[name="g-recaptcha-response"]');
    if (cfInput && cfInput.value && cfInput.value.length > 20) {
      return cfInput.value;
    }
    return null;
  }

  function tryClickTurnstile() {
    try {
      // 1. Target all Cloudflare Turnstile wrappers & containers
      const cfWrappers = document.querySelectorAll(
        '.cf-turnstile, [data-sitekey], #cf-turnstile, div[id*="cf-"], div[class*="turnstile"], div.cf-turnstile-wrapper'
      );
      for (const wrap of cfWrappers) {
        wrap.click();

        const rect = wrap.getBoundingClientRect();
        if (rect.width > 0 && rect.height > 0) {
          const cx = rect.left + Math.min(30, rect.width / 2);
          const cy = rect.top + Math.min(30, rect.height / 2);
          const evtOpts = { bubbles: true, cancelable: true, view: window, clientX: cx, clientY: cy };
          wrap.dispatchEvent(new MouseEvent('mouseover', evtOpts));
          wrap.dispatchEvent(new MouseEvent('mouseenter', evtOpts));
          wrap.dispatchEvent(new MouseEvent('mousemove', evtOpts));
          wrap.dispatchEvent(new MouseEvent('mousedown', evtOpts));
          wrap.dispatchEvent(new MouseEvent('mouseup', evtOpts));
          wrap.dispatchEvent(new MouseEvent('click', evtOpts));
        }
      }

      // 2. Target Turnstile iframes and their parents
      const iframes = document.querySelectorAll('iframe[src*="challenges.cloudflare.com"], iframe[src*="turnstile"]');
      for (const ifr of iframes) {
        ifr.click();
        if (ifr.parentElement) {
          ifr.parentElement.click();
          const pRect = ifr.parentElement.getBoundingClientRect();
          if (pRect.width > 0 && pRect.height > 0) {
            ifr.parentElement.dispatchEvent(new MouseEvent('click', {
              bubbles: true,
              cancelable: true,
              clientX: pRect.left + 25,
              clientY: pRect.top + 25
            }));
          }
        }
      }
    } catch (e) {}
  }

  function tryResetTurnstile() {
    try {
      const script = document.createElement("script");
      script.textContent = `
        if (window.turnstile && typeof window.turnstile.reset === 'function') {
          try { window.turnstile.reset(); } catch(e){}
        }
      `;
      (document.head || document.documentElement).appendChild(script);
      script.remove();
    } catch (e) {}
  }

  function simulateHumanMouse() {
    try {
      window.focus();
      const x = 80 + Math.floor(Math.random() * 200);
      const y = 80 + Math.floor(Math.random() * 200);
      const evtOpts = { bubbles: true, cancelable: true, view: window, clientX: x, clientY: y };
      document.dispatchEvent(new MouseEvent('mousemove', evtOpts));
      document.dispatchEvent(new MouseEvent('mouseover', evtOpts));
    } catch(e) {}
  }

  let turnstileSolved = false;
  // 80 * 250ms = 20.0s max timeout for Turnstile
  for (let step = 0; step < 80; step++) {
    const token = getTurnstileToken();
    if (token) {
      turnstileSolved = true;
      console.log("[FastLogin] ✅ Cloudflare Turnstile ยืนยันสำเร็จ (มี Token)!");
      showHUD(`🛡️ Turnstile ผ่านแล้ว! กำลังเข้าสู่ระบบ: ${currentTask.username}`, "#10b981");
      break;
    }

    // Natural human mouse movement every 500ms
    if (step % 2 === 0) {
      simulateHumanMouse();
    }

    // Gentle click / reset triggers at 2s and 6s
    if (step === 8 || step === 24) {
      tryClickTurnstile();
      tryResetTurnstile();
    }

    if (step % 8 === 0) {
      showHUD(`🛡️ รอ Cloudflare Turnstile ยืนยันอัตโนมัติ... (${(step * 0.25).toFixed(1)}s)`, "#8b5cf6");
    }

    if (checkIfRateLimited()) {
      await sendWorkerDone(wid, {
        status: "failed",
        worker_id: parseInt(wid, 10),
        username: currentTask.username,
        reason: "ติด Limit IP (Too Many Requests 429 / Cloudflare 1015)",
        is_limit: true,
        time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
      });
      return;
    }

    await new Promise(r => setTimeout(r, 250));
  }

  if (!turnstileSolved) {
    console.warn("[FastLogin] ⚠️ Turnstile หมุนค้างเกิน 20s ➔ ล้างแคช Cloudflare ทั้งหมดและรีเซ็ตหน้าใหม่");
    showHUD(`🔄 Turnstile ไม่ผ่านใน 20s ➔ ล้างแคชแล้วเริ่มใหม่`, "#f59e0b");
    await sendWorkerDone(wid, {
      status: "failed",
      worker_id: parseInt(wid, 10),
      username: currentTask.username,
      reason: "Cloudflare Turnstile หมุนค้าง (ส่งรันซ้ำ)",
      time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
    });

    // Hard purge all cookies, service workers, and Turnstile challenge data before restart
    try {
      await safeSendMessage({ type: "HARD_PURGE_SESSION" }, 1500);
      sessionStorage.clear();
      localStorage.clear();
    } catch (e) {}

    window.location.replace(`https://passport.thehof.gg/hall-of-fame-web/login?_t=${Date.now()}#wid=${wid}`);
    return;
  }

  // Step 3: Fast HTTP Login via Background Service Worker (CORS-Free & Instant)
  console.log(`[FastLogin] 🚀 กำลังยิง HTTP Login ผ่าน Background Service Worker สำหรับ '${currentTask.username}'...`);
  showHUD(`🚀 กำลังยิงเข้าสู่ระบบ (Background API): ${currentTask.username}...`, "#3b82f6");

  // 1. ดึง CSRF Token (_token) จากหน้าเว็บ
  let csrfToken = "";
  const csrfEl = document.querySelector('input[name="_token"]') || document.querySelector('meta[name="csrf-token"]');
  if (csrfEl) {
    csrfToken = csrfEl.value || csrfEl.getAttribute("content") || "";
  }

  // 2. ขอคิว Pacing ป้องกันชน Limit ก่อนกดยิงล็อกอิน
  try {
    await safeSendMessage({ type: "ACQUIRE_SUBMIT", wid: wid }, 2000);
  } catch (e) {}

  await new Promise(r => setTimeout(r, 100 + Math.floor(Math.random() * 300)));

  let formAction = "https://passport.thehof.gg/hall-of-fame-web/login?theme=talesrunner-web";
  const formEl = document.querySelector('form');
  if (formEl && formEl.action && formEl.action.startsWith("http")) {
    formAction = formEl.action;
  }

  // 3. ส่งคำขอให้ Background.js ยิง HTTP POST เข้า API ล็อกอิน (ไม่ติด CORS)
  let bgRes = null;
  try {
    bgRes = await safeSendMessage({
      type: "HTTP_LOGIN",
      wid: wid,
      username: currentTask.username,
      password: currentTask.password,
      turnstile_token: turnstileToken,
      csrf_token: csrfToken,
      url: formAction
    }, 12000);
  } catch (e) {
    console.warn("[FastLogin] Background HTTP_LOGIN call error:", e);
  }

  if (bgRes && bgRes.success) {
    const resText = bgRes.resText || "";
    const resLower = resText.toLowerCase();

    // ก. ตรวจจับ Error รหัสผ่านผิด / ไม่พบบัญชี
    if (resText.includes("รหัสผ่านไม่ถูกต้อง") || resText.includes("ไม่พบบัญชี") || resText.includes("ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง") || resLower.includes("invalid credential") || resLower.includes("incorrect password")) {
      console.warn(`[FastLogin] ❌ รหัสผ่านไม่ถูกต้องสำหรับ '${currentTask.username}'`);
      await sendWorkerDone(wid, {
        status: "failed",
        worker_id: parseInt(wid, 10),
        username: currentTask.username,
        reason: "รหัสผ่านไม่ถูกต้อง / ไม่พบบัญชี",
        time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
      });
      await cleanupAndReturnToLogin(wid);
      return;
    }

    // ข. ตรวจจับ Rate Limit 429 / 1015
    if (bgRes.status === 429 || resLower.includes("too many requests") || resLower.includes("error 1015") || resLower.includes("rate limit")) {
      console.warn(`[FastLogin] ⚠️ ติด Limit IP (429/1015) หลังยิงล็อกอิน!`);
      await sendWorkerDone(wid, {
        status: "failed",
        worker_id: parseInt(wid, 10),
        username: currentTask.username,
        reason: "ติด Limit IP (Too Many Requests 429)",
        is_limit: true,
        time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
      });
      await cleanupAndReturnToLogin(wid);
      return;
    }

    // ค. ตรวจสอบและดึง Token ที่ได้รับ
    let token = bgRes.token || "";
    if (!token) {
      for (let tAttempt = 0; tAttempt < 10; tAttempt++) {
        const candidate = await extractToken();
        if (candidate && candidate.length > 50) {
          const payload = decodeJwt(candidate);
          if (payload && (!payload.exp || Math.floor(Date.now() / 1000) <= payload.exp)) {
            const tokenUser = (payload.username || payload.account_name || payload.account || "").toString().toLowerCase();
            const curUser = (currentTask.username || "").toLowerCase();
            if (!tokenUser || !curUser || tokenUser === curUser) {
              token = candidate;
              break;
            }
          }
        }
        await new Promise(r => setTimeout(r, 150));
      }
    }

    // ง. หากได้รับ Token เรียบร้อยแล้ว (สำเร็จทันทีใน ~1.5s ⚡)
    if (token) {
      console.log(`[FastLogin] ⚡ Background HTTP Login สำเร็จทันที! บัญชี: '${currentTask.username}' | Token Length: ${token.length}`);
      showHUD(`🎉 เข้าสู่ระบบสำเร็จ (Background API): ${currentTask.username}`, "#10b981");

      const elapsed = `${((Date.now() - tStart) / 1000).toFixed(1)}s`;
      await sendWorkerDone(wid, {
        status: "success",
        worker_id: parseInt(wid, 10),
        username: currentTask.username,
        token: token,
        detail: "🔑 สกัด Token สำเร็จ (Direct)",
        time: elapsed
      });

      await cleanupAndReturnToLogin(wid);
      return;
    }

    // หาก HTTP Login ยิงผ่านแล้วแต่ JWT Token รอประมวลผลบนหน้า Member
    // ให้กระโดดไปหน้า Member Portal ทันทีโดยไม่ต้องพิมพ์หรือกดปุ่มใดๆ ใน DOM
    if (bgRes.status === 200 || bgRes.status === 302 || (bgRes.resUrl && bgRes.resUrl.includes("member.thehof.gg"))) {
      console.log("[FastLogin] 🚀 HTTP Login ผ่านในเบื้องหลัง ➔ กระโดดไปหน้า Member Portal ทันที (Zero DOM)");
      showHUD(`🎉 ล็อกอินสำเร็จ (HTTP) ➔ กำลังไปยังหน้าสมาชิก...`, "#10b981");
      window.location.replace("https://member.thehof.gg/");
      return;
    }
  }

  // Step 4: Fallback หากยิง HTTP ไม่สำเร็จจริงๆ ให้ใช้วิธีกรอกผ่าน DOM Submit
  console.log("[FastLogin] 🔄 Fallback to DOM Form submission...");
  let passInput = document.querySelector('input[type="password"]') || document.querySelector('#password');
  let userInput = document.querySelector('#username') || document.querySelector('#hofId') || document.querySelector('input[name="username"]');

  if (userInput && passInput) {
    setInputValue(userInput, currentTask.username);
    setInputValue(passInput, currentTask.password);
    await new Promise(r => setTimeout(r, 200));

    let submitBtn = document.querySelector('button[type="submit"]') || document.querySelector('.btn-primary') || document.querySelector('form button');
    if (submitBtn) {
      submitBtn.click();
    } else if (userInput.form) {
      userInput.form.submit();
    }
  }

  // Step 5: Monitor login result & navigation (60 * 250ms = 15.0s)
  for (let waitStep = 0; waitStep < 60; waitStep++) {
    await new Promise(r => setTimeout(r, 250));

    // หากเปลี่ยนหน้าไปยังหน้าสมาชิกแล้ว Scenario A จะทำงานต่อทันที
    if (window.location.hostname.includes("member.thehof.gg") || !window.location.pathname.includes("/login")) {
      console.log("[FastLogin] เปลี่ยนเส้นทางไปยังหน้าสมาชิกแล้ว!");
      return; // Scenario A will handle extraction & redeem
    }

    if (checkIfRateLimited()) {
      console.warn("[FastLogin] ⚠️ ตรวจพบ Too Many Requests (429) หลังกดยิงล็อกอิน!");
      await sendWorkerDone(wid, {
        status: "failed",
        worker_id: parseInt(wid, 10),
        username: currentTask.username,
        reason: "ติด Limit IP (Too Many Requests 429)",
        is_limit: true,
        time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
      });
      await cleanupAndReturnToLogin(wid);
      return;
    }

    const text = document.body ? document.body.innerText : "";
    if (text.includes("รหัสผ่านไม่ถูกต้อง") || text.includes("ไม่พบบัญชี") || text.includes("ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง") || text.includes("Invalid credentials")) {
      console.warn("[FastLogin] Invalid credentials detected!");
      await sendWorkerDone(wid, {
        status: "failed",
        worker_id: parseInt(wid, 10),
        username: currentTask.username,
        reason: "รหัสผ่านไม่ถูกต้อง / ไม่พบบัญชี",
        time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
      });
      await cleanupAndReturnToLogin(wid);
      return;
    }
  }

  // If timeout without redirect
  console.warn("[FastLogin] Timeout waiting for redirect!");
  const isLimTimeout = checkIfRateLimited();
  await sendWorkerDone(wid, {
    status: "failed",
    worker_id: parseInt(wid, 10),
    username: currentTask.username,
    reason: isLimTimeout ? "ติด Limit IP (Too Many Requests 429)" : "หน้าเว็บไม่เปลี่ยนเส้นทาง (เข้าสู่ระบบไม่สำเร็จ)",
    is_limit: isLimTimeout,
    time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
  });
  await cleanupAndReturnToLogin(wid);
})();
