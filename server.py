from fastapi import FastAPI
from pydantic import BaseModel
import uvicorn
import time
import pyautogui
from DrissionPage import ChromiumOptions, ChromiumPage

# สร้างอินสแตนซ์ของ FastAPI สำหรับทำ Local API Server
app = FastAPI()

# กำหนดโครงสร้างข้อมูลสำหรับรับค่า Request (รับค่า URL จากฝั่ง Go)
class TurnstileRequest(BaseModel):
    url: str

@app.post("/get-token")
def get_token(req: TurnstileRequest):
    print(f"\n🌍 ได้รับคำขอให้เข้าไปแก้ Captcha ที่: {req.url}")
    
    # กำหนดค่าคอนฟิกของเบราว์เซอร์ Chromium
    co = ChromiumOptions()
    co.auto_port() # สุ่มพอร์ตอัตโนมัติเพื่อป้องกันการชนกันของพอร์ต
    
    # 📌 ส่วนการล็อกขนาดหน้าต่างเบราว์เซอร์ (กว้าง 1024, สูง 1000 พิกเซล)
    co.set_argument('--window-size=1024,1000')
    
    # ปิดการตรวจจับสถานะการใช้งานระบบอัตโนมัติจาก Cloudflare
    co.set_argument('--disable-blink-features=AutomationControlled')
    
    # เปิดใช้งานหน้าเว็บเบราว์เซอร์ผ่าน DrissionPage
    page = ChromiumPage(co)
    
    try:
        # สั่งให้เบราว์เซอร์วิ่งไปที่ URL เป้าหมาย
        page.get(req.url)
        print("⏳ กำลังรอเว็บโหลดและตรวจสอบระบบ Cloudflare (หน่วงเวลา 6 วินาที)...")

        # หน่วงเวลาเพื่อให้หน้าเว็บและสคริปต์ป้องกันโหลดขึ้นมาครบถ้วน
        time.sleep(6)
       
        # จำลองการคลิกเมาส์จริงด้วย PyAutoGUI ไปที่พิกัดที่กำหนด (x=386, y=744)
        print("🖱️ กำลังจำลองการคลิกเมาส์ที่พิกัด X: 386, Y: 744...")
        try:
            # ใช้พิกัดที่ล็อกไว้ร่วมกับขนาดหน้าต่าง 1024x1000
            pyautogui.click(x=386, y=744)
            print("✅ ส่งคำสั่งคลิกที่กล่อง Turnstile สำเร็จ!")
        except Exception as click_err:
            print(f"⚠️ เกิดข้อผิดพลาดในการคลิกพิกัด: {click_err}")

        token = ""
        print("⏳ กำลังรอรับ Token จาก Cloudflare (สูงสุด 45 วินาที)...")
        
        # วนลูปตรวจสอบ Token จากช่องอินพุตที่ซ่อนอยู่บนหน้าเว็บ
        for i in range(45):
            try:
                # ค้นหา element ของ Cloudflare Turnstile Response
                cf_input = page.ele('@name=cf-turnstile-response', timeout=1)
                
                if cf_input and cf_input.value:
                    token = cf_input.value
                    print(f"🎉 ผ่านด่านสำเร็จในวินาทีที่ {i+1}! ได้รับ Token เรียบร้อย")
                    break
            except Exception:
                pass
               
            time.sleep(1)
           
        # ส่งผลลัพธ์กลับไปยัง Local API (ฝั่ง Go)
        if token:
            return {"status": "success", "token": token}
        else:
            print("❌ หมดเวลา 45 วินาที: ไม่พบ Token จาก Cloudflare")
            return {"status": "error", "message": "หมดเวลา 45 วินาที ไม่ได้รับ Token"}
           
    except Exception as server_err:
        print(f"❌ เกิดข้อผิดพลาดในระบบ: {server_err}")
        return {"status": "error", "message": str(server_err)}
       
    finally:
        # ปิดเบราว์เซอร์ทุกครั้งหลังทำงานเสร็จ เพื่อเคลียร์แรมและทรัพยากรเครื่อง
        print("🧹 กำลังปิดเบราว์เซอร์เพื่อเคลียร์ระบบ...")
        page.quit()

if __name__ == "__main__":
    print("🚀 เริ่มรัน Local API Server ที่ http://127.0.0.1:5000")
    uvicorn.run(app, host="127.0.0.1", port=5000)