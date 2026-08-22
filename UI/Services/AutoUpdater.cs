using System;
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
        public static readonly string CurrentVersion = "1.4";
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
        }

        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                
                string hwid = CloudLicenseService.GetHWID();
                string url = $"{CloudLicenseService.WebAppUrl}?action=checkUpdate&app=apibot&currentVer={CurrentVersion}&hwid={Uri.EscapeDataString(hwid)}";
                
                string json = await client.GetStringAsync(url);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("success", out var s) && s.GetBoolean())
                {
                    string latestVer = root.TryGetProperty("latestVersion", out var lv) ? lv.GetString() ?? "" : "";
                    string dlToken = root.TryGetProperty("downloadToken", out var dt) ? dt.GetString() ?? "" : "";
                    string rawUrl = root.TryGetProperty("downloadUrl", out var du) ? du.GetString() ?? "" : "";
                    string cl = root.TryGetProperty("changelog", out var c) ? c.GetString() ?? "" : "";

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
                            Changelog = cl
                        };
                    }
                }
            }
            catch { }

            return null;
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
                    int sVal = i < sParts.Length && int.TryParse(sParts[i], out int sv) ? sv : 0;
                    int lVal = i < lParts.Length && int.TryParse(lParts[i], out int lv) ? lv : 0;

                    if (sVal > lVal) return true;
                    if (sVal < lVal) return false;
                }
            }
            catch { }
            return false;
        }

        public static void ApplyUpdateAndRestart(string downloadedFilePath)
        {
            try
            {
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName 
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ApibotWarZ.Launcher.exe");
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                string updaterScript = Path.Combine(currentDir, "update.bat");

                bool isZip = downloadedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                string batchContent;
                if (isZip)
                {
                    batchContent = $@"@echo off
timeout /t 1 /nobreak > nul
:retry
taskkill /F /IM ApibotWarZ.Launcher.exe > nul 2>&1
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
                    string targetName = Path.GetFileName(downloadedFilePath).Replace("ApibotWarZ_update", "ApibotWarZ.Launcher");
                    string targetPath = Path.Combine(currentDir, targetName);

                    batchContent = $@"@echo off
timeout /t 1 /nobreak > nul
:retry
taskkill /F /IM ApibotWarZ.Launcher.exe > nul 2>&1
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
