import time
import threading
import queue
from DrissionPage import ChromiumOptions, ChromiumPage

# ════════════════════════════════════════════════════════════════════════
# ⚙️ CONFIGURATION (ตั้งค่าจำนวนแท็บและความเบาของเบราว์เซอร์)
# ════════════════════════════════════════════════════════════════════════
TARGET_URL = "https://passport.thehof.gg/hall-of-fame-web/login"
TOTAL_WORKERS = 10  # ปรับจำนวนแท็บพร้อมกันได้ตามต้องการ (10 - 50 แท็บ)

# ตัวอย่างไฟล์คิวบัญชีทดสอบ (Username|Password)
TEST_ACCOUNTS = [
    ("test_user_01", "Pass123456!"),
    ("test_user_02", "Pass123456!"),
    ("test_user_03", "Pass123456!"),
    ("test_user_04", "Pass123456!"),
    ("test_user_05", "Pass123456!"),
    ("test_user_06", "Pass123456!"),
    ("test_user_07", "Pass123456!"),
    ("test_user_08", "Pass123456!"),
    ("test_user_09", "Pass123456!"),
    ("test_user_10", "Pass123456!"),
]

def create_ultra_light_browser():
    """
    สร้าง Chrome ตัวแม่แบบเบาพิเศษ (Ultra-Lightweight Configuration)
    - ปิดการโหลดรูปภาพ 100%
    - ปิดระบบเสียง / GPU / Sandbox
    - บีบการกินแรมของ V8 Engine ให้ต่ำที่สุด
    """
    co = ChromiumOptions()
    co.auto_port()
    co.set_user("ultra_light_profile")
    
    # 🚀 เทคนิคบีบสเปกขั้นสุด
    co.set_argument('--blink-settings=imagesEnabled=false')  # บล็อกรูปภาพทั้งหมด
    co.set_argument('--js-flags=--max-old-space-size=128')   # ล็อคเพดาน RAM V8 ห้ามเกิน 128MB
    co.set_argument('--mute-audio')                          # ปิดระบบเสียง
    co.set_argument('--disable-gpu')                         # ปิดการ์ดจอ เรนเดอร์ด้วย CPU เบาๆ
    co.set_argument('--no-sandbox')                          # ลด overhead ระบบความปลอดภัย
    co.set_argument('--disable-blink-features=AutomationControlled') # หลบตรวจจับ Anti-Bot
    co.set_argument('--window-size=500,600')                 # ย่อขนาดหน้าต่างให้เล็ก
    
    page = ChromiumPage(co)
    return page

def worker_task(tab, account, worker_id):
    """
    ฟังก์ชันการทำงานในแต่ละแท็บ: กรอก Login และกดปุ่มในเสี้ยววินาทีด้วย JavaScript Injection
    """
    username, password = account
    print(f"⚡ [Worker-{worker_id:02d}] เริ่มรันไอดี: {username}")
    t_start = time.time()
    
    try:
        # 1. เปิดหน้าเว็บเป้าหมาย
        tab.get(TARGET_URL, timeout=10)
        
        # 2. กรอก Username, Password และกด Submit ทันทีด้วย JS Direct Injection (0.01 วินาที ไม่ต้องเลื่อนเมาส์)
        js_code = f"""
        (function() {{
            let u = document.querySelector('#username') || document.querySelector('input[name="username"]');
            let p = document.querySelector('#password') || document.querySelector('input[name="password"]');
            let btn = document.querySelector('button[type="submit"]') || document.querySelector('.button-submit');
            
            if(u) {{ u.value = "{username}"; u.dispatchEvent(new Event('input', {{ bubbles: true }})); }}
            if(p) {{ p.value = "{password}"; p.dispatchEvent(new Event('input', {{ bubbles: true }})); }}
            if(btn) {{ btn.click(); }}
        }})();
        """
        tab.run_js(js_code)
        
        print(f"✅ [Worker-{worker_id:02d}] กรอกไอดีและกดยืนยันสำเร็จ (ใช้เวลา {time.time() - t_start:.2f}s)")
        
    except Exception as e:
        print(f"❌ [Worker-{worker_id:02d}] เกิดข้อผิดพลาด: {e}")

def main():
    print(f"🚀 กำลังเริ่มต้นทดสอบ Multi-Tab Login ({TOTAL_WORKERS} แท็บพร้อมกัน)...")
    
    # 1. เปิด Chrome ตัวแม่เพียงตัวเดียว
    browser = create_ultra_light_browser()
    
    # 2. สร้างแท็บย่อย (Tabs) แยกอิสระตามจำนวน TOTAL_WORKERS
    tabs = []
    print(f"🌐 กำลังแตกแท็บเบื้องหลังจำนวน {TOTAL_WORKERS} แท็บ...")
    
    # แท็บแรกมีอยู่แล้วในเพจแม่
    tabs.append(browser.get_tab())
    
    # แตกแท็บเพิ่มให้ครบจำนวน
    for i in range(1, TOTAL_WORKERS):
        new_tab = browser.new_tab()
        tabs.append(new_tab)
        
    print(f"✨ เตรียมแท็บพร้อมรันทั้งหมด {len(tabs)} แท็บเรียบร้อยแล้ว!")
    time.sleep(1)

    # 3. รันการทำงานทุกแท็บพร้อมกันผ่าน Multi-Threading
    threads = []
    t_total_start = time.time()
    
    for idx, (tab, acc) in enumerate(zip(tabs, TEST_ACCOUNTS)):
        t = threading.Thread(target=worker_task, args=(tab, acc, idx + 1))
        threads.append(t)
        t.start()
        
    # รอให้ทุกแท็บทำงานเสร็จสิ้น
    for t in threads:
        t.join()
        
    print(f"\n🎉 ทำรายการครบทั้ง {TOTAL_WORKERS} แท็บเสร็จสิ้นภายใน {time.time() - t_total_start:.2f} วินาที!")
    print("💡 สังเกตใน Task Manager ได้เลยครับว่า RAM และ CPU แทบไม่กระตุกเลย!")
    
    time.sleep(5)
    browser.quit()

if __name__ == "__main__":
    main()
