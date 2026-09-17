import sys
import os
import json
import time
import argparse
try:
    from curl_cffi import requests as tls_requests
    HAS_CURL_CFFI = True
except Exception:
    tls_requests = None
    HAS_CURL_CFFI = False

import requests as std_requests
from concurrent.futures import ThreadPoolExecutor

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

if getattr(sys, 'frozen', False):
    BASE_DIR = os.path.dirname(os.path.abspath(sys.executable))
else:
    BASE_DIR = os.path.dirname(os.path.abspath(__file__))

TOKENS_FILE = os.path.join(BASE_DIR, "tokens.json")
DEFAULT_EVENT_ID = "a297cbd7-c1c4-448f-9e9f-0f2a35d28ac3" # STAY ALIVE event

def load_tokens():
    if not os.path.exists(TOKENS_FILE):
        print(f"⚠️ ไม่พบไฟล์ {TOKENS_FILE}")
        return {}
    try:
        with open(TOKENS_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
            return {k: v for k, v in data.items() if isinstance(v, dict) and "token" in v}
    except Exception as e:
        print(f"❌ อ่านไฟล์ tokens.json ไม่สำเร็จ: {e}")
        return {}

def redeem_account(username: str, token: str, event_id: str):
    t0 = time.time()
    url = f"https://core-api.thehof.gg/me/events/{event_id}/redeem"
    headers = {
        "Authorization": f"Bearer {token}",
        "Origin": "https://member.thehof.gg",
        "Referer": "https://member.thehof.gg/",
        "Accept": "application/json, text/plain, */*",
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36"
    }
    
    max_attempts = 2
    last_err = ""
    for attempt in range(max_attempts):
        try:
            r = None
            if attempt == 0 and HAS_CURL_CFFI and tls_requests:
                try:
                    r = tls_requests.post(url, headers=headers, timeout=15, impersonate="chrome120")
                except Exception:
                    r = std_requests.post(url, headers=headers, timeout=15)
            else:
                r = std_requests.post(url, headers=headers, timeout=15)
            
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
                print(f"🎉 [{username}] ✅ รับรางวัลสำเร็จ ({elapsed_ms}ms) ➔ {items_str}")
                return {"username": username, "status": "success", "time_ms": elapsed_ms, "items": items_str}
            
            elif r.status_code in (400, 422):
                res_json = r.json() if r.text.startswith("{") else {}
                msg = res_json.get("message", r.text[:60])
                msg_lower = msg.lower()
                
                if ("something went wrong" in msg_lower or "try again" in msg_lower or "try agian" in msg_lower) and attempt < max_attempts - 1:
                    time.sleep(1.5)
                    continue
                
                if "ไม่พบ" in msg or "already" in msg_lower or "เคยรับ" in msg or "can't found" in msg_lower or "not found" in msg_lower or "reward" in msg_lower:
                    print(f"🟡 [{username}] รับไปแล้วก่อนหน้า ({elapsed_ms}ms)")
                    return {"username": username, "status": "already_claimed", "time_ms": elapsed_ms, "message": "รับรางวัลไปแล้ว"}
                else:
                    print(f"⚠️ [{username}] {msg} ({elapsed_ms}ms)")
                    return {"username": username, "status": "failed", "time_ms": elapsed_ms, "message": msg}
            elif r.status_code >= 500:
                if attempt < max_attempts - 1:
                    time.sleep(1.5)
                    continue
                print(f"❌ [{username}] HTTP {r.status_code} ({elapsed_ms}ms)")
                return {"username": username, "status": "error", "time_ms": elapsed_ms, "message": f"HTTP {r.status_code}"}
            else:
                print(f"❌ [{username}] HTTP {r.status_code} ({elapsed_ms}ms)")
                return {"username": username, "status": "error", "time_ms": elapsed_ms, "message": f"HTTP {r.status_code}"}
                
        except Exception as ex:
            last_err = str(ex)
            if attempt < max_attempts - 1:
                time.sleep(1.5)
                continue
    
    elapsed_ms = int((time.time() - t0) * 1000)
    print(f"💥 [{username}] Error: {last_err}")
    return {"username": username, "status": "error", "time_ms": elapsed_ms, "message": last_err or "Timeout / Network Error"}

def main():
    parser = argparse.ArgumentParser(description="HOF Ultra-Fast Direct Reward Claimer")
    parser.add_argument("--event", type=str, default=DEFAULT_EVENT_ID, help="ID ของกิจกรรมรับรางวัล (Event UUID)")
    parser.add_argument("--threads", type=int, default=20, help="จำนวนคำขอยิงพร้อมกัน")
    args = parser.parse_args()

    print("=" * 70)
    print("⚡ HOF Ultra-Fast Direct Reward Claimer (Zero-Latency API Engine)")
    print("=" * 70)

    tokens = load_tokens()
    if not tokens:
        print(f"⚠️ ไม่พบ Token ในไฟล์ '{TOKENS_FILE}'")
        print("👉 กรุณารัน FastLoginSuite เพื่อล็อกอินและเก็บ Token ของแต่ละบัญชีก่อนครับ\n")
        return

    total = len(tokens)
    print(f"📋 พบบัญชีที่มี Token พร้อมใช้งานทั้งหมด: {total} บัญชี")
    print(f"🎯 Event ID: {args.event}")
    print(f"⚡ Concurrency: {args.threads} Threads")
    print("-" * 70)
    print("🚀 เริ่มต้นยิงรับรางวัลพร้อมกันในเสี้ยววินาที...")
    print("-" * 70)

    t_start = time.time()
    results = []

    with ThreadPoolExecutor(max_workers=min(args.threads, total)) as executor:
        futures = [
            executor.submit(redeem_account, user, data["token"], args.event)
            for user, data in tokens.items()
        ]
        for f in futures:
            results.append(f.result())

    total_time = time.time() - t_start
    success_count = sum(1 for r in results if r["status"] == "success")
    already_count = sum(1 for r in results if r["status"] == "already_claimed")
    fail_count = sum(1 for r in results if r["status"] in ("failed", "error"))

    print("\n" + "=" * 70)
    print(f"📊 สรุปผลการรับรางวัลทั้งหมด ({total_time:.2f} วินาที):")
    print(f"  ✅ รับสำเร็จใหม่: {success_count} บัญชี")
    print(f"  🟡 รับไปแล้วก่อนหน้า: {already_count} บัญชี")
    print(f"  ❌ ไม่สำเร็จ: {fail_count} บัญชี")
    print(f"  📦 รวมทั้งหมด: {total} บัญชี")
    print("=" * 70 + "\n")

if __name__ == "__main__":
    main()
