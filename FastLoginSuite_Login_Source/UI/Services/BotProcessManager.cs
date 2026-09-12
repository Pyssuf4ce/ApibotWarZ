using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ApibotWarZ.UI.Models;

namespace ApibotWarZ.UI.Services
{
    public class BotProcessManager
    {
        private Process? _botProcess;
        private Process? _serverProcess;
        private bool _isRunning = false;
        private readonly object _lock = new object();

        public bool IsRunning
        {
            get { lock (_lock) return _isRunning; }
            private set { lock (_lock) _isRunning = value; }
        }

        public event Action<string, Color>? OnLog;
        public event Action<RegisteredAccount>? OnAccountRegistered;
        public event Action<string>? OnAccountRunning;
        public event Action<string, string, string>? OnAccountSuccess;
        public event Action<string, string, string>? OnAccountLimit;
        public event Action<string, string, string>? OnAccountRetry;
        public event Action<string, string, string>? OnAccountFail;
        public event Action<bool>? OnStateChanged;
        public event Action<string>? OnVpnChanged;

        public static string GetWorkingDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, "FastLogin.exe")) || File.Exists(Path.Combine(baseDir, "login_main.go")) || File.Exists(Path.Combine(baseDir, "FastLoginSuite.exe")))
            {
                return baseDir;
            }

            // Check parent directory (if running from bin/Debug/net10.0-windows etc.)
            string? parent = Directory.GetParent(baseDir)?.FullName;
            while (parent != null)
            {
                if (File.Exists(Path.Combine(parent, "FastLogin.exe")) || File.Exists(Path.Combine(parent, "login_main.go")) || File.Exists(Path.Combine(parent, "FastLoginSuite.exe")))
                {
                    return parent;
                }
                var next = Directory.GetParent(parent);
                if (next == null || next.FullName == parent) break;
                parent = next.FullName;
            }

            return baseDir;
        }

        public void KillExistingProcesses()
        {
            string[] targets = { "openvpn.exe", "server.exe", "FastLogin.exe", "FastLoginSuite.exe", "main.exe" };
            foreach (var target in targets)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/F /IM {target}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(1000);
                }
                catch { }
            }

            try
            {
                var psiDns = new ProcessStartInfo
                {
                    FileName = "ipconfig",
                    Arguments = "/flushdns",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psiDns);
                p?.WaitForExit(1000);
            }
            catch { }
        }

        public async Task<bool> StartAsync(int threads, bool startCaptchaServer, string accountsFile = "accounts.txt")
        {
            if (IsRunning) return false;

            string workDir = GetWorkingDir();
            EmitLog($"[Launcher] 📂 Working Directory: {workDir}", Color.FromArgb(180, 180, 200));

            // Clean up previous runs
            KillExistingProcesses();
            await Task.Delay(500);

            // 1. Start Captcha Server (Python or Compiled server.exe)
            if (startCaptchaServer)
            {
                EmitLog("[Launcher] ⚡ กำลังเริ่มต้น Captcha Worker Engine...", Color.FromArgb(120, 200, 255));
                string serverPy = Path.Combine(workDir, "server.py");
                string serverExe = Path.Combine(workDir, "server.exe");

                bool started = await StartCaptchaProcessAsync(workDir, serverPy, serverExe, threads);
                if (!started)
                {
                    EmitLog("[Launcher] ❌ Captcha Worker ไม่สามารถทำงานได้ กรุณาตรวจสอบว่าติดตั้ง Python หรือมี server.exe", Color.FromArgb(255, 80, 80));
                    return false;
                }
            }

            // 2. Start FastLogin Engine
            string botExe = Path.Combine(workDir, "FastLogin.exe");
            if (!File.Exists(botExe))
            {
                botExe = Path.Combine(workDir, "FastLoginSuite.exe");
            }

            ProcessStartInfo botPsi;
            if (File.Exists(botExe))
            {
                botPsi = new ProcessStartInfo
                {
                    FileName = botExe,
                    Arguments = $"-threads {threads} -file \"{accountsFile}\"",
                    WorkingDirectory = workDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
            }
            else
            {
                // Fallback to go run if executable not yet built
                botPsi = new ProcessStartInfo
                {
                    FileName = "go",
                    Arguments = $"run login_main.go -threads {threads} -file \"{accountsFile}\"",
                    WorkingDirectory = workDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
            }

            try
            {
                _botProcess = new Process { StartInfo = botPsi, EnableRaisingEvents = true };
                _botProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        ParseBotLog(e.Data);
                    }
                };
                _botProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        ParseBotLog(e.Data);
                    }
                };
                _botProcess.Exited += (s, e) =>
                {
                    if (IsRunning)
                    {
                        EmitLog("[Launcher] ℹ️ Go Bot Process สิ้นสุดการทำงาน", Color.FromArgb(200, 200, 200));
                        Stop();
                    }
                };

                _botProcess.Start();
                _botProcess.BeginOutputReadLine();
                _botProcess.BeginErrorReadLine();

                IsRunning = true;
                OnStateChanged?.Invoke(true);
                EmitLog($"[Launcher] 🚀 เริ่มต้น ApibotWarZ สำเร็จ (Threads: {threads})", Color.FromArgb(60, 220, 130));
                return true;
            }
            catch (Exception ex)
            {
                EmitLog($"[Launcher] ❌ เปิด ApibotWarZ ไม่สำเร็จ: {ex.Message}", Color.FromArgb(255, 80, 80));
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning && _botProcess == null && _serverProcess == null) return;

            IsRunning = false;
            OnStateChanged?.Invoke(false);
            EmitLog("[Launcher] 🛑 กำลังหยุดการทำงานของทุก Process...", Color.FromArgb(255, 140, 60));

            try
            {
                if (_botProcess != null && !_botProcess.HasExited)
                {
                    _botProcess.Kill(true);
                    _botProcess.Dispose();
                }
            }
            catch { }
            finally { _botProcess = null; }

            try
            {
                if (_serverProcess != null && !_serverProcess.HasExited)
                {
                    _serverProcess.Kill(true);
                    _serverProcess.Dispose();
                }
            }
            catch { }
            finally { _serverProcess = null; }

            KillExistingProcesses();
            EmitLog("[Launcher] ✅ หยุดการทำงานเรียบร้อยแล้ว", Color.FromArgb(150, 150, 170));
        }

        private void ParseBotLog(string line)
        {
            Color color = Color.FromArgb(220, 220, 230);

            if (line.Contains("🌟 สมัครสมาชิกเสร็จสมบูรณ์") || line.Contains("[ACC_SUCCESS]") || line.Contains("🎉 สมัครสมาชิกสำเร็จเรียบร้อย"))
            {
                color = Color.FromArgb(60, 225, 130); // Bright Green
            }
            else if (line.Contains("❌") || line.Contains("💥") || line.Contains("Error") || line.Contains("panic"))
            {
                color = Color.FromArgb(255, 90, 90); // Red
            }
            else if (line.Contains("⚠️") || line.Contains("ติด Limit IP") || line.Contains("โดนบล็อค IP"))
            {
                color = Color.FromArgb(255, 180, 70); // Orange/Amber
            }
            else if (line.Contains("VPN") || line.Contains("สลับ VPN"))
            {
                color = Color.FromArgb(80, 190, 255); // Cyan
            }
            else if (line.Contains("🧩") || line.Contains("Captcha") || line.Contains("Turnstile"))
            {
                color = Color.FromArgb(190, 140, 255); // Purple
            }
            else if (line.Contains("📧") || line.Contains("Temp Mail") || line.Contains("OTP"))
            {
                color = Color.FromArgb(255, 220, 100); // Yellow
            }

            // Extract account status updates
            if (line.StartsWith("[ACC_RUNNING]"))
            {
                string username = line.Substring("[ACC_RUNNING]".Length).Trim();
                if (!string.IsNullOrEmpty(username))
                {
                    OnAccountRunning?.Invoke(username);
                }
            }
            else if (line.StartsWith("[ACC_SUCCESS]"))
            {
                string content = line.Substring("[ACC_SUCCESS]".Length).Trim();
                var parts = content.Split('|');
                string username = parts.Length > 0 ? parts[0].Trim() : "";
                string time = parts.Length > 1 ? parts[1].Trim() : "-";
                string detail = parts.Length > 2 ? parts[2].Trim() : "เข้าสู่ระบบสำเร็จ";

                if (!string.IsNullOrEmpty(username))
                {
                    OnAccountSuccess?.Invoke(username, time, detail);
                }
            }
            else if (line.StartsWith("[ACC_LIMIT]"))
            {
                string content = line.Substring("[ACC_LIMIT]".Length).Trim();
                var parts = content.Split('|');
                string username = parts.Length > 0 ? parts[0].Trim() : "";
                string time = parts.Length > 1 ? parts[1].Trim() : "-";
                string reason = parts.Length > 2 ? parts[2].Trim() : "ติด Limit Cloudflare ชั่วคราว (รอรันซ้ำ)";

                if (!string.IsNullOrEmpty(username))
                {
                    OnAccountLimit?.Invoke(username, time, reason);
                }
            }
            else if (line.StartsWith("[ACC_RETRY]"))
            {
                string content = line.Substring("[ACC_RETRY]".Length).Trim();
                var parts = content.Split('|');
                string username = parts.Length > 0 ? parts[0].Trim() : "";
                string time = parts.Length > 1 ? parts[1].Trim() : "-";
                string reason = parts.Length > 2 ? parts[2].Trim() : "เกิดปัญหาชั่วคราว (รอรันซ้ำ)";

                if (!string.IsNullOrEmpty(username))
                {
                    OnAccountRetry?.Invoke(username, time, reason);
                }
            }
            else if (line.StartsWith("[ACC_FAIL]"))
            {
                string content = line.Substring("[ACC_FAIL]".Length).Trim();
                var parts = content.Split('|');
                string username = parts.Length > 0 ? parts[0].Trim() : "";
                string time = parts.Length > 1 ? parts[1].Trim() : "-";
                string reason = parts.Length > 2 ? parts[2].Trim() : "เข้าสู่ระบบไม่สำเร็จ";

                if (!string.IsNullOrEmpty(username))
                {
                    OnAccountFail?.Invoke(username, time, reason);
                }
            }

            // Detect VPN change (ดักจับทุกรูปแบบ เช่น "ได้รับ IP ใหม่: ...", "ตรวจพบ IP: ...")
            if (line.Contains("IP") || line.Contains("VPN"))
            {
                var match = Regex.Match(line, @"(?:IP ใหม่:\s*|ตรวจพบ IP:\s*|IP:\s*|VPN:\s*|เชื่อมต่อ\s*)([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})");
                if (match.Success)
                {
                    OnVpnChanged?.Invoke(match.Groups[1].Value);
                }
                else
                {
                    var fallbackMatch = Regex.Match(line, @"([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})");
                    if (fallbackMatch.Success && (line.Contains("เชื่อมต่อ") || line.Contains("สำเร็จ") || line.Contains("Connected")))
                    {
                        OnVpnChanged?.Invoke(fallbackMatch.Groups[1].Value);
                    }
                }
            }

            EmitLog(line, color);
        }

        private void ParseServerLog(string line)
        {
            Color color = Color.FromArgb(170, 175, 195);
            if (line.Contains("🎉") || line.Contains("ผ่านด่านสำเร็จ"))
            {
                color = Color.FromArgb(80, 210, 140);
            }
            else if (line.Contains("❌") || line.Contains("error"))
            {
                color = Color.FromArgb(255, 100, 100);
            }
            else if (line.Contains("⚡") || line.Contains("Smart Click Engine"))
            {
                color = Color.FromArgb(160, 130, 255);
            }

            EmitLog($"[Captcha] {line}", color);
        }

        private async Task<bool> StartCaptchaProcessAsync(string workDir, string serverPy, string serverExe, int threads)
        {
            // Approach 1: If server.py exists, try running via Python directly
            if (File.Exists(serverPy))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"server.py --threads {threads}",
                        WorkingDirectory = workDir,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

                    _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _serverProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ParseServerLog(e.Data); };
                    _serverProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ParseServerLog(e.Data); };

                    _serverProcess.Start();
                    _serverProcess.BeginOutputReadLine();
                    _serverProcess.BeginErrorReadLine();

                    if (await WaitForServerReadyAsync(15000))
                    {
                        EmitLog("[Launcher] ✅ Captcha Worker Engine พร้อมใช้งานแล้ว (http://127.0.0.1:5000)", Color.FromArgb(60, 220, 130));
                        return true;
                    }

                    // If python started but failed health check, kill and try exe
                    if (_serverProcess != null && !_serverProcess.HasExited)
                    {
                        _serverProcess.Kill(true);
                        _serverProcess.Dispose();
                        _serverProcess = null;
                    }
                }
                catch { }
            }

            // Approach 2: Try running compiled server.exe
            if (File.Exists(serverExe))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = serverExe,
                        Arguments = $"--threads {threads}",
                        WorkingDirectory = workDir,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

                    _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _serverProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ParseServerLog(e.Data); };
                    _serverProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ParseServerLog(e.Data); };

                    _serverProcess.Start();
                    _serverProcess.BeginOutputReadLine();
                    _serverProcess.BeginErrorReadLine();

                    if (await WaitForServerReadyAsync(15000))
                    {
                        EmitLog("[Launcher] ✅ Captcha Worker Engine (EXE) พร้อมใช้งานแล้ว (http://127.0.0.1:5000)", Color.FromArgb(60, 220, 130));
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private async Task<bool> WaitForServerReadyAsync(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(600) };

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_serverProcess != null && _serverProcess.HasExited)
                {
                    return false;
                }

                try
                {
                    var resp = await client.GetAsync("http://127.0.0.1:5000/docs");
                    if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 404)
                    {
                        return true;
                    }
                }
                catch
                {
                    await Task.Delay(400);
                }
            }

            return false;
        }

        private void EmitLog(string message, Color color)
        {
            OnLog?.Invoke(message, color);
        }
    }
}
