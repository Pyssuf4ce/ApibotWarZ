import os
import sys
import time

try:
    from DrissionPage import ChromiumOptions, ChromiumPage
    HAS_DP = True
except ImportError:
    HAS_DP = False

TARGET_URL = "https://passport.thehof.gg/hall-of-fame-web/login"

def find_chrome_exe():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    candidates = [
        os.path.join(base_dir, "chrome-win64", "chrome.exe"),
        os.path.join(base_dir, "chrome", "chrome.exe"),
        os.path.join(os.path.dirname(base_dir), "chrome-win64", "chrome.exe"),
        os.path.join(os.path.dirname(base_dir), "Dist_FastLoginSuite", "chrome-win64", "chrome.exe"),
        os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ]
    for c in candidates:
        if os.path.exists(c):
            return c
    return "chrome.exe"

def run_incognito_cdp_test(wid=1):
    print("=" * 65, flush=True)
    print(f"🧪 [CDP Test Engine] เริ่มต้นทดสอบ Turnstile ด้วย DrissionPage (Worker #{wid})", flush=True)
    print("⚙️  โหมด: Incognito 100% (RAM Ephemeral Profile | ไม่บันทึกแคช)", flush=True)
    print("🌐 โปรโตคอล: Chrome DevTools Protocol (CDP)", flush=True)
    print("=" * 65, flush=True)

    if not HAS_DP:
        print("❌ Error: ไม่พบโมดูล DrissionPage กรุณาติดตั้งด้วย: pip install DrissionPage", flush=True)
        return

    chrome_bin = find_chrome_exe()
    co = ChromiumOptions()
    
    # 1. 100% Incognito in RAM
    co.incognito(True)
    co.auto_port()
    
    if os.path.exists(chrome_bin):
        co.set_browser_path(chrome_bin)

    # 2. Anti-Detection & Optimization
    co.set_argument('--no-first-run')
    co.set_argument('--no-default-browser-check')
    co.set_argument('--disable-blink-features=AutomationControlled')
    co.set_argument('--disable-infobars')
    co.set_argument('--window-size=920,720')
    co.set_argument('--window-position=80,80')

    print("🚀 กำลังเปิด Chrome (Incognito + CDP Port)...", flush=True)
    page = ChromiumPage(co)
    
    try:
        print(f"🔗 กำลังโหลดหน้าเว็บเป้าหมาย: {TARGET_URL}", flush=True)
        page.get(TARGET_URL, timeout=15)
        
        print("⏳ รอการเรนเดอร์ของ Cloudflare Turnstile...", flush=True)
        t_start = time.time()
        solved = False

        for step in range(1, 41): # 40 * 0.5s = 20s
            time.sleep(0.5)
            elapsed = time.time() - t_start

            # 1. ตรวจสอบว่าได้ Token แล้วหรือยัง
            try:
                cf_input = page.ele('@name=cf-turnstile-response', timeout=0.1) or \
                           page.ele('@name=g-recaptcha-response', timeout=0.1)
                if cf_input and cf_input.value and len(cf_input.value) > 20:
                    print(f"🎉 [CDP Test] ✅ Turnstile ผ่านแล้วใน {elapsed:.1f}s! (Token: {cf_input.value[:20]}...)", flush=True)
                    solved = True
                    break
            except Exception:
                pass

            # 2. ตรวจสอบและทะลวง Iframe ของ Cloudflare ด้วย CDP
            try:
                cf_frame = page.get_frame('@src^https://challenges.cloudflare.com')
                if cf_frame:
                    cb = cf_frame.ele('@type=checkbox', timeout=0.2) or \
                         cf_frame.ele('.ctp-checkbox-label', timeout=0.2) or \
                         cf_frame.ele('#challenge-stage', timeout=0.2) or \
                         cf_frame.ele('tag:body', timeout=0.2)
                    
                    if cb and (step in [4, 8, 14, 22]):
                        print(f"🖱️ [CDP Test ({elapsed:.1f}s)] ตรวจพบกล่อง Turnstile Iframe ➔ ส่ง Native CDP Click...", flush=True)
                        cb.click()
            except Exception as e:
                pass

            if step % 6 == 0:
                print(f"🛡️ [CDP Test] กำลังเฝ้าสังเกตการณ์ Turnstile... ({elapsed:.1f}s)", flush=True)

        if not solved:
            print(f"⚠️ [CDP Test] ครบกำหนด 20s ไม่พบ Token อัตโนมัติ (หน้าต่างยังคงเปิดอยู่เพื่อให้ทดสอบคลิกด้วยตนเอง)", flush=True)
        
        print("💡 หน้าต่าง Chrome Test Mode กำลังเปิดอยู่ กดปิดหน้าต่าง Chrome เมื่อทดสอบเสร็จสิ้น", flush=True)
        
        while True:
            try:
                if not page.tabs_count:
                    break
                time.sleep(1)
            except Exception:
                break

    except Exception as ex:
        print(f"❌ [CDP Test] เกิดข้อผิดพลาด: {ex}", flush=True)

if __name__ == "__main__":
    wid = 1
    if len(sys.argv) > 1 and sys.argv[1].isdigit():
        wid = int(sys.argv[1])
    run_incognito_cdp_test(wid)
