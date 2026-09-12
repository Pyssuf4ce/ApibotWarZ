// FastLogin Chrome Extension - Cloudflare Turnstile Automated Solver Clicker
(function() {
  console.log("[FastLogin CF] Turnstile iframe clicker loaded:", window.location.href);

  function triggerEvents(el) {
    if (!el) return;
    try {
      el.focus();
      const rect = el.getBoundingClientRect();
      const cx = rect.left + rect.width / 2;
      const cy = rect.top + rect.height / 2;
      const opts = { bubbles: true, cancelable: true, view: window, clientX: cx, clientY: cy, button: 0, buttons: 1 };

      el.dispatchEvent(new PointerEvent("pointerover", opts));
      el.dispatchEvent(new MouseEvent("mouseover", opts));
      el.dispatchEvent(new PointerEvent("pointerenter", opts));
      el.dispatchEvent(new MouseEvent("mouseenter", opts));
      el.dispatchEvent(new PointerEvent("pointerdown", opts));
      el.dispatchEvent(new MouseEvent("mousedown", opts));
      el.dispatchEvent(new PointerEvent("pointerup", opts));
      el.dispatchEvent(new MouseEvent("mouseup", opts));
      el.dispatchEvent(new MouseEvent("click", opts));
      if (typeof el.click === "function") el.click();
    } catch(e) {}
  }

  function tryClickTurnstile() {
    // 1. If already solved, stop
    if (document.querySelector('#success') || 
        document.querySelector('.ctp-checkbox-checked') || 
        document.querySelector('input[type="checkbox"]:checked')) {
      return true;
    }

    // 2. Try standard checkbox input
    const checkbox = document.querySelector('input[type="checkbox"]');
    if (checkbox && !checkbox.checked) {
      console.log("[FastLogin CF] 🖱️ คลิก Checkbox Cloudflare Turnstile...");
      triggerEvents(checkbox);
      return true;
    }

    // 3. Try challenge stage or wrapper elements
    const stage = document.querySelector('#challenge-stage') || 
                  document.querySelector('.ctp-checkbox-label') || 
                  document.querySelector('#cf-stage') ||
                  document.querySelector('label') ||
                  document.querySelector('#checkbox') ||
                  document.querySelector('div[role="button"]');

    if (stage) {
      console.log("[FastLogin CF] 🖱️ คลิก Stage / Label Cloudflare Turnstile...");
      triggerEvents(stage);
      return true;
    }

    return false;
  }

  // Poll aggressively every 350ms
  let attempts = 0;
  const interval = setInterval(() => {
    attempts++;
    const solved = tryClickTurnstile();
    if (solved || attempts > 70) {
      clearInterval(interval);
    }
  }, 350);

  // Trigger on DOM mutation
  const observer = new MutationObserver(() => {
    tryClickTurnstile();
  });

  if (document.body) {
    observer.observe(document.body, { childList: true, subtree: true });
    tryClickTurnstile();
  } else {
    document.addEventListener("DOMContentLoaded", () => {
      observer.observe(document.body, { childList: true, subtree: true });
      tryClickTurnstile();
    });
  }
})();
