package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"regexp"

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
	fmt.Printf("✨ ได้อีเมลสำหรับสมัคร: %s (รหัสผ่าน: %s)\n", email, password)

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
	reference := sendOTPRequest(client, email, turnstileToken)
	if reference == "" {
		fmt.Println("❌ ส่งคำขอรับ OTP ไม่สำเร็จ")
		return
	}

	// 5. วนลูปเช็คกล่องจดหมายเพื่อรอรับ OTP จาก Mail.tm
	fmt.Println("⏳ กำลังรอรับ OTP จากกล่องข้อความ...")
	otp := fetchOTP(email, password)

	if otp != "" {
		fmt.Printf("🚀 กำลังนำ OTP %s ไปกรอกลงในเว็บไซต์พร้อมกับ Reference: %s...\n", otp, reference)
		verifyToken := submitOTP(client, email, otp, reference)
		
		if verifyToken != "" {
			fmt.Println("🚀 กำลังเตรียมข้อมูลเพื่อสมัครสมาชิกขั้นตอนสุดท้าย...")
			// หน้าเว็บขั้นตอนสุดท้ายมี Cloudflare Captcha อีกรอบ ต้องขอ Token ใหม่อีก 1 ครั้ง
			fmt.Println("🧩 กำลังขอ Token Captcha ใหม่สำหรับการกดยืนยันสมัคร...")
			turnstileToken2 := solveTurnstileLocal(TargetURL) 
			
			if turnstileToken2 != "" {
				completeRegistration(client, email, verifyToken, turnstileToken2)
			}
		}
	}
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
	
	// สุ่มตัวอักษรให้ดูเป็นอีเมลคนจริงๆ (mail.tm ไม่รองรับจุด . ในชื่ออีเมล)
	emailPrefix := fmt.Sprintf("davidsmith%d", time.Now().UnixNano()%100000)
	email := fmt.Sprintf("%s@%s", emailPrefix, domain)
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
func sendOTPRequest(client tls_client.HttpClient, email string, turnstileToken string) string {
	// ⚠️ หมายเหตุ: URL ของ API ส่ง OTP และโครงสร้าง JSON (Payload) อาจต้องเปลี่ยนตาม Endpoint จริงที่แกะได้จาก Network Tab
	apiEndpoint := "https://core-api.thehof.gg/player/register/otp/request"

	payloadMap := map[string]string{
		"email":         email,
		"captcha_type":  "CF_TURNSTILE",
		"captcha_token": turnstileToken,
		"publisher_id":  "75d3fde3-21e3-4305-a617-536259abf340",
	}
	payload, _ := json.Marshal(payloadMap)

	req, err := fhttp.NewRequest("POST", apiEndpoint, bytes.NewReader(payload))
	if err != nil {
		return ""
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

	resp, err := client.Do(req)
	if err != nil {
		return ""
	}
	defer resp.Body.Close()
	
	// อ่าน Response Body เพื่อดูข้อความที่เซิร์ฟเวอร์ตอบกลับมาจริงๆ
	respBody := new(bytes.Buffer)
	respBody.ReadFrom(resp.Body)
	fmt.Printf("🔍 Server Response (%d): %s\n", resp.StatusCode, respBody.String())

	// ถ้า Status เป็น 200 หรือ 201 ถือว่าส่งคำขอสำเร็จ
	if resp.StatusCode == 200 || resp.StatusCode == 201 {
		fmt.Printf("✅ ส่ง OTP ไปยัง %s สำเร็จ!\n", email)
		
		// ดึง reference ออกมาจาก JSON Response
		var resJson struct {
			Data struct {
				Reference string `json:"reference"`
			} `json:"data"`
		}
		json.Unmarshal(respBody.Bytes(), &resJson)
		return resJson.Data.Reference
	}

	fmt.Printf("⚠️ ส่งคำขอไม่สำเร็จ Status Code: %d\n", resp.StatusCode)
	return ""
}

// --- ฟังก์ชันยืนยัน OTP ---
func submitOTP(client tls_client.HttpClient, email string, otp string, reference string) string {
	apiEndpoint := "https://core-api.thehof.gg/player/register/otp/verify"

	// โครงสร้างข้อมูลตามที่เว็บต้องการ
	payloadMap := map[string]string{
		"email":        email,
		"reference":    reference,
		"otp":          otp,
		"publisher_id": "75d3fde3-21e3-4305-a617-536259abf340",
	}
	payload, _ := json.Marshal(payloadMap)

	req, err := fhttp.NewRequest("POST", apiEndpoint, bytes.NewReader(payload))
	if err != nil {
		fmt.Println("❌ สร้างคำขอยืนยัน OTP ไม่สำเร็จ")
		return ""
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

	resp, err := client.Do(req)
	if err != nil {
		fmt.Printf("❌ ยิง API ยืนยัน OTP ไม่สำเร็จ: %v\n", err)
		return ""
	}
	defer resp.Body.Close()

	// อ่าน Response 
	respBody := new(bytes.Buffer)
	respBody.ReadFrom(resp.Body)
	
	fmt.Printf("🔍 Verify Response (%d): %s\n", resp.StatusCode, respBody.String())

	if resp.StatusCode == 200 || resp.StatusCode == 201 {
		fmt.Println("🎉 ยืนยัน OTP สำเร็จเรียบร้อยแล้ว!")
		
		var verifyRes struct {
			Data struct {
				VerifyToken string `json:"verify_token"`
			} `json:"data"`
		}
		json.Unmarshal(respBody.Bytes(), &verifyRes)
		return verifyRes.Data.VerifyToken
	} else {
		fmt.Println("⚠️ ยืนยัน OTP ไม่สำเร็จ")
		return ""
	}
}

// --- ฟังก์ชันสมัครสมาชิกขั้นตอนสุดท้าย ---
func completeRegistration(client tls_client.HttpClient, email, verifyToken, turnstileToken string) {
	apiEndpoint := "https://core-api.thehof.gg/player/register"

	// สุ่ม Username เพื่อไม่ให้ซ้ำ
	username := fmt.Sprintf("davidz%d", time.Now().UnixNano()%1000000)

	payloadMap := map[string]interface{}{
		"username":                username,
		"email":                   email,
		"verify_token":            verifyToken,
		"country_code":            "TH",
		"calling_code":            "+66",
		"phone_number":            "098127376",
		"password":                "Thailand2024",
		"password_confirmation":   "Thailand2024",
		"is_newsletter_accepted":  true,
		"captcha_type":            "CF_TURNSTILE",
		"captcha_token":           turnstileToken,
		"game_id":                 "a17fe1fe-0e1a-49bd-9a8d-f2da7f9a2e74",
		"publisher_id":            "75d3fde3-21e3-4305-a617-536259abf340",
	}
	payload, _ := json.Marshal(payloadMap)

	req, err := fhttp.NewRequest("POST", apiEndpoint, bytes.NewReader(payload))
	if err != nil {
		fmt.Println("❌ สร้างคำขอสมัครสมาชิกไม่สำเร็จ")
		return
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

	resp, err := client.Do(req)
	if err != nil {
		fmt.Printf("❌ ยิง API สมัครสมาชิกไม่สำเร็จ: %v\n", err)
		return
	}
	defer resp.Body.Close()

	respBody := new(bytes.Buffer)
	respBody.ReadFrom(resp.Body)
	
	fmt.Printf("🔍 Final Register Response (%d): %s\n", resp.StatusCode, respBody.String())

	if resp.StatusCode == 200 || resp.StatusCode == 201 {
		fmt.Println("🌟 สมัครสมาชิกเสร็จสมบูรณ์ 100% 🌟")
		fmt.Printf("===============================\n")
		fmt.Printf("Email: %s\n", email)
		fmt.Printf("Password: Thailand2024\n")
		fmt.Printf("Username: %s\n", username)
		fmt.Printf("===============================\n")
	} else {
		fmt.Println("⚠️ สมัครสมาชิกขั้นตอนสุดท้ายไม่สำเร็จ")
	}
}

// --- ฟังก์ชันเช็คกล่องจดหมายเพื่อดึงรหัส OTP ---
func fetchOTP(email, password string) string {
	// ล็อกอินเพื่อเอา Token ของ Mail.tm
	authPayload, _ := json.Marshal(map[string]string{"address": email, "password": password})
	resp, err := http.Post("https://api.mail.tm/token", "application/json", bytes.NewBuffer(authPayload))
	if err != nil {
		fmt.Printf("❌ ไม่สามารถเชื่อมต่อกับ api.mail.tm/token ได้: %v\n", err)
		return ""
	}
	defer resp.Body.Close()

	if resp.StatusCode != 200 {
		errBody := new(bytes.Buffer)
		errBody.ReadFrom(resp.Body)
		fmt.Printf("❌ ล็อกอิน Mail.tm ไม่ผ่าน (Status: %d) - %s\n", resp.StatusCode, errBody.String())
		return ""
	}

	var authRes struct {
		Token string `json:"token"`
	}
	json.NewDecoder(resp.Body).Decode(&authRes)
	if authRes.Token == "" {
		fmt.Println("❌ ล็อกอิน Mail.tm สำเร็จ แต่ไม่ได้รับ Token")
		return ""
	}

	// วนลูปเช็คอีเมลเข้า
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
			intro := msgRes.HydraMember[0].Intro
			msgId := msgRes.HydraMember[0].Id
			fmt.Printf("📬 มีอีเมลเข้าแล้ว! หัวข้อ: %s\n", msgRes.HydraMember[0].Subject)
			fmt.Printf("📝 เนื้อหาเบื้องต้น: %s\n", intro)
			
			// ใช้ Regex ดึงตัวเลข 6 หลักออกมาจากข้อความ (Intro)
			re := regexp.MustCompile(`\b\d{6}\b`)
			otp := re.FindString(intro)
			if otp != "" {
				fmt.Printf("🎯 สกัดรหัส OTP สำเร็จ: %s\n", otp)
				return otp
			}

			// ถ้า Intro ว่างเปล่า หรือหาไม่เจอ ให้ลองดึงเนื้อหาเต็มของอีเมลมาเช็ค
			fmt.Println("🔍 ไม่พบ OTP ใน Intro กำลังดึงเนื้อหาเต็มของอีเมลมาตรวจสอบ...")
			reqFull, _ := http.NewRequest("GET", "https://api.mail.tm/messages/"+msgId, nil)
			reqFull.Header.Set("Authorization", "Bearer "+authRes.Token)
			respFull, errFull := client.Do(reqFull)
			if errFull == nil {
				var msgFull struct {
					Text string `json:"text"`
					Html []string `json:"html"`
				}
				json.NewDecoder(respFull.Body).Decode(&msgFull)
				respFull.Body.Close()
				
				// ลองหาใน Text แบบปกติก่อน
				otpFull := re.FindString(msgFull.Text)
				if otpFull != "" {
					fmt.Printf("🎯 สกัดรหัส OTP จากเนื้อหาเต็มสำเร็จ: %s\n", otpFull)
					return otpFull
				}

				// ถ้าหาแบบปกติไม่เจอ อาจจะมีการเว้นวรรค เช่น 2 9 1 2 9 8 (จากรูป)
				// ใช้ Regex หาตัวเลขที่อาจมีช่องว่างคั่น
				reSpaced := regexp.MustCompile(`(?:\D|^)(\d\s*){6}(?:\D|$)`)
				spacedMatch := reSpaced.FindString(msgFull.Text)
				if spacedMatch != "" {
					// ลบช่องว่างและอักขระที่ไม่ใช่ตัวเลขออกให้เหลือแค่ 6 ตัว
					reClean := regexp.MustCompile(`\D+`)
					cleanOtp := reClean.ReplaceAllString(spacedMatch, "")
					if len(cleanOtp) >= 6 {
						otpFinal := cleanOtp[:6]
						fmt.Printf("🎯 สกัดรหัส OTP จากเนื้อหาแบบเว้นวรรคสำเร็จ: %s\n", otpFinal)
						return otpFinal
					}
				}
			}

			fmt.Println("⚠️ มีอีเมลเข้า แต่หาตัวเลข OTP 6 หลักไม่เจอ")
			return ""
		}
		fmt.Println("🔄 กำลังรออีเมล OTP เข้า...")
	}
	fmt.Println("❌ หมดเวลา: ไม่พบอีเมล OTP ส่งเข้ามา")
	return ""
}