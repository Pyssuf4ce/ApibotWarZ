package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"math/rand"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"net/http"
	"sync"
	"syscall"
	"time"

	fhttp "github.com/bogdanfinn/fhttp"
	tls_client "github.com/bogdanfinn/tls-client"
	"github.com/bogdanfinn/tls-client/profiles"
)

// ค่ากำหนดสำหรับการใช้งาน (ปรับแต่งตรงนี้ตามต้องการ)
const (
	TargetURL = "https://member.thehof.gg/register"
)

var (
	MaxConcurrent = 5 // จำนวนบอทที่รันพร้อมกัน (สามารถปรับผ่าน -threads ได้)
	vpnLock          sync.Mutex
	lastVPNChange    time.Time
	accountFileLock  sync.Mutex
	domainCacheLock  sync.RWMutex
	cachedDomain     string
	cachedDomainProv string

	// ระบบ VPN Coordinator: ป้องกันบอทแย่งกันสลับ VPN
	vpnCoordMu         sync.Mutex
	vpnSwitchInProgress bool
	vpnSwitchDone      chan struct{}

	// แคชรายชื่อ VPN Gate ไว้ในแรม ไม่ต้องโหลดซ้ำทุกรอบ
	cachedVpnServers []VpnServer
	cachedVpnTime    time.Time
	cachedVpnLock    sync.Mutex

	// ระบบจำ IP ที่ติด Limit ถาวรในแรม 45 นาที (ห้ามสุ่มซ้ำเด็ดขาด)
	globalBlockedIPs = make(map[string]time.Time)
	globalBlockedMu  sync.Mutex

	// ระบบติดตามสถานะของแต่ละบอท (Worker Stage Tracking & Grace Barrier)
	botStages   = make(map[int]BotStage)
	botStagesMu sync.RWMutex

	// คิวอีเมลชั่วคราวพร้อมใช้ล่วงหน้า (Pre-warmed Mail Pool)
	mailPool = make(chan *TempMailAccount, 10)
)

// นิยามลำดับขั้นตอนการทำงานของบอท เพื่อใช้ทำ Barrier & Grace Period
type BotStage int

const (
	StageIdle BotStage = iota
	StageInit
	StageTempMail
	StageCaptcha1
	StageRequestOTP
	StageWaitOTP
	StageSubmitOTP   // Critical: มี OTP แล้ว กำลังส่ง verify_token
	StageCaptcha2   // Critical: แก้ Captcha รอบ 2 เพื่อยิงสมัคร
	StageCompleteReg // Critical: กำลังส่งคำขอสมัครสมาชิกขั้นตอนสุดท้าย
)

func setBotStage(botID int, stage BotStage) {
	botStagesMu.Lock()
	botStages[botID] = stage
	botStagesMu.Unlock()
}

func isAnyBotInCriticalStage(excludeBotID int) bool {
	botStagesMu.RLock()
	defer botStagesMu.RUnlock()
	for id, stage := range botStages {
		if id == excludeBotID {
			continue
		}
		if stage == StageSubmitOTP || stage == StageCaptcha2 || stage == StageCompleteReg {
			return true
		}
	}
	return false
}

func getCriticalBots(excludeBotID int) []int {
	botStagesMu.RLock()
	defer botStagesMu.RUnlock()
	var list []int
	for id, stage := range botStages {
		if id == excludeBotID {
			continue
		}
		if stage == StageSubmitOTP || stage == StageCaptcha2 || stage == StageCompleteReg {
			list = append(list, id)
		}
	}
	return list
}

// ระบบสร้างอีเมลล่วงหน้าเติมเข้า Pool อยู่ตลอดเวลาแบบ Async
func startMailProducer() {
	go func() {
		defer func() {
			if r := recover(); r != nil {
				fmt.Printf("💥 Mail Producer Panic Recovered: %v\n", r)
				time.Sleep(2 * time.Second)
				startMailProducer()
			}
		}()

		for {
			vpnCoordMu.Lock()
			isSwitching := vpnSwitchInProgress
			vpnCoordMu.Unlock()

			if isSwitching {
				time.Sleep(1 * time.Second)
				continue
			}

			// เลี้ยงให้มีอีเมลพร้อมใช้อยู่ใน Pool อย่างน้อย 6-8 บัญชี
			if len(mailPool) >= 6 {
				time.Sleep(800 * time.Millisecond)
				continue
			}

			acc := createTempMail(1)
			if acc != nil && acc.Email != "" {
				mailPool <- acc
			} else {
				time.Sleep(1 * time.Second)
			}
		}
	}()
}

// โครงสร้างข้อมูลสำหรับบัญชีอีเมลชั่วคราว
type TempMailAccount struct {
	Email       string
	Password    string
	ProviderURL string
	AuthToken   string
}

func main() {
	threadsFlag := flag.Int("threads", 5, "จำนวนบอทที่รันพร้อมกัน")
	flag.Parse()

	if *threadsFlag > 0 {
		MaxConcurrent = *threadsFlag
	}

	// ปิด OpenVPN เก่าที่อาจค้างอยู่ และล้าง DNS ให้เน็ตสะอาดตั้งแต่เริ่มต้น
	exec.Command("taskkill", "/F", "/IM", "openvpn.exe").Run()
	exec.Command("ipconfig", "/flushdns").Run()

	// เริ่มระบบผลิตอีเมลล่วงหน้า (Pre-warmed Mail Producer)
	startMailProducer()

	fmt.Printf("🚀 เริ่มรันบอทอัตโนมัติ (ระบบรันพร้อมกัน %d ตัว ทำงานแยกกันหมด)...\n", MaxConcurrent)

	for i := 1; i <= MaxConcurrent; i++ {
		go runBot(i)
	}

	// ล็อกโปรแกรมไว้ไม่ให้ปิดตัวเองตลอดกาล (บอทจะวนทำงานเองในแต่ละ goroutine)
	select {}
}

func runBot(botID int) {
	defer func() {
		setBotStage(botID, StageIdle)
		if r := recover(); r != nil {
			fmt.Printf("[Bot-%d] 💥 กู้คืนระบบจากการขัดข้อง (Panic Recovered): %v (เริ่มทำงานใหม่...)\n", botID, r)
			time.Sleep(2 * time.Second)
			go runBot(botID)
		}
	}()

	// ⚡ หน่วงเวลาเริ่มต้นรอบแรกสั้นๆ (0.3s ต่อบอท) เพื่อกระจายคิวเปิดหน้าต่างไม่ให้กระชากพร้อมกันในเสี้ยววินาทีแรก
	if botID > 1 {
		time.Sleep(time.Duration((botID-1)*350) * time.Millisecond)
	}

	fmt.Printf("[Bot-%d] 🚀 เริ่มต้นทำงาน...\n", botID)
	consecutiveFails := 0

	// ฟังก์ชันตรวจสอบและรอหาก VPN กำลังถูกสลับ
	checkVPNPause := func() {
		vpnCoordMu.Lock()
		if vpnSwitchInProgress {
			waitCh := vpnSwitchDone
			vpnCoordMu.Unlock()
			fmt.Printf("[Bot-%d] ⏳ VPN กำลังสลับอยู่ รอให้เสร็จก่อนเริ่มงาน...\n", botID)
			<-waitCh
		} else {
			vpnCoordMu.Unlock()
		}
	}

	for {
		setBotStage(botID, StageIdle)
		// 0. ถ้า VPN กำลังสลับอยู่ → รอให้เสร็จก่อน ไม่ต้องไปขอ Captcha ให้เปลือง
		checkVPNPause()

		setBotStage(botID, StageInit)
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

		// 2. ดึงอีเมลชั่วคราวจาก Mail Pool ทันที (Pre-warmed Mail Pool: 0ms)
		checkVPNPause()
		setBotStage(botID, StageTempMail)
		fmt.Printf("[Bot-%d] 📧 กำลังเตรียม Temp Mail...\n", botID)
		var mailAcc *TempMailAccount
		select {
		case mailAcc = <-mailPool:
			fmt.Printf("[Bot-%d] ⚡ [Instant Mail Pool] ได้รับอีเมลทันที: %s (Provider: %s)\n", botID, mailAcc.Email, mailAcc.ProviderURL)
		default:
			mailAcc = createTempMail(botID)
			if mailAcc != nil && mailAcc.Email != "" {
				fmt.Printf("[Bot-%d] ✨ ได้อีเมล: %s (Provider: %s)\n", botID, mailAcc.Email, mailAcc.ProviderURL)
			}
		}

		if mailAcc == nil || mailAcc.Email == "" {
			fmt.Printf("[Bot-%d] ❌ สร้าง Temp Mail ไม่สำเร็จ (กำลังลองใหม่...)\n", botID)
			consecutiveFails++
			if consecutiveFails >= 3 {
				fmt.Printf("[Bot-%d] ⚠️ เน็ต VPN หลุดหรือไม่ตอบสนองติดต่อกัน 3 ครั้ง! สั่งสลับ VPN ตัวใหม่ทันที...\n", botID)
				consecutiveFails = 0
				requestVPNSwitch(botID)
			} else {
				time.Sleep(2 * time.Second)
			}
			continue
		}

		// 3. ขอ Turnstile Token รอบที่ 1
		checkVPNPause()
		setBotStage(botID, StageCaptcha1)
		fmt.Printf("[Bot-%d] 🧩 กำลังขอ Token Captcha รอบแรก...\n", botID)
		turnstileToken := solveTurnstileLocal(TargetURL, botID)
		if turnstileToken == "" {
			fmt.Printf("[Bot-%d] ❌ ไม่ได้ Token รอบแรก (กำลังลองใหม่...)\n", botID)
			consecutiveFails++
			if consecutiveFails >= 3 {
				fmt.Printf("[Bot-%d] ⚠️ เน็ต VPN หลุดหรือไม่ตอบสนองติดต่อกัน 3 ครั้ง! สั่งสลับ VPN ตัวใหม่ทันที...\n", botID)
				consecutiveFails = 0
				requestVPNSwitch(botID)
			} else {
				time.Sleep(2 * time.Second)
			}
			continue
		}
		fmt.Printf("[Bot-%d] ✅ ได้รับ Token รอบแรก!\n", botID)

		// 4. ส่งขอ OTP
		checkVPNPause()
		setBotStage(botID, StageRequestOTP)
		reference := sendOTPRequest(client, mailAcc.Email, turnstileToken, botID)
		if reference == "IP_BLOCKED" {
			fmt.Printf("[Bot-%d] 🔄 โดนบล็อค IP ตั้งแต่ตอนขอ OTP! ระบบกำลังทำการสลับ VPN อัตโนมัติ...\n", botID)
			consecutiveFails = 0
			requestVPNSwitch(botID)
			continue
		} else if reference == "" {
			fmt.Printf("[Bot-%d] ❌ ขอ OTP ไม่สำเร็จ (กำลังลองใหม่...)\n", botID)
			consecutiveFails++
			if consecutiveFails >= 3 {
				fmt.Printf("[Bot-%d] ⚠️ เน็ต VPN หลุดหรือไม่ตอบสนองติดต่อกัน 3 ครั้ง! สั่งสลับ VPN ตัวใหม่ทันที...\n", botID)
				consecutiveFails = 0
				requestVPNSwitch(botID)
			}
			continue
		}

		// 🚀 4.5. สั่งขอ Turnstile Token รอบที่ 2 แบบขนานทันที (Parallel Captcha Pre-fetching)
		captcha2Chan := make(chan string, 1)
		go func() {
			t2 := solveTurnstileLocal(TargetURL, botID)
			captcha2Chan <- t2
		}()

		// 5. อ่าน OTP จากกล่องข้อความ
		setBotStage(botID, StageWaitOTP)
		fmt.Printf("[Bot-%d] ⏳ กำลังรอ OTP จากกล่องข้อความ (รอสูงสุด 15 วินาที)...\n", botID)
		otp := fetchOTP(mailAcc, botID)
		if otp == "" {
			fmt.Printf("[Bot-%d] ❌ รอ OTP นานเกินไป หรือหา OTP ไม่เจอ (กำลังลองใหม่...)\n", botID)
			continue
		}

		// 6. ยืนยัน OTP (Critical Stage)
		setBotStage(botID, StageSubmitOTP)
		fmt.Printf("[Bot-%d] 🚀 กำลังนำ OTP %s ไปยืนยัน (Ref: %s)...\n", botID, otp, reference)
		verifyToken := submitOTP(client, mailAcc.Email, otp, reference, botID)
		if verifyToken == "" {
			fmt.Printf("[Bot-%d] ❌ ยืนยัน OTP ไม่ผ่าน (กำลังลองใหม่...)\n", botID)
			continue
		}

		// 7. ดึง Turnstile Token รอบที่ 2 จาก Background Goroutine ที่แก้เตรียมไว้แล้ว
		setBotStage(botID, StageCaptcha2)
		fmt.Printf("[Bot-%d] 🧩 กำลังดึง Token Captcha รอบสองที่แก้เตรียมไว้ล่วงหน้า...\n", botID)
		turnstileToken2 := <-captcha2Chan
		if turnstileToken2 == "" {
			// Fallback: หากรอบขนานเกิดเหตุขัดข้อง ให้ลองขอใหม่อีก 1 ครั้ง
			fmt.Printf("[Bot-%d] ⚠️ Token รอบสองหลุด กำลังขอใหม่ทันที...\n", botID)
			turnstileToken2 = solveTurnstileLocal(TargetURL, botID)
		}

		if turnstileToken2 == "" {
			fmt.Printf("[Bot-%d] ❌ ไม่ได้ Token รอบสอง (กำลังลองใหม่...)\n", botID)
			consecutiveFails++
			if consecutiveFails >= 3 {
				fmt.Printf("[Bot-%d] ⚠️ เน็ต VPN หลุดหรือไม่ตอบสนองติดต่อกัน 3 ครั้ง! สั่งสลับ VPN ตัวใหม่ทันที...\n", botID)
				consecutiveFails = 0
				requestVPNSwitch(botID)
			} else {
				time.Sleep(2 * time.Second)
			}
			continue
		}
		fmt.Printf("[Bot-%d] ⚡ ได้รับ Token Captcha รอบสองเรียบร้อย!\n", botID)

		// 8. สมัครสมาชิกขั้นสุดท้าย (Critical Stage)
		setBotStage(botID, StageCompleteReg)
		status := completeRegistration(client, mailAcc.Email, verifyToken, turnstileToken2, botID)
		setBotStage(botID, StageIdle)
		if status == 0 {
			consecutiveFails = 0 // รีเซ็ตตัวนับเมื่อสำเร็จ
			fmt.Printf("[Bot-%d] 🎉 สมัครสมาชิกสำเร็จเรียบร้อย! กำลังเริ่มสมัครบัญชีถัดไป...\n", botID)
			continue // กลับไปวนลูปเพื่อสมัครไอดีถัดไป
		} else if status == 1 {
			// ถ้าเจอ 1 แปลว่า IP โดนบล็อค ให้ตัด VPN เก่า แล้วต่อ VPN ใหม่ทันที
			fmt.Printf("[Bot-%d] 🔄 โดนบล็อค IP! ระบบกำลังทำการสลับ VPN อัตโนมัติ...\n", botID)
			consecutiveFails = 0
			requestVPNSwitch(botID)
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

// --- ฟังก์ชันสร้าง Temp Mail รองรับหลาย Provider (mail.gw / mail.tm) ความเร็วสูงและเสถียร ---
func createTempMail(botID int) *TempMailAccount {
	jar := tls_client.NewCookieJar()
	options := []tls_client.HttpClientOption{
		tls_client.WithTimeoutSeconds(10),
		tls_client.WithClientProfile(profiles.Chrome_120),
		tls_client.WithCookieJar(jar),
	}
	client, err := tls_client.NewHttpClient(tls_client.NewNoopLogger(), options...)
	if err != nil {
		return nil
	}

	allProviders := []string{
		"https://api.mail.gw",
		"https://api.mail.tm",
	}

	// กระจายบอทไปยังผู้ให้บริการสลับกัน
	startIndex := 0
	if botID > 0 {
		startIndex = (botID - 1) % len(allProviders)
	} else {
		startIndex = rand.Intn(len(allProviders))
	}

	orderedProviders := []string{
		allProviders[startIndex],
		allProviders[(startIndex+1)%len(allProviders)],
	}

	password := "P@ssw0rd123!"

	for _, providerURL := range orderedProviders {
		// 1. ดึง Domain จากแคช หรือเรียกใหม่ถ้ายังไม่มี
		var domain string
		domainCacheLock.RLock()
		if cachedDomain != "" && cachedDomainProv == providerURL {
			domain = cachedDomain
		}
		domainCacheLock.RUnlock()

		if domain == "" {
			req, err := fhttp.NewRequest("GET", providerURL+"/domains", nil)
			if err != nil {
				continue
			}
			req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
			req.Header.Set("Accept", "application/json")

			resp, err := client.Do(req)
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

			domain = parseDomain(bodyBytes)
			if domain != "" {
				domainCacheLock.Lock()
				cachedDomain = domain
				cachedDomainProv = providerURL
				domainCacheLock.Unlock()
			}
		}

		if domain == "" {
			continue
		}

		// 2. สร้างชื่ออีเมลแบบสุ่มไม่ซ้ำ (ตัวอักษรสุ่ม + ตัวเลขสุ่ม)
		randLetters := randomLetters(5)
		randSeed := rand.Intn(1000000)
		emailPrefix := fmt.Sprintf("%s%d%d%04d", randLetters, botID, time.Now().Unix()%100000, randSeed%9000+1000)
		email := fmt.Sprintf("%s@%s", emailPrefix, domain)

		// 3. ยิงสร้างบัญชีอีเมล
		payload, _ := json.Marshal(map[string]string{"address": email, "password": password})
		req2, err := fhttp.NewRequest("POST", providerURL+"/accounts", bytes.NewBuffer(payload))
		if err != nil {
			continue
		}
		req2.Header.Set("Content-Type", "application/json")
		req2.Header.Set("Accept", "application/json")
		req2.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

		resp2, err := client.Do(req2)
		if err != nil {
			continue
		}
		statusCode := resp2.StatusCode
		resp2.Body.Close()

		if statusCode == 200 || statusCode == 201 {
			// ขอ Auth Token เก็บไว้ล่วงหน้าทันที เพื่อไม่ให้เกิด Rate Limit 429 ในตอน fetchOTP
			authToken := ""
			authPayload, _ := json.Marshal(map[string]string{"address": email, "password": password})
			reqToken, errToken := fhttp.NewRequest("POST", providerURL+"/token", bytes.NewBuffer(authPayload))
			if errToken == nil {
				reqToken.Header.Set("Content-Type", "application/json")
				reqToken.Header.Set("Accept", "application/json")
				reqToken.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
				respToken, errDo := client.Do(reqToken)
				if errDo == nil && respToken != nil && respToken.StatusCode == 200 {
					var authRes struct {
						Token string `json:"token"`
					}
					json.NewDecoder(respToken.Body).Decode(&authRes)
					respToken.Body.Close()
					authToken = authRes.Token
				} else if respToken != nil {
					respToken.Body.Close()
				}
			}

			return &TempMailAccount{
				Email:       email,
				Password:    password,
				ProviderURL: providerURL,
				AuthToken:   authToken,
			}
		}
	}

	return nil
}

// --- ฟังก์ชันแก้ Cloudflare Turnstile ผ่าน Local API Server ---
func solveTurnstileLocal(pageURL string, botID int) string {
	// ⚡ ถ้า VPN กำลังสลับอยู่ ไม่ต้องยิงขอ Captcha ให้ค้าง
	vpnCoordMu.Lock()
	isSwitching := vpnSwitchInProgress
	vpnCoordMu.Unlock()
	if isSwitching {
		fmt.Printf("[Bot-%d] ⏳ VPN กำลังสลับอยู่ ข้ามการขอ Captcha รอบนี้\n", botID)
		return ""
	}

	payloadMap := map[string]string{
		"url": pageURL,
	}
	body, _ := json.Marshal(payloadMap)

	localClient := &http.Client{
		Timeout: 20 * time.Second, // ลด Timeout เหลือ 20 วินาที ป้องกันค้างยาว
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

	var resp *fhttp.Response
	var err error

	for retry := 1; retry <= 3; retry++ {
		req, reqErr := fhttp.NewRequest("POST", apiEndpoint, bytes.NewReader(payload))
		if reqErr != nil {
			fmt.Println("❌ สร้างคำขอยืนยัน OTP ไม่สำเร็จ")
			return ""
		}
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

		resp, err = client.Do(req)
		if err == nil {
			break
		}
		fmt.Printf("[Bot-%d] ⚠️ ยิง API ยืนยัน OTP ขัดข้อง (รอบ %d/3): %v (กำลังลองใหม่...)\n", botID, retry, err)
		time.Sleep(2 * time.Second)
	}

	if err != nil || resp == nil {
		fmt.Printf("[Bot-%d] ❌ ยิง API ยืนยัน OTP ไม่สำเร็จ: %v\n", botID, err)
		return ""
	}
	defer resp.Body.Close()

	// อ่าน Response 
	respBody := new(bytes.Buffer)
	respBody.ReadFrom(resp.Body)
	respStr := respBody.String()
	fmt.Printf("🔍 Verify Response (%d): %s\n", resp.StatusCode, respStr)

	// เช็คว่าโดนบล็อค IP (Rate limit) ตอน Verify OTP หรือไม่
	if strings.Contains(respStr, "\\u0e04\\u0e38\\u0e13\\u0e15\\u0e49\\u0e2d\\u0e07\\u0e23\\u0e2d") || strings.Contains(respStr, "คุณต้องรอ") {
		fmt.Printf("[Bot-%d] ⚠️ ติด Limit IP ตอนยืนยัน OTP! สั่งสลับ VPN อัตโนมัติทันที...\n", botID)
		requestVPNSwitch(botID)
		return ""
	}

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

// --- ฟังก์ชันสุ่มตัวอักษรภาษาอังกฤษ ---
func randomLetters(n int) string {
	const letters = "abcdefghijklmnopqrstuvwxyz"
	b := make([]byte, n)
	for i := range b {
		b[i] = letters[rand.Intn(len(letters))]
	}
	return string(b)
}

func randomUpperLetter() string {
	const upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
	return string(upper[rand.Intn(len(upper))])
}

// 🎲 สุ่มสร้าง ID บัญชีแบบผสมปนกันมั่ว 100% (ตัวใหญ่ + ตัวเล็ก + ตัวเลข คละตำแหน่งกันอิสระ ไม่ฟิกซ์หน้าหลัง)
// เช่น: Ycbn7IVYj9, kFQ2p9U7bQ, Kw5GLmD3sK (ID และ Password ใช้ค่าเดียวกันได้ 100% เพราะครบเงื่อนไขความปลอดภัย)
func generateRandomAccountID(botID int) string {
	const (
		upperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
		lowerChars = "abcdefghijklmnopqrstuvwxyz"
		digitChars = "0123456789"
		allChars   = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
	)

	// บังคับให้มีอย่างน้อย: 1 ตัวใหญ่, 1 ตัวเล็ก, 1 ตัวเลข เพื่อให้ผ่านเงื่อนไข Password เสมอ
	var result []byte
	result = append(result, upperChars[rand.Intn(len(upperChars))])
	result = append(result, lowerChars[rand.Intn(len(lowerChars))])
	result = append(result, digitChars[rand.Intn(len(digitChars))])

	// สุ่มเพิ่มอีก 7-9 ตัวจากทุกตัวอักษรและตัวเลขปนกัน (ความยาวรวม 10-12 ตัว)
	totalLen := 10 + rand.Intn(3)
	for len(result) < totalLen {
		result = append(result, allChars[rand.Intn(len(allChars))])
	}

	// สลับตำแหน่งตัวอักษรทั้งหมดแบบสุ่ม 100% (Shuffle)
	rand.Shuffle(len(result), func(i, j int) {
		result[i], result[j] = result[j], result[i]
	})

	// เพื่อความปลอดภัยของ Form Validation (ห้ามขึ้นต้นด้วยตัวเลข)
	if result[0] >= '0' && result[0] <= '9' {
		letterChars := "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
		result[0] = letterChars[rand.Intn(len(letterChars))]
	}

	return string(result)
}

// --- ฟังก์ชันสมัครสมาชิกขั้นตอนสุดท้าย ---
func completeRegistration(client tls_client.HttpClient, email, verifyToken, turnstileToken string, botID int) int {
	apiEndpoint := "https://core-api.thehof.gg/player/register"

	// สุ่ม Username และ Password (ID = Pass) แบบมั่วไม่ซ้ำ
	username := generateRandomAccountID(botID)

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

	var resp *fhttp.Response
	var err error

	for retry := 1; retry <= 3; retry++ {
		req, reqErr := fhttp.NewRequest("POST", apiEndpoint, bytes.NewReader(payload))
		if reqErr != nil {
			fmt.Println("❌ สร้างคำขอสมัครสมาชิกไม่สำเร็จ")
			return 2
		}
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

		resp, err = client.Do(req)
		if err == nil {
			break
		}
		fmt.Printf("[Bot-%d] ⚠️ ยิง API สมัครสมาชิกขัดข้อง (รอบ %d/3): %v (กำลังลองใหม่...)\n", botID, retry, err)
		time.Sleep(2 * time.Second)
	}

	if err != nil || resp == nil {
		fmt.Printf("[Bot-%d] ❌ ยิง API สมัครสมาชิกไม่สำเร็จ: %v\n", botID, err)
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
		fmt.Printf("[ACC_SUCCESS] %s\n", username)
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

	var authToken string
	if acc.AuthToken != "" {
		authToken = acc.AuthToken
	} else {
		authPayload, _ := json.Marshal(map[string]string{"address": acc.Email, "password": acc.Password})
		for retry := 0; retry < 3; retry++ {
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
				time.Sleep(1 * time.Second)
				continue
			}

			if resp.StatusCode == 200 {
				var authRes struct {
					Token string `json:"token"`
				}
				json.NewDecoder(resp.Body).Decode(&authRes)
				resp.Body.Close()
				authToken = authRes.Token
				break
			}

			resp.Body.Close()
			if resp.StatusCode == 429 {
				time.Sleep(1500 * time.Millisecond) // หน่วงเวลากัน Rate limit
				continue
			}
			time.Sleep(1 * time.Second)
		}
	}

	if authToken == "" {
		fmt.Printf("[Bot-%d] ❌ ล็อกอิน Mail ไม่ผ่าน (อาจติด Rate Limit)\n", botID)
		return ""
	}

	// วนลูปเช็คอีเมลเข้า (เช็คแบบความเร็วสูง 350ms ในช่วงแรก สูงสุด 12 รอบ ~7.5 วินาที)
	for i := 0; i < 12; i++ {
		// ⚡ ถ้ามีบอทตัวอื่นสั่งสลับ VPN แล้ว → ให้ยกเลิกการรอ OTP ทันที 0ms เพื่อเตรียมรับ IP ใหม่
		vpnCoordMu.Lock()
		isSwitching := vpnSwitchInProgress
		vpnCoordMu.Unlock()
		if isSwitching {
			fmt.Printf("[Bot-%d] 🛑 VPN กำลังสลับระบบ ยกเลิกการรอ OTP เพื่อเริ่มรอบใหม่ทันที\n", botID)
			return ""
		}

		if i < 6 {
			time.Sleep(350 * time.Millisecond) // เร็วพิเศษช่วง 2 วินาทีแรก
		} else {
			time.Sleep(600 * time.Millisecond)
		}

		req, err := http.NewRequest("GET", acc.ProviderURL+"/messages", nil)
		if err != nil {
			continue
		}
		req.Header.Set("Authorization", "Bearer "+authToken)
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
			subject := messages[0].Subject
			fmt.Printf("[Bot-%d] 📬 มีอีเมลเข้าแล้ว! หัวข้อ: %s\n", botID, subject)
			if intro != "" {
				fmt.Printf("[Bot-%d] 📝 เนื้อหาเบื้องต้น: %s\n", botID, intro)
			}

			// ใช้ Regex ดึงตัวเลข 6 หลักออกมาจากข้อความ (Intro หรือ Subject) ทันที
			re := regexp.MustCompile(`\b\d{6}\b`)
			otp := re.FindString(intro)
			if otp == "" || otp == "000000" {
				otp = re.FindString(subject)
			}
			if otp != "" && otp != "000000" {
				fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP สำเร็จ: %s\n", botID, otp)
				return otp
			}

			// ถ้า Intro ว่างเปล่า หรือหาไม่เจอ ให้ลองดึงเนื้อหาเต็มของอีเมลมาเช็ค
			// ⚡ Inner Retry Loop: ยิง /messages/{id} ซ้ำสูงสุด 5 ครั้ง (หน่วง 500ms)
			// แทนการกลับไปวนลูปนอกซึ่งเสียเวลากับ /messages list อีกรอบ
			fmt.Printf("[Bot-%d] 🔍 กำลังดึงเนื้อหาเต็มของอีเมลมาตรวจสอบ...\n", botID)
			for retryBody := 0; retryBody < 5; retryBody++ {
				if retryBody > 0 {
					time.Sleep(500 * time.Millisecond)
					fmt.Printf("[Bot-%d] 🔄 ดึงเนื้อหาเต็มซ้ำ (ครั้งที่ %d/5)...\n", botID, retryBody+1)
				}

				reqFull, _ := http.NewRequest("GET", acc.ProviderURL+"/messages/"+msgId, nil)
				reqFull.Header.Set("Authorization", "Bearer "+authToken)
				reqFull.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
				reqFull.Header.Set("Accept", "application/json")

				respFull, errFull := httpClient.Do(reqFull)
				if errFull != nil || respFull == nil || respFull.StatusCode != 200 {
					if respFull != nil {
						respFull.Body.Close()
					}
					continue
				}

				rawBytes, _ := io.ReadAll(respFull.Body)
				respFull.Body.Close()

				// ลองดึง intro จาก response ของ individual message ด้วย (mail.gw อาจเติมทีหลัง)
				var msgFull struct {
					Text  string      `json:"text"`
					Html  interface{} `json:"html"`
					Intro string      `json:"intro"`
				}
				json.Unmarshal(rawBytes, &msgFull)

				// ถ้า intro มาแล้วใน individual response → ลอง regex ทันที
				if msgFull.Intro != "" {
					otpFromIntro := re.FindString(msgFull.Intro)
					if otpFromIntro != "" && otpFromIntro != "000000" {
						fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP จาก Intro (retry) สำเร็จ: %s\n", botID, otpFromIntro)
						return otpFromIntro
					}
				}

				// รวม Text และ Html
				fullContent := msgFull.Text
				switch h := msgFull.Html.(type) {
				case string:
					fullContent += " " + h
				case []interface{}:
					for _, item := range h {
						if str, ok := item.(string); ok {
							fullContent += " " + str
						}
					}
				}

				// ถ้ายังว่างเปล่า ให้ใช้ Raw Response Body ทั้งหมด
				if fullContent == "" {
					fullContent = string(rawBytes)
				}

				// ถ้า fullContent ยังสั้นเกินไป (< 30 ตัวอักษร) แสดงว่า mail.gw ยังไม่ process เสร็จ → retry
				if len(strings.TrimSpace(fullContent)) < 30 {
					continue
				}

				// ลบเนื้อหาใน <style> กรอง CSS ขยะทิ้ง (ป้องกันการจับ #000000)
				reStyle := regexp.MustCompile(`(?is)<style[^>]*>.*?</style>`)
				fullContent = reStyle.ReplaceAllString(fullContent, " ")

				// ลบ HTML Tags และช่องว่างขยะ
				reTags := regexp.MustCompile(`<[^>]*>`)
				cleanContent := reTags.ReplaceAllString(fullContent, " ")

				// 1. ลองหาเจาะจงเฉพาะท่อนที่มีคำว่า Hall of Fame หรือ Reference หรือ OTP
				reContext := regexp.MustCompile(`(?i)(?:Hall of Fame|ผู้เล่น|OTP|verification|code)[^\d]{0,40}(\d{6})`)
				ctxMatch := reContext.FindStringSubmatch(cleanContent)
				if len(ctxMatch) > 1 && ctxMatch[1] != "000000" {
					fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP จากบริบทเนื้อหาสำเร็จ: %s\n", botID, ctxMatch[1])
					return ctxMatch[1]
				}

				// 2. ลองหาในรูปแบบปกติติดกัน 6 ตัว (ข้าม 000000 ที่อาจเป็นเศษ CSS)
				reSixDigits := regexp.MustCompile(`\b\d{6}\b`)
				matches := reSixDigits.FindAllString(cleanContent, -1)
				for _, match := range matches {
					if match != "000000" {
						fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP จากเนื้อหาเต็มสำเร็จ: %s\n", botID, match)
						return match
					}
				}

				// 3. ถ้าหาแบบปกติไม่เจอ ให้ดูกลุ่มตัวเลข 6 ตัว
				reGroups := regexp.MustCompile(`\d+`)
				numberGroups := reGroups.FindAllString(cleanContent, -1)
				for _, numStr := range numberGroups {
					if len(numStr) == 6 && numStr != "000000" {
						fmt.Printf("[Bot-%d] 🎯 สกัดรหัส OTP จากกลุ่มตัวเลขสำเร็จ: %s\n", botID, numStr)
						return numStr
					}
				}

				// 4. ถ้ายาวๆ แล้วมีเว้นวรรค เราเอาเฉพาะตัวเลขใน cleanContent มาต่อกัน
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
			} // end inner retry loop

			// ถ้าลอง 5 ครั้งแล้วยังดึงไม่ได้ ให้กลับไปวนลูปนอกตรวจซ้ำ
			fmt.Printf("[Bot-%d] ⏳ มีอีเมลเข้าแล้วแต่เนื้อหายังโหลดไม่สมบูรณ์ (กำลังตรวจซ้ำ...)\n", botID)
		}
		fmt.Printf("[Bot-%d] 🔄 กำลังรออีเมล OTP เข้า... (รอบที่ %d)\n", botID, i+1)
	}
	fmt.Printf("[Bot-%d] ❌ หมดเวลา: ไม่พบอีเมล OTP ส่งเข้ามา\n", botID)
	return ""
}

// ==========================================
// ส่วนของการควบคุม VPN (VPN Gate + OpenVPN ความเร็วสูง + Health Check)
// ==========================================

// โครงสร้างข้อมูลสำหรับอ่านไฟล์ CSV จาก VPN Gate
type VpnServer struct {
	HostName string
	IP       string
	Score    int
	Ping     int
	Speed    int64 // in bps
	Country  string
	Config   string // Base64 เข้ารหัส OpenVPN Config
}

var (
	currentVpnCmd  *exec.Cmd
	currentKnownIP string
	initialHomeIP  string
)

// --- ฟังก์ชันทดสอบว่าเน็ตวิ่งจริงหรือไม่ผ่าน VPN (ใช้ HTTP เพื่อความเร็ว ไม่ต้องรอ TLS) ---
func checkInternetConnection() (string, bool) {
	// ⚠️ สำคัญ: DisableKeepAlives = true → บังคับสร้าง TCP connection ใหม่ทุกครั้ง
	// ป้องกัน Go ใช้ connection เก่าที่วิ่งผ่าน route เดิม (ก่อน VPN สลับ)
	transport := &http.Transport{
		DisableKeepAlives: true,
	}
	testClient := &http.Client{
		Timeout:   5 * time.Second,
		Transport: transport,
	}

	// ใช้ HTTP แทน HTTPS เพื่อไม่ต้องรอ TLS handshake ตอน VPN กำลังสลับ
	endpoints := []string{
		"http://api.ipify.org",
		"http://icanhazip.com",
		"http://ifconfig.me/ip",
		"http://checkip.amazonaws.com",
		"https://api.ipify.org",
	}

	for _, url := range endpoints {
		resp, err := testClient.Get(url)
		if err != nil {
			continue
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		ip := strings.TrimSpace(string(body))
		// ตรวจสอบว่าเป็น IP จริงๆ (มีจุด เช่น 1.2.3.4)
		if ip != "" && strings.Contains(ip, ".") && len(ip) <= 15 {
			return ip, true
		}
	}
	return "", false
}

// --- ฟังก์ชันรันคำสั่งเบื้องหลังแบบซ่อนหน้าต่าง 100% ไม่แย่งโฟกัส ไม่พับหน้าต่าง ---
func runHidden(name string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	return cmd.Run()
}

// --- ฟังก์ชันตัดการเชื่อมต่อ VPN ---
func disconnectVPN() {
	fmt.Println("🔌 กำลังตัดการเชื่อมต่อ VPN ปัจจุบัน...")
	if currentVpnCmd != nil && currentVpnCmd.Process != nil {
		currentVpnCmd.Process.Kill()
		currentVpnCmd.Wait()
		currentVpnCmd = nil
	}
	// 1. ปิด openvpn.exe ทั้งหมดแบบเงียบๆ ไม่เด้งหน้าต่าง ไม่แย่งโฟกัส
	runHidden("taskkill", "/F", "/IM", "openvpn.exe")
	// 2. ลบ route ชั่วคราวของ OpenVPN
	runHidden("route", "delete", "0.0.0.0", "mask", "128.0.0.0")
	runHidden("route", "delete", "128.0.0.0", "mask", "128.0.0.0")
	// 3. ล้างแคช DNS ทันที
	runHidden("ipconfig", "/flushdns")
	time.Sleep(1 * time.Second)
}

// --- ระบบ VPN Coordinator: ป้องกันบอทแย่งกันสลับ VPN ---
// บอทตัวแรกที่เจอ IP_BLOCKED จะเป็นคนทำหน้าที่สลับ VPN
// บอทตัวอื่นๆ จะรอจนกว่าการสลับจะเสร็จ แล้วเริ่มทำงานต่อด้วย IP ใหม่
func requestVPNSwitch(botID int) {
	setBotStage(botID, StageIdle)

	vpnCoordMu.Lock()
	if vpnSwitchInProgress {
		// มีบอทตัวอื่นกำลังสลับ VPN อยู่แล้ว → รอให้เสร็จก่อน
		waitCh := vpnSwitchDone
		vpnCoordMu.Unlock()
		fmt.Printf("[Bot-%d] ⏳ รอบอทตัวอื่นสลับ VPN ให้เสร็จก่อน...\n", botID)
		<-waitCh // รอจนกว่า channel จะถูกปิด (= VPN สลับเสร็จ)
		fmt.Printf("[Bot-%d] ✅ VPN สลับเสร็จแล้ว! เริ่มทำงานต่อด้วย IP ใหม่\n", botID)
		return
	}

	// เช็คว่าถ้าเพิ่งสลับ VPN เสร็จไปไม่ถึง 6 วินาที แปลว่า Error นี้มาจากคำสั่งเก่าตกค้าง (In-flight request)
	if time.Since(lastVPNChange) < 6*time.Second && currentKnownIP != "" && currentKnownIP != initialHomeIP {
		vpnCoordMu.Unlock()
		fmt.Printf("[Bot-%d] 🔄 (คำสั่งตกค้างจาก IP เดิม) VPN เพิ่งสลับเป็น IP ใหม่ (%s) แล้ว เริ่มทำงานต่อได้ทันที!\n", botID, currentKnownIP)
		return
	}

	// เราเป็นตัวแรก → รับหน้าที่สลับ VPN
	vpnSwitchInProgress = true
	vpnSwitchDone = make(chan struct{})
	vpnCoordMu.Unlock()

	fmt.Printf("[Bot-%d] 🔧 รับหน้าที่เป็นผู้สลับ VPN ให้ทุกตัว...\n", botID)

	// 🛡️ Grace Period Barrier: ถ้ามีบอทอื่นกำลังยืนยัน OTP หรือสมัครขั้นตอนสุดท้าย ให้รอสั้นๆ ให้จบงานก่อนตัดเน็ต
	if critical := getCriticalBots(botID); len(critical) > 0 {
		fmt.Printf("⏳ [VPN Coordinator] ตรวจพบบอท %v กำลังส่งข้อมูลขั้นตอนสำคัญ (OTP/Final Register) -> รอ Grace Period สูงสุด 4.5 วิ...\n", critical)
		graceDeadline := time.Now().Add(4500 * time.Millisecond)
		for time.Now().Before(graceDeadline) {
			time.Sleep(200 * time.Millisecond)
			if !isAnyBotInCriticalStage(botID) {
				fmt.Println("✨ [VPN Coordinator] บอทขั้นตอนสำคัญทำงานเสร็จสิ้นแล้ว! เริ่มตัดสลับ VPN ได้อย่างปลอดภัย")
				break
			}
		}
	}

	connectRandomVPN()

	// สลับเสร็จ → แจ้งบอทตัวอื่นทั้งหมดให้เริ่มทำงานต่อ
	vpnCoordMu.Lock()
	vpnSwitchInProgress = false
	close(vpnSwitchDone) // ปลุกบอททุกตัวที่รออยู่
	vpnCoordMu.Unlock()
}

// --- ฟังก์ชันดาวน์โหลดและแคชรายชื่อ VPN คุณภาพสูงจากทั่วโลก (เอเชีย/อเมริกา/ยุโรป) ---
func fetchAndCacheVPNServers(force bool) {
	cachedVpnLock.Lock()
	defer cachedVpnLock.Unlock()

	if !force && len(cachedVpnServers) > 0 && time.Since(cachedVpnTime) < 30*time.Minute {
		return
	}

	fmt.Println("🌍 กำลังดาวน์โหลดและอัปเดตลิสต์ VPN ความเร็วสูง (ทั่วโลก / เอเชีย / อเมริกา / ยุโรป)...")
	vpnClient := &http.Client{Timeout: 15 * time.Second}
	resp, err := vpnClient.Get("http://www.vpngate.net/api/iphone/")
	if err != nil {
		fmt.Printf("⚠️ ดาวน์โหลดลิสต์ VPN ไม่สำเร็จ: %v\n", err)
		return
	}
	defer resp.Body.Close()

	bodyBytes, err := io.ReadAll(resp.Body)
	if err != nil {
		return
	}

	lines := strings.Split(string(bodyBytes), "\n")
	var list []VpnServer
	seenIP := make(map[string]bool)

	// 1. คัดเลือกเซิร์ฟเวอร์ความเร็วสูง (เน็ตแรง >= 8 Mbps, Ping <= 130ms)
	for i := 2; i < len(lines); i++ {
		cols := strings.Split(lines[i], ",")
		if len(cols) > 14 {
			country := strings.TrimSpace(cols[5])
			ip := strings.TrimSpace(cols[1])
			if country == "" || ip == "" || seenIP[ip] {
				continue
			}

			var score, ping int
			var speed int64
			fmt.Sscanf(cols[2], "%d", &score)
			fmt.Sscanf(cols[3], "%d", &ping)
			fmt.Sscanf(cols[4], "%d", &speed)

			// เกณฑ์คุณภาพ: ปิงต่ำ หรือสปีดสูงมาก
			isHighQuality := false
			if ping > 0 && ping <= 90 && speed >= 6*1000*1000 {
				isHighQuality = true
			} else if ping > 0 && ping <= 140 && speed >= 12*1000*1000 {
				isHighQuality = true
			}

			if isHighQuality {
				seenIP[ip] = true
				list = append(list, VpnServer{
					HostName: cols[0],
					IP:       ip,
					Score:    score,
					Ping:     ping,
					Speed:    speed,
					Country:  country,
					Config:   cols[14],
				})
			}
		}
	}

	// 2. เติมเซิร์ฟเวอร์สำรองให้พูลแน่น (Ping <= 200ms, Speed >= 4 Mbps)
	if len(list) < 80 {
		for i := 2; i < len(lines); i++ {
			cols := strings.Split(lines[i], ",")
			if len(cols) > 14 {
				country := strings.TrimSpace(cols[5])
				ip := strings.TrimSpace(cols[1])
				if country == "" || ip == "" || seenIP[ip] {
					continue
				}

				var score, ping int
				var speed int64
				fmt.Sscanf(cols[2], "%d", &score)
				fmt.Sscanf(cols[3], "%d", &ping)
				fmt.Sscanf(cols[4], "%d", &speed)

				if ping > 0 && ping <= 200 && speed >= 4*1000*1000 {
					seenIP[ip] = true
					list = append(list, VpnServer{
						HostName: cols[0],
						IP:       ip,
						Score:    score,
						Ping:     ping,
						Speed:    speed,
						Country:  country,
						Config:   cols[14],
					})
				}
			}
		}
	}

	if len(list) > 0 {
		// เรียงตาม Ping ต่ำสุดก่อน (ทำให้บอทเลือกโซนเอเชียก่อนอัตโนมัติ)
		sort.Slice(list, func(i, j int) bool {
			return list[i].Ping < list[j].Ping
		})
		cachedVpnServers = list
		cachedVpnTime = time.Now()
		fmt.Printf("⚡ แคชลิสต์ VPN คุณภาพสูงพิเศษเรียบร้อย (%d เซิร์ฟเวอร์พร้อมใช้งานทั่วโลก)\n", len(list))
	}
}

// --- ฟังก์ชันเชื่อมต่อ VPN แบบสุ่มและตรวจสอบคุณภาพ ---
func connectRandomVPN() bool {
	vpnLock.Lock()
	defer vpnLock.Unlock()

	// บันทึก Home IP ดั้งเดิมไว้ครั้งแรก
	if initialHomeIP == "" {
		initialHomeIP, _ = checkInternetConnection()
		if initialHomeIP != "" {
			fmt.Printf("🏠 IP ประจำเครื่องดั้งเดิม (Local IP): %s\n", initialHomeIP)
		}
	}

	// 1. ตรวจสอบว่ามีไฟล์ .ovpn ในโฟลเดอร์ vpn_configs/ หรือไม่
	customConfigs, _ := filepath.Glob("vpn_configs/*.ovpn")
	if len(customConfigs) > 0 {
		absAuthPath, _ := filepath.Abs("vpn_auth.txt")
		hasAuth := false
		if _, err := os.Stat(absAuthPath); err == nil {
			hasAuth = true
		}

		rand.Seed(time.Now().UnixNano())
		rand.Shuffle(len(customConfigs), func(i, j int) {
			customConfigs[i], customConfigs[j] = customConfigs[j], customConfigs[i]
		})

		for attempt := 1; attempt <= 6 && attempt <= len(customConfigs); attempt++ {
			disconnectVPN()
			chosen := customConfigs[attempt-1]
			fmt.Printf("📁 [ProtonVPN %d] กำลังเชื่อมต่อ: %s...\n", attempt, filepath.Base(chosen))

			absCfgPath, _ := filepath.Abs(chosen)
			var cmd *exec.Cmd
			if hasAuth {
				cmd = exec.Command("C:\\Program Files\\OpenVPN\\bin\\openvpn.exe", "--config", absCfgPath, "--auth-user-pass", absAuthPath)
			} else {
				cmd = exec.Command("C:\\Program Files\\OpenVPN\\bin\\openvpn.exe", "--config", absCfgPath)
			}
			cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}

			if err := cmd.Start(); err == nil {
				currentVpnCmd = cmd
				runHidden("ipconfig", "/flushdns")
				time.Sleep(2 * time.Second)
				for h := 0; h < 6; h++ {
					time.Sleep(1000 * time.Millisecond)
					ip, ok := checkInternetConnection()
					if h%2 == 0 {
						if ok {
							fmt.Printf("🔍 [Health Check %d/6] ตรวจพบ IP: %s (IP บ้าน: %s)\n", h+1, ip, initialHomeIP)
						} else {
							fmt.Printf("🔍 [Health Check %d/6] ❌ เน็ตกำลังเชื่อมต่อ...\n", h+1)
						}
					}
					if ok && ip != "" && (initialHomeIP == "" || ip != initialHomeIP) {
						// ⚡ Live Benchmark
						fmt.Printf("⚡ กำลังทดสอบความเร็วจริงของ VPN (Live Latency Benchmark)...\n")
						benchClient := &http.Client{Timeout: 2000 * time.Millisecond}
						benchStart := time.Now()
						benchResp, benchErr := benchClient.Get(TargetURL)
						benchLatency := time.Since(benchStart).Milliseconds()

						if benchErr != nil || benchLatency > 1500 {
							if benchErr != nil {
								fmt.Printf("❌ [VPN Benchmark Failed] เซิร์ฟเวอร์นี้ไม่ตอบสนองต่อเว็บเป้าหมาย (Error: %v) -> สั่งตัดทิ้งและสลับตัวใหม่ทันที...\n", benchErr)
							} else {
								benchResp.Body.Close()
								fmt.Printf("⚠️ [VPN Benchmark Slow] เซิร์ฟเวอร์นี้ช้าเกินไป (Latency: %d ms > 1500 ms) -> สั่งตัดทิ้งและสลับตัวใหม่ทันที...\n", benchLatency)
							}
							disconnectVPN()
							break
						}
						benchResp.Body.Close()

						lastVPNChange = time.Now()
						currentKnownIP = ip
						fmt.Printf("✅ [ProtonVPN Connected & Verified] สลับ IP สำเร็จ! ได้รับ IP ใหม่: %s | ปิงสดเว็บเป้าหมาย: %d ms\n", ip, benchLatency)
						return true
					}
				}
			}
			disconnectVPN()
		}
	}

	// 2. ใช้ VPN Gate แบบมีแคชความเร็วสูง
	fetchAndCacheVPNServers(false)

	globalBlockedMu.Lock()
	if currentKnownIP != "" && currentKnownIP != initialHomeIP {
		globalBlockedIPs[currentKnownIP] = time.Now()
	}
	// ล้าง IP ที่ติดแบล็กลิสต์เกิน 45 นาทีออก
	for ip, t := range globalBlockedIPs {
		if time.Since(t) > 45*time.Minute {
			delete(globalBlockedIPs, ip)
		}
	}
	// คัดลอกรายการแบล็กลิสต์ปัจจุบัน
	activeBlocked := make(map[string]bool)
	for ip := range globalBlockedIPs {
		activeBlocked[ip] = true
	}
	globalBlockedMu.Unlock()

	for attempt := 1; attempt <= 12; attempt++ {
		disconnectVPN()

		cachedVpnLock.Lock()
		var available []VpnServer
		for _, s := range cachedVpnServers {
			if !activeBlocked[s.IP] {
				available = append(available, s)
			}
		}
		cachedVpnLock.Unlock()

		// 🛡️ ป้องกันบั๊กแคชหมด: ถ้ารายการว่าง ให้ล้าง Blacklist ทันที และดาวน์โหลดชุดใหม่มาเชื่อมต่อทันที
		if len(available) == 0 {
			fmt.Println("⚠️ เซิร์ฟเวอร์ในพูลถูกใช้งานครบแล้ว! ทำการรีเซ็ต Blacklist และดาวน์โหลดพูล VPN ชุดใหม่อัตโนมัติทันที...")
			globalBlockedMu.Lock()
			globalBlockedIPs = make(map[string]time.Time)
			activeBlocked = make(map[string]bool)
			globalBlockedMu.Unlock()

			fetchAndCacheVPNServers(true)

			cachedVpnLock.Lock()
			for _, s := range cachedVpnServers {
				if !activeBlocked[s.IP] {
					available = append(available, s)
				}
			}
			cachedVpnLock.Unlock()

			if len(available) == 0 {
				time.Sleep(2 * time.Second)
				continue
			}
		}

		// 🎯 สุ่มเลือกจาก Top 25 เซิร์ฟเวอร์ที่เร็วและปิงต่ำสุด
		topLimit := 25
		if len(available) < topLimit {
			topLimit = len(available)
		}
		rand.Seed(time.Now().UnixNano())
		selected := available[rand.Intn(topLimit)]
		activeBlocked[selected.IP] = true // บันทึกไว้ไม่ให้สุ่มซ้ำในรอบนี้

		globalBlockedMu.Lock()
		globalBlockedIPs[selected.IP] = time.Now() // จำไว้ใน global blacklist 45 นาที
		globalBlockedMu.Unlock()

		speedMbps := float64(selected.Speed) / (1000 * 1000)
		fmt.Printf("🎯 [Attempt %d/12] เลือก VPN: %s (IP: %s, Ping: %d ms, Speed: %.1f Mbps)\n", attempt, selected.Country, selected.IP, selected.Ping, speedMbps)

		configBytes, err := base64.StdEncoding.DecodeString(selected.Config)
		if err != nil {
			continue
		}

		configStr := string(configBytes)
		if !strings.Contains(configStr, "redirect-gateway") {
			configStr += "\nredirect-gateway def1\n"
		}
		if !strings.Contains(configStr, "route-delay") {
			configStr += "\nroute-delay 2\n"
		}

		configFileName := fmt.Sprintf("vpn_%d.ovpn", time.Now().UnixNano())
		err = os.WriteFile(configFileName, []byte(configStr), 0644)
		if err != nil {
			continue
		}

		fmt.Println("🚀 กำลังเชื่อมต่อ OpenVPN และรอระบบ Reroute IP ใหม่อย่างสมบูรณ์...")

		absCfgPath, _ := filepath.Abs(configFileName)
		cmd := exec.Command("C:\\Program Files\\OpenVPN\\bin\\openvpn.exe", "--config", absCfgPath)
		cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
		err = cmd.Start()
		if err != nil {
			fmt.Printf("❌ ไม่สามารถรัน OpenVPN ได้: %v\n", err)
			os.Remove(configFileName)
			continue
		}
		currentVpnCmd = cmd

		go func(name string) {
			time.Sleep(30 * time.Second)
			os.Remove(name)
		}(configFileName)

		connected := false
		var newIP string
		startTime := time.Now()

		runHidden("ipconfig", "/flushdns")
		time.Sleep(2 * time.Second)

		// ⚡ เช็ค IP แบบรวดเร็ว 6 ครั้ง (ครั้งละ 1 วินาที ไม่เสียเวลารอนาน)
		for h := 0; h < 6; h++ {
			time.Sleep(1000 * time.Millisecond)
			ip, ok := checkInternetConnection()
			if h%2 == 0 {
				if ok {
					fmt.Printf("🔍 [Health Check %d/6] ตรวจพบ IP: %s (IP บ้าน: %s)\n", h+1, ip, initialHomeIP)
				} else {
					fmt.Printf("🔍 [Health Check %d/6] ❌ เช็ค IP ไม่สำเร็จ (เน็ตยังไม่พร้อม)\n", h+1)
				}
			}
			if ok && ip != "" && (initialHomeIP == "" || ip != initialHomeIP) {
				newIP = ip
				connected = true
				break
			}
		}

		if connected {
			// ⚡ Live Benchmark: ทดสอบยิงไปที่เว็บเป้าหมายตรงๆ เพื่อวัด Latency สด
			fmt.Printf("⚡ กำลังทดสอบความเร็วจริงของ VPN (Live Latency Benchmark)...\n")
			benchClient := &http.Client{Timeout: 2000 * time.Millisecond}
			benchStart := time.Now()
			benchResp, benchErr := benchClient.Get(TargetURL)
			benchLatency := time.Since(benchStart).Milliseconds()

			if benchErr != nil || benchLatency > 1500 {
				if benchErr != nil {
					fmt.Printf("❌ [VPN Benchmark Failed] เซิร์ฟเวอร์นี้ไม่ตอบสนองต่อเว็บเป้าหมาย (Error: %v) -> สั่งตัดทิ้งและสลับตัวใหม่ทันที...\n", benchErr)
				} else {
					benchResp.Body.Close()
					fmt.Printf("⚠️ [VPN Benchmark Slow] เซิร์ฟเวอร์นี้ช้าเกินไป (Latency: %d ms > 1500 ms) -> สั่งตัดทิ้งและสลับตัวใหม่ทันที...\n", benchLatency)
				}
				disconnectVPN()
				continue
			}
			benchResp.Body.Close()

			lastVPNChange = time.Now()
			currentKnownIP = newIP
			fmt.Printf("✅ [VPN Connected & Verified] สลับ IP สำเร็จ! ได้รับ IP ใหม่: %s | ปิงสดเว็บเป้าหมาย: %d ms (ใช้เวลาเชื่อมต่อ %.1f วินาที)\n", newIP, benchLatency, time.Since(startTime).Seconds())

			// ⚡ รีเฟรช Warm Browser Pool หลังสลับ VPN สำเร็จ
			// ป้องกัน Cloudflare Turnstile block เพราะ session IP เก่าไม่ตรงกับ IP ใหม่
			go func() {
				refreshClient := &http.Client{Timeout: 10 * time.Second}
				refreshReq, _ := http.NewRequest("POST", "http://127.0.0.1:5000/refresh-pool", nil)
				if refreshReq != nil {
					resp, err := refreshClient.Do(refreshReq)
					if err != nil {
						fmt.Printf("⚠️ รีเฟรช Captcha Pool ไม่สำเร็จ: %v\n", err)
					} else {
						resp.Body.Close()
						fmt.Println("🔄 สั่งรีเฟรช Warm Browser Pool หลังสลับ VPN เรียบร้อย")
					}
				}
			}()

			return true
		} else {
			fmt.Println("⚠️ VPN นี้เชื่อมต่อไม่สำเร็จ หรือ IP ยังไม่ยอมเปลี่ยน! กำลังตัดการเชื่อมต่อและสลับไปใช้เซิร์ฟเวอร์ตัวถัดไป...")
			disconnectVPN()
		}
	}

	fmt.Println("❌ ไม่สามารถเชื่อมต่อ VPN ที่ใช้งานได้หลังลองครบ 12 ครั้ง")
	return false
}