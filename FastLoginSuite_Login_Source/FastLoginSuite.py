import os
import sys
import time
import queue
import threading
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

try:
    import windnd
    HAS_WINDND = True
except ImportError:
    HAS_WINDND = False

from DrissionPage import ChromiumOptions, ChromiumPage

BG_DARK = "#10121B"
PANEL_DARK = "#181B28"
CARD_DARK = "#202436"
CARD_BORDER = "#2A2E45"
ACCENT = "#6C63FF"
ACCENT_HOVER = "#8279FF"
SUCCESS = "#3CD77D"
DANGER = "#F05050"
TEXT_MAIN = "#F0F0FA"
TEXT_DIM = "#878CA5"

LOGIN_TARGET_URL = "https://passport.thehof.gg/hall-of-fame-web/login"

class FastLoginApp:
    def __init__(self, root):
        self.root = root
        self.root.title("⚡ HOF Fast Login Suite — Ultra Multi-Tab Engine v1.0")
        self.root.geometry("1020x640")
        self.root.minsize(920, 540)
        self.root.configure(bg=BG_DARK)

        self.accounts = []
        self.is_running = False
        self.stop_requested = False

        self.setup_ui()

        if HAS_WINDND:
            windnd.hook_dropfiles(self.root, func=self.on_files_dropped)

    def setup_ui(self):
        panel_top = tk.Frame(self.root, bg=PANEL_DARK, height=70, bd=0)
        panel_top.pack(fill=tk.X, side=tk.TOP)

        lbl_title = tk.Label(panel_top, text="⚡ HOF Fast Login Suite", font=("Segoe UI", 16, "bold"), fg=TEXT_MAIN, bg=PANEL_DARK)
        lbl_title.pack(side=tk.LEFT, padx=20, pady=(12, 0))

        lbl_sub = tk.Label(panel_top, text="Ultra-Light Multi-Tab Engine • Drag & Drop File Ready", font=("Segoe UI", 8), fg=TEXT_DIM, bg=PANEL_DARK)
        lbl_sub.place(x=22, y=42)

        self.btn_stop = tk.Button(panel_top, text="■ Stop", font=("Segoe UI", 9, "bold"), fg="#FFFFFF", bg=DANGER, activebackground="#FF6B6B", bd=0, padx=15, pady=6, command=self.stop_process, state=tk.DISABLED)
        self.btn_stop.pack(side=tk.RIGHT, padx=(0, 20), pady=16)

        self.btn_start = tk.Button(panel_top, text="▶ Start Engine", font=("Segoe UI", 9, "bold"), fg="#FFFFFF", bg=ACCENT, activebackground=ACCENT_HOVER, bd=0, padx=15, pady=6, command=self.start_process)
        self.btn_start.pack(side=tk.RIGHT, padx=10, pady=16)

        panel_cfg = tk.Frame(self.root, bg=BG_DARK, height=50)
        panel_cfg.pack(fill=tk.X, side=tk.TOP, padx=20, pady=10)

        btn_load = tk.Button(panel_cfg, text="📁 เลือกไฟล์ไอดี (.txt)", font=("Segoe UI", 9), fg=TEXT_MAIN, bg=CARD_DARK, activebackground=CARD_BORDER, bd=1, relief="solid", command=self.browse_file)
        btn_load.pack(side=tk.LEFT, padx=(0, 10))

        btn_clear = tk.Button(panel_cfg, text="🗑️ ล้างรายการ", font=("Segoe UI", 9), fg=TEXT_MAIN, bg=CARD_DARK, activebackground=CARD_BORDER, bd=1, relief="solid", command=self.clear_accounts)
        btn_clear.pack(side=tk.LEFT, padx=5)

        lbl_threads = tk.Label(panel_cfg, text="จำนวนแท็บพร้อมกัน:", font=("Segoe UI", 9), fg=TEXT_MAIN, bg=BG_DARK)
        lbl_threads.pack(side=tk.LEFT, padx=(25, 5))

        self.spn_threads = tk.Spinbox(panel_cfg, from_=1, to=50, width=5, font=("Segoe UI", 10, "bold"), bg=PANEL_DARK, fg=TEXT_MAIN, bd=1, buttonbackground=CARD_DARK)
        self.spn_threads.delete(0, "end")
        self.spn_threads.insert(0, "10")
        self.spn_threads.pack(side=tk.LEFT)

        self.lbl_stats = tk.Label(panel_cfg, text="ทั้งหมด: 0 | สำเร็จ: 0 | ล้มเหลว: 0", font=("Segoe UI", 9, "bold"), fg=TEXT_DIM, bg=BG_DARK)
        self.lbl_stats.pack(side=tk.RIGHT, padx=10)

        self.lbl_dropzone = tk.Label(self.root, text="💡 ลากไฟล์ accounts.txt มาวางในหน้านี้ได้เลย (รองรับรูปแบบ user|pass หรือ user:pass)", font=("Segoe UI", 9), fg=TEXT_DIM, bg=CARD_DARK, bd=1, relief="solid", height=2)
        self.lbl_dropzone.pack(fill=tk.X, padx=20, pady=(0, 10))

        style = ttk.Style()
        style.theme_use("default")
        style.configure("Treeview", background=PANEL_DARK, foreground=TEXT_MAIN, fieldbackground=PANEL_DARK, rowheight=32, font=("Segoe UI", 9.5))
        style.configure("Treeview.Heading", background=CARD_DARK, foreground=TEXT_DIM, font=("Segoe UI Semibold", 9.5, "bold"), borderwidth=0)
        style.map("Treeview", background=[("selected", ACCENT)])

        columns = ("#", "Username", "Password", "Status", "Time", "Log")
        self.tree = ttk.Treeview(self.root, columns=columns, show="headings", selectmode="browse")
        
        self.tree.heading("#", text="#")
        self.tree.heading("Username", text="Username / ID")
        self.tree.heading("Password", text="Password")
        self.tree.heading("Status", text="Status")
        self.tree.heading("Time", text="Time")
        self.tree.heading("Log", text="Log / Result")

        self.tree.column("#", width=50, anchor="center")
        self.tree.column("Username", width=220, anchor="w")
        self.tree.column("Password", width=160, anchor="w")
        self.tree.column("Status", width=120, anchor="center")
        self.tree.column("Time", width=90, anchor="center")
        self.tree.column("Log", width=340, anchor="w")

        scrollbar = ttk.Scrollbar(self.root, orient=tk.VERTICAL, command=self.tree.yview)
        self.tree.configure(yscroll=scrollbar.set)
        
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y, padx=(0, 20), pady=(0, 20))
        self.tree.pack(fill=tk.BOTH, expand=True, padx=(20, 0), pady=(0, 20))

    def on_files_dropped(self, files):
        for f in files:
            path = f.decode("utf-8") if isinstance(f, bytes) else f
            if path.endswith(".txt") and os.path.exists(path):
                self.load_accounts_from_file(path)
                break

    def browse_file(self):
        file_path = filedialog.askopenfilename(filetypes=[("Text Files", "*.txt"), ("All Files", "*.*")])
        if file_path:
            self.load_accounts_from_file(file_path)

    def parse_account_line(self, line):
        import re
        line = line.strip()
        if not line or line.startswith("#") or line.startswith("//"):
            return None, None

        # 1. Labeled format: ID: x | PASS: y
        m = re.search(r'(?i)ID\s*:\s*(\S+)\s*\|\s*PASS\s*:\s*(\S+)', line)
        if m:
            return m.group(1).strip(), m.group(2).strip()

        # 2. Delimited format: |, :, ,, \t
        for sep in ['|', ':', ',', '\t']:
            if sep in line:
                parts = line.split(sep, 1)
                if len(parts) == 2:
                    u = parts[0].strip()
                    p = parts[1].strip()
                    if u and p:
                        return u, p

        # 3. Space delimited
        if ' ' in line:
            parts = line.split(' ', 1)
            if len(parts) == 2:
                u = parts[0].strip()
                p = parts[1].strip()
                if u and p:
                    return u, p

        # 4. Single string format: ID and Password are the same!
        return line, line

    def load_accounts_from_file(self, file_path):
        try:
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                lines = f.readlines()

            count = 0
            for line in lines:
                user, pas = self.parse_account_line(line)
                if user and pas:
                    self.accounts.append({"user": user, "pass": pas, "status": "Waiting", "time": "-", "log": "Queued"})
                    count += 1

            self.refresh_table()
            messagebox.showinfo("สำเร็จ", f"โหลดไอดีจากไฟล์สำเร็จทั้งหมด {count} บัญชี!")
        except Exception as ex:
            messagebox.showerror("Error", f"อ่านไฟล์ไม่สำเร็จ: {ex}")

    def clear_accounts(self):
        if self.is_running:
            return
        self.accounts.clear()
        self.refresh_table()

    def refresh_table(self):
        for item in self.tree.get_children():
            self.tree.delete(item)

        total = len(self.accounts)
        done = 0
        failed = 0

        for i, acc in enumerate(self.accounts, 1):
            self.tree.insert("", "end", iid=str(i-1), values=(i, acc["user"], "••••••••", acc["status"], acc["time"], acc["log"]))
            if acc["status"] == "Success":
                done += 1
            elif acc["status"] == "Failed":
                failed += 1

        self.lbl_stats.config(text=f"ทั้งหมด: {total} | สำเร็จ: {done} | ล้มเหลว: {failed}")

    def update_row(self, index, status, log_msg, elapsed="-"):
        if index < len(self.accounts):
            self.accounts[index]["status"] = status
            self.accounts[index]["log"] = log_msg
            self.accounts[index]["time"] = elapsed
            
            if self.tree.exists(str(index)):
                self.tree.item(str(index), values=(index + 1, self.accounts[index]["user"], "••••••••", status, elapsed, log_msg))
            
            self.refresh_stats_only()

    def refresh_stats_only(self):
        total = len(self.accounts)
        done = sum(1 for a in self.accounts if a["status"] == "Success")
        failed = sum(1 for a in self.accounts if a["status"] == "Failed")
        self.lbl_stats.config(text=f"ทั้งหมด: {total} | สำเร็จ: {done} | ล้มเหลว: {failed}")

    def start_process(self):
        if self.is_running or not self.accounts:
            if not self.accounts:
                messagebox.showwarning("คำเตือน", "กรุณาลากไฟล์หรือเลือกไฟล์ accounts.txt ก่อนเริ่มรัน!")
            return

        try:
            threads_count = int(self.spn_threads.get())
        except ValueError:
            threads_count = 10

        self.is_running = True
        self.stop_requested = False
        self.btn_start.config(state=tk.DISABLED, bg=CARD_DARK)
        self.btn_stop.config(state=tk.NORMAL)

        threading.Thread(target=self.run_engine, args=(threads_count,), daemon=True).start()

    def stop_process(self):
        self.stop_requested = True
        self.btn_stop.config(state=tk.DISABLED)

    def run_engine(self, max_tabs):
        try:
            co = ChromiumOptions()
            co.auto_port()
            co.set_user("fast_login_profile")
            # Images enabled for Cloudflare Turnstile rendering
            co.set_argument('--js-flags=--max-old-space-size=128')
            co.set_argument('--mute-audio')
            co.set_argument('--disable-gpu')
            co.set_argument('--no-sandbox')
            co.set_argument('--disable-blink-features=AutomationControlled')
            co.set_argument('--window-size=500,650')
            # Background execution optimization (prevents tabs from sleeping)
            co.set_argument('--disable-background-timer-throttling')
            co.set_argument('--disable-backgrounding-occluded-windows')
            co.set_argument('--disable-renderer-backgrounding')

            browser = ChromiumPage(co)
            
            task_queue = queue.Queue()
            for idx, acc in enumerate(self.accounts):
                if acc["status"] != "Success":
                    task_queue.put((idx, acc))

            num_workers = min(max_tabs, len(self.accounts))
            
            # Pre-create all tabs immediately with Isolated Contexts
            tabs = [browser.get_tab()]
            for _ in range(1, num_workers):
                tabs.append(browser.new_tab(new_context=True))

            def worker_thread(tab, worker_id):
                while not task_queue.empty() and not self.stop_requested:
                    try:
                        idx, acc = task_queue.get_nowait()
                    except queue.Empty:
                        break

                    user = acc["user"]
                    pas = acc["pass"]
                    t0 = time.time()

                    self.root.after(0, self.update_row, idx, "Running", "กำลังเปิดหน้าล็อกอิน...", "-")

                    try:
                        tab.get(LOGIN_TARGET_URL, timeout=15)

                        # Check & Solve Turnstile Captcha if present
                        clicked_cf = False
                        for step in range(30):
                            try:
                                u_ele = tab.ele('#hofId', timeout=0.1) or tab.ele('#username', timeout=0.1) or tab.ele('@name=username', timeout=0.1)
                                if u_ele and u_ele.is_displayed():
                                    break
                            except Exception:
                                pass

                            try:
                                cf_input = tab.ele('@name=cf-turnstile-response', timeout=0.1)
                                if cf_input and cf_input.value:
                                    time.sleep(0.4)
                                    break
                            except Exception:
                                pass

                            if not clicked_cf and step >= 2:
                                try:
                                    iframe = tab.ele('tag:iframe@src*cloudflare', timeout=0.2)
                                    if iframe:
                                        tab.actions.move_to(iframe).click()
                                        clicked_cf = True
                                    else:
                                        tab.actions.move_to((250, 300)).click()
                                        clicked_cf = True
                                except Exception:
                                    pass
                            elif clicked_cf and step % 10 == 0:
                                try:
                                    iframe = tab.ele('tag:iframe@src*cloudflare', timeout=0.1)
                                    if iframe:
                                        tab.actions.move_to(iframe).click()
                                except Exception:
                                    pass

                            time.sleep(0.25)
                        
                        js_login = f"""
                        (function() {{
                            let u = document.querySelector('#hofId') || document.querySelector('#username') || document.querySelector('input[name="username"]');
                            let p = document.querySelector('#password') || document.querySelector('input[name="password"]');
                            let btn = document.querySelector('.button-submit') || document.querySelector('button[type="submit"]');
                            
                            if(u) {{ u.value = "{user}"; u.dispatchEvent(new Event('input', {{ bubbles: true }})); }}
                            if(p) {{ p.value = "{pas}"; p.dispatchEvent(new Event('input', {{ bubbles: true }})); }}
                            if(btn) {{ btn.click(); }}
                        }})();
                        """
                        tab.run_js(js_login)
                        
                        time.sleep(1.5)
                        elapsed_str = f"{time.time() - t0:.1f}s"

                        curr_url = tab.url
                        if "login" not in curr_url or "dashboard" in curr_url or "member" in curr_url:
                            self.root.after(0, self.update_row, idx, "Success", "✅ เข้าสู่ระบบสำเร็จ!", elapsed_str)
                        else:
                            self.root.after(0, self.update_row, idx, "Failed", "❌ รหัสผ่านไม่ถูกต้อง หรือติดยืนยันตัวตน", elapsed_str)

                    except Exception as err:
                        elapsed_str = f"{time.time() - t0:.1f}s"
                        self.root.after(0, self.update_row, idx, "Failed", f"Error: {err}", elapsed_str)
                    finally:
                        task_queue.task_done()

            active_threads = []
            for w_idx, tab in enumerate(tabs):
                t = threading.Thread(target=worker_thread, args=(tab, w_idx + 1))
                active_threads.append(t)
                t.start()

            for t in active_threads:
                t.join()

            try:
                browser.quit()
            except Exception:
                pass

        except Exception as g_err:
            messagebox.showerror("Error", f"เกิดข้อผิดพลาดในระบบ Engine: {g_err}")
        finally:
            self.root.after(0, self.finish_process)

    def finish_process(self):
        self.is_running = False
        self.btn_start.config(state=tk.NORMAL, bg=ACCENT)
        self.btn_stop.config(state=tk.DISABLED, bg=DANGER)
        messagebox.showinfo("แจ้งเตือน", "ทำงานเสร็จสิ้นครบทุกรายการแล้ว!")

if __name__ == "__main__":
    root = tk.Tk()
    app = FastLoginApp(root)
    root.mainloop()
