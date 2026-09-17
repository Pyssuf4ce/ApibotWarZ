using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ApibotWarZ.UI.Models;

namespace ApibotWarZ.UI.Services
{
    public class LoginServerResponse
    {
        public string Status { get; set; } = "failed";
        public bool IsLimit { get; set; }
        public bool IsBadPwd { get; set; }
        public string Detail { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Elapsed { get; set; } = "0.0s";
        public string Items { get; set; } = string.Empty;
        public string RedeemStatus { get; set; } = string.Empty;
    }

    public class HarvestSessionService
    {
        private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 20
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private const string ServerBaseUrl = "http://127.0.0.1:5000";

        public async Task<bool> WaitForServerHealthAsync(int maxAttempts = 20, CancellationToken ct = default)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                if (ct.IsCancellationRequested) return false;
                try
                {
                    using var resp = await _httpClient.GetAsync($"{ServerBaseUrl}/health", ct);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch { }
                await Task.Delay(500, ct);
            }
            return false;
        }

        public async Task<bool> PrepareBatchAsync(int count, int batchNum, int totalBatches, CancellationToken ct = default)
        {
            try
            {
                var payload = new { count, batch_num = batchNum, total_batches = totalBatches };
                using var resp = await _httpClient.PostAsJsonAsync($"{ServerBaseUrl}/batch/prepare", payload, ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task CloseBatchAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _httpClient.PostAsync($"{ServerBaseUrl}/batch/close", null, ct);
            }
            catch { }
        }

        public async Task<LoginServerResponse> LoginWorkerAsync(int workerId, string username, string password, string eventId, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var payload = new
                {
                    url = "https://passport.thehof.gg/hall-of-fame-web/login",
                    username,
                    password,
                    event_id = eventId,
                    worker_id = workerId
                };

                using var resp = await _httpClient.PostAsJsonAsync($"{ServerBaseUrl}/login", payload, ct);
                sw.Stop();
                string elapsed = $"{sw.Elapsed.TotalSeconds:F1}s";

                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var node = JsonNode.Parse(json);
                    string status = node?["status"]?.GetValue<string>() ?? "failed";
                    bool isLimit = node?["is_limit"]?.GetValue<bool>() ?? false;
                    bool isBadPwd = node?["is_bad_pwd"]?.GetValue<bool>() ?? false;
                    string detail = node?["detail"]?.GetValue<string>() ?? "เข้าสู่ระบบสำเร็จ";
                    string reason = node?["reason"]?.GetValue<string>() ?? "";
                    string time = node?["time"]?.GetValue<string>() ?? elapsed;
                    string items = node?["items"]?.GetValue<string>() ?? "";
                    string redeemStatus = node?["redeem_status"]?.GetValue<string>() ?? "";

                    if (string.IsNullOrEmpty(reason)) reason = detail;

                    return new LoginServerResponse
                    {
                        Status = status,
                        IsLimit = isLimit,
                        IsBadPwd = isBadPwd,
                        Detail = detail,
                        Reason = reason,
                        Elapsed = time,
                        Items = items,
                        RedeemStatus = redeemStatus
                    };
                }
                else
                {
                    return new LoginServerResponse
                    {
                        Status = "failed",
                        Reason = $"Server HTTP {(int)resp.StatusCode}",
                        Elapsed = elapsed
                    };
                }
            }
            catch (OperationCanceledException)
            {
                return new LoginServerResponse { Status = "canceled", Reason = "ผู้ใช้สั่งหยุด", Elapsed = $"{sw.Elapsed.TotalSeconds:F1}s" };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new LoginServerResponse { Status = "failed", Reason = ex.Message, Elapsed = $"{sw.Elapsed.TotalSeconds:F1}s" };
            }
        }

        public async Task ExecuteHarvestBatchAsync(
            List<RegisteredAccount> accounts,
            string eventId,
            int concurrency,
            Action<string, Color> onLog,
            Action<string> onRunning,
            Action<string, string, string> onSuccess,
            Action<string, string, string> onLimit,
            Action<string, string, string> onRetry,
            Action<string, string, string> onFail,
            Action<int, int, int, string> onCompleted,
            CancellationToken ct = default)
        {
            var swTotal = Stopwatch.StartNew();
            int threadsLimit = Math.Max(1, Math.Min(concurrency, 5));

            var pendingAccounts = accounts.Where(a => !a.IsCompleted).ToList();
            if (pendingAccounts.Count == 0)
            {
                onLog?.Invoke("🎉 ทุกบัญชีในรายการดำเนินการสำเร็จเรียบร้อยแล้ว!", Color.FromArgb(52, 211, 153));
                onCompleted?.Invoke(accounts.Count, 0, 0, "0.0s");
                return;
            }

            onLog?.Invoke($"🚀 เริ่มต้นเก็บ Session Token ทั้งหมด {pendingAccounts.Count} บัญชี (รอบละ {threadsLimit} จอ)...", Color.FromArgb(120, 200, 255));

            int totalBatches = (int)Math.Ceiling(pendingAccounts.Count / (double)threadsLimit);
            var retryQueue = new List<RegisteredAccount>();
            int successCount = accounts.Count(a => a.IsCompleted);
            int failCount = 0;

            for (int batchIdx = 0; batchIdx < totalBatches; batchIdx++)
            {
                if (ct.IsCancellationRequested) break;

                var batch = pendingAccounts.Skip(batchIdx * threadsLimit).Take(threadsLimit).ToList();
                int batchNum = batchIdx + 1;

                onLog?.Invoke($"\n======================================================", Color.FromArgb(70, 80, 110));
                onLog?.Invoke($"📦 [รอบที่ {batchNum}/{totalBatches}] เริ่มต้นรอบใหม่สำหรับ {batch.Count} บัญชี", Color.FromArgb(200, 215, 255));
                onLog?.Invoke($"======================================================", Color.FromArgb(70, 80, 110));

                bool prepared = await PrepareBatchAsync(batch.Count, batchNum, totalBatches, ct);
                if (!prepared && !ct.IsCancellationRequested)
                {
                    onLog?.Invoke($"⚠️ การเตรียมแท็บในรอบที่ {batchNum} ติดขัด พยายามดำเนินการต่อ...", Color.FromArgb(251, 191, 36));
                }

                var tasks = new List<Task>();
                for (int i = 0; i < batch.Count; i++)
                {
                    int workerId = i + 1;
                    var acc = batch[i];

                    tasks.Add(Task.Run(async () =>
                    {
                        if (ct.IsCancellationRequested) return;

                        onRunning?.Invoke(acc.Username);
                        onLog?.Invoke($"[Bot-{workerId}] 🔓 กำลังเข้าสู่ระบบและตรวจสอบรางวัลสำหรับ '{acc.Username}'...", Color.FromArgb(160, 175, 210));

                        var res = await LoginWorkerAsync(workerId, acc.Username, acc.Password, eventId, ct);
                        if (ct.IsCancellationRequested) return;

                        if (res.Status == "success")
                        {
                            Interlocked.Increment(ref successCount);
                            acc.SessionStatus = "🟢 พร้อมใช้งาน";
                            string detail = string.IsNullOrEmpty(res.Detail) ? "เข้าสู่ระบบสำเร็จ" : res.Detail;
                            if (res.RedeemStatus == "success")
                            {
                                onLog?.Invoke($"[Bot-{workerId}] 🎉 [{acc.Username}] ล็อกอิน & รับรางวัลสำเร็จ ({res.Elapsed}) ➔ {res.Items}", Color.FromArgb(52, 211, 153));
                            }
                            else if (res.RedeemStatus == "already_claimed")
                            {
                                onLog?.Invoke($"[Bot-{workerId}] 🟡 [{acc.Username}] ล็อกอินสำเร็จ (เคยรับรางวัลไปแล้ว) ({res.Elapsed})", Color.FromArgb(251, 191, 36));
                            }
                            else
                            {
                                onLog?.Invoke($"[Bot-{workerId}] 🎉 [{acc.Username}] เข้าสู่ระบบสำเร็จ ({res.Elapsed}) ➔ {detail}", Color.FromArgb(52, 211, 153));
                            }
                            onSuccess?.Invoke(acc.Username, res.Elapsed, detail);
                        }
                        else
                        {
                            if (res.IsLimit)
                            {
                                onLog?.Invoke($"[Bot-{workerId}] ⚠️ บัญชี '{acc.Username}' ติด Limit IP ชั่วคราว ({res.Elapsed}) ➔ นำเข้าคิวรันซ้ำ", Color.FromArgb(251, 191, 36));
                                onLimit?.Invoke(acc.Username, res.Elapsed, res.Reason);
                                lock (retryQueue) retryQueue.Add(acc);
                            }
                            else if (res.Status == "retry")
                            {
                                onLog?.Invoke($"[Bot-{workerId}] 🔄 บัญชี '{acc.Username}' หมดเวลาตอบกลับ ({res.Elapsed}) ➔ นำเข้าคิวรันซ้ำ", Color.FromArgb(147, 197, 253));
                                onRetry?.Invoke(acc.Username, res.Elapsed, res.Reason);
                                lock (retryQueue) retryQueue.Add(acc);
                            }
                            else
                            {
                                Interlocked.Increment(ref failCount);
                                onLog?.Invoke($"[Bot-{workerId}] ❌ บัญชี '{acc.Username}' ไม่สำเร็จ: {res.Reason} ({res.Elapsed})", Color.FromArgb(248, 113, 113));
                                onFail?.Invoke(acc.Username, res.Elapsed, res.Reason);
                            }
                        }
                    }, ct));
                }

                await Task.WhenAll(tasks);
                await CloseBatchAsync(CancellationToken.None);
                await Task.Delay(1000, CancellationToken.None);
            }

            if (!ct.IsCancellationRequested && retryQueue.Count > 0)
            {
                onLog?.Invoke($"\n🔄 [รอบเก็บตก] พบไอดีที่ต้องรันซ้ำ {retryQueue.Count} บัญชี กำลังเริ่มดำเนินการ...", Color.FromArgb(147, 197, 253));
                int retryBatches = (int)Math.Ceiling(retryQueue.Count / (double)threadsLimit);

                for (int rIdx = 0; rIdx < retryBatches; rIdx++)
                {
                    if (ct.IsCancellationRequested) break;

                    var batch = retryQueue.Skip(rIdx * threadsLimit).Take(threadsLimit).ToList();
                    await PrepareBatchAsync(batch.Count, rIdx + 1, retryBatches, ct);

                    var tasks = new List<Task>();
                    for (int i = 0; i < batch.Count; i++)
                    {
                        int workerId = i + 1;
                        var acc = batch[i];

                        tasks.Add(Task.Run(async () =>
                        {
                            if (ct.IsCancellationRequested) return;

                            onRunning?.Invoke(acc.Username);
                            var res = await LoginWorkerAsync(workerId, acc.Username, acc.Password, eventId, ct);
                            if (ct.IsCancellationRequested) return;

                            if (res.Status == "success")
                            {
                                Interlocked.Increment(ref successCount);
                                acc.SessionStatus = "🟢 พร้อมใช้งาน";
                                string detail = string.IsNullOrEmpty(res.Detail) ? "เข้าสู่ระบบสำเร็จ" : res.Detail;
                                onLog?.Invoke($"[Bot-{workerId}] 🎉 [{acc.Username}] เข้าสู่ระบบสำเร็จ ({res.Elapsed}) ➔ {detail}", Color.FromArgb(52, 211, 153));
                                onSuccess?.Invoke(acc.Username, res.Elapsed, detail);
                            }
                            else
                            {
                                Interlocked.Increment(ref failCount);
                                onLog?.Invoke($"[Bot-{workerId}] ❌ บัญชี '{acc.Username}' ล้มเหลว: {res.Reason} ({res.Elapsed})", Color.FromArgb(248, 113, 113));
                                onFail?.Invoke(acc.Username, res.Elapsed, res.Reason);
                            }
                        }, ct));
                    }

                    await Task.WhenAll(tasks);
                    await CloseBatchAsync(CancellationToken.None);
                    await Task.Delay(1000, CancellationToken.None);
                }
            }

            swTotal.Stop();
            string totalTime = $"{swTotal.Elapsed.TotalSeconds:F1}s";
            await CloseBatchAsync(CancellationToken.None);

            int remaining = accounts.Count(a => !a.IsCompleted);
            if (!ct.IsCancellationRequested)
            {
                if (remaining == 0)
                {
                    onLog?.Invoke($"\n🎉 ดำเนินการเข้าสู่ระบบเสร็จสิ้นครบทุกบัญชีแล้ว! ({totalTime})", Color.FromArgb(52, 211, 153));
                }
                else
                {
                    onLog?.Invoke($"\n🏁 จบการทำงาน ({totalTime}): สำเร็จ {successCount} | ล้มเหลว {failCount} | ตกค้าง {remaining}", Color.FromArgb(251, 191, 36));
                }
            }

            onCompleted?.Invoke(successCount, failCount, remaining, totalTime);
        }
    }
}
