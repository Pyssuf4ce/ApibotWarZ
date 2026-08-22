package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"math/rand"
	"os"
	"os/exec"
	"regexp"
	"strings"

	"net/http"
	"sync"
	"time"

	fhttp "github.com/bogdanfinn/fhttp"
	tls_client "github.com/bogdanfinn/tls-client"
	"github.com/bogdanfinn/tls-client/profiles"
)

// ค่ากำหนดสำหรับการใช้งาน (ปรับแต่งตรงนี้ตามต้องการ)
const (
	TargetURL     = "https://member.thehof.gg/register"
	MaxConcurrent = 5 // ⚠️ จำนวนบอทที่รันพร้อมกัน 5 ตัว ทำงานแยกกันหมด
)

var (
	vpnLock         sync.Mutex
	lastVPNChange   time.Time
	accountFileLock sync.Mutex
)

// โครงสร้างข้อมูลสำหรับบัญชีอีเมลชั่วคราว
type TempMailAccount struct {
	Email       string
	Password    string
	ProviderURL string
}

func main() {
	fmt.Printf("🚀 เริ่มรันบอทอัตโนมัติ (ระบบรันพร้อมกัน %d ตัว ทำงานแยกกันหมด)...\n", MaxConcurrent)

	var wg sync.WaitGroup

	for i := 1; i <= MaxConcurrent; i++ {
		wg.Add(1)
		go func(botID int) {
			defer wg.Done()
			runBot(botID)
		}(i)
	}

	wg.Wait()
	fmt.Println("🎉 บอททุกตัวทำงานเสร็จสิ้น!")
}

func runBot(botID int) {
	fmt.Printf("[Bot-%d] 🚀 เริ่มต้นทำงาน...\n", botID)

	for {
		fmt.Printf("[Bot-%d] 🔄 (เริ่มรอบใหม่) กำลังเตรียมระบบและสร้าง HTTP Client...\n", botID)
		// 1. สร้าง HTTP Client แบบปลอม TLS Fingerprint
		jar := tls_client.NewCookieJar()
		options := []tls_client.HttpClientOption{
			tls_client.WithTimeoutSeconds(30),
			tls_client.WithClientProfile(profiles.Chrome_120),
			tls_client.WithCookieJar(jar),
		}
		client, err := tls_client.NewHttpClient(tls_client.NewNoopLogger(), options...)
		if err != nil {
			fmt.Printf("[Bot-%d] ❌ สร้าง HTTP Client ไม่สำเร็จ: %v (กำลังลองใหม่...)\n", botID, err)
			time.Sleep(2 * time.Second)
			continue
		}

		// 2. สร้างอีเมลชั่วคราว (ระบบหลาย Provider สำรองอัตโนมัติ)
		fmt.Printf("[Bot-%d] 📧 กำลังสร้าง Temp Mail...\n", botID)
		mailAcc := createTempMail(botID)
		if mailAcc == nil || mailAcc.Email == "" {
			fmt.Printf("[Bot-%d] ❌ สร้าง Temp Mail ไม่สำเร็จ (กำลังลองใหม่...)\n", botID)
			time.Sleep(2 * time.Second)
			continue
		}
		fmt.Printf("[Bot-%d] ✨ ได้อีเมล: %s (Provider: %s)\n", botID, mailAcc.Email, mailAcc.ProviderURL)

		// 3. ขอ Turnstile Token รอบที่ 1
		fmt.Printf("[Bot-%d] 🧩 กำลังขอ Token Captcha รอบแรก...\n", botID)
		turnstileToken := solveTurnstileLocal(TargetURL, botID)
		if turnstileToken == "" {
			fmt.Printf("[Bot-%d] ❌ ไม่ได้ Token รอบแรก (กำลังลองใหม่...)\n", botID)
			continue
		}
		fmt.Printf("[Bot-%d] ✅ ได้รับ Token รอบแรก!\n", botID)

		// 4. ส่งขอ OTP
		reference := sendOTPRequest(client, mailAcc.Email, turnstileToken, botID)
		if reference == "IP_BLOCKED" {
			fmt.Printf("[Bot-%d] 🔄 โดนบล็อค IP ตั้งแต่ตอนขอ OTP! ระบบกำลังทำการสลับ VPN อัตโนมัติ...\n", botID)
			connectRandomVPN()
			continue
		} else if reference == "" {
			fmt.Printf("[Bot-%d] ❌ ขอ OTP ไม่สำเร็จ (กำลังลองใหม่...)\n", botID)
			continue
		}

		// 5. อ่าน OTP จากกล่องข้อความ
		fmt.Printf("[Bot-%d] ⏳ กำลังรอ OTP จากกล่องข้อความ (รอสูงสุด 15 วินาที)...\n", botID)
		otp := fetchOTP(mailAcc, botID)
		if otp == "" {
			fmt.Printf("[Bot-%d] ❌ รอ OTP นานเกินไป หรือหา OTP ไม่เจอ (กำลังลองใหม่...)\n", botID)
			continue
		}

		// 6. ยืนยัน OTP
		fmt.Printf("[Bot-%d] 🚀 กำลังนำ OTP %s ไปยืนยัน (Ref: %s)...\n", botID, otp, reference)
		verifyToken := submitOTP(client, mailAcc.Email, otp, reference, botID)
		if verifyToken == "" {
			fmt.Printf("[Bot-%d] ❌ ยืนยัน OTP ไม่ผ่าน (กำลังลองใหม่...)\n", botID)
			continue
		}

		// 7. ขอ Turnstile Token รอบที่ 2 สำหรับหน้าสุดท้าย
		fmt.Printf("[Bot-%d] 🧩 กำลังขอ Token Captcha รอบสอง...\n", botID)
		turnstileToken2 := solveTurnstileLocal(TargetURL, botID) 
		if turnstileToken2 == "" {
			fmt.Printf("[Bot-%d] ❌ ไม่ได้ Token รอบสอง (กำลังลองใหม่...)\n", botID)
			continue
		}

		// 8. สมัครสมาชิกขั้นสุดท้าย
		status := completeRegistration(client, mailAcc.Email, verifyToken, turnstileToken2, botID)
		if status == 0 {
			fmt.Printf("[Bot-%d] 🎉 สมัครสมาชิกสำเร็จเรียบร้อย! กำลังเริ่มสมัครบัญชีถัดไป...\n", botID)
			continue // กลับไปวนลูปเพื่อสมัครไอดีถัดไป
		} else if status == 1 {
			// ถ้าเจอ 1 แปลว่า IP โดนบล็อค ให้ตัด VPN เก่า แล้วต่อ VPN ใหม่ทันที
			fmt.Printf("[Bot-%d] 🔄 โดนบล็อค IP! ระบบกำลังทำการสลับ VPN อัตโนมัติ...\n", botID)
			connectRandomVPN()
			continue
		} else {
			fmt.Printf("[Bot-%d] ❌ สมัครสมาชิกขั้นตอนสุดท้ายไม่สำเร็จ (กำลังเริ่มใหม่ตั้งแต่ต้น...)\n", botID)
			continue
		}
	}
}

// Helper to extract domain from response body supporting both JSON array and Hydra format
func parseDomain(body []byte) string {
	var arrayRes []struct {
		Domain string `json:"domain"`
	}
	if err := json.Unmarshal(body, &arrayRes); err == nil && len(arrayRes) > 0 && arrayRes[0].Domain != "" {
		return arrayRes[0].Domain
	}

	var hydraRes struct {
		HydraMember []struct {
			Domain string `json:"domain"`
		} `json:"hydra:member"`
	}
	if err := json.Unmarshal(body, &hydraRes); err == nil && len(hydraRes.HydraMember) > 0 && hydraRes.HydraMember[0].Domain != "" {
		return hydraRes.HydraMember[0].Domain
	}

	return ""
}

type MailMessageItem struct {
	Id      string `json:"id"`
	Subject string `json:"subject"`
	Intro   string `json:"intro"`
}

// Helper to extract message list from response body supporting both JSON array and Hydra format
func parseMessages(body []byte) []MailMessageItem {
	var listRes []MailMessageItem
	if err := json.Unmarshal(body, &listRes); err == nil && len(listRes) > 0 {
		return listRes
	}

	var hydraRes struct {
		HydraMember []MailMessageItem `json:"hydra:member"`
	}
	if err := json.Unmarshal(body, &hydraRes); err == nil && len(hydraRes.HydraMember) > 0 {
		return hydraRes.HydraMember
	}

	return nil
}

// --- ฟังก์ชันสร้าง Temp Mail รองรับหลาย Provider (mail.tm / mail.gw) และมีระบบ Retry ---
func createTempMail(botID int) *TempMailAccount {
	providers := []string{
		"https://api.mail.tm",
		"https://api.mail.gw",
	}

	httpClient := &http.Client{
		Timeout: 10 * time.Second,
	}

	for attempt := 1; attempt <= 3; attempt++ {
		for _, providerURL := range providers {
			// 1. ดึง Domain ที่ใช้งานได้
			req, err := http.NewRequest("GET", providerURL+"/domains", nil)
			if err != nil {
				continue
			}
			req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
			req.Header.Set("Accept", "application/json")

			resp, err := httpClient.Do(req)
			if err != nil || resp.StatusCode != 200 {
				if resp != nil {
					resp.Body.Close()
				}
				continue
			}

			bodyBytes, err := io.ReadAll(resp.Body)
			resp.Body.Close()
			if err != nil {
				continue
			}

			domain := parseDomain(bodyBytes)
			if domain == "" {
				continue
			}

			// สุ่มตัวอักษรและเลขให้ดูเป็นอีเมลคนจริงๆ พร้อมเพิ่ม botID และ nano timestamp ป้องกันการชนกัน
			randSeed := rand.Intn(10000)
			emailPrefix := fmt.Sprintf("david%d%d%d", botID, time.Now().UnixNano()%100000, randSeed)
			email := fmt.Sprintf("%s@%s", emailPrefix, domain)
			password := "P@ssw0rd123!"

			payload, _ := json.Marshal(map[string]string{"address": email, "password": password})
			req2, err := http.NewRequest("POST", providerURL+"/accounts", bytes.NewBuffer(payload))
			if err != nil {
				continue
			}
			req2.Header.Set("Content-Type", "application/json")
			req2.Header.Set("Accept", "application/json")
			req2.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

			resp2, err := httpClient.Do(req2)
			if err != nil {
				continue
			}
			statusCode := resp2.StatusCode
			resp2.Body.Close()

			if statusCode == 200 || statusCode == 201 {
				return &TempMailAccount{
					Email:       email,
					Password:    password,
					ProviderURL: providerURL,
				}
			}
		}
		time.Sleep(1 * time.Second)
	}

	return nil
}

// --- ฟังก์ชันแก้ Cloudflare Turnstile ผ่าน Local API Server ---
func solveTurnstileLocal(pageURL string, botID int) string {
	payloadMap := map[string]string{
		"url": pageURL,
	}
	body, _ := json.Marshal(payloadMap)

	localClient := &http.Client{
		Timeout: 60 * time.Second, // กำหนด Timeout 60 วินาทีเพื่อรองรับการรันพร้อมกันหลายตัว
	}

	req, err := http.NewRequest("POST", "http://127.0.0.1:5000/get-token", bytes.NewBuffer(body))
	if err != nil {
		fmt.Printf("[Bot-%d] ❌ สร้าง Request แก้ Captcha ไม่สำเร็จ: %v\n", botID, err)
		return ""
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := localClient.Do(req)
	if err != nil {
		fmt.Printf("[Bot-%d] ❌ ไม่สามารถเชื่อมต่อกับ Local API ได้: %v (อย่าลืมเปิด server.py หรือ server.exe)\n", botID, err)
		return ""
	}
	defer resp.Body.Close()

	var result struct {
		Status  string `json:"status"`
		Token   string `json:"token"`
		Message string `json:"message"`
	}
	json.NewDecoder(resp.Body).Decode(&result)

	if result.Status == "success" {
		return result.Token
	}

	fmt.Printf("[Bot-%d] ❌ โหลด Token ไม่สำเร็จ: %s\n", botID, result.Message)
	return ""
}
// --- ฟังก์ชันยิงขอ OTP ไปที่เว็บเป้าหมาย ---
func sendOTPRequest(client tls_client.HttpClient, email string, turnstileToken string, botID int) string {
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
	respStr := respBody.String()
	fmt.Printf("🔍 Server Response (%d): %s\n", resp.StatusCode, respStr)

	// เช็คว่ามี Error แจ้งเตือนเรื่องการรอ (IP Block) ตอนขอ OTP หรือไม่
	if strings.Contains(respStr, "\\u0e04\\u0e38\\u0e13\\u0e15\\u0e49\\u0e2d\\u0e07\\u0e23\\u0e2d") || strings.Contains(respStr, "คุณต้องรอ") {
		fmt.Printf("[Bot-%d] ⚠️ ติด Limit IP ตอนขอ OTP! เซิร์ฟเวอร์แจ้งให้รอ\n", botID)
		return "IP_BLOCKED" 
	}

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
func submitOTP(client tls_client.HttpClient, email string, otp string, reference string, botID int) string {
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
func completeRegistration(client tls_client.HttpClient, email, verifyToken, turnstileToken string, botID int) int {
	apiEndpoint := "https://core-api.thehof.gg/player/register"

	// สุ่ม Username ให้มีตัวพิมพ์ใหญ่ผสมพิมพ์เล็กและตัวเลข เพื่อให้ผ่านเงื่อนไขตั้งรหัสผ่าน (ID=Pass)
	username := fmt.Sprintf("Davidz%d%d", botID, time.Now().UnixNano()%1000000)

	payloadMap := map[string]interface{}{
		"username":                username,
		"email":                   email,
		"verify_token":            verifyToken,
		"country_code":            "TH",
		"calling_code":            "+66",
		"phone_number":            "098127376",
		"password":                username,
		"password_confirmation":   username,
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
		return 2
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

	resp, err := client.Do(req)
	if err != nil {
		fmt.Printf("❌ ยิง API สมัครสมาชิกไม่สำเร็จ: %v\n", err)
		return 2
	}
	defer resp.Body.Close()

	respBody := new(bytes.Buffer)
	respBody.ReadFrom(resp.Body)
	respStr := respBody.String()
	
	fmt.Printf("🔍 Final Register Response (%d): %s\n", resp.StatusCode, respStr)

	// เช็คว่ามี Error แจ้งเตือนเรื่องการรอ 6 นาทีหรือไม่ (เช็คจากข้อความภาษาไทยที่ถูกเข้ารหัส Unicode)
	// "\u0e04\u0e38\u0e13\u0e15\u0e49\u0e2d\u0e07\u0e23\u0e2d" = "คุณต้องรอ"
	if strings.Contains(respStr, "\\u0e04\\u0e38\\u0e13\\u0e15\\u0e49\\u0e2d\\u0e07\\u0e23\\u0e2d") || strings.Contains(respStr, "คุณต้องรอ") {
		fmt.Printf("[Bot-%d] ⚠️ ติด Limit IP! เซิร์ฟเวอร์แจ้งให้รอ 6 นาที\n", botID)
		return 1 // รหัส 1 หมายถึง IP โดนบล็อคชั่วคราว
	}

	if resp.StatusCode == 200 || resp.StatusCode == 201 {
		fmt.Println("🌟 สมัครสมาชิกเสร็จสมบูรณ์ 100% 🌟")
		fmt.Printf("===============================\n")
		fmt.Printf("Email: %s\n", email)
		fmt.Printf("Password: %s\n", username)
		fmt.Printf("Username: %s\n", username)
		fmt.Printf("===============================\n")
		
		// บันทึกบัญชีที่สมัครสำเร็จลงไฟล์ accounts.txt
		saveAccount(username)
		return 0 // รหัส 0 หมายถึง สำเร็จ
	} else {
		fmt.Println("⚠️ สมัครสมาชิกขั้นตอนสุดท้ายไม่สำเร็จ")
		return 2 // รหัส 2 หมายถึง Error อื่นๆ
	}
}

// --- ฟังก์ชันบันทึกข้อมูลบัญชีลงไฟล์ ---
func saveAccount(username string) {
	accountFileLock.Lock()
	defer accountFileLock.Unlock()

	// สร้างหรือเปิดไฟล์เพื่อเขียนต่อท้าย
	file, err := os.OpenFile("accounts.txt", os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
	if err != nil {
		fmt.Printf("❌ ไม่สามารถเปิดไฟล์ accounts.txt ได้: %v\n", err)
		return
	}
	defer file.Close()

	// รูปแบบ: เก็บบันทึกแค่ ID (Username)
	logEntry := fmt.Sprintf("%s\n", username)
	if _, err := file.WriteString(logEntry); err != nil {
		fmt.Printf("❌ ไม่สามารถเขียนข้อมูลบัญชีลงไฟล์ได้: %v\n", err)
	} else {
		fmt.Println("📝 บันทึก ID ลงไฟล์ accounts.txt เรียบร้อยแล้ว")
	}
}

// --- ฟังก์ชันเช็คกล่องจดหมายเพื่อดึงรหัส OTP ---
func fetchOTP(acc *TempMailAccount, botID int) string {
	if acc == nil {
		return ""
	}

	httpClient := &http.Client{
		Timeout: 10 * time.Second,
	}

	// ล็อกอินเพื่อเอา Token ของ Provider
	authPayload, _ := json.Marshal(map[string]string{"address": acc.Email, "password": acc.Password})
	reqAuth, err := http.NewRequest("POST", acc.ProviderURL+"/token", bytes.NewBuffer(authPayload))
	if err != nil {
		fmt.Printf("[Bot-%d] ❌ สร้างคำขอล็อกอิน Mail ไม่สำเร็จ: %v\n", botID, err)
		return ""
	}
	reqAuth.Header.Set("Content-Type", "application/json")
	reqAuth.Header.Set("Accept", "application/json")
	reqAuth.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

	resp, err := httpClient.Do(reqAuth)
	if err != nil {
		fmt.Printf("[Bot-%d] ❌ ไม่สามารถเชื่อมต่อกับ %s/token ได้: %v\n", botID, acc.ProviderURL, err)
		return ""
	}
	defer resp.Body.Close()

	if resp.StatusCode != 200 {
		errBody := new(bytes.Buffer)
		errBody.ReadFrom(resp.Body)
		fmt.Printf("[Bot-%d] ❌ ล็อกอิน Mail ไม่ผ่าน (Status: %d) - %s\n", botID, resp.StatusCode, errBody.String())
		return ""
	}

	var authRes struct {
		Token string `json:"token"`
	}
	json.NewDecoder(resp.Body).Decode(&authRes)
	if authRes.Token == "" {
		fmt.Printf("[Bot-%d] ❌ ล็อกอิน Mail สำเร็จ แต่ไม่ได้รับ Token\n", botID)
		return ""
	}

	// วนลูปเช็คอีเมลเข้า (รอสูงสุด 15 วินาที)
	for i := 0; i < 7; i++ {
		time.Sleep(2 * time.Second)

		req, err := http.NewRequest("GET", acc.ProviderURL+"/messages", nil)
		if err != nil {
			continue
		}
		req.Header.Set("Authorization", "Bearer "+authRes.Token)
		req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
		req.Header.Set("Accept", "application/json")

		resp2, err := httpClient.Do(req)
		if err != nil {
			continue
		}

		bodyBytes, err := io.ReadAll(resp2.Body)
		resp2.Body.Close()
		if err != nil {
			continue
		}

		messages := parseMessages(bodyBytes)
		if len(messages) > 0 {
			intro := messages[0].Intro
			msgId := messages[0].Id
			fmt.Printf("[Bot-%d] 📬 มีอีเมลเข้าแล้ว! หัวข้อ: %s\n", botID, messages[0].Subject)
			fmt.Printf("[Bot-%d] 📝 เนื้อหาเบื้องต้น: %s\n", botID, intro)

			// ใช้ Regex ดึงตัวเลข 6 หลักออกมาจากข้อความ (Intro)
			re := regexp.MustCompile(`\b\d{6}\b`)
			otp := re.FindString(intro)
			if otp != "" && otp != "000000" {
				fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP สำเร็จ: %s\n", botID, otp)
				return otp
			}

			// ถ้า Intro ว่างเปล่า หรือหาไม่เจอ ให้ลองดึงเนื้อหาเต็มของอีเมลมาเช็ค
			fmt.Printf("[Bot-%d] 🔍 ไม่พบ OTP ใน Intro กำลังดึงเนื้อหาเต็มของอีเมลมาตรวจสอบ...\n", botID)
			reqFull, _ := http.NewRequest("GET", acc.ProviderURL+"/messages/"+msgId, nil)
			reqFull.Header.Set("Authorization", "Bearer "+authRes.Token)
			reqFull.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
			reqFull.Header.Set("Accept", "application/json")

			respFull, errFull := httpClient.Do(reqFull)
			if errFull == nil {
				var msgFull struct {
					Text string   `json:"text"`
					Html []string `json:"html"`
				}
				json.NewDecoder(respFull.Body).Decode(&msgFull)
				respFull.Body.Close()

				// รวม Text และ Html
				fullContent := msgFull.Text
				for _, h := range msgFull.Html {
					fullContent += h
				}

				// ลบเนื้อหาใน <style> กรอง CSS ขยะทิ้ง (ป้องกันการจับ #000000)
				reStyle := regexp.MustCompile(`(?is)<style[^>]*>.*?</style>`)
				fullContent = reStyle.ReplaceAllString(fullContent, " ")

				// ลบ HTML Tags และลบช่องว่าง/ขึ้นบรรทัดใหม่ทั้งหมด
				reTags := regexp.MustCompile(`<[^>]*>`)
				cleanContent := reTags.ReplaceAllString(fullContent, " ")

				// ลองหาในรูปแบบปกติติดกัน 6 ตัว (ข้าม 000000 ที่อาจเป็นเศษ CSS)
				reSixDigits := regexp.MustCompile(`\b\d{6}\b`)
				matches := reSixDigits.FindAllString(cleanContent, -1)
				for _, match := range matches {
					if match != "000000" {
						fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP จากเนื้อหาเต็มสำเร็จ: %s\n", botID, match)
						return match
					}
				}

				// ถ้าหาแบบปกติไม่เจอ ให้ลบตัวอักษรที่ไม่ใช่ตัวเลขออก แล้วดูว่ามีตัวเลข 6 ตัวเรียงกันไหม
				reGroups := regexp.MustCompile(`\d+`)
				numberGroups := reGroups.FindAllString(cleanContent, -1)
				for _, numStr := range numberGroups {
					if len(numStr) == 6 && numStr != "000000" {
						fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP จากกลุ่มตัวเลขสำเร็จ: %s\n", botID, numStr)
						return numStr
					}
				}

				// ถ้ายาวๆ แล้วมีเว้นวรรค เราเอาเฉพาะตัวเลขใน cleanContent มาต่อกัน
				reSpaced2 := regexp.MustCompile(`(?:\D|^)(\d\s*){6}(?:\D|$)`)
				spacedMatch := reSpaced2.FindString(cleanContent)
				if spacedMatch != "" {
					reClean := regexp.MustCompile(`\D+`)
					cleanOtp := reClean.ReplaceAllString(spacedMatch, "")
					if len(cleanOtp) >= 6 {
						otpFinal := cleanOtp[:6]
						if otpFinal != "000000" {
							fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP จากเนื้อหาแบบเว้นวรรคสำเร็จ: %s\n", botID, otpFinal)
							return otpFinal
						}
					}
				}
			}

			fmt.Printf("[Bot-%d] ⚠️ มีอีเมลเข้า แต่หาตัวเลข OTP 6 หลักไม่เจอ\n", botID)
			return ""
		}
		fmt.Printf("[Bot-%d] 🔄 กำลังรออีเมล OTP เข้า...\n", botID)
	}
	fmt.Printf("[Bot-%d] ❌ หมดเวลา: ไม่พบอีเมล OTP ส่งเข้ามา\n", botID)
	return ""
}

// ==========================================
// ส่วนของการควบคุม VPN (VPN Gate + OpenVPN)
// ==========================================

// โครงสร้างข้อมูลสำหรับอ่านไฟล์ CSV จาก VPN Gate
type VpnServer struct {
	HostName string
	IP       string
	Country  string
	Speed    string
	Config   string // Base64 เข้ารหัส OpenVPN Config
}

var currentVpnCmd *exec.Cmd

// --- ฟังก์ชันตัดการเชื่อมต่อ VPN ---
func disconnectVPN() {
	fmt.Println("🔌 กำลังตัดการเชื่อมต่อ VPN ปัจจุบัน...")
	if currentVpnCmd != nil && currentVpnCmd.Process != nil {
		currentVpnCmd.Process.Kill()
		currentVpnCmd.Wait()
		currentVpnCmd = nil
	}
	// สั่งปิด openvpn.exe ทั้งหมดในเครื่องเพื่อความชัวร์
	exec.Command("taskkill", "/F", "/IM", "openvpn.exe").Run()
	time.Sleep(2 * time.Second)
}

// --- ฟังก์ชันดึงรายชื่อ VPN และเชื่อมต่อแบบสุ่ม ---
func connectRandomVPN() bool {
	vpnLock.Lock()
	defer vpnLock.Unlock()

	// เช็คว่าถ้าเพิ่งเปลี่ยน VPN ไปไม่ถึง 45 วินาที ให้ข้ามไปเลย เพื่อไม่ให้บอทตีกัน
	if time.Since(lastVPNChange) < 45*time.Second {
		fmt.Println("🔄 เพิ่งสลับ VPN ไปเมื่อครู่นี้ บอทตัวนี้จะใช้ VPN ใหม่ไปเลย...")
		return true
	}

	disconnectVPN()
	fmt.Println("🌍 กำลังดึงรายชื่อ VPN จาก VPN Gate (ญี่ปุ่น, เกาหลี, ไทย)...")

	// ดึงข้อมูลรายชื่อ VPN (รูปแบบ CSV)
	resp, err := http.Get("http://www.vpngate.net/api/iphone/")
	if err != nil {
		fmt.Printf("❌ โหลดรายชื่อ VPN ไม่สำเร็จ: %v\n", err)
		return false
	}
	defer resp.Body.Close()

	bodyBytes, _ := io.ReadAll(resp.Body)
	csvData := string(bodyBytes)
	lines := strings.Split(csvData, "\n")

	var servers []VpnServer

	// ข้าม 2 บรรทัดแรกที่เป็น Header
	for i := 2; i < len(lines); i++ {
		columns := strings.Split(lines[i], ",")
		if len(columns) > 14 {
			country := columns[5]
			// กรองเอาเฉพาะโซนเอเชีย เพื่อให้เน็ตไม่ช้าเกินไปจนขอ Captcha ไม่ผ่าน
			if country == "Japan" || country == "Thailand" || country == "Korea Republic of" || country == "Vietnam" {
				servers = append(servers, VpnServer{
					HostName: columns[0],
					IP:       columns[1],
					Country:  country,
					Speed:    columns[4],
					Config:   columns[14], // ข้อมูล Config อยู่คอลัมน์ที่ 14 (Base64)
				})
			}
		}
	}

	if len(servers) == 0 {
		fmt.Println("❌ ไม่พบเซิร์ฟเวอร์ VPN ในโซนเอเชีย")
		return false
	}

	// สุ่มเลือก VPN มา 1 ตัว
	rand.Seed(time.Now().UnixNano())
	selected := servers[rand.Intn(len(servers))]
	fmt.Printf("🎯 สุ่มได้ VPN: %s (IP: %s)\n", selected.Country, selected.IP)

	// ถอดรหัส Base64 ของ Config ไฟล์
	configBytes, err := base64.StdEncoding.DecodeString(selected.Config)
	if err != nil {
		fmt.Println("❌ ถอดรหัสไฟล์ Config ไม่สำเร็จ")
		return false
	}

	// เขียนไฟล์ Config ลงเครื่องเพื่อเตรียมรัน
	configFileName := fmt.Sprintf("vpn_%d.ovpn", time.Now().UnixNano())
	err = os.WriteFile(configFileName, configBytes, 0644)
	if err != nil {
		fmt.Printf("❌ สร้างไฟล์ VPN ไม่สำเร็จ: %v\n", err)
		return false
	}

	fmt.Println("🚀 กำลังเชื่อมต่อ VPN (รอประมาณ 15 วินาที)...")

	// ใช้คำสั่ง CMD เรียก OpenVPN ให้ทำงานโดยอ่านไฟล์ที่โหลดมา
	openVpnPath := `C:\Program Files\OpenVPN\bin\openvpn.exe`
	
	// รัน OpenVPN แบบ Background พร้อมรับ Log กรณีพัง
	currentVpnCmd = exec.Command(openVpnPath, "--config", configFileName)
	
	// สั่งรันใน Background
	err = currentVpnCmd.Start()
	if err != nil {
		fmt.Printf("❌ ไม่สามารถรัน OpenVPN ได้: %v\n(โปรดเช็คว่าติดตั้ง OpenVPN ไว้ที่ C:\\Program Files\\OpenVPN\\bin\\openvpn.exe หรือไม่)\n", err)
		return false
	}

	// ลบไฟล์ Config เก่าทิ้งหลังจากรันเสร็จ (หรือรอให้โปรแกรมปิดค่อยลบ)
	go func(name string) {
		time.Sleep(30 * time.Second)
		os.Remove(name)
	}(configFileName)

	// รอให้ VPN เชื่อมต่อติด (ให้เวลา 12 วินาที)
	time.Sleep(12 * time.Second)
	
	// อัปเดตเวลาที่เปลี่ยน VPN ล่าสุด
	lastVPNChange = time.Now()
	
	fmt.Println("✅ เชื่อมต่อ VPN เสร็จสิ้น! น่าจะได้ IP ใหม่แล้ว")
	return true
}