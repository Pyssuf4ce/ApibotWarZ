import sys
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

from fastapi import FastAPI
from pydantic import BaseModel
import uvicorn
import time
import threading
from DrissionPage import ChromiumOptions, ChromiumPage

# สร้างอินสแตนซ์ของ FastAPI สำหรับทำ Local API Server
app = FastAPI()

# กำหนด Semaphore รองรับ 5 บอทพร้อมกัน
browser_semaphore = threading.Semaphore(5)
request_counter = 0
counter_lock = threading.Lock()

# กำหนดโครงสร้างข้อมูลสำหรับรับค่า Request (รับค่า URL จากฝั่ง Go)
class TurnstileRequest(BaseModel):
    url: str

@app.post("/get-token")
def get_token(req: TurnstileRequest):
    global request_counter
    with counter_lock:
        request_counter += 1
        req_id = request_counter

    print(f"\n[Worker-{req_id}] 🌍 ได้รับคำขอแก้ Captcha: {req.url}")
    
    with browser_semaphore:
        # กำหนดค่าคอนฟิกของเบราว์เซอร์ Chromium เพื่อความเสถียรและหลบเลี่ยง Cloudflare
        co = ChromiumOptions()
        co.auto_port() # สุ่มพอร์ตอัตโนมัติเพื่อป้องกันการชนกันของพอร์ต
        co.set_user(f"worker_{req_id % 10}") # แยกโปรไฟล์เดี่ยวของแต่ละ Worker ป้องกันการล็อกไฟล์แย่งกัน
        co.set_argument('--window-size=1024,1000')
        co.set_argument('--disable-blink-features=AutomationControlled')
        co.set_argument('--no-default-browser-check')
        co.set_argument('--no-first-run')
        co.set_argument('--mute-audio')
        co.incognito(True)
        
        page = None
        t_start = time.time()
        try:
            page = ChromiumPage(co)
            page.get(req.url)
            print(f"[Worker-{req_id}] ⏳ หน้าเว็บกำลังโหลด...")

            # หน่วงเวลาเริ่มต้นสั้นๆ 2 วินาที
            time.sleep(2.0)

            token = ""
            clicked = False
            print(f"[Worker-{req_id}] ⚡ เริ่มระบบ Smart Click Engine แก้ Captcha...")
            
            # วนลูปตรวจสอบ Token และคลิกอย่างถูกต้อง (คลิก 1 ครั้งแล้วรอผล ป้องกันการคลิกซ้ำจนระบบรวน)
            for step in range(25):
                # 1. เช็คว่ามี Token ปรากฏขึ้นมาหรือยัง
                try:
                    cf_input = page.ele('@name=cf-turnstile-response', timeout=0.2)
                    if cf_input and cf_input.value:
                        token = cf_input.value
                        print(f"[Worker-{req_id}] 🎉 ผ่านด่านสำเร็จใน {time.time()-t_start:.1f} วินาที! ได้รับ Token เรียบร้อย")
                        break
                except Exception:
                    pass

                # 2. ส่งคำสั่งคลิกครั้งแรกตรงจุด Checkbox เมื่อหน้าเว็บโหลดพร้อม
                if not clicked and step >= 1:
                    try:
                        iframe = page.ele('tag:iframe@src*cloudflare', timeout=0.4)
                        if iframe:
                            loc = iframe.rect.location
                            page.actions.move_to((int(loc[0] + 30), int(loc[1] + 35))).click()
                        else:
                            page.actions.move_to((367, 599)).click()
                        clicked = True
                    except Exception:
                        try:
                            page.actions.move_to((367, 599)).click()
                            clicked = True
                        except Exception:
                            pass
                elif clicked and step % 6 == 0:
                    # คลิกซ้ำเฉพาะกรณีผ่านไป 5-6 วินาทีแล้ว Token ยังไม่มา (Retry)
                    try:
                        iframe = page.ele('tag:iframe@src*cloudflare', timeout=0.2)
                        if iframe:
                            loc = iframe.rect.location
                            page.actions.move_to((int(loc[0] + 30), int(loc[1] + 35))).click()
                        else:
                            page.actions.move_to((367, 599)).click()
                    except Exception:
                        pass

                time.sleep(0.8)
               
            # ส่งผลลัพธ์กลับไปยัง Local API (ฝั่ง Go)
            if token:
                return {"status": "success", "token": token}
            else:
                print(f"[Worker-{req_id}] ❌ หมดเวลา: ไม่พบ Token จาก Cloudflare (ใช้เวลา {time.time()-t_start:.1f}s)")
                return {"status": "error", "message": "หมดเวลา ไม่ได้รับ Token"}
               
        except Exception as server_err:
            print(f"[Worker-{req_id}] ❌ เกิดข้อผิดพลาดในระบบ: {server_err}")
            return {"status": "error", "message": str(server_err)}
           
        finally:
            # ปิดเบราว์เซอร์ทุกครั้งหลังทำงานเสร็จ เพื่อเคลียร์แรมและทรัพยากรเครื่อง
            if page:
                try:
                    page.quit()
                except Exception:
                    pass

if __name__ == "__main__":
    print("🚀 เริ่มรัน Local API Server (Active Re-Click Engine) ที่ http://127.0.0.1:5000")
    uvicorn.run(app, host="127.0.0.1", port=5000)