using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ApibotWarZ.UI.Services
{
    public static class AutoUpdater
    {
        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var asm = typeof(AutoUpdater).Assembly;
                    string? path = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        path = asm.Location;
                    }
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(path);
                        if (!string.IsNullOrWhiteSpace(fvi.ProductVersion))
                        {
                            string pv = fvi.ProductVersion.Split('+')[0].Trim();
                            if (!string.IsNullOrEmpty(pv) && pv != "1.0.0.0") return pv;
                        }
                        if (!string.IsNullOrWhiteSpace(fvi.FileVersion) && fvi.FileVersion != "1.0.0.0")
                        {
                            return fvi.FileVersion.Trim();
                        }
                    }

                    var ver = asm.GetName().Version;
                    if (ver != null && (ver.Major > 0 || ver.Minor > 0 || ver.Build > 0))
                    {
                        if (ver.Build > 0) return $"{ver.Major}.{ver.Minor}.{ver.Build}";
                        return $"{ver.Major}.{ver.Minor}";
                    }
                }
                catch { }
                return "2.1.1";
            }
        }
        private static readonly byte[] ObfuscationKey = Encoding.UTF8.GetBytes("W@rZ_Ap1_B0t_S3cur3_Upd@t3_2026!");

        public class UpdateInfo
        {
            [JsonPropertyName("hasUpdate")]
            public bool HasUpdate { get; set; }

            [JsonPropertyName("latestVersion")]
            public string LatestVersion { get; set; } = string.Empty;

            [JsonPropertyName("downloadToken")]
            public string DownloadToken { get; set; } = string.Empty;

            [JsonPropertyName("downloadUrl")]
            public string DownloadUrl { get; set; } = string.Empty;

            [JsonPropertyName("changelog")]
            public string Changelog { get; set; } = string.Empty;

            [JsonPropertyName("mandatory")]
            public bool Mandatory { get; set; } = true;

            [JsonPropertyName("isTestChannel")]
            public bool IsTestChannel { get; set; } = false;
        }

        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                
                string firebaseUrl = CloudLicenseService.CleanUrl(CloudLicenseService.FirebaseUrl);
                if (string.IsNullOrWhiteSpace(firebaseUrl)) return null;

                // 1. Try /versions/fastlogin.json first; if empty or null, fallback to /versions/apibot.json
                string url = $"{firebaseUrl}/versions/fastlogin.json";
                var resp = await client.GetAsync(url);
                string json = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : "";

                if (string.IsNullOrWhiteSpace(json) || json == "null")
                {
                    url = $"{firebaseUrl}/versions/apibot.json";
                    resp = await client.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) return null;
                    json = await resp.Content.ReadAsStringAsync();
                    if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
                }

                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // ─── 1. Targeted Test Channel (โหมดอัปเดตเฉพาะเครื่องทดสอบ) ───
                bool testEnabled = root.TryGetProperty("testEnabled", out var te) && te.GetBoolean();
                if (testEnabled)
                {
                    string hwid = CloudLicenseService.GetHWID();
                    string machineName = Environment.MachineName;
                    string licenseKey = ConfigManager.Load()?.LicenseKey ?? "";

                    if (IsMachineTargeted(root, hwid, machineName, licenseKey))
                    {
                        string testVer = root.TryGetProperty("testVersion", out var tv) ? tv.GetString() ?? "" : "";
                        string rawTestUrl = root.TryGetProperty("testDownloadUrl", out var tdu) ? tdu.GetString() ?? "" : "";
                        string testCl = root.TryGetProperty("testChangelog", out var tc) ? tc.GetString() ?? "" : "";
                        string resolvedTestUrl = !string.IsNullOrEmpty(rawTestUrl) ? NormalizeDownloadUrl(rawTestUrl) : "";

                        if (IsNewerVersion(testVer, CurrentVersion) && !string.IsNullOrEmpty(resolvedTestUrl))
                        {
                            return new UpdateInfo
                            {
                                HasUpdate = true,
                                LatestVersion = testVer,
                                DownloadUrl = resolvedTestUrl,
                                Changelog = string.IsNullOrWhiteSpace(testCl)
                                    ? "🧪 อัปเดตเวอร์ชันทดสอบเฉพาะเครื่อง (Beta / Test Channel)"
                                    : testCl,
                                IsTestChannel = true,
                                Mandatory = false
                            };
                        }
                    }
                }

                // ─── 2. Fallback: Public Release Channel (เวอร์ชันทั่วไปสำหรับทุกเครื่อง) ───
                string latestVer = root.TryGetProperty("latestVersion", out var lv) ? lv.GetString() ?? "" : "";
                string rawUrl = root.TryGetProperty("downloadUrl", out var du) ? du.GetString() ?? "" : "";
                string cl = root.TryGetProperty("changelog", out var c) ? c.GetString() ?? "" : "";
                string dlToken = root.TryGetProperty("downloadToken", out var dt) ? dt.GetString() ?? "" : "";

                string resolvedUrl = "";
                if (!string.IsNullOrEmpty(dlToken))
                {
                    resolvedUrl = DecryptToken(dlToken);
                }
                else if (!string.IsNullOrEmpty(rawUrl))
                {
                    resolvedUrl = NormalizeDownloadUrl(rawUrl);
                }

                if (IsNewerVersion(latestVer, CurrentVersion) && !string.IsNullOrEmpty(resolvedUrl))
                {
                    return new UpdateInfo
                    {
                        HasUpdate = true,
                        LatestVersion = latestVer,
                        DownloadUrl = resolvedUrl,
                        Changelog = cl,
                        IsTestChannel = false,
                        Mandatory = true
                    };
                }
            }
            catch { }

            return null;
        }

        private static bool IsMachineTargeted(JsonElement root, string hwid, string machineName, string licenseKey)
        {
            try
            {
                if (!root.TryGetProperty("testTargets", out var ttEl)) return false;

                var targets = new List<string>();

                if (ttEl.ValueKind == JsonValueKind.String)
                {
                    string raw = ttEl.GetString() ?? "";
                    var items = raw.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var item in items)
                    {
                        string t = item.Trim();
                        if (!string.IsNullOrEmpty(t)) targets.Add(t);
                    }
                }
                else if (ttEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ttEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string t = item.GetString()?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(t)) targets.Add(t);
                        }
                    }
                }
                else if (ttEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in ttEl.EnumerateObject())
                    {
                        string t = prop.Name.Trim();
                        if (!string.IsNullOrEmpty(t)) targets.Add(t);
                    }
                }

                if (targets.Count == 0) return false;

                foreach (var target in targets)
                {
                    if (string.IsNullOrWhiteSpace(target)) continue;

                    // Match HWID (full or prefix >= 6 chars)
                    if (!string.IsNullOrEmpty(hwid))
                    {
                        if (hwid.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
                        if (target.Length >= 6 && hwid.StartsWith(target, StringComparison.OrdinalIgnoreCase)) return true;
                        if (hwid.Length >= 6 && target.StartsWith(hwid, StringComparison.OrdinalIgnoreCase)) return true;
                    }

                    // Match MachineName
                    if (!string.IsNullOrEmpty(machineName))
                    {
                        if (machineName.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
                    }

                    // Match LicenseKey
                    if (!string.IsNullOrEmpty(licenseKey))
                    {
                        if (licenseKey.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
                        if (target.Length >= 6 && licenseKey.Contains(target, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch { }

            return false;
        }

        public static string DecryptToken(string base64Token)
        {
            try
            {
                byte[] data = Convert.FromBase64String(base64Token);
                byte[] decrypted = new byte[data.Length];

                for (int i = 0; i < data.Length; i++)
                {
                    decrypted[i] = (byte)(data[i] ^ ObfuscationKey[i % ObfuscationKey.Length]);
                }

                string rawUrl = Encoding.UTF8.GetString(decrypted);
                return NormalizeDownloadUrl(rawUrl);
            }
            catch
            {
                return "";
            }
        }

        public static string NormalizeDownloadUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            url = url.Trim();

            if (url.Contains("drive.google.com") || url.Contains("drive.usercontent.google.com"))
            {
                string fileId = "";
                if (url.Contains("/d/"))
                {
                    int start = url.IndexOf("/d/") + 3;
                    int end = url.IndexOf("/", start);
                    if (end == -1) end = url.IndexOf("?", start);
                    if (end == -1) end = url.Length;
                    fileId = url.Substring(start, end - start);
                }
                else if (url.Contains("id="))
                {
                    int start = url.IndexOf("id=") + 3;
                    int end = url.IndexOf("&", start);
                    if (end == -1) end = url.Length;
                    fileId = url.Substring(start, end - start);
                }

                if (!string.IsNullOrEmpty(fileId))
                {
                    return $"https://drive.usercontent.google.com/download?id={fileId}&export=download&confirm=t";
                }
            }

            if (url.Contains("dropbox.com"))
            {
                return url.Replace("?dl=0", "?dl=1");
            }

            return url;
        }

        private static bool IsNewerVersion(string serverVer, string localVer)
        {
            try
            {
                var sParts = serverVer.Trim().TrimStart('v', 'V').Split('.');
                var lParts = localVer.Trim().TrimStart('v', 'V').Split('.');

                int maxLen = Math.Max(sParts.Length, lParts.Length);
                for (int i = 0; i < maxLen; i++)
                {
                    int sVal = i < sParts.Length ? ParseLeadingInt(sParts[i]) : 0;
                    int lVal = i < lParts.Length ? ParseLeadingInt(lParts[i]) : 0;

                    if (sVal > lVal) return true;
                    if (sVal < lVal) return false;
                }
            }
            catch { }
            return false;
        }

        private static int ParseLeadingInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i > 0 && int.TryParse(s.Substring(0, i), out int val)) return val;
            return 0;
        }

        public static void ApplyUpdateAndRestart(string downloadedFilePath)
        {
            try
            {
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName 
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FastLogin.Launcher.exe");
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                string updaterScript = Path.Combine(currentDir, "update.bat");

                bool isZip = downloadedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                string batchContent;
                if (isZip)
                {
                    batchContent = $@"@echo off
timeout /t 1 /nobreak > nul
:retry
taskkill /F /IM FastLogin.Launcher.exe > nul 2>&1
taskkill /F /IM FastLogin.exe > nul 2>&1
taskkill /F /IM server.exe > nul 2>&1
powershell -Command ""Expand-Archive -Path '{downloadedFilePath}' -DestinationPath '{currentDir}' -Force"" > nul 2>&1
del ""{downloadedFilePath}"" > nul 2>&1
if exist ""{downloadedFilePath}"" (
    timeout /t 1 /nobreak > nul
    goto retry
)
start """" ""{currentExePath}""
del ""%~f0""
";
                }
                else
                {
                    string targetName = Path.GetFileName(downloadedFilePath).Replace("FastLogin_update", "FastLogin.Launcher");
                    string targetPath = Path.Combine(currentDir, targetName);

                    batchContent = $@"@echo off
timeout /t 1 /nobreak > nul
:retry
taskkill /F /IM FastLogin.Launcher.exe > nul 2>&1
taskkill /F /IM FastLogin.exe > nul 2>&1
taskkill /F /IM server.exe > nul 2>&1
move /y ""{downloadedFilePath}"" ""{targetPath}"" > nul 2>&1
if exist ""{downloadedFilePath}"" (
    timeout /t 1 /nobreak > nul
    goto retry
)
start """" ""{currentExePath}""
del ""%~f0""
";
                }

                File.WriteAllText(updaterScript, batchContent);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{updaterScript}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = currentDir
                };

                Process.Start(psi);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"เกิดข้อผิดพลาดในการอัปเดต: {ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
