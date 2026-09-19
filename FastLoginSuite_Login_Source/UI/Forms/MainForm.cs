using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ApibotWarZ.UI.Models;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Forms
{
    public class MainForm : Form
    {
        private static readonly Color BgDark = Color.FromArgb(11, 15, 25);
        private static readonly string StateFileName = "accounts_state.json";

        private readonly BotProcessManager _botManager;
        private readonly AppConfig _config;
        private readonly BindingList<RegisteredAccount> _accountsList = new();
        private readonly Dictionary<string, RegisteredAccount> _accountMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cachedTokenUsers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<(string message, Color color, string time)> _logQueue = new();
        
        private string _currentRunningFile = "accounts.txt";
        private readonly Stopwatch _uptimeTimer = new();
        private readonly System.Windows.Forms.Timer _uiTimer;
        private readonly System.Windows.Forms.Timer _logFlushTimer;
        private int _totalRegisteredCount = 0;
        private int _successCount = 0;
        private int _failCount = 0;
        private DateTime _lastAccountsModifiedTime = DateTime.MinValue;

        // License & Expiry State
        private DateTime? _licenseExpiryDate;
        private string _rawExpiryText = "";

        // WebView2 Browser Control
        private WebView2? _webView;
        private bool _isWebViewReady = false;

        public MainForm(string expiryText = "")
        {
            _botManager = new BotProcessManager();
            _config = ConfigManager.Load();

            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.DoubleBuffered = true;

            InitLicenseCountdown(expiryText);
            SetupUI();
            LoadExistingAccounts();

            _botManager.OnLog += BotManager_OnLog;
            _botManager.OnAccountRunning += BotManager_OnAccountRunning;
            _botManager.OnAccountSuccess += BotManager_OnAccountSuccess;
            _botManager.OnTokenCaptured += BotManager_OnTokenCaptured;
            _botManager.OnAccountLimit += BotManager_OnAccountLimit;
            _botManager.OnAccountRetry += BotManager_OnAccountRetry;
            _botManager.OnAccountFail += BotManager_OnAccountFail;
            _botManager.OnStateChanged += BotManager_OnStateChanged;
            _botManager.OnVpnChanged += BotManager_OnVpnChanged;

            // Heartbeat Timer for Uptime & License
            _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            // Background Batch Log Flush Timer
            _logFlushTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _logFlushTimer.Tick += FlushLogs;
            _logFlushTimer.Start();

            // Auto reload accounts if accounts.txt modified externally
            this.Activated += (s, e) =>
            {
                if (!_botManager.IsRunning && _currentRunningFile == "accounts.txt")
                {
                    string workDir = BotProcessManager.GetWorkingDir();
                    string accountsPath = Path.Combine(workDir, "accounts.txt");
                    if (File.Exists(accountsPath))
                    {
                        DateTime lastWrite = File.GetLastWriteTimeUtc(accountsPath);
                        if (lastWrite != _lastAccountsModifiedTime)
                        {
                            _lastAccountsModifiedTime = lastWrite;
                            LoadExistingAccounts();
                            SyncAllDataToWebView();
                        }
                    }
                }
            };
        }

        private static string GetAppVersionString()
        {
            try
            {
                string cur = AutoUpdater.CurrentVersion;
                if (!string.IsNullOrWhiteSpace(cur))
                {
                    return $"v{cur.TrimStart('v', 'V')}";
                }
            }
            catch { }
            return "v2.0 Pro";
        }

        private void SetupUI()
        {
            string verStr = GetAppVersionString();
            this.Text = $"FastLogin Suite {verStr} — Multi-Tab Auto Login Suite";
            this.Size = new Size(1180, 760);
            this.MinimumSize = new Size(960, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9f);
            this.ForeColor = Color.White;
            this.ShowIcon = true;
            this.Padding = new Padding(0);

            // Drag and Drop support
            this.AllowDrop = true;
            this.DragEnter += (s, e) =>
            {
                if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effect = DragDropEffects.Copy;
                }
            };
            this.DragDrop += (s, e) =>
            {
                if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        foreach (var file in files)
                        {
                            if (file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                            {
                                ImportAccountsFromTxtFile(file);
                                break;
                            }
                        }
                    }
                }
            };

            // Initialize WebView2
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = BgDark
            };
            this.Controls.Add(_webView);

            InitializeWebViewAsync();
        }

        private async void InitializeWebViewAsync()
        {
            if (_webView == null) return;

            try
            {
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastLoginSuite", "WebView2Data");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;

                _webView.NavigationCompleted += (s, e) =>
                {
                    _isWebViewReady = true;
                    SyncAllDataToWebView();
                    SendProfilesDataToWebView();

                    // Safety delayed retries in case JS event listener attached after navigation completed
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(250);
                        if (this.IsHandleCreated && !this.IsDisposed)
                        {
                            this.BeginInvoke(new Action(() =>
                            {
                                SyncAllDataToWebView();
                                SendProfilesDataToWebView();
                            }));
                        }
                        await Task.Delay(750);
                        if (this.IsHandleCreated && !this.IsDisposed)
                        {
                            this.BeginInvoke(new Action(() =>
                            {
                                SyncAllDataToWebView();
                            }));
                        }
                    });
                };

                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "index.html");
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(htmlPath);
                }
                else
                {
                    string devHtmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "index.html");
                    if (File.Exists(devHtmlPath))
                    {
                        _webView.CoreWebView2.Navigate(Path.GetFullPath(devHtmlPath));
                    }
                    else
                    {
                        MessageBox.Show(this, $"ไม่พบไฟล์ UI (index.html) ที่ตำแหน่ง:\n{htmlPath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"เกิดข้อผิดพลาดในการโหลด WebView2 Runtime:\n\n{ex.Message}\n\nกรุณาติดตั้ง Microsoft Edge WebView2 Runtime จากเว็บไซต์ทางการของ Microsoft", "WebView2 Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp)) return;
                string type = typeProp.GetString() ?? "";

                switch (type)
                {
                    case "START_BOT":
                        _ = HandleStartButtonClickAsync();
                        break;
                    case "STOP_BOT":
                        StopBot();
                        break;
                    case "RUN_FROM_ROW":
                        if (root.TryGetProperty("index", out var rIdx))
                        {
                            _ = RunFromAccountAsync(rIdx.GetInt32());
                        }
                        break;
                    case "RUN_SELECTED":
                        _ = RunSelectedAccountsAsync();
                        break;
                    case "RUN_RANGE":
                        if (root.TryGetProperty("start", out var rStart) && root.TryGetProperty("end", out var rEnd))
                        {
                            _ = RunRangeAccountsAsync(rStart.GetInt32(), rEnd.GetInt32());
                        }
                        break;
                    case "RUN_FAILED":
                        _ = RerunFailedAccountsAsync();
                        break;
                    case "RESET_SELECTED":
                        ResetSelectedAccountsStatus();
                        break;
                    case "CLEAR_ALL_STATE":
                        ClearAllState();
                        break;
                    case "SET_THREAD_COUNT":
                        if (root.TryGetProperty("threads", out var threadProp))
                        {
                            _config.BotThreads = threadProp.GetInt32();
                            ConfigManager.Save(_config);
                        }
                        break;
                    case "SAVE_CONFIG":
                        if (root.TryGetProperty("config", out var cfgProp))
                        {
                            if (cfgProp.TryGetProperty("operationMode", out var m)) _config.OperationMode = m.GetString() ?? _config.OperationMode;
                            if (cfgProp.TryGetProperty("batchCooldownSeconds", out var bc)) _config.BatchCooldownSeconds = bc.GetInt32();
                            if (cfgProp.TryGetProperty("deepCooldownEvery", out var de)) _config.DeepCooldownEvery = de.GetInt32();
                            if (cfgProp.TryGetProperty("deepCooldownSeconds", out var ds)) _config.DeepCooldownSeconds = ds.GetInt32();
                            if (cfgProp.TryGetProperty("limitCooldownSeconds", out var ls)) _config.LimitCooldownSeconds = ls.GetInt32();
                            if (cfgProp.TryGetProperty("autoStartCaptcha", out var ac)) _config.AutoStartCaptcha = ac.GetBoolean();
                            if (cfgProp.TryGetProperty("eventUuid", out var eu)) _config.EventId = eu.GetString() ?? _config.EventId;
                            ConfigManager.Save(_config);
                            RefreshTokensFromStorage(showLog: true);
                        }
                        break;
                    case "DIRECT_REDEEM":
                        _config.OperationMode = "redeem_only";
                        ConfigManager.Save(_config);
                        _ = HandleStartButtonClickAsync();
                        break;
                    case "APP_READY":
                    case "WEBVIEW_READY":
                    case "GET_INIT_DATA":
                        _isWebViewReady = true;
                        SyncAllDataToWebView();
                        SendProfilesDataToWebView();
                        break;
                    case "OPEN_ACCOUNTS":
                        OpenAccountsFile();
                        break;
                    case "IMPORT_ACCOUNTS":
                        PromptImportAccounts();
                        break;
                    case "IMPORT_ACCOUNTS_CONTENT":
                        {
                            string content = root.TryGetProperty("content", out var dropContentProp) ? dropContentProp.GetString() ?? "" : "";
                            string fileName = root.TryGetProperty("fileName", out var dropFnProp) ? dropFnProp.GetString() ?? "accounts.txt" : "accounts.txt";
                            ImportAccountsFromContent(content, fileName);
                            break;
                        }
                    case "OPEN_PROFILES":
                    case "GET_PROFILES":
                        SendProfilesDataToWebView();
                        break;
                    case "CREATE_BOT_PROFILES":
                        if (root.TryGetProperty("count", out var cProp))
                        {
                            int count = cProp.GetInt32();
                            bool ok = ChromeProfileService.CreateBotProfiles(count);
                            if (ok)
                            {
                                _config.ChromeBotProfileCount = count;
                                ConfigManager.Save(_config);
                            }
                            SendProfilesDataToWebView();
                        }
                        break;
                    case "DELETE_ALL_PROFILES":
                        ChromeProfileService.DeleteBotProfiles();
                        SendProfilesDataToWebView();
                        break;
                    case "DELETE_SINGLE_PROFILE":
                        if (root.TryGetProperty("id", out var delIdProp))
                        {
                            int wid = delIdProp.GetInt32();
                            ChromeProfileService.DeleteSingleBotProfile(wid);
                            SendProfilesDataToWebView();
                        }
                        break;
                    case "LAUNCH_SINGLE_PROFILE":
                        if (root.TryGetProperty("id", out var lIdProp))
                        {
                            int wid = lIdProp.GetInt32();
                            ChromeProfileService.LaunchProfile(wid);
                        }
                        break;
                    case "LAUNCH_ALL_PROFILES":
                        ChromeProfileService.LaunchAllProfiles();
                        break;
                    case "OPEN_PROFILES_FOLDER":
                        try
                        {
                            string dir = ChromeProfileService.GetBotProfilesBaseDir();
                            if (Directory.Exists(dir))
                            {
                                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = dir, UseShellExecute = true });
                            }
                        }
                        catch { }
                        break;
                    case "RESET_ACCOUNTS":
                        ResetSelectedAccountsStatus();
                        break;
                    case "CLEAR_CACHE":
                        ClearBrowserCache();
                        break;
                    case "BACKUP_TOKENS":
                        BackupTokensFile();
                        break;
                    case "CLEAR_LOGS":
                        while (_logQueue.TryDequeue(out _)) { }
                        break;
                    case "COPY_LOGS":
                        if (root.TryGetProperty("text", out var logTextProp))
                        {
                            string logTxt = logTextProp.GetString() ?? "";
                            if (!string.IsNullOrEmpty(logTxt))
                            {
                                try { Clipboard.SetText(logTxt); } catch { }
                            }
                        }
                        break;
                    case "COPY_HWID":
                        string hwid = CloudLicenseService.GetHWID();
                        Clipboard.SetText(hwid);
                        break;
                    case "SET_SELECTED_INDICES":
                        if (root.TryGetProperty("indices", out var indicesProp) && indicesProp.ValueKind == JsonValueKind.Array)
                        {
                            var selectedSet = new HashSet<int>();
                            foreach (var item in indicesProp.EnumerateArray())
                            {
                                selectedSet.Add(item.GetInt32());
                            }
                            for (int i = 0; i < _accountsList.Count; i++)
                            {
                                _accountsList[i].IsSelected = selectedSet.Contains(i);
                            }
                        }
                        break;
                    case "TOGGLE_SELECT_ACCOUNT":
                        if (root.TryGetProperty("index", out var idxProp) && root.TryGetProperty("selected", out var selProp))
                        {
                            int idx = idxProp.GetInt32();
                            if (idx >= 0 && idx < _accountsList.Count)
                            {
                                _accountsList[idx].IsSelected = selProp.GetBoolean();
                            }
                        }
                        break;
                    case "TOGGLE_SELECT_ALL":
                        if (root.TryGetProperty("selected", out var allSelProp))
                        {
                            bool allSel = allSelProp.GetBoolean();
                            foreach (var acc in _accountsList) acc.IsSelected = allSel;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebView IPC] WebMessageReceived Error: {ex.Message}");
            }
        }

        private void PostAction(string type, params (string Key, object? Value)[] pairs)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = type
            };
            foreach (var (k, v) in pairs)
            {
                dict[k] = v;
            }
            PostMessageToWebView(dict);
        }

        private void PostMessageToWebView(object payload)
        {
            if (_webView == null || _webView.IsDisposed) return;

            try
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() => PostMessageToWebView(payload)));
                    return;
                }

                if (_webView.CoreWebView2 == null) return;

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(payload, options);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PostMessageToWebView] Error: {ex.Message}");
            }
        }

        private void SyncAllDataToWebView()
        {
            try
            {
                string hwid = CloudLicenseService.GetHWID();
                string licenseDisplay = string.IsNullOrWhiteSpace(_rawExpiryText) || _rawExpiryText.Equals("Active", StringComparison.OrdinalIgnoreCase)
                    ? "VIP Lifetime (Active)"
                    : _rawExpiryText;

                var tokensDict = GetCachedTokensFullDictionary();

                var accountsArray = _accountsList.Select(a => new Dictionary<string, object?>
                {
                    ["index"] = a.Index,
                    ["username"] = a.Username,
                    ["password"] = a.Password,
                    ["status"] = a.Status,
                    ["sessionStatus"] = a.SessionStatus,
                    ["registeredAt"] = a.RegisteredAt,
                    ["resultDetail"] = a.ResultDetail,
                    ["isSelected"] = a.IsSelected
                }).ToList();

                var initPayload = new Dictionary<string, object?>
                {
                    ["type"] = "INIT_DATA",
                    ["version"] = GetAppVersionString(),
                    ["hwid"] = hwid,
                    ["license"] = licenseDisplay,
                    ["isRunning"] = _botManager.IsRunning,
                    ["config"] = _config,
                    ["accounts"] = accountsArray,
                    ["tokens"] = tokensDict
                };

                PostMessageToWebView(initPayload);

                try
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    string json = JsonSerializer.Serialize(initPayload, options);
                    _ = _webView?.CoreWebView2?.ExecuteScriptAsync($"if (typeof handleInitData === 'function') {{ handleInitData({json}); }}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SyncAllDataToWebView] Error: {ex.Message}");
            }
        }

        private void SendProfilesDataToWebView()
        {
            try
            {
                var profiles = ChromeProfileService.GetBotProfiles();
                var profilesArray = profiles.Select(p => new Dictionary<string, object?>
                {
                    ["id"] = p.WorkerId,
                    ["name"] = p.DisplayName,
                    ["dir"] = p.DirectoryName,
                    ["path"] = p.Path,
                    ["exists"] = p.ExistsOnDisk
                }).ToList();

                var payload = new Dictionary<string, object?>
                {
                    ["type"] = "PROFILES_DATA",
                    ["count"] = _config.ChromeBotProfileCount > 0 ? _config.ChromeBotProfileCount : 5,
                    ["profiles"] = profilesArray
                };
                PostMessageToWebView(payload);

                try
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    string json = JsonSerializer.Serialize(payload, options);
                    _ = _webView?.CoreWebView2?.ExecuteScriptAsync($"if (typeof handleProfilesData === 'function') {{ handleProfilesData({json}); }}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SendProfilesDataToWebView] Error: {ex.Message}");
            }
        }

        private Dictionary<string, object> GetCachedTokensFullDictionary()
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string workDir = BotProcessManager.GetWorkingDir();
                string[] possibleFiles = new[]
                {
                    Path.Combine(workDir, "tokens.json"),
                    Path.Combine(workDir, "tokens.json.bak"),
                    Path.Combine(baseDir, "tokens.json"),
                    Path.Combine(baseDir, "tokens.json.bak")
                };

                foreach (var file in possibleFiles)
                {
                    if (File.Exists(file))
                    {
                        try
                        {
                            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                            string content = sr.ReadToEnd();
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                using var doc = JsonDocument.Parse(content);
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                {
                                    if (prop.Value.ValueKind == JsonValueKind.Object &&
                                        prop.Value.TryGetProperty("token", out var tok))
                                    {
                                        string? tokStr = tok.GetString();
                                        if (!string.IsNullOrWhiteSpace(tokStr) && tokStr.Length > 20)
                                        {
                                            dict[prop.Name.Trim()] = new Dictionary<string, object> { ["token"] = tokStr, ["valid"] = true };
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return dict;
        }

        // ═══════════════════════════════════════════
        //  LICENSE REAL-TIME COUNTDOWN
        // ═══════════════════════════════════════════
        private void InitLicenseCountdown(string expiryText)
        {
            _rawExpiryText = expiryText?.Trim() ?? "";

            if (DateTime.TryParse(_rawExpiryText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedUtc))
            {
                _licenseExpiryDate = parsedUtc.ToLocalTime();
            }
            else if (DateTime.TryParse(_rawExpiryText, out var parsedLocal))
            {
                _licenseExpiryDate = parsedLocal;
            }
            else if (DateTimeOffset.TryParse(_rawExpiryText, out var parsedOffset))
            {
                _licenseExpiryDate = parsedOffset.LocalDateTime;
            }
            else
            {
                _licenseExpiryDate = null;
            }
        }

        private void UpdateLicenseCountdown()
        {
            if (_licenseExpiryDate.HasValue)
            {
                var remaining = _licenseExpiryDate.Value - DateTime.Now;
                if (remaining.TotalSeconds > 0)
                {
                    string text = remaining.TotalDays >= 1
                        ? $"สิทธิ์: เหลือ {remaining.Days} วัน {remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                        : $"สิทธิ์: เหลือ {remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    
                    PostAction("INIT_DATA", ("license", text));
                }
                else
                {
                    PostAction("INIT_DATA", ("license", "สิทธิ์: หมดอายุแล้ว"));

                    if (_botManager.IsRunning)
                    {
                        AppendLog("[License] ❌ สิทธิ์การใช้งานหมดอายุแล้ว ระบบทำการหยุดการทำงานอัตโนมัติ", Color.FromArgb(248, 113, 113));
                        StopBot();
                    }
                }
            }
        }

        private void LoadExistingAccounts()
        {
            string workDir = BotProcessManager.GetWorkingDir();
            string accountsPath = Path.Combine(workDir, "accounts.txt");
            string statePath = Path.Combine(workDir, StateFileName);

            _accountsList.Clear();
            _accountMap.Clear();
            _totalRegisteredCount = 0;
            _successCount = 0;
            _failCount = 0;

            var savedStates = new Dictionary<string, AccountStateItem>(StringComparer.OrdinalIgnoreCase);
            string bakPath = statePath + ".bak";
            List<AccountStateItem>? stateList = null;

            if (File.Exists(statePath))
            {
                try
                {
                    string json = File.ReadAllText(statePath);
                    stateList = JsonSerializer.Deserialize<List<AccountStateItem>>(json);
                }
                catch { }
            }

            if (stateList == null && File.Exists(bakPath))
            {
                try
                {
                    string json = File.ReadAllText(bakPath);
                    stateList = JsonSerializer.Deserialize<List<AccountStateItem>>(json);
                }
                catch { }
            }

            if (stateList != null)
            {
                foreach (var item in stateList)
                {
                    if (!string.IsNullOrEmpty(item.Username))
                    {
                        savedStates[item.Username] = item;
                    }
                }
            }

            if (File.Exists(accountsPath))
            {
                try
                {
                    _lastAccountsModifiedTime = File.GetLastWriteTimeUtc(accountsPath);
                    var lines = File.ReadAllLines(accountsPath);
                    foreach (var line in lines)
                    {
                        var (user, pass) = ParseAccountLine(line);
                        if (!string.IsNullOrEmpty(user) && !_accountMap.ContainsKey(user))
                        {
                            var acc = new RegisteredAccount
                            {
                                Index = _accountsList.Count + 1,
                                Username = user,
                                Password = pass ?? user,
                                Status = "⏳ รอคิว",
                                RegisteredAt = "-",
                                ResultDetail = "อยู่ในคิวรอการตรวจสอบ",
                                IsSelected = true
                            };

                            if (savedStates.TryGetValue(user, out var state))
                            {
                                if (!string.IsNullOrEmpty(state.Status)) acc.Status = state.Status;
                                if (!string.IsNullOrEmpty(state.RegisteredAt)) acc.RegisteredAt = state.RegisteredAt;
                                if (!string.IsNullOrEmpty(state.ResultDetail)) acc.ResultDetail = state.ResultDetail;
                                if (!string.IsNullOrEmpty(state.SessionStatus)) acc.SessionStatus = state.SessionStatus;
                                acc.IsSelected = state.IsSelected;
                            }

                            _accountsList.Add(acc);
                            _accountMap[user] = acc;
                            _totalRegisteredCount++;

                            if (acc.IsCompleted) _successCount++;
                            else if (acc.Status.Contains("ล้มเหลว") || acc.Status.Contains("ผิด")) _failCount++;
                        }
                    }

                    RefreshTokensFromStorage(showLog: false);
                    string stateMsg = _successCount > 0 ? $" (จำสถานะเดิมสำเร็จแล้ว {_successCount} บัญชี)" : "";
                    AppendLog($"[System] โหลดข้อมูล accounts.txt สำเร็จ พบไอดีในคิวทั้งหมด {_totalRegisteredCount} บัญชี{stateMsg}", Color.FromArgb(140, 200, 255));
                }
                catch (Exception ex)
                {
                    AppendLog($"[System] โหลด accounts.txt ไม่สำเร็จ: {ex.Message}", Color.FromArgb(248, 113, 113));
                }
            }
        }

        private void SaveState()
        {
            try
            {
                string workDir = BotProcessManager.GetWorkingDir();
                string statePath = Path.Combine(workDir, StateFileName);
                string tmpPath = statePath + ".tmp";
                string bakPath = statePath + ".bak";

                var list = new List<AccountStateItem>();
                lock (_accountsList)
                {
                    foreach (var acc in _accountsList)
                    {
                        list.Add(new AccountStateItem
                        {
                            Username = acc.Username,
                            Password = acc.Password,
                            Status = acc.Status,
                            RegisteredAt = acc.RegisteredAt,
                            ResultDetail = acc.ResultDetail,
                            SessionStatus = acc.SessionStatus,
                            IsSelected = acc.IsSelected
                        });
                    }
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(list, options);
                File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);

                if (File.Exists(statePath))
                {
                    try { File.Copy(statePath, bakPath, true); } catch { }
                    try { File.Delete(statePath); } catch { }
                }

                File.Move(tmpPath, statePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[State] SaveState error: {ex.Message}");
            }
        }

        private void ResetSelectedAccountsStatus()
        {
            var targets = _accountsList.Where(a => a.IsSelected).ToList();
            if (targets.Count == 0) targets = _accountsList.ToList();

            foreach (var acc in targets)
            {
                acc.Status = "⏳ รอคิว";
                acc.RegisteredAt = "-";
                acc.ResultDetail = "อยู่ในคิวรอการตรวจสอบ";
            }

            _successCount = _accountsList.Count(a => a.IsCompleted);
            _failCount = _accountsList.Count(a => a.Status.Contains("ล้มเหลว") || a.Status.Contains("ผิด"));

            SaveState();
            SyncAllDataToWebView();
            AppendLog($"[System] 🔄 รีเซ็ตสถานะ {targets.Count} บัญชีเป็น 'รอคิว' เรียบร้อย", Color.FromArgb(251, 191, 36));
            PostAction("TOAST", ("message", $"🔄 รีเซ็ตสถานะ {targets.Count} บัญชีเป็น 'รอคิว' เรียบร้อย"));
        }

        private void ClearAllState()
        {
            string workDir = BotProcessManager.GetWorkingDir();
            string statePath = Path.Combine(workDir, StateFileName);
            try
            {
                if (File.Exists(statePath)) File.Delete(statePath);
            }
            catch { }

            foreach (var acc in _accountsList)
            {
                acc.Status = "⏳ รอคิว";
                acc.RegisteredAt = "-";
                acc.ResultDetail = "อยู่ในคิวรอการตรวจสอบ";
            }
            _successCount = 0;
            _failCount = 0;

            SaveState();
            SyncAllDataToWebView();
            AppendLog("[System] 🗑️ ล้างประวัติสถานะและรีเซ็ตทุกบัญชีเรียบร้อย", Color.FromArgb(248, 113, 113));
            PostAction("TOAST", ("message", "🗑️ ล้างประวัติสถานะทุกบัญชีเรียบร้อย"));
        }

        private void ClearBrowserCache()
        {
            try
            {
                string workDir = BotProcessManager.GetWorkingDir();
                string profilesDir = Path.Combine(workDir, "chrome_profiles");
                if (Directory.Exists(profilesDir))
                {
                    Directory.Delete(profilesDir, true);
                }
                PostAction("TOAST", ("message", "🧹 เคลียร์แคชโปรไฟล์ Chrome ทั้งหมดเรียบร้อย"));
                AppendLog("[System] 🧹 ล้างแคชโปรไฟล์ Chrome ทั้งหมดสำเร็จ", Color.FromArgb(52, 211, 153));
            }
            catch (Exception ex)
            {
                PostAction("TOAST", ("message", $"❌ ไม่สามารถลบแคชได้: {ex.Message}"));
            }
        }

        private void BackupTokensFile()
        {
            try
            {
                string workDir = BotProcessManager.GetWorkingDir();
                string tokensPath = Path.Combine(workDir, "tokens.json");
                if (File.Exists(tokensPath))
                {
                    string backupName = $"tokens_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                    string backupPath = Path.Combine(workDir, backupName);
                    File.Copy(tokensPath, backupPath, true);
                    PostAction("TOAST", ("message", $"💾 สำรองไฟล์ Token เรียบร้อย ({backupName})"));
                    AppendLog($"[Tokens] 💾 สำรองคลัง Token เป็น '{backupName}' สำเร็จ", Color.FromArgb(52, 211, 153));
                }
                else
                {
                    PostAction("TOAST", ("message", "⚠️ ยังไม่มีไฟล์ tokens.json ในระบบ"));
                }
            }
            catch (Exception ex)
            {
                PostAction("TOAST", ("message", $"❌ สำรองไฟล์ไม่สำเร็จ: {ex.Message}"));
            }
        }

        private async Task RunFromAccountAsync(int startIndex)
        {
            if (_botManager.IsRunning) return;
            if (startIndex < 0 || startIndex >= _accountsList.Count) return;

            var targetAccounts = new List<RegisteredAccount>();
            for (int i = startIndex; i < _accountsList.Count; i++)
            {
                targetAccounts.Add(_accountsList[i]);
            }

            await RunCustomAccountQueueAsync(targetAccounts, $"เริ่มรันตั้งแต่ไอดีลำดับที่ {startIndex + 1} ({_accountsList[startIndex].Username}) ทั้งหมด {targetAccounts.Count} บัญชี");
        }

        private async Task RunSelectedAccountsAsync()
        {
            if (_botManager.IsRunning) return;

            var selected = _accountsList.Where(a => a.IsSelected).ToList();
            if (selected.Count == 0)
            {
                PostAction("TOAST", ("message", "⚠️ กรุณาติ๊กเลือกไอดีที่ต้องการรันก่อน (หรือคลิกลากคลุมแถวที่ต้องการ)"));
                return;
            }

            await RunCustomAccountQueueAsync(selected, $"รันเฉพาะไอดีที่เลือกไว้ทั้งหมด {selected.Count} บัญชี");
        }

        private async Task RunRangeAccountsAsync(int start1Based, int end1Based)
        {
            if (_botManager.IsRunning) return;

            if (_accountsList.Count == 0)
            {
                PostAction("TOAST", ("message", "⚠️ ไม่มีบัญชีในระบบ กรุณานำเข้าบัญชีก่อน"));
                return;
            }

            int s = Math.Max(1, Math.Min(start1Based, end1Based));
            int e = Math.Min(_accountsList.Count, Math.Max(start1Based, end1Based));

            if (s > _accountsList.Count)
            {
                PostAction("TOAST", ("message", $"⚠️ ลำดับเริ่มต้น ({s}) มากกว่าจำนวนบัญชีทั้งหมดที่มี ({_accountsList.Count})"));
                return;
            }

            var rangeAccounts = new List<RegisteredAccount>();
            for (int i = s - 1; i < e; i++)
            {
                rangeAccounts.Add(_accountsList[i]);
            }

            await RunCustomAccountQueueAsync(rangeAccounts, $"รันช่วงลำดับที่ {s} ถึง {e} ทั้งหมด {rangeAccounts.Count} บัญชี");
        }

        private async Task RerunFailedAccountsAsync()
        {
            if (_botManager.IsRunning) return;

            var failed = _accountsList.Where(a => a.Status.Contains("ล้มเหลว") || a.Status.Contains("ผิด")).ToList();
            if (failed.Count == 0)
            {
                PostAction("TOAST", ("message", "ℹ️ ไม่พบบัญชีที่ผิดพลาดในคิวขณะนี้"));
                return;
            }

            await RunCustomAccountQueueAsync(failed, $"รันเฉพาะไอดีที่ผิดพลาดทั้งหมด {failed.Count} บัญชี");
        }

        private async Task HandleStartButtonClickAsync()
        {
            if (_accountsList.Count == 0)
            {
                MessageBox.Show(this, "ไม่พบบัญชีในระบบ กรุณาใส่บัญชีใน accounts.txt หรือลากไฟล์ .txt เข้ามาในโปรแกรม", "ไม่มีบัญชี", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int selectedCount = _accountsList.Count(a => a.IsSelected);
            if (selectedCount > 0 && selectedCount < _accountsList.Count)
            {
                var selected = _accountsList.Where(a => a.IsSelected).ToList();
                await RunCustomAccountQueueAsync(selected, $"รันเฉพาะไอดีที่เลือกไว้ทั้งหมด {selected.Count} บัญชี");
                return;
            }

            var pendingAccounts = _accountsList.Where(a => !a.IsCompleted).ToList();
            if (pendingAccounts.Count == 0)
            {
                var dr = MessageBox.Show(this, "ทุกบัญชีเข้าสู่ระบบสำเร็จครบถ้วนแล้ว (100%)!\n\nคุณต้องการรีเซ็ตสถานะทั้งหมดเพื่อเริ่มรันใหม่ตั้งแต่ต้นใช่หรือไม่?", "รันเสร็จสมบูรณ์แล้ว", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes)
                {
                    foreach (var acc in _accountsList)
                    {
                        acc.Status = "⏳ รอคิว";
                        acc.RegisteredAt = "-";
                        acc.ResultDetail = "อยู่ในคิวรอการตรวจสอบ";
                    }
                    _successCount = 0;
                    _failCount = 0;
                    SaveState();
                    SyncAllDataToWebView();
                    await RunCustomAccountQueueAsync(_accountsList.ToList(), $"รีเซ็ตและเริ่มรันใหม่ทั้งหมด {_accountsList.Count} บัญชี");
                }
                return;
            }

            if (pendingAccounts.Count == _accountsList.Count)
            {
                await RunCustomAccountQueueAsync(_accountsList.ToList(), $"เริ่มรันบัญชีทั้งหมด {_accountsList.Count} บัญชี");
            }
            else
            {
                await RunCustomAccountQueueAsync(pendingAccounts, $"พบ {pendingAccounts.Count} บัญชีที่ยังไม่เสร็จ (ข้าม {_accountsList.Count - pendingAccounts.Count} บัญชีที่สำเร็จแล้ว) กำลังเริ่มรันเฉพาะบัญชีที่เหลือ");
            }
        }

        private async Task RunCustomAccountQueueAsync(List<RegisteredAccount> accounts, string logDescription)
        {
            if (_botManager.IsRunning) return;

            string workDir = BotProcessManager.GetWorkingDir();
            string queuePath = Path.Combine(workDir, "accounts_queue.txt");

            try
            {
                var lines = new List<string>();
                foreach (var acc in accounts)
                {
                    string pass = string.IsNullOrEmpty(acc.Password) ? acc.Username : acc.Password;
                    lines.Add($"ID: {acc.Username} | PASS: {pass}");
                    acc.Status = "⏳ รอคิว";
                    acc.ResultDetail = "อยู่ในคิวรอทำงาน";
                }
                File.WriteAllLines(queuePath, lines);
                AppendLog($"[System] ⚡ {logDescription}...", Color.FromArgb(255, 180, 80));
            }
            catch (Exception ex)
            {
                AppendLog($"[System] ❌ สร้างไฟล์คิว accounts_queue.txt ไม่สำเร็จ: {ex.Message}", Color.FromArgb(248, 113, 113));
                return;
            }

            SyncAllDataToWebView();
            await StartBotAsync("accounts_queue.txt");
        }

        private async Task StartBotAsync(string accountsFile = "accounts.txt")
        {
            _currentRunningFile = accountsFile;
            _uptimeTimer.Restart();

            PostAction("STATE_CHANGED", ("isRunning", true));

            int threads = _config.BotThreads;
            bool autoCaptcha = _config.AutoStartCaptcha;

            bool started = await _botManager.StartAsync(
                threads,
                autoCaptcha,
                accountsFile,
                _config.BatchCooldownSeconds,
                _config.DeepCooldownEvery,
                _config.DeepCooldownSeconds,
                _config.LimitCooldownSeconds,
                _config.OperationMode
            );

            if (!started)
            {
                StopBot();
            }
        }

        private void StopBot()
        {
            _botManager.Stop();
            _uptimeTimer.Stop();
            var ts = _uptimeTimer.Elapsed;
            string uptimeStr = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            PostAction("UPTIME", ("uptime", uptimeStr));

            PostAction("STATE_CHANGED", ("isRunning", false));
        }

        private void BotManager_OnLog(string message, Color color)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            _logQueue.Enqueue((message, color, time));
        }

        private void AppendLog(string message, Color color)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            _logQueue.Enqueue((message, color, time));
        }

        private void FlushLogs(object? sender, EventArgs e)
        {
            if (_logQueue.IsEmpty) return;

            while (_logQueue.TryDequeue(out var item))
            {
                string colorHex = $"#{item.color.R:X2}{item.color.G:X2}{item.color.B:X2}";
                PostAction("LOG",
                    ("time", item.time),
                    ("message", item.message),
                    ("color", colorHex));
            }
        }

        private void BotManager_OnTokenCaptured(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;

            lock (_cachedTokenUsers)
            {
                _cachedTokenUsers.Add(username);
            }

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.SessionStatus = "🟢 มี Token";
            }

            PostAction("TOKEN_CAPTURED", ("username", username));
            PostAction("TOKENS_VAULT_UPDATE", ("tokens", GetCachedTokensFullDictionary()));
        }

        private void BotManager_OnAccountRunning(string username)
        {
            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = "⚡ กำลังล็อกอิน...";
                acc.RegisteredAt = DateTime.Now.ToString("HH:mm:ss");
                acc.ResultDetail = "กำลังเปิดแท็บและตรวจสอบ Captcha...";
            }

            PostAction("ACCOUNT_RUNNING", ("username", username));
        }

        private void BotManager_OnAccountSuccess(string username, string elapsed, string detail)
        {
            string resultMsg = string.IsNullOrWhiteSpace(detail) ? "เข้าสู่ระบบสำเร็จ" : detail;
            string statusMsg = (_config.OperationMode == "harvest" || resultMsg.Contains("Token")) ? "🔑 เก็บ Token" : "✅ สำเร็จ";

            lock (_cachedTokenUsers)
            {
                _cachedTokenUsers.Add(username);
            }
            string sessionTag = "🟢 มี Token";

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = statusMsg;
                acc.SessionStatus = sessionTag;
                acc.RegisteredAt = elapsed;
                acc.ResultDetail = resultMsg;
            }

            _successCount++;
            PostAction("ACCOUNT_SUCCESS",
                ("username", username),
                ("elapsed", elapsed),
                ("detail", resultMsg));
            SaveState();
        }

        private void BotManager_OnAccountLimit(string username, string elapsed, string reason)
        {
            string limitDetail = string.IsNullOrWhiteSpace(reason) ? "ติด Limit Cloudflare ชั่วคราว (รอรันซ้ำ)" : reason;

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = "⏳ ติด Limit";
                acc.RegisteredAt = elapsed;
                acc.ResultDetail = limitDetail;
            }

            PostAction("ACCOUNT_LIMIT",
                ("username", username),
                ("elapsed", elapsed),
                ("detail", limitDetail));
            SaveState();
        }

        private void BotManager_OnAccountRetry(string username, string elapsed, string reason)
        {
            string retryDetail = string.IsNullOrWhiteSpace(reason) ? "เกิดปัญหาชั่วคราว (รอรันซ้ำ)" : reason;

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = "🔄 รอรันซ้ำ";
                acc.RegisteredAt = elapsed;
                acc.ResultDetail = retryDetail;
            }

            PostAction("ACCOUNT_RETRY",
                ("username", username),
                ("elapsed", elapsed),
                ("detail", retryDetail));
            SaveState();
        }

        private void BotManager_OnAccountFail(string username, string elapsed, string reason)
        {
            string failDetail = string.IsNullOrWhiteSpace(reason) ? "รหัสผ่านไม่ถูกต้อง หรือล็อกอินไม่ผ่าน" : reason;

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = "❌ ล้มเหลว";
                acc.RegisteredAt = elapsed;
                acc.ResultDetail = failDetail;
            }

            _failCount++;
            PostAction("ACCOUNT_FAIL",
                ("username", username),
                ("elapsed", elapsed),
                ("detail", failDetail));
            SaveState();
        }

        private void BotManager_OnStateChanged(bool isRunning)
        {
            PostAction("STATE_CHANGED", ("isRunning", isRunning));
            if (!isRunning)
            {
                _uptimeTimer.Stop();
                var ts = _uptimeTimer.Elapsed;
                string uptimeStr = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
                PostAction("UPTIME", ("uptime", uptimeStr));
                GC.Collect(2, GCCollectionMode.Optimized);
            }
        }

        private void BotManager_OnVpnChanged(string ip)
        {
            AppendLog($"[VPN] 🌐 ตรวจพบ IP ใหม่: {ip}", Color.FromArgb(80, 190, 255));
        }

        private int _uiTokenSyncCounter = 0;
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            UpdateLicenseCountdown();

            if (_uptimeTimer.IsRunning)
            {
                var ts = _uptimeTimer.Elapsed;
                string uptimeStr = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
                PostAction("UPTIME", ("uptime", uptimeStr));

                _uiTokenSyncCounter++;
                if (_uiTokenSyncCounter % 3 == 0)
                {
                    RefreshTokensFromStorage(showLog: false);
                }
            }
        }

        private static readonly Regex LabeledFormatRegex = new Regex(
            @"ID\s*:\s*(?<id>\S+)\s*\|\s*PASS\s*:\s*(?<pass>\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RescueRegex = new Regex(
            @"([a-zA-Z0-9_.\-]+)[|:,\t]([a-zA-Z0-9_.\-]+)$",
            RegexOptions.Compiled);

        public static (string? id, string? pass) ParseAccountLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return (null, null);

            string trimmed = line.Trim();

            var labeled = LabeledFormatRegex.Match(trimmed);
            if (labeled.Success)
            {
                return (labeled.Groups["id"].Value.Trim(), labeled.Groups["pass"].Value.Trim());
            }

            if (trimmed.StartsWith("#") || trimmed.StartsWith("//"))
            {
                var rescue = RescueRegex.Match(trimmed);
                if (rescue.Success)
                {
                    string rId = rescue.Groups[1].Value.Trim();
                    string rPass = rescue.Groups[2].Value.Trim();
                    if (!rId.Equals("myaccount01", StringComparison.OrdinalIgnoreCase) &&
                        !rId.Equals("myaccount02", StringComparison.OrdinalIgnoreCase))
                    {
                        return (rId, rPass);
                    }
                }
                return (null, null);
            }

            char[] separators = new[] { '|', ':', ',', '\t' };
            foreach (var sep in separators)
            {
                if (trimmed.Contains(sep))
                {
                    var parts = trimmed.Split(new[] { sep }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        var id = parts[0].Trim();
                        var pass = parts[1].Trim();
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(pass)) return (id, pass);
                    }
                }
            }

            if (trimmed.Contains(' '))
            {
                var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    var id = parts[0].Trim();
                    var pass = parts[1].Trim();
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(pass)) return (id, pass);
                }
            }

            return (trimmed, trimmed);
        }

        private void PromptImportAccounts()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                Title = "เลือกไฟล์ไอดี accounts.txt"
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                ImportAccountsFromTxtFile(ofd.FileName);
            }
        }

        private void ImportAccountsFromTxtFile(string filePath)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                int added = 0;
                string workDir = BotProcessManager.GetWorkingDir();
                string targetPath = Path.Combine(workDir, "accounts.txt");

                using (var sw = File.AppendText(targetPath))
                {
                    foreach (var line in lines)
                    {
                        var (user, pass) = ParseAccountLine(line);
                        if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                        {
                            sw.WriteLine($"{user}|{pass}");
                            added++;
                        }
                    }
                }

                LoadExistingAccounts();
                SyncAllDataToWebView();

                AppendLog($"📁 ลากวาง/นำเข้าไฟล์ '{Path.GetFileName(filePath)}' สำเร็จ อ่านพบทั้งหมด {added} บัญชี!", Color.FromArgb(52, 211, 153));
                PostAction("TOAST", ("message", $"นำเข้าคิวไอดีสำเร็จทั้งหมด {added} บัญชี!"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"ไม่สามารถอ่านไฟล์ได้: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportAccountsFromContent(string content, string fileName)
        {
            try
            {
                var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                int added = 0;
                string workDir = BotProcessManager.GetWorkingDir();
                string targetPath = Path.Combine(workDir, "accounts.txt");

                using (var sw = File.AppendText(targetPath))
                {
                    foreach (var line in lines)
                    {
                        var (user, pass) = ParseAccountLine(line);
                        if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                        {
                            sw.WriteLine($"{user}|{pass}");
                            added++;
                        }
                    }
                }

                LoadExistingAccounts();
                SyncAllDataToWebView();

                AppendLog($"📁 ลากวาง/นำเข้าไฟล์ '{fileName}' สำเร็จ อ่านพบทั้งหมด {added} บัญชี!", Color.FromArgb(52, 211, 153));
                PostAction("TOAST", ("message", $"นำเข้าคิวไอดีสำเร็จทั้งหมด {added} บัญชี!"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"ไม่สามารถประมวลผลข้อมูลไฟล์ได้: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenAccountsFile()
        {
            string workDir = BotProcessManager.GetWorkingDir();
            string accountsPath = Path.Combine(workDir, "accounts.txt");

            if (!File.Exists(accountsPath))
            {
                File.WriteAllText(accountsPath, "");
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{accountsPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"ไม่สามารถเปิดไฟล์ได้: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID_F12 = 0x0F12;
        private const uint VK_F12 = 0x7B;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public void RefreshTokensFromStorage(bool showLog = false)
        {
            var tokenDict = GetCachedTokensFullDictionary();
            lock (_cachedTokenUsers)
            {
                _cachedTokenUsers.Clear();
                foreach (var k in tokenDict.Keys) _cachedTokenUsers.Add(k);
            }

            int hasTokenCount = 0;
            foreach (var acc in _accountsList)
            {
                if (_cachedTokenUsers.Contains(acc.Username))
                {
                    acc.SessionStatus = "🟢 มี Token";
                    hasTokenCount++;
                }
                else
                {
                    acc.SessionStatus = "⚪ ไม่มี";
                }
            }

            PostAction("TOKENS_VAULT_UPDATE", ("tokens", tokenDict));

            if (showLog)
            {
                string modeText = _config.OperationMode switch
                {
                    "harvest" => "🔑 [ล็อกอินเก็บ Token]",
                    "redeem_only" => "⚡ [ยิงรับของ 100 จอ (Token)]",
                    _ => "🎁 [ล็อกอิน + รับของ (ปกติ)]"
                };
                AppendLog($"[โหมด] ปรับเป็น {modeText} — ตรวจพบบัญชีที่มี Token ในคลัง: {hasTokenCount}/{_accountsList.Count} บัญชี", Color.FromArgb(120, 200, 255));
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RegisterHotKey(this.Handle, HOTKEY_ID_F12, 0, VK_F12);
            RefreshTokensFromStorage(showLog: false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnregisterHotKey(this.Handle, HOTKEY_ID_F12);
            StopBot();
            SaveState();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID_F12)
            {
                AppendLog("[Emergency Hotkey] กดปุ่ม [F12] สั่งหยุดการทำงานทันที!", Color.FromArgb(255, 80, 80));
                StopBot();
            }
            base.WndProc(ref m);
        }
    }
}
