package main

import (
	"bytes"
	"encoding/json"
	"fmt"

	"net/http"
	"time"

	fhttp "github.com/bogdanfinn/fhttp"
	tls_client "github.com/bogdanfinn/tls-client"
	"github.com/bogdanfinn/tls-client/profiles"
)

// ค่ากำหนดสำหรับการใช้งาน (ปรับแต่งตรงนี้ตามต้องการ)
const (
	CapSolverAPIKey = "YOUR_CAPSOLVER_API_KEY" // 🔑 ใส่ API Key ของ CapSolver ที่นี่
	SiteKey         = "0x4AAAAAAAYP8TXvr_dvr7T" // 🎯 SiteKey ที่เราเพิ่งแกะมา
	TargetURL       = "https://member.thehof.gg/register"
)

func main() {
	fmt.Println("🚀 เริ่มรันบอทอัตโนมัติ (Go + TLS-Client + CapSolver + Mail.tm)...")

	// 1. สร้าง HTTP Client แบบปลอม TLS Fingerprint (Chrome 120)
	jar := tls_client.NewCookieJar()
	options := []tls_client.HttpClientOption{
		tls_client.WithTimeoutSeconds(30),
		tls_client.WithClientProfile(profiles.Chrome_120),
		tls_client.WithCookieJar(jar),
	}
	client, err := tls_client.NewHttpClient(tls_client.NewNoopLogger(), options...)
	if err != nil {
		fmt.Printf("❌ สร้าง HTTP Client ไม่สำเร็จ: %v\n", err)
		return
	}

	// 2. สร้างอีเมลชั่วคราวจาก Mail.tm
	fmt.Println("📧 กำลังสร้าง Temp Mail...")
	email, password := createTempMail()
	if email == "" {
		fmt.Println("❌ สร้าง Temp Mail ไม่สำเร็จ ยกเลิกการทำงาน")
		return
	}
	fmt.Printf("✨ ได้อีเมลสำหรับสมัคร: %s\n", email)

	// 3. สั่งแก้ Cloudflare Turnstile ผ่าน CapSolver API เพื่อเอา Token
	fmt.Println("🧩 กำลังให้ Local API (DrissionPage) แก้ Captcha...")
	// ไม่ต้องใช้ SiteKey แล้ว ส่งแค่ TargetURL ให้ Browser เข้าไปจัดการได้เลย
	turnstileToken := solveTurnstileLocal(TargetURL) 

	if turnstileToken == "" {
		fmt.Println("❌ แก้ Captcha ไม่ผ่าน / ไม่ได้ Token ยกเลิกการทำงาน")
		return
	}
	fmt.Println("✅ ได้รับ Turnstile Token เรียบร้อย!")

	// 4. ส่งข้อมูลสมัครสมาชิก / ขอรับ OTP ไปยังเว็บไซต์เป้าหมาย
	fmt.Println("📤 กำลังส่งคำขอรับ OTP ไปยังเว็บไซต์...")
	success := sendOTPRequest(client, email, turnstileToken)
	if !success {
		fmt.Println("❌ ส่งคำขอรับ OTP ไม่สำเร็จ")
		return
	}

	// 5. วนลูปเช็คกล่องจดหมายเพื่อรอรับ OTP จาก Mail.tm
	fmt.Println("⏳ กำลังรอรับ OTP จากกล่องข้อความ...")
	fetchOTP(email, password)
}

// --- ฟังก์ชันสร้าง Temp Mail จาก Mail.tm ---
func createTempMail() (string, string) {
	resp, err := http.Get("https://api.mail.tm/domains")
	if err != nil {
		return "", ""
	}
	defer resp.Body.Close()

	var domainRes struct {
		HydraMember []struct {
			Domain string `json:"domain"`
		} `json:"hydra:member"`
	}
	json.NewDecoder(resp.Body).Decode(&domainRes)
	if len(domainRes.HydraMember) == 0 {
		return "", ""
	}

	domain := domainRes.HydraMember[0].Domain
	email := fmt.Sprintf("user_%d@%s", time.Now().UnixNano(), domain)
	password := "P@ssw0rd123!"

	payload, _ := json.Marshal(map[string]string{"address": email, "password": password})
	resp2, err := http.Post("https://api.mail.tm/accounts", "application/json", bytes.NewBuffer(payload))
	if err != nil || resp2.StatusCode != 201 {
		return "", ""
	}
	defer resp2.Body.Close()

	return email, password
}

// --- ฟังก์ชันแก้ Cloudflare Turnstile ผ่าน CapSolver ---
func solveTurnstileLocal(pageURL string) string {
    // 1. สร้าง Payload ส่งแค่ URL ไปให้ Python
    payloadMap := map[string]string{
        "url": pageURL,
    }
    body, _ := json.Marshal(payloadMap)

    // 2. ยิง POST ไปที่ Python Server ในเครื่องเราเอง (พอร์ต 5000)
    resp, err := http.Post("http://127.0.0.1:5000/get-token", "application/json", bytes.NewBuffer(body))
    if err != nil {
        fmt.Printf("❌ ไม่สามารถเชื่อมต่อกับ Local API ได้: %v\n(อย่าลืมเปิดรัน python server.py ทิ้งไว้)\n", err)
        return ""
    }
    defer resp.Body.Close()

    // 3. อ่านผลลัพธ์ (Token) ที่ Python ส่งกลับมา
    var result struct {
        Status  string `json:"status"`
        Token   string `json:"token"`
        Message string `json:"message"`
    }
    json.NewDecoder(resp.Body).Decode(&result)

    if result.Status == "success" {
        return result.Token
    }

    fmt.Printf("❌ โหลด Token ไม่สำเร็จ: %s\n", result.Message)
    return ""
}
// --- ฟังก์ชันยิงขอ OTP ไปที่เว็บเป้าหมาย ---
func sendOTPRequest(client tls_client.HttpClient, email string, turnstileToken string) bool {
	// ⚠️ หมายเหตุ: URL ของ API ส่ง OTP และโครงสร้าง JSON (Payload) อาจต้องเปลี่ยนตาม Endpoint จริงที่แกะได้จาก Network Tab
	apiEndpoint := "https://member.thehof.gg/api/send-otp"

	payloadMap := map[string]string{
		"email":                  email,
		"cf-turnstile-response":  turnstileToken,
	}
	payload, _ := json.Marshal(payloadMap)

	req, err := fhttp.NewRequest("POST", apiEndpoint, bytes.NewReader(payload))
	if err != nil {
		return false
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

	resp, err := client.Do(req)
	if err != nil {
		return false
	}
	defer resp.Body.Close()

	// ถ้า Status เป็น 200 หรือ 201 ถือว่าส่งคำขอสำเร็จ
	if resp.StatusCode == 200 || resp.StatusCode == 201 {
		fmt.Printf("✅ ส่ง OTP ไปยัง %s สำเร็จ!\n", email)
		return true
	}

	fmt.Printf("⚠️ ส่งคำขอไม่สำเร็จ Status Code: %d\n", resp.StatusCode)
	return false
}

// --- ฟังก์ชันเช็คกล่องจดหมายเพื่อดึงรหัส OTP ---
func fetchOTP(email, password string) {
	// ล็อกอินเพื่อเอา Token ของ Mail.tm
	authPayload, _ := json.Marshal(map[string]string{"address": email, "password": password})
	resp, err := http.Post("https://api.mail.tm/token", "application/json", bytes.NewBuffer(authPayload))
	if err != nil {
		return
	}
	defer resp.Body.Close()

	var authRes struct {
		Token string `json:"token"`
	}
	json.NewDecoder(resp.Body).Decode(&authRes)
	if authRes.Token == "" {
		return
	}

	// วนลูปเช็คอีเมลเข้า (ลองเช็คทุกๆ 3 วินาที เป็นเวลา 1 นาที)
	client := &http.Client{}
	for i := 0; i < 20; i++ {
		time.Sleep(3 * time.Second)

		req, _ := http.NewRequest("GET", "https://api.mail.tm/messages", nil)
		req.Header.Set("Authorization", "Bearer "+authRes.Token)
		resp2, err := client.Do(req)
		if err != nil {
			continue
		}

		var msgRes struct {
			HydraMember []struct {
				Id      string `json:"id"`
				Subject string `json:"subject"`
				Intro   string `json:"intro"`
			} `json:"hydra:member"`
		}
		json.NewDecoder(resp2.Body).Decode(&msgRes)
		resp2.Body.Close()

		if len(msgRes.HydraMember) > 0 {
			fmt.Printf("📬 มีอีเมลเข้าแล้ว! หัวข้อ: %s\n", msgRes.HydraMember[0].Subject)
			fmt.Printf("📝 เนื้อหาเบื้องต้น / OTP: %s\n", msgRes.HydraMember[0].Intro)
			return
		}
		fmt.Println("🔄 กำลังรออีเมล OTP เข้า...")
	}
	fmt.Println("❌ หมดเวลา: ไม่พบอีเมล OTP ส่งเข้ามา")
}