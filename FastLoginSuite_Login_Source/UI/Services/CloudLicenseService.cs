using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ApibotWarZ.UI.Services
{
    public class CloudLicenseService
    {
        public static string WebAppUrl { get; set; } = "https://warz-license.skysaber086.workers.dev";
        public static string FirebaseUrl { get; set; } = "https://licensewebproject-default-rtdb.asia-southeast1.firebasedatabase.app";

        private static readonly HttpClientHandler _handler = new HttpClientHandler { AllowAutoRedirect = true };
        private static readonly HttpClient _httpClient = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(15) };

        private static string _cachedPublicIp = "";
        private static DateTime _lastIpFetch = DateTime.MinValue;

        public static string CurrentBoundKey { get; set; } = "";

        static CloudLicenseService()
        {
            var cfg = ConfigManager.Load();
            if (!string.IsNullOrWhiteSpace(cfg.FirebaseDatabaseUrl))
            {
                FirebaseUrl = CleanUrl(cfg.FirebaseDatabaseUrl);
            }
            if (!string.IsNullOrWhiteSpace(cfg.LicenseKey))
            {
                CurrentBoundKey = cfg.LicenseKey.Trim().ToUpperInvariant();
            }
        }

        public static string CleanUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            if (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);
            return url;
        }

        private static string GetEndpoint(string path)
        {
            return $"{CleanUrl(FirebaseUrl)}{path}.json";
        }

        /// <summary>
        /// Pure HWID Auto-Login: Checks if this machine's HWID is already bound to a valid active license in Firebase.
        /// </summary>
        public async Task<(bool Success, string Message, string Key, string Expiry, string RawExpiry)> CheckHwidAsync()
        {
            try
            {
                string hwid = GetHWID();
                string ip = await GetPublicIpAsync();

                // 1. Fetch bound key for this HWID from Firebase
                string boundKey = "";
                string hwidUrl = GetEndpoint($"/hwids/apibot/{hwid}");
                try
                {
                    var hwidResp = await _httpClient.GetAsync(hwidUrl);
                    if (hwidResp.IsSuccessStatusCode)
                    {
                        string boundKeyJson = await hwidResp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(boundKeyJson) && boundKeyJson != "null")
                        {
                            boundKey = boundKeyJson.Trim('"', ' ', '\r', '\n');
                        }
                    }
                }
                catch { }

                // Fallback: Check local config if HWID node was missing or network hiccup
                if (string.IsNullOrEmpty(boundKey))
                {
                    var localCfg = ConfigManager.Load();
                    if (!string.IsNullOrWhiteSpace(localCfg.LicenseKey))
                    {
                        boundKey = localCfg.LicenseKey.Trim().ToUpperInvariant();
                    }
                }

                if (string.IsNullOrEmpty(boundKey))
                {
                    return (false, "ไม่พบเครื่องนี้ในระบบ กรุณากรอก License Key เพื่อเปิดใช้งาน", "", "", "");
                }

                CurrentBoundKey = boundKey;

                // 2. Fetch Key details from Firebase
                string keyUrl = GetEndpoint($"/keys/apibot/{boundKey}");
                var keyResp = await _httpClient.GetAsync(keyUrl);
                if (!keyResp.IsSuccessStatusCode)
                    return (false, "ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ Firebase ได้", "", "", "");

                string keyJson = await keyResp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(keyJson) || keyJson == "null")
                    return (false, "License Key ถูกลบออกจากระบบแล้ว", "", "", "");

                using var doc = JsonDocument.Parse(keyJson);
                var root = doc.RootElement;

                string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "Active" : "Active";
                string rawExpiry = root.TryGetProperty("expiry", out var exp) ? exp.GetString() ?? "Lifetime" : "Lifetime";
                bool proxyEnabled = root.TryGetProperty("proxyEnabled", out var pe) && pe.GetBoolean();
                string proxyCountry = root.TryGetProperty("proxyCountry", out var pc) ? pc.GetString() ?? "TH" : "TH";
                int proxyMaxIps = root.TryGetProperty("proxyMaxIps", out var pmi) && pmi.TryGetInt32(out var pmiVal) ? pmiVal : 10;

                string boundHwid = root.TryGetProperty("hwid", out var h) ? h.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(boundHwid) && !boundHwid.Equals(hwid, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "License Key นี้ถูกผูกกับเครื่องอื่นแล้ว (HWID Mismatch)", boundKey, "", "");
                }

                // Auto-repair / ensure HWID binding in Firebase
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string hwidUrl = GetEndpoint($"/hwids/apibot/{hwid}");
                        using var hwidContent = new StringContent($"\"{boundKey}\"", Encoding.UTF8, "application/json");
                        await _httpClient.PutAsync(hwidUrl, hwidContent);
                    }
                    catch { }
                });

                if (status.Equals("Banned", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "License Key / เครื่องนี้ถูกระงับการใช้งาน (Banned)", boundKey, "", "");
                }

                if (!rawExpiry.Equals("Lifetime", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(rawExpiry))
                {
                    if (DateTime.TryParse(rawExpiry, out var expDate) && DateTime.UtcNow > expDate.ToUniversalTime())
                    {
                        return (false, "สิทธิ์การใช้งานของ License Key นี้หมดอายุแล้ว (Expired)", boundKey, "หมดอายุ", rawExpiry);
                    }
                }

                // 3. Parse Allowed Specific Nodes (if configured by Admin on Web Dashboard)
                var allowedNodesList = new List<string>();
                if (root.TryGetProperty("allowedNodes", out var anEl) && anEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in anEl.EnumerateArray())
                    {
                        string? ns = n.GetString();
                        if (!string.IsNullOrWhiteSpace(ns)) allowedNodesList.Add(ns.Trim().ToLowerInvariant());
                    }
                }

                // 4. Sync Private Proxy Multi-Account Configuration from Firebase
                await SyncProxyConfigAsync(proxyEnabled, proxyCountry, proxyMaxIps, allowedNodesList);

                // 4. Update last login & IP asynchronously
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string patchJson = $"{{\"lastLogin\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\"lastIp\":\"{JsonEscape(ip)}\"}}";
                        using var content = new StringContent(patchJson, Encoding.UTF8, "application/json");
                        await _httpClient.PatchAsync(keyUrl, content);
                    }
                    catch { }
                });

                // 5. Send initial Live Heartbeat
                _ = SendHeartbeatAsync("พร้อมทำงาน (Idle)", boundKey);

                string formattedExpiry = FormatExpiry(rawExpiry);
                return (true, "ยืนยันสิทธิ์เครื่องสำเร็จ", boundKey, formattedExpiry, rawExpiry);
            }
            catch (Exception ex)
            {
                return (false, $"ข้อผิดพลาดในการเชื่อมต่อ: {ex.Message}", "", "", "");
            }
        }

        /// <summary>
        /// Key Activation: Binds a new License Key to this machine's HWID in Firebase.
        /// </summary>
        public async Task<(bool Success, string Message, string Expiry, string RawExpiry)> ActivateKeyAsync(string licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
                return (false, "กรุณากรอก License Key", "", "");

            string cleanKey = licenseKey.Trim().ToUpperInvariant();

            try
            {
                string hwid = GetHWID();
                string ip = await GetPublicIpAsync();

                // 1. Fetch Key details
                string keyUrl = GetEndpoint($"/keys/apibot/{cleanKey}");
                var keyResp = await _httpClient.GetAsync(keyUrl);
                if (!keyResp.IsSuccessStatusCode)
                    return (false, "ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ Firebase ได้", "", "");

                string keyJson = await keyResp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(keyJson) || keyJson == "null")
                    return (false, "ไม่พบ License Key นี้ในระบบ (Invalid Key)", "", "");

                using var doc = JsonDocument.Parse(keyJson);
                var root = doc.RootElement;

                string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "Active" : "Active";
                string rawExpiry = root.TryGetProperty("expiry", out var exp) ? exp.GetString() ?? "Lifetime" : "Lifetime";
                string boundHwid = root.TryGetProperty("hwid", out var h) ? h.GetString() ?? "" : "";
                bool proxyEnabled = root.TryGetProperty("proxyEnabled", out var pe) && pe.GetBoolean();
                string proxyCountry = root.TryGetProperty("proxyCountry", out var pc) ? pc.GetString() ?? "TH" : "TH";
                int proxyMaxIps = root.TryGetProperty("proxyMaxIps", out var pmi) && pmi.TryGetInt32(out var pmiVal) ? pmiVal : 10;

                if (status.Equals("Banned", StringComparison.OrdinalIgnoreCase))
                    return (false, "License Key นี้ถูกระงับการใช้งาน (Banned)", "", "");

                if (!rawExpiry.Equals("Lifetime", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(rawExpiry))
                {
                    if (DateTime.TryParse(rawExpiry, out var expDate) && DateTime.UtcNow > expDate.ToUniversalTime())
                        return (false, "License Key นี้หมดอายุการใช้งานแล้ว (Expired)", "", "");
                }

                // 2. Validate HWID binding
                if (!string.IsNullOrEmpty(boundHwid) && !boundHwid.Equals(hwid, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "License Key นี้ถูกผูกกับเครื่องอื่นแล้ว (HWID Mismatch)", "", "");
                }

                // 3. Bind HWID to Key and Key to HWID
                string hwidUrl = GetEndpoint($"/hwids/apibot/{hwid}");
                using var hwidContent = new StringContent($"\"{cleanKey}\"", Encoding.UTF8, "application/json");
                await _httpClient.PutAsync(hwidUrl, hwidContent);

                string patchJson = $"{{\"hwid\":\"{JsonEscape(hwid)}\",\"lastLogin\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\"lastIp\":\"{JsonEscape(ip)}\"}}";
                using var keyContent = new StringContent(patchJson, Encoding.UTF8, "application/json");
                await _httpClient.PatchAsync(keyUrl, keyContent);

                CurrentBoundKey = cleanKey;

                // 4. Save to local config
                var cfg = ConfigManager.Load();
                cfg.LicenseKey = cleanKey;
                ConfigManager.Save(cfg);

                // 5. Sync Proxy Config & Send initial Live Heartbeat
                await SyncProxyConfigAsync(proxyEnabled, proxyCountry, proxyMaxIps);
                _ = SendHeartbeatAsync("เข้าสู่ระบบสำเร็จ", cleanKey);

                string formattedExpiry = FormatExpiry(rawExpiry);
                return (true, "เปิดใช้งาน License Key สำเร็จ!", formattedExpiry, rawExpiry);
            }
            catch (Exception ex)
            {
                return (false, $"ข้อผิดพลาดในการเปิดใช้งาน: {ex.Message}", "", "");
            }
        }

        /// <summary>
        /// Syncs Private Proxy Multi-Account Configuration from Firebase Realtime DB.
        /// </summary>
        public static async Task SyncProxyConfigAsync(bool keyProxyAllowed, string country = "TH", int maxIps = 10, List<string>? allowedNodes = null)
        {
            if (!keyProxyAllowed)
            {
                // VpnGateService.SetRemoteProxyConfig("", "", "TH", false, 10, null);
                return;
            }

            try
            {
                string proxyUrl = GetEndpoint("/proxy");
                var resp = await _httpClient.GetAsync(proxyUrl);
                if (!resp.IsSuccessStatusCode) return;

                string json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool globalEnabled = !root.TryGetProperty("globalEnabled", out var ge) || ge.GetBoolean();
                if (!globalEnabled)
                {
                    // VpnGateService.SetRemoteProxyConfig("", "", "TH", false, 10, null);
                    return;
                }

                // 1. Check if local machine has installed Private Proxy device auth, sync it to Cloud automatically
                string? localDevUser = null; string? localDevPass = null;
                if (!string.IsNullOrEmpty(localDevUser) && !string.IsNullOrEmpty(localDevPass))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string syncUrl = GetEndpoint("/proxy/device_auth");
                            var payload = new { device_user = localDevUser, device_pass = localDevPass, updated_at = DateTime.UtcNow.ToString("o") };
                            string jsonPayload = JsonSerializer.Serialize(payload);
                            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                            await _httpClient.PatchAsync(syncUrl, content);
                        }
                        catch { }
                    });
                }

                // 2. Check if Firebase Cloud has active synced device_auth (IKEv2 radius credentials)
                if (root.TryGetProperty("device_auth", out var devAuthEl))
                {
                    string cloudDevUser = devAuthEl.TryGetProperty("device_user", out var cdu) ? cdu.GetString() ?? "" : "";
                    string cloudDevPass = devAuthEl.TryGetProperty("device_pass", out var cdp) ? cdp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(cloudDevUser) && !string.IsNullOrEmpty(cloudDevPass))
                    {
                        // VpnGateService.SetRemoteProxyConfig(cloudDevUser, cloudDevPass, country, true, maxIps, allowedNodes);
                        return;
                    }
                }

                // 3. Fallback to accounts list
                if (root.TryGetProperty("accounts", out var accs) && accs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var acc in accs.EnumerateArray())
                    {
                        bool isAccEnabled = !acc.TryGetProperty("enabled", out var en) || en.GetBoolean();
                        if (isAccEnabled)
                        {
                            string user = acc.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
                            string pass = acc.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";

                            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                            {
                                // VpnGateService.SetRemoteProxyConfig(user, pass, country, true, maxIps, allowedNodes);
                                return;
                            }
                        }
                    }
                }

                // If no account with valid user & pass found, disable remote proxy
                // VpnGateService.SetRemoteProxyConfig("", "", "TH", false, 10, null);
            }
            catch { }
        }

        /// <summary>
        /// Real-time Fast Live Heartbeat: Updates machine status, IP, and timestamp on Firebase Realtime DB.
        /// </summary>
        public static async Task SendHeartbeatAsync(string statusText, string? key = null)
        {
            try
            {
                string hwid = GetHWID();
                string activeKey = !string.IsNullOrEmpty(key) ? key : CurrentBoundKey;
                string ip = await GetPublicIpAsync();
                string node = "Direct";
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                string hbJson = $"{{\"key\":\"{JsonEscape(activeKey)}\",\"app\":\"daily\",\"status\":\"{JsonEscape(statusText)}\",\"ip\":\"{JsonEscape(ip)}\",\"active_node\":\"{JsonEscape(node)}\",\"last_seen\":{nowMs}}}";

                string url = $"{CleanUrl(FirebaseUrl)}/live_heartbeats/{hwid}.json";
                using var content = new StringContent(hbJson, Encoding.UTF8, "application/json");
                await _httpClient.PatchAsync(url, content);
            }
            catch { }
        }

        /// <summary>
        /// Fetches currently active nodes leased by other online machines to prevent node collision.
        /// </summary>
        public static async Task<HashSet<string>> GetOnlineBusyNodesAsync(string currentHwid)
        {
            var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string url = $"{CleanUrl(FirebaseUrl)}/live_heartbeats.json";
                var resp = await _httpClient.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return busy;

                string json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return busy;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long activeThresholdMs = nowMs - 45000; // active within 45s

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        string hwid = prop.Name;
                        if (hwid.Equals(currentHwid, StringComparison.OrdinalIgnoreCase)) continue;

                        var hb = prop.Value;
                        if (hb.ValueKind == JsonValueKind.Object)
                        {
                            long lastSeen = hb.TryGetProperty("last_seen", out var ls) && ls.TryGetInt64(out var lsVal) ? lsVal : 0;
                            if (lastSeen >= activeThresholdMs)
                            {
                                string node = hb.TryGetProperty("active_node", out var an) ? an.GetString() ?? "" : "";
                                if (!string.IsNullOrEmpty(node))
                                {
                                    busy.Add(node);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return busy;
        }

        /// <summary>
        /// Fast Runtime Check: Ensures Key is still Active and not Expired or Banned.
        /// </summary>
        public async Task<(bool Valid, string Message, string ExpiryText, string RawExpiryText)> CheckLiveStatusAsync(string? key = null)
        {
            string activeKey = !string.IsNullOrEmpty(key) ? key : CurrentBoundKey;
            if (string.IsNullOrEmpty(activeKey)) return (false, "ไม่พบคีย์", "", "");

            try
            {
                string keyUrl = GetEndpoint($"/keys/apibot/{activeKey}");
                var resp = await _httpClient.GetAsync(keyUrl);
                if (!resp.IsSuccessStatusCode) return (true, "Offline check pass", "", "");

                string json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                    return (false, "License Key ถูกลบออกจากระบบ", "", "");

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "Active" : "Active";
                string rawExpiry = root.TryGetProperty("expiry", out var exp) ? exp.GetString() ?? "Lifetime" : "Lifetime";

                if (status.Equals("Banned", StringComparison.OrdinalIgnoreCase))
                    return (false, "License Key ถูกระงับการใช้งานในระบบ (Banned)", "", rawExpiry);

                if (!rawExpiry.Equals("Lifetime", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(rawExpiry))
                {
                    if (DateTime.TryParse(rawExpiry, out var expDate) && DateTime.UtcNow > expDate.ToUniversalTime())
                        return (false, "License Key หมดอายุการใช้งานแล้ว (Expired)", "หมดอายุ", rawExpiry);
                }

                return (true, "Active", FormatExpiry(rawExpiry), rawExpiry);
            }
            catch
            {
                return (true, "Connection warning", "", "");
            }
        }

        /// <summary>
        /// Formats expiry dates into friendly Thai display (e.g. 24/08/2569 (เหลือ 23 ชม.) or ตลอดชีพ)
        /// </summary>
        public static string FormatExpiry(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("Lifetime", StringComparison.OrdinalIgnoreCase) || raw.Equals("Active", StringComparison.OrdinalIgnoreCase))
                return "ตลอดชีพ (Lifetime)";

            if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) || DateTime.TryParse(raw, out dt))
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                int thaiYear = dt.Year > 2500 ? dt.Year : dt.Year + 543;
                string dateStr = $"{dt.Day:D2}/{dt.Month:D2}/{thaiYear}";

                if (diff.TotalSeconds <= 0) return $"{dateStr} (หมดอายุ)";
                if (diff.TotalDays >= 1) return $"{dateStr} (เหลือ {(int)diff.TotalDays} วัน)";
                if (diff.TotalHours >= 1) return $"{dateStr} (เหลือ {(int)diff.TotalHours} ชม. {diff.Minutes} นาที)";
                return $"{dateStr} (เหลือ {Math.Max(1, (int)diff.TotalMinutes)} นาที)";
            }

            return raw;
        }

        private static string JsonEscape(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            return val.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static async Task<string> GetPublicIpAsync()
        {
            if (!string.IsNullOrEmpty(_cachedPublicIp) && (DateTime.Now - _lastIpFetch).TotalMinutes < 5)
            {
                return _cachedPublicIp;
            }

            try
            {
                using var quickClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                _cachedPublicIp = (await quickClient.GetStringAsync("https://api.ipify.org")).Trim();
                _lastIpFetch = DateTime.Now;
                return _cachedPublicIp;
            }
            catch
            {
                return !string.IsNullOrEmpty(_cachedPublicIp) ? _cachedPublicIp : "Unknown";
            }
        }

        public static bool IsVirtualMachine()
        {
            try
            {
                // 1. Check System Manufacturer / Model in Registry
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SystemInformation"))
                {
                    if (key != null)
                    {
                        string manufacturer = key.GetValue("SystemManufacturer")?.ToString() ?? "";
                        string model = key.GetValue("SystemProductName")?.ToString() ?? "";
                        string bios = key.GetValue("BIOSVersion")?.ToString() ?? "";

                        string combined = $"{manufacturer} {model} {bios}".ToLowerInvariant();
                        if (combined.Contains("vmware") || combined.Contains("virtualbox") || combined.Contains("vbox") ||
                            combined.Contains("qemu") || combined.Contains("xen") || combined.Contains("hyper-v") ||
                            combined.Contains("virtual machine") || combined.Contains("parallels") || combined.Contains("bochs"))
                        {
                            return true;
                        }
                    }
                }

                // 2. Check Disk / SCSI Controller in Registry
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\Scsi\Scsi Port 0\Scsi Bus 0\Target Id 0\Logical Unit Id 0"))
                {
                    if (key != null)
                    {
                        string id = key.GetValue("Identifier")?.ToString()?.ToLowerInvariant() ?? "";
                        if (id.Contains("vmware") || id.Contains("vbox") || id.Contains("qemu") || id.Contains("virtual"))
                        {
                            return true;
                        }
                    }
                }

                // 3. Check Network Interface MAC prefix / Description
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    string desc = nic.Description.ToLowerInvariant();
                    if (desc.Contains("vmware") || desc.Contains("virtualbox") || desc.Contains("hyper-v"))
                    {
                        return true;
                    }
                    string mac = nic.GetPhysicalAddress().ToString().ToUpperInvariant();
                    if (mac.StartsWith("000569") || mac.StartsWith("000C29") || mac.StartsWith("005056") || mac.StartsWith("080027"))
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        public static string GetHWID()
        {
            try
            {
                string machineGuid = "";
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    machineGuid = key?.GetValue("MachineGuid")?.ToString() ?? "";
                }

                string raw = $"{Environment.MachineName}_{Environment.ProcessorCount}_{machineGuid}";
                using var sha256 = SHA256.Create();
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
            }
            catch
            {
                return Environment.MachineName.ToUpperInvariant();
            }
        }
    }
}







