using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ApibotWarZ.UI.Services
{
    public static class DiscordNotifier
    {
        private const string WebhookUrl = "https://discord.com/api/webhooks/1540393576658378816/j6Cao3E0ZgFwszKjGt5K5jK-n1of3yujQsrARbH4peHG54CxB-xBVI7g7SViAVcPfK2l";
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static async Task SendLoginAlertAsync(string licenseKey, string expiry, string hwid)
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var pcInfo = await GatherSystemSpecsAsync();

                        var embed = new
                        {
                            title = "🟢 Apibot WarZ — User Login / Key Activated",
                            description = "มีผู้ใช้งานเข้าสู่ระบบโปรแกรม Apibot WarZ (Auto Register)",
                            color = 0x6C63FF,
                            fields = new object[]
                            {
                                new { name = "🔑 License Key", value = $"```{licenseKey}```", inline = false },
                                new { name = "⏳ อายุการใช้งาน (Expiry)", value = expiry, inline = true },
                                new { name = "🛡️ HWID รหัสเครื่อง", value = $"```{hwid}```", inline = false },
                                new { name = "👤 เครื่อง / User", value = $"{pcInfo.UserName} @ {pcInfo.MachineName}", inline = true },
                                new { name = "💻 ระบบปฏิบัติการ (OS)", value = pcInfo.OsName, inline = true },
                                new { name = "⚙️ CPU", value = $"{pcInfo.CpuName} ({pcInfo.CpuCores} Cores)", inline = false },
                                new { name = "🎮 GPU (การ์ดจอ)", value = pcInfo.GpuName, inline = true },
                                new { name = "🧠 RAM (แรม)", value = $"{pcInfo.TotalRamGb:F1} GB", inline = true },
                                new { name = "🖥️ ความละเอียดหน้าจอ", value = pcInfo.Resolution, inline = true },
                                new { name = "🌐 IP / Network", value = $"{pcInfo.PublicIp} ({pcInfo.Location})", inline = false },
                                new { name = "⏰ เวลาที่เข้าใช้งาน", value = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"), inline = true }
                            },
                            footer = new
                            {
                                text = "Apibot WarZ • Cloud Security"
                            },
                            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        };

                        var payload = new
                        {
                            username = "Apibot Security",
                            avatar_url = "https://cdn-icons-png.flaticon.com/512/2092/2092663.png",
                            embeds = new[] { embed }
                        };

                        string json = JsonSerializer.Serialize(payload);
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        await _http.PostAsync(WebhookUrl, content);
                    }
                    catch { }
                });
            }
            catch { }
        }

        private class SystemSpecs
        {
            public string MachineName { get; set; } = Environment.MachineName;
            public string UserName { get; set; } = Environment.UserName;
            public string OsName { get; set; } = "Windows";
            public string CpuName { get; set; } = "Unknown CPU";
            public int CpuCores { get; set; } = Environment.ProcessorCount;
            public double TotalRamGb { get; set; } = 0;
            public string GpuName { get; set; } = "Unknown GPU";
            public string Resolution { get; set; } = "Unknown";
            public string PublicIp { get; set; } = "Unknown";
            public string Location { get; set; } = "Unknown";
        }

        private static async Task<SystemSpecs> GatherSystemSpecsAsync()
        {
            var specs = new SystemSpecs();

            // 1. OS Name
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string prodName = key?.GetValue("ProductName")?.ToString() ?? "";
                string displayVer = key?.GetValue("DisplayVersion")?.ToString() ?? "";
                specs.OsName = string.IsNullOrEmpty(displayVer) ? prodName : $"{prodName} ({displayVer})";
                if (string.IsNullOrWhiteSpace(specs.OsName)) specs.OsName = Environment.OSVersion.ToString();
            }
            catch { }

            // 2. CPU Name
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                specs.CpuName = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(specs.CpuName))
                {
                    specs.CpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Generic CPU";
                }
            }
            catch { }

            // 3. RAM (GB)
            try
            {
                var mem = new MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref mem))
                {
                    specs.TotalRamGb = (double)mem.ullTotalPhys / (1024 * 1024 * 1024);
                }
            }
            catch { }

            // 4. GPU Name
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
                specs.GpuName = key?.GetValue("DriverDesc")?.ToString() ?? "Standard Display Adapter";
            }
            catch { }

            // 5. Screen Resolution
            try
            {
                var screen = Screen.PrimaryScreen;
                if (screen != null)
                {
                    specs.Resolution = $"{screen.Bounds.Width} x {screen.Bounds.Height}";
                }
            }
            catch { }

            // 6. Public IP & Location
            try
            {
                var ipRes = await _http.GetStringAsync("http://ip-api.com/json");
                using var doc = JsonDocument.Parse(ipRes);
                var root = doc.RootElement;
                if (root.TryGetProperty("query", out var q)) specs.PublicIp = q.GetString() ?? "Unknown";
                string country = root.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                string city = root.TryGetProperty("city", out var ci) ? ci.GetString() ?? "" : "";
                specs.Location = string.IsNullOrEmpty(city) ? country : $"{city}, {country}";
            }
            catch
            {
                specs.PublicIp = "Local / Hidden";
                specs.Location = "Thailand";
            }

            return specs;
        }
    }
}
