import sys
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

from contextlib import asynccontextmanager
from fastapi import FastAPI
from pydantic import BaseModel
import uvicorn
import time
import queue
import threading
from DrissionPage import ChromiumOptions, ChromiumPage

POOL_SIZE = 3
browser_pool = queue.Queue(maxsize=POOL_SIZE)
request_counter = 0
counter_lock = threading.Lock()

def create_browser_instance(worker_id: int):
    co = ChromiumOptions()
    co.auto_port()
    co.set_user(f"warm_worker_{worker_id}")
    w = 1000 + (worker_id % 5) * 20
    h = 950 + (worker_id % 5) * 15
    co.set_argument(f'--window-size={w},{h}')
    co.set_argument('--disable-blink-features=AutomationControlled')
    co.set_argument('--no-default-browser-check')
    co.set_argument('--no-first-run')
    co.set_argument('--mute-audio')
    co.set_argument('--no-sandbox')
    co.set_argument('--disable-gpu')
    co.incognito(True)
    page = ChromiumPage(co)
    try:
        page.set.timeouts(page_load=8, script=8)
    except Exception:
        pass
    return page

def init_warm_pool():
    print(f"⚡ กำลังเตรียม Persistent Warm Browser Pool ({POOL_SIZE} Instances) ในเบื้องหลัง...")
    for i in range(POOL_SIZE):
        try:
            page = create_browser_instance(i)
            browser_pool.put((i, page))
            print(f"✅ Warm Worker-{i} พร้อมใช้งาน")
        except Exception as e:
            print(f"⚠️ ไม่สามารถเปิด Warm Worker-{i}: {e}")

def shutdown_pool():
    while not browser_pool.empty():
        try:
            _, page = browser_pool.get_nowait()
            page.quit()
        except Exception:
            pass

@asynccontextmanager
async def lifespan(app: FastAPI):
    # เริ่มต้น Warm Pool ใน Background Thread เพื่อให้เซิร์ฟเวอร์เปิดรับคำขอได้ทันที 0ms
    threading.Thread(target=init_warm_pool, daemon=True).start()
    yield
    shutdown_pool()

# สร้างอินสแตนซ์ของ FastAPI พร้อม Lifespan Handler
app = FastAPI(lifespan=lifespan)

# ⚡ Endpoint รีเฟรช Warm Browser Pool หลังสลับ VPN
# เพื่อล้าง session/cookies ของ IP เก่าที่ Cloudflare จำได้ → ป้องกัน Turnstile block
@app.post("/refresh-pool")
def refresh_pool():
    refreshed = 0
    failed = 0
    temp_browsers = []

    # ดึง browser ทุกตัวออกมารีเฟรช
    while not browser_pool.empty():
        try:
            worker_id, page = browser_pool.get_nowait()
            temp_browsers.append((worker_id, page))
        except Exception:
            break

    for worker_id, page in temp_browsers:
        try:
            page.get("about:blank", timeout=3)
            refreshed += 1
            print(f"🔄 Refresh Warm Worker-{worker_id} สำเร็จ (ล้าง session IP เก่า)")
        except Exception as e:
            print(f"⚠️ Refresh Worker-{worker_id} ล้มเหลว: {e} → กำลังสร้างใหม่")
            try:
                page.quit()
            except Exception:
                pass
            try:
                page = create_browser_instance(worker_id)
                print(f"✅ สร้าง Worker-{worker_id} ใหม่สำเร็จ")
            except Exception as e2:
                print(f"❌ สร้าง Worker-{worker_id} ใหม่ล้มเหลว: {e2}")
                failed += 1
                continue
        browser_pool.put((worker_id, page))

    msg = f"🔄 รีเฟรช Warm Pool เสร็จสิ้น: สำเร็จ {refreshed} ตัว, ล้มเหลว {failed} ตัว"
    print(msg)
    return {"status": "ok", "refreshed": refreshed, "failed": failed}

class TurnstileRequest(BaseModel):
    url: str

@app.post("/get-token")
def get_token(req: TurnstileRequest):
    global request_counter
    with counter_lock:
        request_counter += 1
        req_id = request_counter

    t_start = time.time()
    # ดึง Browser Instance จาก Pool
    worker_id, page = browser_pool.get()
    should_recreate = False

    print(f"[Worker-{req_id}] 🌍 ได้รับคำขอแก้ Captcha บน Warm Worker-{worker_id}")

    try:
        page.get(req.url, timeout=8)

        token = ""
        clicked = False

        # วนลูปตรวจจับ Token อย่างรวดเร็ว (55 รอบ x 0.15s = สูงสุด ~8.2s เร็วทันใจ ไม่ค้าง)
        for step in range(55):
            # 1. เช็คว่ามี Token ใน DOM หรือยัง
            try:
                cf_input = page.ele('@name=cf-turnstile-response', timeout=0.1)
                if cf_input and cf_input.value:
                    token = cf_input.value
                    print(f"[Worker-{req_id}] 🎉 ผ่านด่านสำเร็จใน {time.time()-t_start:.1f} วินาที! ได้รับ Token เรียบร้อย")
                    break
            except Exception:
                pass

            # 2. เช็ค iframe และคลิกทันทีที่โผล่มา (เริ่มตรวจตั้งแต่ 300ms)
            if not clicked and step >= 2:
                try:
                    iframe = page.ele('tag:iframe@src*cloudflare', timeout=0.2)
                    if iframe:
                        page.actions.move_to(iframe).click()
                        clicked = True
                    else:
                        page.actions.move_to((367, 599)).click()
                        clicked = True
                except Exception:
                    pass
            elif clicked and step % 15 == 0:
                # คลิกซ้ำทุก ~2.2s ถ้ายังไม่ผ่าน
                try:
                    iframe = page.ele('tag:iframe@src*cloudflare', timeout=0.1)
                    if iframe:
                        page.actions.move_to(iframe).click()
                    else:
                        page.actions.move_to((367, 599)).click()
                except Exception:
                    pass

            time.sleep(0.15)

        if token:
            return {"status": "success", "token": token}
        else:
            print(f"[Worker-{req_id}] ❌ หมดเวลา: ไม่พบ Token จาก Cloudflare (ใช้เวลา {time.time()-t_start:.1f}s)")
            return {"status": "error", "message": "หมดเวลา ไม่ได้รับ Token"}

    except Exception as server_err:
        print(f"[Worker-{req_id}] ❌ เกิดข้อผิดพลาดบน Worker-{worker_id}: {server_err}")
        should_recreate = True
        return {"status": "error", "message": str(server_err)}

    finally:
        if should_recreate:
            try:
                page.quit()
            except Exception:
                pass
            try:
                new_page = create_browser_instance(worker_id)
                browser_pool.put((worker_id, new_page))
            except Exception as e:
                print(f"❌ Recreate Worker-{worker_id} ล้มเหลว: {e}")
        else:
            browser_pool.put((worker_id, page))

if __name__ == "__main__":
    print("🚀 เริ่มรัน Local API Server (Persistent Warm Pool Engine) ที่ http://127.0.0.1:5000")
    uvicorn.run(app, host="127.0.0.1", port=5000)