using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Services
{
    public static class DiscordNotifier
    {
        private static readonly HttpClient _http;
        private static readonly Dictionary<string, DateTime> _lastErrorAlerts = new();
        private static readonly object _alertLock = new();

        static DiscordNotifier()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public static byte[]? CaptureFullDesktopScreenshot()
        {
            try
            {
                var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    bounds = new Rectangle(0, 0, 1920, 1080);
                }

                using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }

                using var ms = new MemoryStream();
                var jpegEncoder = ImageCodecInfo.GetImageDecoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (jpegEncoder != null)
                {
                    using var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                    bmp.Save(ms, jpegEncoder, encoderParams);
                }
                else
                {
                    bmp.Save(ms, ImageFormat.Png);
                }

                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        public static List<string> GetCustomWebhookUrls()
        {
            var list = new List<string>();
            try
            {
                string workDir = BotProcessManager.GetWorkingDir();
                string webhookFilePath = Path.Combine(workDir, "webhook.txt");
                if (!File.Exists(webhookFilePath))
                {
                    File.WriteAllText(webhookFilePath, "# วาง URL Discord Webhook ของคุณในบรรทัดด้านล่างนี้ (ใส่ได้สูงสุด 2 ลิงก์ บรรทัดละ 1 ลิงก์)\r\n# ตัวอย่าง: https://discord.com/api/webhooks/123456789/abcdefgh...\r\n# หากไม่ต้องการใช้งาน ให้ปล่อยว่างไว้\r\n\r\n", Encoding.UTF8);
                    return list;
                }

                string[] lines = File.ReadAllLines(webhookFilePath);
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

                    if (trimmed.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!list.Contains(trimmed))
                        {
                            list.Add(trimmed);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static async Task SendPayloadToUrlAsync(string targetUrl, string jsonPayload, string jsonFallback, byte[]? screenshotBytes, string accountId, string tag)
        {
            try
            {
                using var formData = new MultipartFormDataContent();
                formData.Add(new StringContent(jsonPayload, Encoding.UTF8, "application/json"), "payload_json");

                if (screenshotBytes != null && screenshotBytes.Length > 0)
                {
                    var imageContent = new ByteArrayContent(screenshotBytes);
                    imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    formData.Add(imageContent, "files[0]", "error_screenshot.jpg");
                }

                using var response = await _http.PostAsync(targetUrl, formData);
                if (!response.IsSuccessStatusCode)
                {
                    using var textContent = new StringContent(jsonFallback, Encoding.UTF8, "application/json");
                    await _http.PostAsync(targetUrl, textContent);
                }
            }
            catch { }
        }

        public static void SendErrorAlert(string accountId, string errorMessage, string? detailedAnalysis = null, string? stepName = null, byte[]? screenshotBytes = null)
        {
            try
            {
                var customUrls = GetCustomWebhookUrls();
                if (customUrls.Count == 0) return;

                lock (_alertLock)
                {
                    string alertKey = $"{accountId}_{stepName}_{errorMessage}";
                    if (_lastErrorAlerts.TryGetValue(alertKey, out var lastTime))
                    {
                        if ((DateTime.UtcNow - lastTime).TotalSeconds < 15)
                        {
                            return; // Debounce
                        }
                    }
                    _lastErrorAlerts[alertKey] = DateTime.UtcNow;

                    if (_lastErrorAlerts.Count > 100)
                    {
                        _lastErrorAlerts.Clear();
                    }
                }

                if (screenshotBytes == null || screenshotBytes.Length == 0)
                {
                    try { screenshotBytes = CaptureFullDesktopScreenshot(); } catch { }
                }

                string hwid = CloudLicenseService.GetHWID();
                string safeAccount = string.IsNullOrWhiteSpace(accountId) ? "Unknown" : accountId;
                string safeMessage = string.IsNullOrWhiteSpace(errorMessage) ? "Unknown error" : errorMessage;
                string safeAnalysis = string.IsNullOrWhiteSpace(detailedAnalysis) ? safeMessage : detailedAnalysis;
                string safeStep = string.IsNullOrWhiteSpace(stepName) ? "Login & Verification" : stepName;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string jsonPayload = BuildErrorPayloadJson(safeAccount, safeMessage, safeAnalysis, safeStep, hwid, screenshotBytes != null && screenshotBytes.Length > 0);
                        string jsonFallback = BuildErrorPayloadJson(safeAccount, safeMessage, safeAnalysis, safeStep, hwid, false);

                        foreach (var customUrl in customUrls)
                        {
                            if (!string.IsNullOrEmpty(customUrl))
                            {
                                await SendPayloadToUrlAsync(customUrl, jsonPayload, jsonFallback, screenshotBytes, safeAccount, "Error Webhook");
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        public static void SendSuccessAlert(string accountId, string detail, string elapsed, string? items = null, byte[]? screenshotBytes = null)
        {
            try
            {
                var customUrls = GetCustomWebhookUrls();
                if (customUrls.Count == 0) return;

                string hwid = CloudLicenseService.GetHWID();
                string safeAccount = string.IsNullOrWhiteSpace(accountId) ? "Unknown" : accountId;
                string safeItems = string.IsNullOrWhiteSpace(items) ? (detail.Contains("รับรางวัล") ? detail : "เข้าสู่ระบบสำเร็จ") : items;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string jsonPayload = BuildSuccessPayloadJson(safeAccount, detail, elapsed, safeItems, hwid, screenshotBytes != null && screenshotBytes.Length > 0);
                        string jsonFallback = BuildSuccessPayloadJson(safeAccount, detail, elapsed, safeItems, hwid, false);

                        foreach (var customUrl in customUrls)
                        {
                            if (!string.IsNullOrEmpty(customUrl))
                            {
                                await SendPayloadToUrlAsync(customUrl, jsonPayload, jsonFallback, screenshotBytes, safeAccount, "Success Webhook");
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static string BuildErrorPayloadJson(string accountId, string errorMessage, string detailedAnalysis, string stepName, string hwid, bool hasScreenshot)
        {
            var fieldsArray = new JsonArray
            {
                new JsonObject { ["name"] = "👤 บัญชี (Account)", ["value"] = $"```{accountId}```", ["inline"] = true },
                new JsonObject { ["name"] = "📍 ขั้นตอน (Step)", ["value"] = stepName, ["inline"] = true },
                new JsonObject { ["name"] = "❌ ข้อความผิดพลาด", ["value"] = $"```{errorMessage}```", ["inline"] = false },
                new JsonObject { ["name"] = "🔍 วิเคราะห์สาเหตุที่แท้จริง", ["value"] = $"```{detailedAnalysis}```", ["inline"] = false },
                new JsonObject { ["name"] = "🛡️ HWID เครื่อง", ["value"] = $"`{hwid}`", ["inline"] = false },
                new JsonObject { ["name"] = "⏰ เวลาที่ตรวจพบ", ["value"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"), ["inline"] = true }
            };

            var embed = new JsonObject
            {
                ["title"] = "🚨 FastLogin Suite — แจ้งเตือนข้อผิดพลาด (Error Alert)",
                ["description"] = $"พบปัญหาในการเข้าสู่ระบบหรือรับรางวัลสำหรับบัญชี `{accountId}`",
                ["color"] = 0xEF5350,
                ["fields"] = fieldsArray,
                ["footer"] = new JsonObject { ["text"] = "FastLogin Suite • Diagnostic Monitoring Engine" },
                ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            if (hasScreenshot)
            {
                embed["image"] = new JsonObject { ["url"] = "attachment://error_screenshot.jpg" };
            }

            var payload = new JsonObject
            {
                ["username"] = "FastLogin Alert 🚨",
                ["avatar_url"] = "https://cdn-icons-png.flaticon.com/512/564/564619.png",
                ["embeds"] = new JsonArray { embed }
            };

            if (hasScreenshot)
            {
                payload["attachments"] = new JsonArray
                {
                    new JsonObject { ["id"] = 0, ["filename"] = "error_screenshot.jpg", ["description"] = "Error Screenshot" }
                };
            }

            return payload.ToJsonString();
        }

        private static string BuildSuccessPayloadJson(string accountId, string detail, string elapsed, string items, string hwid, bool hasScreenshot)
        {
            var fieldsArray = new JsonArray
            {
                new JsonObject { ["name"] = "👤 บัญชี (Account)", ["value"] = $"```{accountId}```", ["inline"] = true },
                new JsonObject { ["name"] = "⏱️ เวลาที่ใช้", ["value"] = elapsed, ["inline"] = true },
                new JsonObject { ["name"] = "📊 สถานะ", ["value"] = "✅ สำเร็จ (Success)", ["inline"] = true },
                new JsonObject { ["name"] = "🎁 ผลลัพธ์ / ของรางวัล", ["value"] = $"```{items}```", ["inline"] = false },
                new JsonObject { ["name"] = "📝 รายละเอียด", ["value"] = detail, ["inline"] = false },
                new JsonObject { ["name"] = "🛡️ HWID เครื่อง", ["value"] = $"`{hwid}`", ["inline"] = false },
                new JsonObject { ["name"] = "⏰ เวลาที่ทำรายการ", ["value"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"), ["inline"] = true }
            };

            var embed = new JsonObject
            {
                ["title"] = "🎉 FastLogin Suite — เข้าสู่ระบบ & รับรางวัลสำเร็จ",
                ["description"] = $"บัญชี `{accountId}` ทำการเข้าสู่ระบบและรับของรางวัลเรียบร้อยแล้ว",
                ["color"] = 0x66D97A,
                ["fields"] = fieldsArray,
                ["footer"] = new JsonObject { ["text"] = "FastLogin Suite • Instant Notification System" },
                ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            if (hasScreenshot)
            {
                embed["image"] = new JsonObject { ["url"] = "attachment://error_screenshot.jpg" };
            }

            var payload = new JsonObject
            {
                ["username"] = "FastLogin Success 🎉",
                ["avatar_url"] = "https://cdn-icons-png.flaticon.com/512/837/837976.png",
                ["embeds"] = new JsonArray { embed }
            };

            if (hasScreenshot)
            {
                payload["attachments"] = new JsonArray
                {
                    new JsonObject { ["id"] = 0, ["filename"] = "error_screenshot.jpg", ["description"] = "Success Screenshot" }
                };
            }

            return payload.ToJsonString();
        }
    }
}
