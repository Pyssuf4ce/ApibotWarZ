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

# กำหนด Semaphore เพื่อจำกัดการเปิดเบราว์เซอร์พร้อมกันสูงสุด 10 ตัว ป้องกันเครื่องค้าง
browser_semaphore = threading.Semaphore(10)
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

    print(f"\n[Worker-{req_id}] 🌍 ได้รับคำขอให้เข้าไปแก้ Captcha ที่: {req.url}")
    
    with browser_semaphore:
        print(f"[Worker-{req_id}] 🚀 เริ่มต้นเปิดเบราว์เซอร์แก้ Captcha...")
        # กำหนดค่าคอนฟิกของเบราว์เซอร์ Chromium
        co = ChromiumOptions()
        co.auto_port() # สุ่มพอร์ตอัตโนมัติเพื่อป้องกันการชนกันของพอร์ต
        
        # 📌 ส่วนการล็อกขนาดหน้าต่างเบราว์เซอร์ (กว้าง 1024, สูง 1000 พิกเซล)
        co.set_argument('--window-size=1024,1000')
        
        # ปิดการตรวจจับสถานะการใช้งานระบบอัตโนมัติจาก Cloudflare
        co.set_argument('--disable-blink-features=AutomationControlled')
        
        # เพิ่มโหมดไม่ระบุตัวตน (Incognito) เพื่อล้าง Cookie เก่าทิ้ง ป้องกันเว็บจำได้
        co.incognito(True)
        
        # เปิดใช้งานหน้าเว็บเบราว์เซอร์ผ่าน DrissionPage
        page = None
        try:
            page = ChromiumPage(co)
            # สั่งให้เบราว์เซอร์วิ่งไปที่ URL เป้าหมาย
            page.get(req.url)
            print(f"[Worker-{req_id}] ⏳ กำลังรอเว็บโหลดและตรวจสอบระบบ Cloudflare (หน่วงเวลา 5 วินาที)...")

            # หน่วงเวลาเพื่อให้หน้าเว็บและสคริปต์ป้องกันโหลดขึ้นมาครบถ้วน
            time.sleep(5)
           
            # จำลองการคลิกเมาส์ภายในเบราว์เซอร์ (ไม่ดึงเมาส์ระบบของเครื่อง)
            print(f"[Worker-{req_id}] 🖱️ กำลังส่งคำสั่งคลิกจำลองที่พิกัด X: 367, Y: 599...")
            try:
                # ใช้ ActionChains ของ DrissionPage คลิกที่พิกัด
                page.actions.move_to((367, 738-139)).click()
                print(f"[Worker-{req_id}] ✅ ส่งคำสั่งคลิกจำลองที่กล่อง Turnstile สำเร็จ!")
            except Exception as click_err:
                print(f"[Worker-{req_id}] ⚠️ เกิดข้อผิดพลาดในการคลิก: {click_err}")

            token = ""
            print(f"[Worker-{req_id}] ⏳ กำลังรอรับ Token จาก Cloudflare (สูงสุด 10 วินาที)...")
            
            # วนลูปตรวจสอบ Token จากช่องอินพุตที่ซ่อนอยู่บนหน้าเว็บ
            for i in range(10):
                try:
                    # ค้นหา element ของ Cloudflare Turnstile Response
                    cf_input = page.ele('@name=cf-turnstile-response', timeout=1)
                    
                    if cf_input and cf_input.value:
                        token = cf_input.value
                        print(f"[Worker-{req_id}] 🎉 ผ่านด่านสำเร็จในวินาทีที่ {i+1}! ได้รับ Token เรียบร้อย")
                        break
                except Exception:
                    pass
                   
                time.sleep(1)
               
            # ส่งผลลัพธ์กลับไปยัง Local API (ฝั่ง Go)
            if token:
                return {"status": "success", "token": token}
            else:
                print(f"[Worker-{req_id}] ❌ หมดเวลา: ไม่พบ Token จาก Cloudflare")
                return {"status": "error", "message": "หมดเวลา ไม่ได้รับ Token"}
               
        except Exception as server_err:
            print(f"[Worker-{req_id}] ❌ เกิดข้อผิดพลาดในระบบ: {server_err}")
            return {"status": "error", "message": str(server_err)}
           
        finally:
            # ปิดเบราว์เซอร์ทุกครั้งหลังทำงานเสร็จ เพื่อเคลียร์แรมและทรัพยากรเครื่อง
            if page:
                print(f"[Worker-{req_id}] 🧹 กำลังปิดเบราว์เซอร์เพื่อเคลียร์ระบบ...")
                try:
                    page.quit()
                except Exception:
                    pass

if __name__ == "__main__":
    print("🚀 เริ่มรัน Local API Server ที่ http://127.0.0.1:5000 (รองรับการรันพร้อมกันหลายบอท)")
    uvicorn.run(app, host="127.0.0.1", port=5000)