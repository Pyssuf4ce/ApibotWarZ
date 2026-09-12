using System;
using System.Globalization;
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
        // ⚡ Cloudflare Worker High-Speed Endpoint (15ms)
        public static string WebAppUrl { get; set; } = "https://warz-license.skysaber086.workers.dev";
        public static readonly string AppName = "apibot";

        private static readonly HttpClientHandler _handler = new HttpClientHandler { AllowAutoRedirect = true };
        private static readonly HttpClient _httpClient = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>
        /// Pure HWID Auto-Login: Checks if this machine's HWID is already bound to a valid active license in the cloud.
        /// </summary>
        public async Task<(bool Success, string Message, string Key, string Expiry)> CheckHwidAsync()
        {
            try
            {
                string hwid = GetHWID();
                string ip = await GetPublicIpAsync();

                string json = $"{{\"action\":\"checkhwid\",\"app\":\"{AppName}\",\"hwid\":\"{JsonEscape(hwid)}\",\"ip\":\"{JsonEscape(ip)}\"}}";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(WebAppUrl, content);
                string respJson = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(respJson);
                var root = doc.RootElement;
                bool success = root.TryGetProperty("success", out var s) && s.GetBoolean();
                string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                string key = root.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                string expiry = root.TryGetProperty("expiry", out var exp) ? exp.GetString() ?? "Lifetime" : "Lifetime";

                if (success)
                {
                    return (true, message, key, string.IsNullOrEmpty(expiry) ? "Lifetime" : expiry);
                }

                return (false, string.IsNullOrEmpty(message) ? "ไม่พบสิทธิ์ของเครื่องนี้ในระบบ" : message, "", "");
            }
            catch (Exception ex)
            {
                return (false, $"ข้อผิดพลาดในการเชื่อมต่อ: {ex.Message}", "", "");
            }
        }

        /// <summary>
        /// Key Activation: Binds a new License Key to this machine's HWID in the cloud.
        /// </summary>
        public async Task<(bool Success, string Message, string Expiry)> ActivateKeyAsync(string licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
                return (false, "กรุณากรอก License Key", "");

            try
            {
                string hwid = GetHWID();
                string ip = await GetPublicIpAsync();

                string json = $"{{\"action\":\"activate\",\"app\":\"{AppName}\",\"key\":\"{JsonEscape(licenseKey.Trim())}\",\"hwid\":\"{JsonEscape(hwid)}\",\"ip\":\"{JsonEscape(ip)}\"}}";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(WebAppUrl, content);
                string respJson = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(respJson);
                var root = doc.RootElement;
                bool success = root.TryGetProperty("success", out var s) && s.GetBoolean();
                string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                string expiry = root.TryGetProperty("expiry", out var exp) ? exp.GetString() ?? "Lifetime" : "Lifetime";

                if (success)
                {
                    return (true, message, string.IsNullOrEmpty(expiry) ? "Lifetime" : expiry);
                }

                return (false, string.IsNullOrEmpty(message) ? "ไม่พบ License Key นี้ในระบบ" : message, "");
            }
            catch (Exception ex)
            {
                return (false, $"ข้อผิดพลาดในการเชื่อมต่อ: {ex.Message}", "");
            }
        }

        /// <summary>
        /// Formats ISO expiry string to friendly Thai representation with live countdown support.
        /// </summary>
        public static string FormatThaiExpiry(string isoExpiry)
        {
            if (string.IsNullOrEmpty(isoExpiry) || isoExpiry.Equals("Lifetime", StringComparison.OrdinalIgnoreCase))
                return "ตลอดชีพ (Lifetime)";

            if (DateTime.TryParse(isoExpiry, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expDate))
            {
                var now = DateTime.UtcNow;
                var diff = expDate.ToUniversalTime() - now;

                var thCulture = new CultureInfo("th-TH");
                string thDateStr = expDate.ToLocalTime().ToString("dd/MM/yyyy", thCulture);

                if (diff.TotalSeconds <= 0)
                    return $"{thDateStr} (หมดอายุแล้ว)";

                if (diff.TotalHours < 24)
                    return $"{thDateStr} (เหลือ {(int)diff.TotalHours} ชม. {diff.Minutes} นาที)";

                int days = (int)Math.Floor(diff.TotalDays);
                int remHours = diff.Hours;
                return remHours > 0 ? $"{thDateStr} (เหลือ {days} วัน {remHours} ชม.)" : $"{thDateStr} (เหลือ {days} วัน)";
            }

            return isoExpiry;
        }

        private static string JsonEscape(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            return val.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        public static async Task<string> GetPublicIpAsync()
        {
            try
            {
                using var quickClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                return await quickClient.GetStringAsync("https://api.ipify.org");
            }
            catch
            {
                return "Unknown";
            }
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
