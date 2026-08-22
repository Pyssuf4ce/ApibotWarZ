from DrissionPage import ChromiumOptions, ChromiumPage
import time

print("🚀 เริ่มระบบทดสอบการคลิก...")

# ตั้งค่าเบราว์เซอร์ให้เหมือนกับบอทตัวจริง
co = ChromiumOptions()
co.auto_port()
co.set_argument('--window-size=1024,1000')
co.set_argument('--disable-blink-features=AutomationControlled')

# เปิดเบราว์เซอร์
page = ChromiumPage(co)
print("🌍 กำลังเปิดเว็บ...")
page.get("https://member.thehof.gg/register")

print("⏳ รอโหลดเว็บ 6 วินาที...")
time.sleep(6)

# 🎯 เปลี่ยนพิกัดที่ต้องการทดสอบตรงนี้ได้เลยครับ!
x = 386
y = 744-139

print(f"🖱️ กำลังมาร์คจุดสีแดงและคลิกที่ X: {x}, Y: {y}")

# รัน Javascript เพื่อสร้าง 'จุดสีแดง' บนหน้าจอ จะได้เห็นชัดๆ ว่ามันคลิกตรงไหน!
js_code = f"""
let dot = document.createElement('div');
dot.style.position = 'absolute';
dot.style.left = '{x}px';
dot.style.top = '{y}px';
dot.style.width = '12px';
dot.style.height = '12px';
dot.style.backgroundColor = 'red';
dot.style.borderRadius = '50%';
dot.style.zIndex = '999999';
dot.style.boxShadow = '0 0 5px black';
dot.style.transform = 'translate(-50%, -50%)'; // ให้อยู่กึ่งกลางพิกัดพอดี
dot.style.pointerEvents = 'none'; // ไม่ให้บังการคลิกจริงๆ
document.body.appendChild(dot);
"""
page.run_js(js_code)

# ส่งคำสั่งคลิก
try:
    page.actions.move_to((x, y)).click()
    print("\n✅ ส่งคำสั่งคลิกไปแล้ว! ลองดูที่หน้าจอว่าจุดสีแดงทับกล่อง Captcha พอดีไหม")
    print("💡 ถ้าไม่พอดี สามารถแก้พิกัด x, y ในโค้ดนี้ แล้วกดรันใหม่ได้เลยครับ")
except Exception as e:
    print(f"⚠️ เกิดข้อผิดพลาดในการคลิก: {e}")

# หยุดหน้าจอรอให้เราดูผล
input("\n🛑 กดปุ่ม Enter บนคีย์บอร์ดที่หน้าจอดำ (Terminal) เพื่อปิดหน้าต่างทดสอบ...")
page.quit()
