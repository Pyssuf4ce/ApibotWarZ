import sys
import os
import argparse
import subprocess
import socket
import re
import json
import time
import queue
import threading
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, Response
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import uvicorn

try:
    from curl_cffi import requests as tls_requests
    HAS_CURL_CFFI = True
except Exception:
    import requests as tls_requests
    HAS_CURL_CFFI = False

DEFAULT_THREADS = 20
request_counter = 0
counter_lock = threading.Lock()
TARGET_URL = "https://passport.thehof.gg/hall-of-fame-web/login"
MEMBER_URL = "https://member.thehof.gg/"
DEFAULT_EVENT_ID = "a297cbd7-c1c4-448f-9e9f-0f2a35d28ac3" # STAY ALIVE event

if getattr(sys, 'frozen', False):
    BASE_DIR = os.path.dirname(os.path.abspath(sys.executable))
else:
    BASE_DIR = os.path.dirname(os.path.abspath(__file__))

local_ext = os.path.join(BASE_DIR, "extension")
if os.path.exists(local_ext):
    EXTENSION_DIR = os.path.abspath(local_ext).rstrip('\\/')
elif hasattr(sys, '_MEIPASS') and os.path.exists(os.path.join(sys._MEIPASS, "extension")):
    EXTENSION_DIR = os.path.abspath(os.path.join(sys._MEIPASS, "extension")).rstrip('\\/')
else:
    EXTENSION_DIR = os.path.abspath(local_ext).rstrip('\\/')

PROFILES_DIR = os.path.join(BASE_DIR, "profiles")
TOKENS_FILE = os.path.join(BASE_DIR, "tokens.json")
os.makedirs(PROFILES_DIR, exist_ok=True)

tokens_lock = threading.Lock()

def get_configured_threads():
    for i, arg in enumerate(sys.argv):
        if arg in ("--threads", "-threads") and i + 1 < len(sys.argv):
            try:
                return int(sys.argv[i + 1])
            except ValueError:
                pass
    if "FASTLOGIN_THREADS" in os.environ:
        try:
            return int(os.environ["FASTLOGIN_THREADS"])
        except ValueError:
            pass
    return DEFAULT_THREADS

threads_limit = get_configured_threads()
login_semaphore = threading.Semaphore(threads_limit)

# Pool of worker IDs (1 to 50)
worker_pool = queue.Queue()
for _wid in range(1, 51):
    worker_pool.put(_wid)

# Worker Chrome processes and tasks
worker_processes = {}     # wid -> subprocess.Popen
worker_processes_lock = threading.Lock()

worker_tasks = {}         # wid -> dict task
worker_events = {}        # wid -> threading.Event
worker_results = {}       # wid -> dict result
worker_tasks_lock = threading.Lock()

def find_chrome_binary():
    candidates = [
        os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        os.path.expandvars(r"%PROGRAMFILES%\Google\Chrome\Application\chrome.exe"),
        os.path.expandvars(r"%PROGRAMFILES(X86)%\Google\Chrome\Application\chrome.exe"),
    ]
    for p in candidates:
        if os.path.exists(p):
            return p
    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe")
        val, _ = winreg.QueryValueEx(key, "")
        if val and os.path.exists(val):
            return val
    except Exception:
        pass
    return "chrome.exe"

def kill_process_tree(pid: int):
    if not pid or sys.platform != "win32":
        return
    try:
        subprocess.run(
            ["taskkill", "/F", "/T", "/PID", str(pid)],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=0x08000000 # CREATE_NO_WINDOW
        )
    except Exception:
        pass

def get_worker_profile_path(wid: int) -> str:
    path = os.path.join(PROFILES_DIR, f"worker_{wid}")
    os.makedirs(path, exist_ok=True)
    return path

def get_bot_profiles_dir() -> str:
    path = os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\BotProfiles")
    os.makedirs(path, exist_ok=True)
    return path

def close_stale_bot_chromes():
    try:
        import psutil
        for proc in psutil.process_iter(['pid', 'name', 'cmdline']):
            try:
                if proc.info['name'] and 'chrome' in proc.info['name'].lower():
                    cmdline = " ".join(proc.info['cmdline'] or [])
                    if "BotProfiles" in cmdline:
                        proc.kill()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
    except Exception:
        pass

def launch_chrome_worker(wid: int):
    with worker_processes_lock:
        proc = worker_processes.get(wid)
        if proc and proc.poll() is None:
            return proc

        chrome_exe = find_chrome_binary()
        bot_profiles_dir = get_bot_profiles_dir()
        profile_path = os.path.join(bot_profiles_dir, f"worker_{wid}")
        os.makedirs(profile_path, exist_ok=True)

        user_data_arg = f"--user-data-dir={profile_path}"
        print(f"🌟 [Worker-{wid}] เปิด Chrome Bot Profile: worker_{wid}", flush=True)

        # Position windows neatly across screen in 5x4 grid for 20 workers
        col = (wid - 1) % 5
        row = ((wid - 1) // 5) % 4
        x = col * 375 + 10
        y = row * 245 + 10

        init_url = f"https://passport.thehof.gg/hall-of-fame-web/login#wid={wid}"

        # Copy extension into worker profile — always re-copy to guarantee freshness in VM
        import shutil
        worker_ext_dir = os.path.join(profile_path, "extension")
        try:
            if os.path.exists(EXTENSION_DIR):
                # Always remove and re-copy to ensure extension is never stale or corrupted
                if os.path.exists(worker_ext_dir):
                    shutil.rmtree(worker_ext_dir, ignore_errors=True)
                shutil.copytree(EXTENSION_DIR, worker_ext_dir)
                load_ext_path = os.path.abspath(worker_ext_dir).rstrip('\\/')
                # Verify the copy worked
                if os.path.exists(os.path.join(worker_ext_dir, "manifest.json")):
                    print(f"✅ [Worker-{wid}] Extension copy OK → {load_ext_path}", flush=True)
                else:
                    print(f"⚠️ [Worker-{wid}] Extension copy FAILED — manifest.json missing in {worker_ext_dir}", flush=True)
                    load_ext_path = EXTENSION_DIR
            else:
                print(f"⚠️ [Worker-{wid}] EXTENSION_DIR not found: {EXTENSION_DIR}", flush=True)
                load_ext_path = EXTENSION_DIR
        except Exception as ex:
            print(f"⚠️ [Worker-{wid}] Extension copy error: {ex}", flush=True)
            load_ext_path = EXTENSION_DIR

        # Pure Chrome flags matching natural human browser launch 100%
        # Removed all automation-revealing flags (--disable-extensions-except, --silent-debugger, --renderer-process-limit, etc.)
        cmd = [
            chrome_exe,
            user_data_arg,
            "--profile-directory=Default",
            f"--load-extension={load_ext_path}",
            f"--window-position={x},{y}",
            "--window-size=460,400",
            "--no-first-run",
            "--no-default-browser-check",
            "--mute-audio",
            init_url
        ]

        try:
            p = subprocess.Popen(
                cmd,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                close_fds=True,
                creationflags=0x00000200 # CREATE_NEW_PROCESS_GROUP
            )
            worker_processes[wid] = p
            print(f"🖥️ [Worker-{wid}] เปิดหน้าต่าง Google Chrome พร้อม Extension Sync แล้ว (PID: {p.pid})", flush=True)
            return p
        except Exception as e:
            print(f"❌ [Worker-{wid}] ไม่สามารถเปิด Chrome ได้: {e}", flush=True)
            return None

def close_worker_chrome(wid: int):
    with worker_processes_lock:
        proc = worker_processes.pop(wid, None)
        if proc:
            kill_process_tree(proc.pid)

def close_all_worker_browsers():
    with worker_processes_lock:
        for wid, proc in list(worker_processes.items()):
            if proc:
                kill_process_tree(proc.pid)
        worker_processes.clear()
    close_stale_bot_chromes()
    print("🧹 ปิดหน้าต่าง Chrome Worker ทั้งหมดเรียบร้อยแล้ว", flush=True)

def is_valid_jwt(token: str) -> bool:
    if not token or not isinstance(token, str):
        return False
    parts = token.strip().split(".")
    return len(parts) == 3 and parts[0].startswith("eyJ") and parts[1].startswith("eyJ")

def save_account_token(username: str, token: str):
    if not is_valid_jwt(token):
        return
    with tokens_lock:
        data = {}
        if os.path.exists(TOKENS_FILE):
            try:
                with open(TOKENS_FILE, "r", encoding="utf-8") as f:
                    data = json.load(f)
            except Exception:
                data = {}
        data[username] = {
            "token": token,
            "updated_at": time.strftime("%Y-%m-%d %H:%M:%S")
        }
        with open(TOKENS_FILE, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)

def get_all_tokens():
    with tokens_lock:
        if os.path.exists(TOKENS_FILE):
            try:
                with open(TOKENS_FILE, "r", encoding="utf-8") as f:
                    raw = json.load(f)
                    return {k: v for k, v in raw.items() if isinstance(v, dict) and is_valid_jwt(v.get("token"))}
            except Exception:
                return {}
        return {}

def redeem_single_account(username: str, token: str, event_id: str):
    if not is_valid_jwt(token):
        return {"username": username, "status": "failed", "time_ms": 0, "message": "Invalid JWT"}
    t0 = time.time()
    url = f"https://core-api.thehof.gg/me/events/{event_id}/redeem"
    headers = {
        "Authorization": f"Bearer {token}",
        "Origin": "https://member.thehof.gg",
        "Referer": "https://member.thehof.gg/",
        "Accept": "application/json, text/plain, */*",
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36"
    }
    
    try:
        if HAS_CURL_CFFI:
            r = tls_requests.post(url, headers=headers, timeout=10, impersonate="chrome120")
        else:
            r = tls_requests.post(url, headers=headers, timeout=10)
        elapsed_ms = int((time.time() - t0) * 1000)
        
        if r.status_code == 200:
            res_json = r.json()
            items_list = []
            try:
                raw_items = res_json.get("data", {}).get("bundle_transaction", {}).get("items", [])
                for itm in raw_items:
                    item_name = itm.get("item", {}).get("name") or "Item"
                    qty = itm.get("quantity", 1)
                    items_list.append(f"{item_name} x{qty}")
            except Exception:
                pass
            
            items_str = ", ".join(items_list) if items_list else "รับไอเทมสำเร็จ"
            print(f"[Redeem] 🎉 [{username}] รับรางวัลสำเร็จ ({elapsed_ms}ms) ➔ {items_str}", flush=True)
            return {"username": username, "status": "success", "time_ms": elapsed_ms, "items": items_str}
        
        elif r.status_code in (400, 422):
            res_json = r.json() if r.text.startswith("{") else {}
            msg = res_json.get("message", r.text[:60])
            msg_lower = msg.lower()
            if "ไม่พบ" in msg or "already" in msg_lower or "เคยรับ" in msg or "can't found" in msg_lower or "not found" in msg_lower or "reward" in msg_lower:
                print(f"[Redeem] 🟡 [{username}] รับไปแล้วก่อนหน้า ({elapsed_ms}ms)", flush=True)
                return {"username": username, "status": "already_claimed", "time_ms": elapsed_ms, "message": "รับรางวัลไปแล้ว"}
            else:
                print(f"[Redeem] ⚠️ [{username}] {msg} ({elapsed_ms}ms)", flush=True)
                return {"username": username, "status": "failed", "time_ms": elapsed_ms, "message": msg}
        else:
            print(f"[Redeem] ❌ [{username}] HTTP {r.status_code} ({elapsed_ms}ms)", flush=True)
            return {"username": username, "status": "error", "time_ms": elapsed_ms, "message": f"HTTP {r.status_code}"}
            
    except Exception as ex:
        elapsed_ms = int((time.time() - t0) * 1000)
        print(f"[Redeem] 💥 [{username}] Error: {ex}", flush=True)
        return {"username": username, "status": "error", "time_ms": elapsed_ms, "message": str(ex)}

# ─── FastAPI Application Lifecycle ───
@asynccontextmanager
async def lifespan(app: FastAPI):
    yield
    close_all_worker_browsers()

app = FastAPI(title="HOF FastLogin Extension Sync Server", lifespan=lifespan)

# Custom CORS and Private Network Access middleware to guarantee 100% preflight success
@app.middleware("http")
async def cors_and_pna_middleware(request: Request, call_next):
    origin = request.headers.get("origin") or "*"
    if request.method == "OPTIONS":
        return Response(
            status_code=200,
            headers={
                "Access-Control-Allow-Origin": origin,
                "Access-Control-Allow-Methods": "GET, POST, OPTIONS, PUT, DELETE",
                "Access-Control-Allow-Headers": "*",
                "Access-Control-Allow-Credentials": "true",
                "Access-Control-Allow-Private-Network": "true",
                "Access-Control-Max-Age": "86400",
            }
        )
    response = await call_next(request)
    response.headers["Access-Control-Allow-Origin"] = origin
    response.headers["Access-Control-Allow-Credentials"] = "true"
    response.headers["Access-Control-Allow-Private-Network"] = "true"
    return response

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
    allow_private_network=True,
)

@app.options("/{full_path:path}")
def handle_options_route(full_path: str, request: Request):
    origin = request.headers.get("origin") or "*"
    return Response(
        status_code=200,
        headers={
            "Access-Control-Allow-Origin": origin,
            "Access-Control-Allow-Methods": "GET, POST, OPTIONS, PUT, DELETE",
            "Access-Control-Allow-Headers": "*",
            "Access-Control-Allow-Credentials": "true",
            "Access-Control-Allow-Private-Network": "true",
            "Access-Control-Max-Age": "86400",
        }
    )

# ─── Extension Communication Endpoints ───
@app.get("/worker_init", response_class=HTMLResponse)
def worker_init_page(wid: int = 1):
    return f"""
    <!DOCTYPE html>
    <html>
    <head>
        <meta charset="utf-8">
        <title>Worker {wid} Initializing...</title>
        <style>
            body {{ font-family: sans-serif; background: #12131a; color: #fff; display: flex; align-items: center; justify-content: center; height: 90vh; text-align: center; }}
            .card {{ background: #1b1c26; padding: 24px; border-radius: 12px; border: 1px solid #2e3044; }}
            .spinner {{ width: 32px; height: 32px; border: 3px solid rgba(255,255,255,0.2); border-top-color: #6366f1; border-radius: 50%; animation: spin 0.8s linear infinite; margin: 0 auto 16px; }}
            @keyframes spin {{ to {{ transform: rotate(360deg); }} }}
        </style>
        <script>
            setTimeout(function() {{
                window.location.href = "https://passport.thehof.gg/hall-of-fame-web/login#wid=" + {wid};
            }}, 800);
        </script>
    </head>
    <body>
        <div class="card">
            <div class="spinner"></div>
            <h3>FastLogin Extension Sync Engine</h3>
            <p>Worker #{wid} กำลังเตรียมความพร้อมเข้าสู่ระบบ...</p>
        </div>
    </body>
    </html>
    """

@app.get("/worker/{wid}/task")
def get_worker_task(wid: int):
    with worker_tasks_lock:
        task = worker_tasks.get(wid)
        return {"wid": wid, "task": task}

class WorkerDonePayload(BaseModel):
    status: str
    worker_id: int
    username: str
    token: str = ""
    reason: str = ""
    time: str = "0s"
    redeem_status: str = ""
    items: str = ""
    detail: str = ""
    is_limit: bool = False

@app.post("/worker/{wid}/done")
def post_worker_done(wid: int, payload: WorkerDonePayload):
    with worker_tasks_lock:
        worker_results[wid] = payload.model_dump()
        worker_tasks[wid] = None
        evt = worker_events.get(wid)
        if evt:
            evt.set()
    return {"status": "ok"}

last_submit_time = 0.0
submit_lock = threading.Lock()

@app.get("/worker/{wid}/acquire_submit")
def api_acquire_submit(wid: int):
    global last_submit_time
    with submit_lock:
        now = time.time()
        diff = now - last_submit_time
        if diff < 1.5:
            time.sleep(1.5 - diff)
        last_submit_time = time.time()
        print(f"🚦 [Worker-{wid}] ได้รับสิทธิ์กดเข้าสู่ระบบ (Pacing Submit 1.5s ป้องกัน 429/1015)", flush=True)
        return {"status": "ok", "wid": wid}

@app.get("/worker/{wid}/status")
def get_worker_status(wid: int):
    proc = worker_processes.get(wid)
    alive = bool(proc and proc.poll() is None)
    with worker_tasks_lock:
        has_task = bool(worker_tasks.get(wid))
    return {"wid": wid, "alive": alive, "has_task": has_task}

# ─── Bot & UI Endpoints ───
class PrepareBatchRequest(BaseModel):
    count: int
    batch_num: int = 1
    total_batches: int = 1

@app.post("/batch/prepare")
def api_prepare_batch(req: PrepareBatchRequest):
    print(f"\n📦 [รอบที่ {req.batch_num}/{req.total_batches}] ⚡ กำลังเปิดและเตรียม Chrome Workers ({req.count} จอ)...", flush=True)
    limit = min(req.count, threads_limit)
    for i in range(1, limit + 1):
        launch_chrome_worker(i)
        if i < limit:
            time.sleep(0.8)  # เว้น 0.8s ระหว่างจอ ป้องกัน Cloudflare 1015 จาก burst page load
    
    # พักให้ Turnstile ด่านแรกในแต่ละจอผ่านอัตโนมัติ
    time.sleep(2.0)
    return {"status": "ready", "ready_count": req.count, "requested_count": req.count}

@app.post("/batch/close")
def api_close_batch():
    close_all_worker_browsers()
    return {"status": "closed"}

@app.get("/health")
def health():
    tokens = get_all_tokens()
    return {
        "status": "ok",
        "engine": "chrome_extension_sync_v1",
        "profiles_dir": PROFILES_DIR,
        "extension_dir": EXTENSION_DIR,
        "tokens_count": len(tokens),
        "threads_limit": threads_limit
    }

@app.get("/tokens")
def list_tokens():
    tokens = get_all_tokens()
    return {"status": "ok", "total_tokens": len(tokens), "accounts": list(tokens.keys())}

class LoginRequest(BaseModel):
    url: str = TARGET_URL
    username: str
    password: str
    event_id: str = DEFAULT_EVENT_ID
    worker_id: int = 0

@app.post("/login")
def do_login(req: LoginRequest):
    global request_counter
    with counter_lock:
        request_counter += 1
        req_id = request_counter

    t_start = time.time()

    # ────────── ด่านที่ 0: ตรวจสอบ Token ใน tokens.json ก่อน (Instant Cache Bypass 0s) ──────────
    tokens = get_all_tokens()
    existing_entry = tokens.get(req.username)
    if existing_entry and isinstance(existing_entry, dict) and "token" in existing_entry:
        token = existing_entry["token"]
        redeem_res = redeem_single_account(req.username, token, req.event_id)
        if redeem_res.get("status") in ("success", "already_claimed") or (redeem_res.get("status") == "failed" and "token" not in redeem_res.get("message", "").lower() and "unauthorized" not in redeem_res.get("message", "").lower()):
            elapsed_str = f"{time.time() - t_start:.2f}s"
            detail_msg = "เข้าสู่ระบบสำเร็จ"
            if redeem_res.get("status") == "success":
                detail_msg = f"🎁 รับรางวัลสำเร็จ: {redeem_res.get('items')}"
            elif redeem_res.get("status") == "already_claimed":
                detail_msg = "🟡 รับรางวัลไปแล้วก่อนหน้า"
            else:
                detail_msg = f"⚠️ รับรางวัล: {redeem_res.get('message', 'ไม่สำเร็จ')}"

            print(f"[Req-{req_id}] ⚡ [{req.username}] พบ Token เดิมในแคช! ยิงตรงผ่าน API สำเร็จทันที {detail_msg} ({elapsed_str}) [0s Browser]", flush=True)
            return {
                "status": "success",
                "username": req.username,
                "time": elapsed_str,
                "cached": True,
                "token_saved": True,
                "redeem_status": redeem_res.get("status"),
                "items": redeem_res.get("items", ""),
                "detail": detail_msg,
                "is_limit": False
            }

    # ────────── ด่านที่ 1: ส่งงานให้ Chrome Worker Extension จัดการ ──────────
    pool_wid = None
    with login_semaphore:
        if req.worker_id > 0:
            assigned_wid = req.worker_id
        else:
            try:
                pool_wid = worker_pool.get_nowait()
                assigned_wid = pool_wid
            except Exception:
                assigned_wid = (req_id % threads_limit) + 1

        try:
            print(f"[Req-{req_id}] ⏳ บัญชี '{req.username}' [Worker-{assigned_wid}] เตรียมพร้อมส่งงานให้ Chrome Extension...", flush=True)
            
            # ตรวจสอบและเปิด Chrome Worker หากยังไม่ได้เปิด
            launch_chrome_worker(assigned_wid)

            # กำหนด Event และ Task
            evt = threading.Event()
            with worker_tasks_lock:
                worker_results[assigned_wid] = None
                worker_events[assigned_wid] = evt
                worker_tasks[assigned_wid] = {
                    "id": req_id,
                    "username": req.username,
                    "password": req.password
                }

            print(f"[Req-{req_id}] 🚀 มอบหมายงานให้ Worker-{assigned_wid} แล้ว กำลังรอ Extension ดำเนินการและข้าม Turnstile...", flush=True)

            # รอ Extension ทำงานเสร็จ สูงสุด 35 วินาที
            completed = evt.wait(timeout=35.0)

            elapsed_str = f"{time.time() - t_start:.1f}s"

            if not completed:
                print(f"[Req-{req_id}] ⚠️ Worker-{assigned_wid} หมดเวลาตอบกลับ (Timeout 35s)", flush=True)
                with worker_tasks_lock:
                    worker_tasks[assigned_wid] = None
                return {
                    "status": "failed",
                    "username": req.username,
                    "time": elapsed_str,
                    "reason": "หมดเวลาการทำงาน (Timeout 35s)",
                    "detail": "หมดเวลาการทำงาน (Timeout 35s)",
                    "is_limit": False
                }

            with worker_tasks_lock:
                res_data = worker_results.get(assigned_wid) or {}

            status = res_data.get("status", "failed")
            token = res_data.get("token", "")
            fail_reason = res_data.get("reason", "เข้าสู่ระบบไม่สำเร็จ")
            is_limit = (
                res_data.get("is_limit", False) or
                "1015" in fail_reason or 
                "limit" in fail_reason.lower() or 
                "rate" in fail_reason.lower() or
                "too many" in fail_reason.lower() or
                "429" in fail_reason
            )

            if is_limit:
                print(f"🛑 [Req-{req_id}] บัญชี '{req.username}' [Worker-{assigned_wid}] ติด Limit IP (429 / 1015) — รอ 30s แล้วรีโหลดหน้าใหม่ (ไม่ปิด Chrome)", flush=True)
                # รอ 30s ให้ Cloudflare cooldown ก่อน — ไม่ต้องปิด Chrome เพราะการเปิดใหม่ทันทียิ่งชนซ้ำ
                time.sleep(30)

            if status == "success":
                if token:
                    save_account_token(req.username, token)

                ext_redeem_status = res_data.get("redeem_status")
                ext_items = res_data.get("items", "")
                ext_detail = res_data.get("detail", "")

                items_str = ""
                detail_msg = "เข้าสู่ระบบสำเร็จ"

                if ext_redeem_status in ("success", "already_claimed") and ext_detail:
                    # Extension redeem สำเร็จหรือรับไปแล้ว — ใช้ผลจาก Extension
                    redeem_status = ext_redeem_status
                    items_str = ext_items
                    detail_msg = ext_detail
                elif ext_redeem_status == "failed" and ext_detail and token:
                    # Extension redeem fail แต่มี token — server retry เอง
                    print(f"[Req-{req_id}] 🔄 [{req.username}] Extension redeem ล้มเหลว กำลัง retry ผ่าน server...", flush=True)
                    time.sleep(0.5)
                    redeem_res = redeem_single_account(req.username, token, req.event_id)
                    redeem_status = redeem_res.get("status")
                    if redeem_status == "success":
                        items_str = redeem_res.get("items", "")
                        detail_msg = f"🎁 รับรางวัลสำเร็จ (retry): {items_str}"
                    elif redeem_status == "already_claimed":
                        detail_msg = "🟡 รับรางวัลไปแล้วก่อนหน้า"
                    else:
                        detail_msg = f"⚠️ รับรางวัล: {redeem_res.get('message', 'ไม่สำเร็จ')}"
                elif token:
                    # Extension ไม่ได้ redeem เลย (unknown) — server redeem เอง
                    print(f"[Req-{req_id}] 🔄 [{req.username}] ไม่มีข้อมูล redeem จาก Extension กำลัง redeem ผ่าน server...", flush=True)
                    redeem_res = redeem_single_account(req.username, token, req.event_id)
                    redeem_status = redeem_res.get("status")
                    if redeem_status == "success":
                        items_str = redeem_res.get("items", "")
                        detail_msg = f"🎁 รับรางวัลสำเร็จ: {items_str}"
                    elif redeem_status == "already_claimed":
                        detail_msg = "🟡 รับรางวัลไปแล้วก่อนหน้า"
                    else:
                        detail_msg = f"⚠️ รับรางวัล: {redeem_res.get('message', 'ไม่สำเร็จ')}"
                else:
                    redeem_status = "success"
                    detail_msg = "เข้าสู่ระบบสำเร็จ (ไม่มี Token)"

                print(f"[Req-{req_id}] 🎉 [{req.username}] {detail_msg} ({elapsed_str})", flush=True)
                return {
                    "status": "success",
                    "username": req.username,
                    "time": elapsed_str,
                    "token_saved": bool(token),
                    "redeem_status": redeem_status,
                    "items": items_str,
                    "detail": detail_msg,
                    "is_limit": False
                }
            else:
                print(f"[Req-{req_id}] ❌ บัญชี '{req.username}' {fail_reason} ({elapsed_str})", flush=True)
                return {
                    "status": "failed",
                    "username": req.username,
                    "time": elapsed_str,
                    "reason": fail_reason,
                    "detail": fail_reason,
                    "is_limit": is_limit
                }

        except Exception as e:
            print(f"[Req-{req_id}] ❌ Error: {e}", flush=True)
            return {"status": "error", "username": req.username, "message": str(e)}

        finally:
            if pool_wid is not None:
                try:
                    worker_pool.put(pool_wid)
                except Exception:
                    pass

class DirectRedeemRequest(BaseModel):
    event_id: str = DEFAULT_EVENT_ID
    threads: int = 10

@app.post("/redeem-all")
def api_redeem_all(req: DirectRedeemRequest):
    tokens = get_all_tokens()
    if not tokens:
        return {"status": "empty", "message": "ยังไม่มี Token ในระบบ กรุณารันล็อกอินเพื่อสะสม Token ก่อน", "results": []}
    
    total = len(tokens)
    print(f"\n🚀 [Ultra Fast Redeem] กำลังยิงรับรางวัลกิจกรรม '{req.event_id}' พร้อมกัน {total} บัญชี...", flush=True)
    t_start = time.time()
    
    results = []
    with ThreadPoolExecutor(max_workers=min(req.threads, 50)) as executor:
        futures = [
            executor.submit(redeem_single_account, user, data["token"], req.event_id)
            for user, data in tokens.items()
        ]
        for f in futures:
            results.append(f.result())
            
    total_elapsed = f"{time.time() - t_start:.2f}s"
    success_count = sum(1 for r in results if r["status"] == "success")
    already_count = sum(1 for r in results if r["status"] == "already_claimed")
    fail_count = sum(1 for r in results if r["status"] in ("failed", "error"))
    
    print(f"\n📊 สรุปผลการรับรางวัล ({total_elapsed}): สำเร็จ {success_count} | รับไปแล้ว {already_count} | ผิดพลาด {fail_count}\n", flush=True)
    return {
        "status": "completed",
        "total": total,
        "success": success_count,
        "already_claimed": already_count,
        "failed": fail_count,
        "time": total_elapsed,
        "results": results
    }

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--threads", type=int, default=5, help="จำนวน Worker Chrome แยกอิสระ")
    parser.add_argument("--port", type=int, default=5000, help="พอร์ต HTTP Server")
    args, _ = parser.parse_known_args()

    threads_limit = args.threads
    login_semaphore = threading.Semaphore(threads_limit)

    # Clean up any stale bot Chromes on startup to prevent duplicate tabs
    close_stale_bot_chromes()

    print(f"🚀 เริ่มรัน Local API Server (Chrome Extension Sync Engine) ที่ http://127.0.0.1:{args.port} (Threads: {threads_limit})")
    uvicorn.run(app, host="127.0.0.1", port=args.port)
