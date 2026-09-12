// FastLogin Chrome Extension - Content Script (Direct Sync Engine)
(async function() {
  console.log("[FastLogin] Content script loaded on:", window.location.href);

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
      el.focus();
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
        chrome.runtime.sendMessage({ type: "GET_ALL_COOKIES" }, (response) => {
          resolve(response);
        });
        setTimeout(() => resolve(null), 1000);
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

  // Robust Server Communication via Extension Service Worker (CORS & PNA Bypassed)
  async function sendWorkerDone(wid, payload) {
    try {
      const res = await new Promise((resolve) => {
        chrome.runtime.sendMessage({ type: "REPORT_DONE", wid: wid, payload: payload }, (response) => {
          resolve(response);
        });
        setTimeout(() => resolve(null), 2500);
      });
      if (res && res.success) {
        console.log("[FastLogin] ✅ รายงานผลลัพธ์ผ่าน Service Worker สำเร็จ");
        return true;
      }
    } catch (e) {
      console.warn("[FastLogin] Service Worker report_done error:", e);
    }

    // Direct fetch fallback
    try {
      await fetch(`http://127.0.0.1:5000/worker/${wid}/done`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });
      console.log("[FastLogin] ✅ รายงานผลลัพธ์ผ่าน Direct Fetch สำเร็จ");
      return true;
    } catch (e) {
      console.error("[FastLogin] Direct fetch error:", e);
      return false;
    }
  }

  async function fetchWorkerTask(wid) {
    try {
      const res = await new Promise((resolve) => {
        chrome.runtime.sendMessage({ type: "FETCH_TASK", wid: wid }, (response) => {
          resolve(response);
        });
        setTimeout(() => resolve(null), 1500);
      });
      if (res && res.success && res.data && res.data.task) {
        return res.data.task;
      }
    } catch (e) {}

    // Direct fetch fallback
    try {
      const r = await fetch(`http://127.0.0.1:5000/worker/${wid}/task`, {
        cache: "no-store",
        headers: { "Accept": "application/json" }
      });
      if (r.ok) {
        const data = await r.json();
        return data?.task || null;
      }
    } catch (e) {}

    return null;
  }

  async function cleanupAndReturnToLogin(wid) {
    try {
      await chrome.storage.local.remove(["fastlogin_user", "fastlogin_task"]);
    } catch (e) {}

    try {
      localStorage.clear();
      sessionStorage.clear();
    } catch (e) {}

    try {
      await new Promise((resolve) => {
        chrome.runtime.sendMessage({ type: "CLEAR_COOKIES" }, () => resolve());
        setTimeout(resolve, 600);
      });
    } catch (e) {}

    setTimeout(() => {
      window.location.href = `https://passport.thehof.gg/hall-of-fame-web/login#wid=${wid}`;
    }, 1200);
  }

  // ══════════════════════════════════════════════════════════════════
  //  SCENARIO A: On member.thehof.gg or after successful login
  // ══════════════════════════════════════════════════════════════════
  const isMemberPage = window.location.hostname.includes("member.thehof.gg") || 
                       (window.location.hostname.includes("thehof.gg") && !window.location.pathname.includes("/login"));

  if (isMemberPage) {
    console.log("[FastLogin] 🎉 ตรวจพบหน้า Member Portal! เริ่มการสกัดโทเคนและรับรางวัล...");
    showHUD("🎉 ล็อกอินสำเร็จ! กำลังสกัด Token และรับรางวัล...", "#10b981");

    // อ่านข้อมูล Worker & Username จาก chrome.storage.local (ไม่หลุดแม้ข้ามโดเมน)
    let wid = "1";
    let username = "";
    try {
      const stored = await chrome.storage.local.get(["fastlogin_wid", "fastlogin_user"]);
      if (stored.fastlogin_wid) wid = stored.fastlogin_wid.toString();
      if (stored.fastlogin_user) username = stored.fastlogin_user;
    } catch (e) {}

    // ดึง Token จากทุกแหล่ง
    let token = "";
    for (let attempt = 0; attempt < 20; attempt++) {
      token = await extractToken();
      if (token && token.length > 30) break;
      await new Promise(r => setTimeout(r, 250));
    }

    console.log(`[FastLogin] Worker-${wid} | บัญชี: '${username}' | Token Length: ${token.length}`);

    let redeemStatus = "unknown";
    let itemsStr = "";
    let detailMsg = "เข้าสู่ระบบสำเร็จ";

    // ยิง API รับรางวัลกิจกรรม — retry สูงสุด 2 รอบถ้า fail ครั้งแรก
    const eventId = "a297cbd7-c1c4-448f-9e9f-0f2a35d28ac3"; // STAY ALIVE event
    const redeemUrl = `https://core-api.thehof.gg/me/events/${eventId}/redeem`;

    for (let redeemAttempt = 0; redeemAttempt < 2; redeemAttempt++) {
      if (redeemAttempt > 0) {
        await new Promise(r => setTimeout(r, 1500)); // รอก่อน retry
      }
      try {
        console.log(`[FastLogin] 🎁 กำลังยิงรับรางวัล (รอบ ${redeemAttempt + 1}):`, redeemUrl);
        const headers = {
          "Accept": "application/json, text/plain, */*",
          "Content-Type": "application/json"
        };
        if (token) {
          headers["Authorization"] = `Bearer ${token}`;
        }
        const r = await fetch(redeemUrl, {
          method: "POST",
          credentials: "include",
          headers: headers
        });

        if (r.ok) {
          const resJson = await r.json().catch(() => ({}));
          const rawItems = resJson?.data?.bundle_transaction?.items || [];
          const itemsList = [];
          for (const itm of rawItems) {
            const iName = itm?.item?.name || "Item";
            const qty = itm?.quantity || 1;
            itemsList.push(`${iName} x${qty}`);
          }
          itemsStr = itemsList.length > 0 ? itemsList.join(", ") : "รับไอเทมสำเร็จ";
          redeemStatus = "success";
          detailMsg = `🎁 รับรางวัลสำเร็จ: ${itemsStr}`;
          console.log(`[FastLogin] 🎉 ${detailMsg}`);
          break; // สำเร็จแล้ว ออกจาก loop retry
        } else {
          const errText = await r.text().catch(() => "");
          let errJson = {};
          try { errJson = JSON.parse(errText); } catch(e) {}
          const msg = errJson.message || errText.slice(0, 80) || "";
          const msgLower = msg.toLowerCase();
          if (msg.includes("ไม่พบ") || msgLower.includes("already") || msg.includes("เคยรับ") || msgLower.includes("not found") || msgLower.includes("reward") || msgLower.includes("can't found")) {
            redeemStatus = "already_claimed";
            detailMsg = "🟡 รับรางวัลไปแล้วก่อนหน้า";
            console.log(`[FastLogin] 🟡 ${detailMsg}`);
            break; // already claimed — ไม่ต้อง retry
          } else {
            redeemStatus = "failed";
            detailMsg = `⚠️ รับรางวัล: ${msg || `HTTP ${r.status}`}`;
            console.warn(`[FastLogin] ⚠️ Redeem attempt ${redeemAttempt + 1} failed: ${detailMsg}`);
            // ถ้าเป็น attempt สุดท้ายก็จบ ถ้ายังมีรอบ retry ต่อ
          }
        }
      } catch (e) {
        redeemStatus = "failed";
        detailMsg = `⚠️ รับรางวัล: Network Error`;
        console.error(`[FastLogin] Redeem attempt ${redeemAttempt + 1} error:`, e);
      }
    }

    // คลิกปุ่มรับของบนหน้าเว็บหากมี (DOM Clicker)
    try {
      const allButtons = Array.from(document.querySelectorAll("button, a, div, span, [role='button']"));
      for (const btn of allButtons) {
        const txt = (btn.innerText || btn.textContent || "").trim();
        if (txt.includes("รับของ") || txt.includes("รับรางวัล") || txt.includes("แลกรับ") || txt.includes("กดรับ") || txt.includes("Claim") || txt.includes("Redeem")) {
          if (!txt.includes("เคยรับ") && !txt.includes("แล้ว")) {
            console.log(`[FastLogin] 🖱️ กำลังกดปุ่มบนหน้าเว็บ: "${txt}"`);
            btn.click();
            await new Promise(r => setTimeout(r, 600));
            break;
          }
        }
      }
    } catch (e) {}

    // ส่งผลลัพธ์กลับไปยัง Local Server (ผ่าน SW หรือ fetch)
    await sendWorkerDone(wid, {
      status: "success",
      worker_id: parseInt(wid, 10),
      username: username,
      token: token,
      redeem_status: redeemStatus,
      items: itemsStr,
      detail: detailMsg,
      time: "0s"
    });

    // ล้างเซสชันและนำหน้าต่างกลับไปรอคิวล็อกอินบัญชีถัดไป
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

  function checkIfRateLimited() {
    const title = (document.title || "").toLowerCase();
    const text = (document.body ? document.body.innerText : "").toLowerCase();
    if (title.includes("too many requests") || title.includes("error 1015") || title.includes("rate limited") || title.includes("429")) {
      return true;
    }
    if (text.includes("too many requests") || text.includes("error 1015") || text.includes("you are being rate limited") || text.includes("429 too many requests") || text.includes("rate limit exceeded")) {
      return true;
    }
    return false;
  }

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

  // Step 1: Poll server for assigned task
  let currentTask = null;
  while (!currentTask) {
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
    await new Promise(r => setTimeout(r, 500));
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

  // Step 2: Wait for Cloudflare Turnstile to auto-pass naturally (Fast Timeout: 6.0s)
  let turnstileSolved = false;
  for (let step = 0; step < 24; step++) { // 24 * 250ms = 6.0s
    const cfInput = document.querySelector('[name="cf-turnstile-response"]');
    if (cfInput && cfInput.value && cfInput.value.length > 30) {
      turnstileSolved = true;
      console.log("[FastLogin] ✅ Cloudflare Turnstile ยืนยันสำเร็จ (มี Token)!");
      showHUD(`🛡️ Turnstile ผ่านแล้ว! กำลังเข้าสู่ระบบ: ${currentTask.username}`, "#10b981");
      break;
    }

    if (step === 0 || step === 8) {
      showHUD(`🛡️ กำลังรอ Cloudflare Turnstile ยืนยันอัตโนมัติ...`, "#8b5cf6");
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
    console.warn("[FastLogin] ⚠️ Turnstile ไม่ผ่านใน 6s ➔ ส่งรันซ้ำทันที");
    showHUD(`🔄 Turnstile ไม่ผ่านใน 6s ➔ นำกลับไปรันซ้ำรอบหน้า`, "#f59e0b");
    await sendWorkerDone(wid, {
      status: "failed",
      worker_id: parseInt(wid, 10),
      username: currentTask.username,
      reason: "Cloudflare Turnstile หมดเวลา (ส่งรันซ้ำ)",
      time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
    });
    location.reload();
    return;
  }

  // Step 3: Fill Username & Password into the form
  console.log(`[FastLogin] ✍️ กำลังกรอกข้อมูลล็อกอินสำหรับ ${currentTask.username}...`);

  let passInput = document.querySelector('input[type="password"]') ||
                  document.querySelector('#password') || 
                  document.querySelector('input[name="password"]') || 
                  document.querySelector('input[autocomplete="current-password"]');

  let userInput = document.querySelector('#username') || 
                  document.querySelector('#hofId') || 
                  document.querySelector('input[name="username"]') || 
                  document.querySelector('input[name="account"]') || 
                  document.querySelector('input[name="email"]') || 
                  document.querySelector('input[autocomplete="username"]') ||
                  document.querySelector('input[type="text"]') ||
                  document.querySelector('input:not([type="password"]):not([type="hidden"]):not([type="checkbox"])');

  if (!userInput || !passInput) {
    console.error("[FastLogin] ❌ ไม่พบช่องกรอกข้อมูลใน DOM!");
    const isLim = checkIfRateLimited();
    await sendWorkerDone(wid, {
      status: "failed",
      worker_id: parseInt(wid, 10),
      username: currentTask.username,
      reason: isLim ? "ติด Limit IP (Too Many Requests 429)" : "ไม่พบช่องกรอกชื่อผู้ใช้หรือรหัสผ่าน",
      is_limit: isLim,
      time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
    });
    return;
  }

  setInputValue(userInput, currentTask.username);
  setInputValue(passInput, currentTask.password);

  await new Promise(r => setTimeout(r, 400));

  // Step 4: Request Paced Submit Permit to guarantee NO 429 Collisions
  console.log(`[FastLogin] 🚦 กำลังรอคิว Pacing ป้องกันชน Limit ก่อนกดยิงล็อกอิน (Worker-${wid})...`);
  try {
    await fetch(`http://127.0.0.1:5000/worker/${wid}/acquire_submit`, { cache: "no-store" });
  } catch (e) {}

  // Jitter delay 0-600ms เพิ่มเติม เพื่อกระจาย timing ป้องกัน 429/1015 แม้ผ่าน pacing แล้ว
  await new Promise(r => setTimeout(r, Math.floor(Math.random() * 600)));

  console.log("[FastLogin] 🚀 กำลังคลิกปุ่มเข้าสู่ระบบ (Submit)...");
  
  // Search for Submit Button across all possible DOM patterns
  let submitBtn = document.querySelector('button[type="submit"]') ||
                  document.querySelector('.button-submit') || 
                  document.querySelector('button.submit') ||
                  document.querySelector('.btn-primary') ||
                  document.querySelector('form button');

  if (!submitBtn) {
    const allBtns = Array.from(document.querySelectorAll('button, [role="button"], input[type="submit"], a.btn, div.button'));
    for (const b of allBtns) {
      const txt = (b.innerText || b.textContent || b.value || "").trim();
      if (txt.includes("เข้าสู่ระบบ") || txt.includes("ล็อกอิน") || txt.includes("Login") || txt.includes("Sign in") || txt.includes("Submit") || txt.includes("ยืนยัน")) {
        submitBtn = b;
        break;
      }
    }
  }

  if (submitBtn) {
    submitBtn.disabled = false;
    submitBtn.removeAttribute('disabled');
    submitBtn.focus();
    submitBtn.click();
    submitBtn.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
  } else if (userInput.form) {
    userInput.form.submit();
  } else if (passInput.form) {
    passInput.form.submit();
  }

  // Step 5: Monitor login result
  for (let waitStep = 0; waitStep < 50; waitStep++) { // 50 * 300ms = 15.0s
    await new Promise(r => setTimeout(r, 300));

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
      return;
    }

    const text = document.body ? document.body.innerText : "";
    if (text.includes("รหัสผ่านไม่ถูกต้อง") || text.includes("ไม่พบบัญชี") || text.includes("ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง")) {
      console.warn("[FastLogin] Invalid credentials detected!");
      await sendWorkerDone(wid, {
        status: "failed",
        worker_id: parseInt(wid, 10),
        username: currentTask.username,
        reason: "รหัสผ่านไม่ถูกต้อง / ไม่พบบัญชี",
        time: `${((Date.now() - tStart) / 1000).toFixed(1)}s`
      });
      location.reload();
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
  location.reload();
})();
