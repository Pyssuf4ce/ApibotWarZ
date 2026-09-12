package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"
)

const (
	TargetURL = "https://passport.thehof.gg/hall-of-fame-web/login"
	ServerURL = "http://127.0.0.1:5000/login"
)

var AppVersion = "1.0.0"

type Account struct {
	Username string
	Password string
}

type LoginRequest struct {
	URL      string `json:"url"`
	Username string `json:"username"`
	Password string `json:"password"`
	WorkerID int    `json:"worker_id,omitempty"`
}

type LoginResponse struct {
	Status       string `json:"status"`
	Username     string `json:"username"`
	Time         string `json:"time,omitempty"`
	Reason       string `json:"reason,omitempty"`
	Message      string `json:"message,omitempty"`
	Detail       string `json:"detail,omitempty"`
	RedeemStatus string `json:"redeem_status,omitempty"`
	Items        string `json:"items,omitempty"`
	IsLimit      bool   `json:"is_limit,omitempty"`
}

type DirectRedeemReq struct {
	Username string `json:"username"`
	EventID  string `json:"event_id,omitempty"`
}

type DirectRedeemResp struct {
	Status       string `json:"status"`
	Username     string `json:"username"`
	RedeemStatus string `json:"redeem_status,omitempty"`
	Items        string `json:"items,omitempty"`
	Detail       string `json:"detail,omitempty"`
	Message      string `json:"message,omitempty"`
	TimeMS       int    `json:"time_ms,omitempty"`
}

type LoginResult struct {
	Success    bool
	IsLimit    bool
	IsBadPwd   bool
	ElapsedStr string
	Reason     string
	Detail     string
}

type PrepareBatchReq struct {
	Count        int `json:"count"`
	BatchNum     int `json:"batch_num"`
	TotalBatches int `json:"total_batches"`
}

type PrepareBatchResp struct {
	Status         string `json:"status"`
	ReadyCount     int    `json:"ready_count"`
	RequestedCount int    `json:"requested_count"`
}

var (
	MaxConcurrent = 5
	allAccounts   []Account
	processedMap  = make(map[string]bool)
	retryCountMap = make(map[string]int)
	limitCountMap = make(map[string]int)
	accountLock   sync.Mutex
)

func waitForServerConnection() {
	client := &http.Client{Timeout: 2 * time.Second}
	healthURL := "http://127.0.0.1:5000/health"

	for {
		resp, err := client.Get(healthURL)
		if err == nil && resp.StatusCode == 200 {
			resp.Body.Close()
			return
		}
		time.Sleep(500 * time.Millisecond)
	}
}

func checkCachedToken(username string) (bool, string, string) {
	client := &http.Client{Timeout: 8 * time.Second}
	reqBody := DirectRedeemReq{Username: username}
	jsonBytes, _ := json.Marshal(reqBody)

	resp, err := client.Post("http://127.0.0.1:5000/direct_redeem", "application/json", bytes.NewBuffer(jsonBytes))
	if err != nil {
		return false, "", ""
	}
	defer resp.Body.Close()

	if resp.StatusCode != 200 {
		return false, "", ""
	}

	body, _ := io.ReadAll(resp.Body)
	var res DirectRedeemResp
	json.Unmarshal(body, &res)

	if res.Status == "success" {
		elapsedStr := fmt.Sprintf("%.2fs", float64(res.TimeMS)/1000.0)
		if res.TimeMS == 0 {
			elapsedStr = "0.1s"
		}
		detail := res.Detail
		if res.RedeemStatus == "success" {
			detail = fmt.Sprintf("🎁 รับรางวัลสำเร็จ: %s", res.Items)
		} else if res.RedeemStatus == "already_claimed" {
			detail = "🟡 รับรางวัลไปแล้วก่อนหน้า"
		}
		return true, elapsedStr, detail
	}

	return false, "", ""
}

func runCachedTokensPass() int {
	accountLock.Lock()
	total := len(allAccounts)
	accountLock.Unlock()

	if total == 0 {
		return 0
	}

	fmt.Printf("\n⚡ [Phase 1: Session Cache Check] กำลังตรวจสอบ Session Token ในคลัง เพื่อข้ามการเปิดเบราว์เซอร์...\n")

	cachedSuccessCount := 0
	var wg sync.WaitGroup
	concurrency := 10
	if MaxConcurrent > 10 {
		concurrency = MaxConcurrent
	}
	sem := make(chan struct{}, concurrency)

	for _, acc := range allAccounts {
		accountLock.Lock()
		if processedMap[acc.Username] {
			accountLock.Unlock()
			continue
		}
		accountLock.Unlock()

		wg.Add(1)
		go func(targetAcc Account) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()

			ok, elapsedStr, detail := checkCachedToken(targetAcc.Username)
			if ok {
				accountLock.Lock()
				processedMap[targetAcc.Username] = true
				cachedSuccessCount++
				accountLock.Unlock()

				fmt.Printf("⚡ [Session Cache] 🎉 [%s] พบ Token เดิม! รับรางวัลสำเร็จทันที (%s) ➔ %s [0s Browser]\n", targetAcc.Username, elapsedStr, detail)
				fmt.Printf("[ACC_SUCCESS] %s|%s|%s\n", targetAcc.Username, elapsedStr, detail)
			}
		}(acc)
	}

	wg.Wait()

	if cachedSuccessCount > 0 {
		fmt.Printf("✅ [Session Cache] เคลียร์บัญชีผ่าน Session Token ในคลังสำเร็จทั้งหมด %d บัญชี โดยไม่ต้องเปิด Chrome!\n", cachedSuccessCount)
	} else {
		fmt.Printf("ℹ️ [Session Cache] ไม่พบบัญชีที่มี Token หรือ Token หมดอายุ ➔ เข้าสู่ระบบเปิด Chrome เพื่อขอ Token ใหม่\n")
	}

	return cachedSuccessCount
}

func prepareBatchOnServer(count int, batchNum int, totalBatches int) bool {
	client := &http.Client{Timeout: 10 * time.Minute}
	reqBody := PrepareBatchReq{
		Count:        count,
		BatchNum:     batchNum,
		TotalBatches: totalBatches,
	}
	jsonBytes, _ := json.Marshal(reqBody)

	resp, err := client.Post("http://127.0.0.1:5000/batch/prepare", "application/json", bytes.NewBuffer(jsonBytes))
	if err != nil {
		fmt.Printf("❌ ไม่สามารถสั่งเตรียมแท็บบนเซิร์ฟเวอร์ได้: %v\n", err)
		return false
	}
	defer resp.Body.Close()

	if resp.StatusCode != 200 {
		return false
	}

	body, _ := io.ReadAll(resp.Body)
	var res PrepareBatchResp
	json.Unmarshal(body, &res)

	if res.Status != "ready" || res.ReadyCount < res.RequestedCount {
		fmt.Printf("⚠️ เซิร์ฟเวอร์เตรียมแท็บได้เพียง %d/%d จอ (ไม่ครบ)\n", res.ReadyCount, res.RequestedCount)
		return false
	}

	return true
}

func closeBatchOnServer() {
	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Post("http://127.0.0.1:5000/batch/close", "application/json", nil)
	if err == nil {
		resp.Body.Close()
	}
}

func doLoginRequest(workerID int, acc Account) LoginResult {
	t0 := time.Now()
	fmt.Printf("[Bot-%d] 🔓 กำลังเข้าสู่ระบบและตรวจสอบรางวัลสำหรับ '%s'...\n", workerID, acc.Username)
	fmt.Printf("[ACC_RUNNING] %s\n", acc.Username)

	client := &http.Client{Timeout: 30 * time.Second}
	reqBody := LoginRequest{
		URL:      TargetURL,
		Username: acc.Username,
		Password: acc.Password,
		WorkerID: workerID,
	}
	jsonBytes, _ := json.Marshal(reqBody)

	resp, err := client.Post(ServerURL, "application/json", bytes.NewBuffer(jsonBytes))
	elapsedStr := fmt.Sprintf("%.1fs", time.Since(t0).Seconds())

	if err != nil {
		fmt.Printf("[Bot-%d] ❌ ติดต่อ Local Captcha Server ไม่ได้ (%s): %v\n", workerID, elapsedStr, err)
		return LoginResult{
			Success:    false,
			IsLimit:    false,
			IsBadPwd:   false,
			ElapsedStr: elapsedStr,
			Reason:     "ติดต่อ Local Server ไม่ได้",
		}
	}

	body, _ := io.ReadAll(resp.Body)
	resp.Body.Close()

	var res LoginResponse
	json.Unmarshal(body, &res)

	if res.Status == "success" {
		detail := res.Detail
		if detail == "" {
			detail = "เข้าสู่ระบบสำเร็จ"
		}
		if res.RedeemStatus == "success" {
			fmt.Printf("[Bot-%d] 🎉 [%s] ล็อกอิน & รับรางวัลสำเร็จ (%s) ➔ %s\n", workerID, acc.Username, elapsedStr, res.Items)
		} else if res.RedeemStatus == "already_claimed" {
			fmt.Printf("[Bot-%d] 🟡 [%s] ล็อกอินสำเร็จ (เคยรับรางวัลไปแล้ว) (%s)\n", workerID, acc.Username, elapsedStr)
		} else if res.RedeemStatus == "token_missing" {
			fmt.Printf("[Bot-%d] ⚠️ [%s] ล็อกอินสำเร็จ (แต่ดึง Token รับของไม่ทัน) (%s)\n", workerID, acc.Username, elapsedStr)
		} else {
			fmt.Printf("[Bot-%d] 🎉 [%s] เข้าสู่ระบบสำเร็จ (%s) ➔ %s\n", workerID, acc.Username, elapsedStr, detail)
		}
		fmt.Printf("[ACC_SUCCESS] %s|%s|%s\n", acc.Username, elapsedStr, detail)
		return LoginResult{
			Success:    true,
			ElapsedStr: elapsedStr,
			Detail:     detail,
		}
	} else {
		reason := res.Reason
		if reason == "" {
			reason = res.Detail
		}
		if reason == "" {
			reason = "เข้าสู่ระบบไม่สำเร็จ หรือรหัสผ่านผิด"
		}
		isLimit := res.IsLimit || strings.Contains(strings.ToLower(reason), "limit") || strings.Contains(reason, "1015") || strings.Contains(strings.ToLower(reason), "too many requests") || strings.Contains(reason, "429")
		isBadPwd := strings.Contains(reason, "รหัสผ่านไม่ถูกต้อง") || strings.Contains(reason, "ไม่พบบัญชี") || strings.Contains(strings.ToLower(reason), "invalid")

		if isLimit {
			fmt.Printf("[Bot-%d] ⚠️ บัญชี '%s' ติด Limit IP ชั่วคราว (%s)\n", workerID, acc.Username, elapsedStr)
		} else if isBadPwd {
			fmt.Printf("[Bot-%d] ❌ บัญชี '%s' แจ้งว่ารหัสผ่านไม่ถูกต้อง (%s)\n", workerID, acc.Username, elapsedStr)
		} else {
			fmt.Printf("[Bot-%d] ⚠️ บัญชี '%s' เกิดปัญหา: %s (%s)\n", workerID, acc.Username, reason, elapsedStr)
		}

		return LoginResult{
			Success:    false,
			IsLimit:    isLimit,
			IsBadPwd:   isBadPwd,
			ElapsedStr: elapsedStr,
			Reason:     reason,
		}
	}
}

func main() {
	threadsFlag := flag.Int("threads", 5, "จำนวนแท็บ/บอทที่รันพร้อมกันในแต่ละรอบ")
	fileFlag := flag.String("file", "accounts.txt", "ไฟล์รายการไอดีที่ต้องการรัน")
	flag.Parse()

	if *threadsFlag > 0 {
		MaxConcurrent = *threadsFlag
	}

	fmt.Printf("🚀 เริ่มต้น FastLogin Batch Engine v%s (รอบละ %d จอ)...\n", AppVersion, MaxConcurrent)

	// 1. รอให้เซิร์ฟเวอร์เปิดก่อน
	waitForServerConnection()

	// 2. ตรวจสอบไฟล์ accounts
	execDir, err := os.Getwd()
	if err != nil {
		execDir = "."
	}
	accountsPath := *fileFlag
	if !filepath.IsAbs(accountsPath) {
		accountsPath = filepath.Join(execDir, accountsPath)
	}
	if _, err := os.Stat(accountsPath); err == nil {
		loadAccounts(accountsPath)
	}

	if len(allAccounts) == 0 {
		fmt.Printf("⚠️ ไม่พบข้อมูลบัญชีในไฟล์ '%s'\n", accountsPath)
		return
	}

	// 3. Phase 1: ตรวจสอบและยิงตรงผ่าน Session Token เดิมที่มีอยู่ในคลัง (Zero-Browser Mode 0s)
	runCachedTokensPass()

	// 4. Phase 2: รันระบบรอบ Chrome สำหรับบัญชีที่ยังไม่มี Token หรือ Token หมดอายุ
	runBatchEngine()

	fmt.Printf("\n[ALL_COMPLETED] 🎉 ดำเนินการเข้าสู่ระบบเสร็จสิ้นครบทุกบัญชีแล้ว!\n")
}

var labeledRegex = regexp.MustCompile(`(?i)ID\s*:\s*(\S+)\s*\|\s*PASS\s*:\s*(\S+)`)

func parseAccountLine(line string) (string, string) {
	line = strings.TrimSpace(line)
	if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, "//") {
		return "", ""
	}

	// 1. Labeled format: ID: xxx | PASS: yyy
	matches := labeledRegex.FindStringSubmatch(line)
	if len(matches) == 3 {
		return strings.TrimSpace(matches[1]), strings.TrimSpace(matches[2])
	}

	// 2. Delimited format: |, :, ,, \t
	for _, sep := range []string{"|", ":", ",", "\t"} {
		if strings.Contains(line, sep) {
			parts := strings.SplitN(line, sep, 2)
			if len(parts) == 2 {
				user := strings.TrimSpace(parts[0])
				pass := strings.TrimSpace(parts[1])
				if user != "" && pass != "" {
					return user, pass
				}
			}
		}
	}

	// 3. Space delimited
	if strings.Contains(line, " ") {
		parts := strings.SplitN(line, " ", 2)
		if len(parts) == 2 {
			user := strings.TrimSpace(parts[0])
			pass := strings.TrimSpace(parts[1])
			if user != "" && pass != "" {
				return user, pass
			}
		}
	}

	// 4. Single string format: ID and Password are the same!
	return line, line
}

func loadAccounts(filePath string) {
	file, err := os.Open(filePath)
	if err != nil {
		return
	}
	defer file.Close()

	scanner := bufio.NewScanner(file)
	newCount := 0

	accountLock.Lock()
	defer accountLock.Unlock()

	for scanner.Scan() {
		user, pass := parseAccountLine(scanner.Text())
		if user != "" && pass != "" {
			if !processedMap[user] {
				found := false
				for _, a := range allAccounts {
					if a.Username == user {
						found = true
						break
					}
				}
				if !found {
					allAccounts = append(allAccounts, Account{Username: user, Password: pass})
					newCount++
				}
			}
		}
	}

	if newCount > 0 {
		fmt.Printf("📥 โหลดบัญชีเข้าสู่ระบบสำเร็จ %d บัญชี\n", newCount)
	}
}

func runBatchEngine() {
	for {
		accountLock.Lock()
		var retryQueue []Account
		var freshQueue []Account
		for _, acc := range allAccounts {
			if !processedMap[acc.Username] {
				if limitCountMap[acc.Username] > 0 || retryCountMap[acc.Username] > 0 {
					retryQueue = append(retryQueue, acc)
				} else {
					freshQueue = append(freshQueue, acc)
				}
			}
		}
		accountLock.Unlock()

		// Prioritize retryQueue first before freshQueue
		pending := append(retryQueue, freshQueue...)

		if len(pending) == 0 {
			break
		}

		totalPending := len(pending)
		batchSize := MaxConcurrent
		totalBatches := (totalPending + batchSize - 1) / batchSize

		if len(retryQueue) > 0 {
			fmt.Printf("⚡ [Priority Queue] นำไอดีรอรันซ้ำ %d บัญชี มาดำเนินการก่อนเป็นลำดับแรก\n", len(retryQueue))
		}
		fmt.Printf("\n📋 มีบัญชีรอเข้าสู่ระบบ %d บัญชี (แบ่งเป็น %d รอบ, รอบละ %d จอ)\n", totalPending, totalBatches, batchSize)

		for bIdx := 0; bIdx < totalBatches; bIdx++ {
			start := bIdx * batchSize
			end := start + batchSize
			if end > totalPending {
				end = totalPending
			}
			currentBatch := pending[start:end]
			currentCount := len(currentBatch)
			batchNum := bIdx + 1

			fmt.Printf("\n======================================================\n")
			fmt.Printf("📦 [รอบที่ %d/%d] เริ่มต้นรอบใหม่สำหรับ %d บัญชี\n", batchNum, totalBatches, currentCount)
			fmt.Printf("======================================================\n")

			// 1. สั่งให้เซิร์ฟเวอร์เปิด Chrome ทีละจอ (1..currentCount) และยืนยันแคปช่าล่วงหน้า
			ok := prepareBatchOnServer(currentCount, batchNum, totalBatches)
			if !ok {
				fmt.Printf("⚠️ [รอบที่ %d/%d] เตรียมแท็บไม่สำเร็จ จะลองใหม่อีกครั้งใน 5 วินาที...\n", batchNum, totalBatches)
				time.Sleep(5 * time.Second)
				continue
			}

			fmt.Printf("🚀 [รอบที่ %d/%d] แคปช่าพร้อมครบทั้ง %d จอแล้ว! เริ่มลำเลียงกดยิงล็อกอิน (Micro-Pacing 0.8s ป้องกัน Limit)...\n", batchNum, totalBatches, currentCount)

			// 2. ลำเลียงยิงล็อกอินทีละจอ โดยปล่อยเธรดห่างกัน 250ms และให้ Server Pacing 0.8s ป้องกันชน 429
			var wg sync.WaitGroup
			var mu sync.Mutex
			batchResults := make(map[string]LoginResult)

			for i, acc := range currentBatch {
				if i > 0 {
					time.Sleep(250 * time.Millisecond) // Stagger 250ms กระจายงานเข้า Chrome แต่ละจอทันที
				}
				wg.Add(1)
				go func(workerID int, targetAcc Account) {
					defer wg.Done()
					res := doLoginRequest(workerID, targetAcc)
					mu.Lock()
					batchResults[targetAcc.Username] = res
					mu.Unlock()
				}(i+1, acc)
			}

			// รอให้ทุกจอล็อกอินเสร็จพร้อมกัน
			wg.Wait()

			// จัดการผลลัพธ์ของแต่ละบัญชีในรอบนี้
			accountLock.Lock()
			requeueCount := 0
			limitEncountered := false

			for _, acc := range currentBatch {
				res := batchResults[acc.Username]
				if res.Success {
					processedMap[acc.Username] = true
				} else if res.IsLimit {
					limitEncountered = true
					limitCountMap[acc.Username]++
					if limitCountMap[acc.Username] <= 3 {
						requeueCount++
						fmt.Printf("[Auto-Retry] ⏳ บัญชี '%s' ติด Limit Cloudflare 1015 (รอบที่ %d/3) — ระบบจะพักคูลดาวน์และรันซ้ำให้อัตโนมัติทันที\n", acc.Username, limitCountMap[acc.Username])
						fmt.Printf("[ACC_LIMIT] %s|%s|ติด Limit Cloudflare 1015 (รอบที่ %d/3 - รอคูลดาวน์ 15s แล้วรันซ้ำทันที)\n", acc.Username, res.ElapsedStr, limitCountMap[acc.Username])
					} else {
						// เกินโควตาลองซ้ำ 3 ครั้งแล้วจริง ถึงจะตัดเป็นล้มเหลว
						processedMap[acc.Username] = true
						fmt.Printf("[Bot] ❌ บัญชี '%s' ติด Limit ต่อเนื่องเกิน 3 ครั้ง สิ้นสุดการลองซ้ำ\n", acc.Username)
						fmt.Printf("[ACC_FAIL] %s|%s|ติด Limit Cloudflare ต่อเนื่องเกินโควตา\n", acc.Username, res.ElapsedStr)
					}
				} else if res.IsBadPwd {
					retryCountMap[acc.Username]++
					if retryCountMap[acc.Username] <= 1 {
						requeueCount++
						fmt.Printf("[Auto-Retry] 🔄 บัญชี '%s' รายงานว่ารหัสผ่านผิด (อาจเกิดจากจังหวะเว็บ) จะลองซ้ำให้อีก 1 ครั้ง\n", acc.Username)
						fmt.Printf("[ACC_RETRY] %s|%s|รหัสผ่านไม่ถูกต้อง (จะลองตรวจซ้ำอีก 1 ครั้ง)\n", acc.Username, res.ElapsedStr)
					} else {
						processedMap[acc.Username] = true
						fmt.Printf("[Bot] ❌ บัญชี '%s' ยืนยันรหัสผ่านไม่ถูกต้อง สิ้นสุดการทำงาน\n", acc.Username)
						fmt.Printf("[ACC_FAIL] %s|%s|รหัสผ่านไม่ถูกต้อง / ไม่พบบัญชี\n", acc.Username, res.ElapsedStr)
					}
				} else {
					// ข้อผิดพลาดชั่วคราว (Captcha timeout, เน็ตกระตุก) -> ให้โอกาสลองใหม่สูงสุด 2 ครั้ง
					retryCountMap[acc.Username]++
					if retryCountMap[acc.Username] <= 2 {
						requeueCount++
						fmt.Printf("[Auto-Retry] 🔄 บัญชี '%s' เกิดปัญหาชั่วคราว (%s) นำกลับเข้าคิวลองใหม่อัตโนมัติ (รอบที่ %d/2)\n", acc.Username, res.Reason, retryCountMap[acc.Username])
						fmt.Printf("[ACC_RETRY] %s|%s|%s (รอบที่ %d/2 - รอรันซ้ำ)\n", acc.Username, res.ElapsedStr, res.Reason, retryCountMap[acc.Username])
					} else {
						processedMap[acc.Username] = true
						fmt.Printf("[Bot] ❌ บัญชี '%s' ไม่ผ่านหลังลองซ้ำครบแล้ว: %s\n", acc.Username, res.Reason)
						fmt.Printf("[ACC_FAIL] %s|%s|%s\n", acc.Username, res.ElapsedStr, res.Reason)
					}
				}
			}
			accountLock.Unlock()

			// 3. จัดการคูลดาวน์และคืน RAM
			if limitEncountered || requeueCount > 0 {
				if limitEncountered {
					fmt.Printf("⏳ [Cool-down] มีบัญชีติด Limit 429 — พักสั้นๆ 3 วินาที เพื่อรันไอดีใหม่ต่อทันที...\n")
					closeBatchOnServer()
					time.Sleep(3 * time.Second)
				} else {
					fmt.Printf("🔄 [Priority Retry] มีบัญชีรอรันซ้ำ %d บัญชี — ดึงกลับมารันซ้ำทันทีในรอบถัดไป...\n", requeueCount)
					closeBatchOnServer()
					time.Sleep(2 * time.Second)
				}
				break // สั่ง break ทันทีเพื่อกลับไปคิวหลัก และดึงไอดีที่รอรันซ้ำขึ้นมาทำก่อนเป็นลำดับแรกทันที!
			} else if (batchNum%3 == 0) || (bIdx+1 == totalBatches) {
				fmt.Printf("🛑 [รอบที่ %d/%d] ล้างแคชรีเฟรชเบราว์เซอร์ และพักคูลดาวน์ 4s...\n", batchNum, totalBatches)
				closeBatchOnServer()
				time.Sleep(4 * time.Second)
			} else {
				if bIdx+1 < totalBatches {
					time.Sleep(500 * time.Millisecond)
				}
			}
		}

		fmt.Printf("\n🎉 ดำเนินการเข้าสู่ระบบเสร็จสิ้นครบทุกบัญชีแล้ว!\n")
	}
}
