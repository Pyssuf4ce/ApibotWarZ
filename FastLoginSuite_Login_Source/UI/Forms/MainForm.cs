using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using ApibotWarZ.UI.Controls;
using ApibotWarZ.UI.Models;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Forms
{
    public class MainForm : Form
    {
        // ────────── Modern Dark Palette ──────────
        private static readonly Color BgDark = Color.FromArgb(12, 14, 22);
        private static readonly Color PanelDark = Color.FromArgb(18, 21, 32);
        private static readonly Color CardDark = Color.FromArgb(24, 28, 42);
        private static readonly Color CardBorder = Color.FromArgb(38, 44, 66);
        private static readonly Color Accent = Color.FromArgb(120, 110, 255);
        private static readonly Color AccentHover = Color.FromArgb(145, 135, 255);
        private static readonly Color Success = Color.FromArgb(52, 211, 153);
        private static readonly Color SuccessHover = Color.FromArgb(74, 222, 168);
        private static readonly Color Warning = Color.FromArgb(251, 191, 36);
        private static readonly Color WarningHover = Color.FromArgb(252, 211, 77);
        private static readonly Color Danger = Color.FromArgb(248, 113, 113);
        private static readonly Color DangerHover = Color.FromArgb(252, 140, 140);
        private static readonly Color TextMain = Color.FromArgb(245, 247, 255);
        private static readonly Color TextDim = Color.FromArgb(140, 148, 175);
        private static readonly Color TextMuted = Color.FromArgb(90, 98, 122);
        private static readonly Color GridLine = Color.FromArgb(26, 30, 46);

        private readonly BotProcessManager _botManager;
        private readonly AppConfig _config;
        private readonly BindingList<RegisteredAccount> _accountsList = new();
        private readonly Dictionary<string, RegisteredAccount> _accountMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly BindingList<RegisteredAccount> _failedAccountsList = new();
        private readonly Dictionary<string, RegisteredAccount> _failedAccountMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cachedTokenUsers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<(string message, Color color, string time)> _logQueue = new();
        private bool _isFailedTabActive = false;
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

        // Log Buffer Limit Constants (prevents UI freezing/lag)
        private const int MaxLogLines = 500;
        private const int TrimBatchLines = 100;
        private int _currentLogLines = 0;

        // UI Controls - Header
        private Panel panelTop = null!;
        private Label lblTitle = null!;
        private Label lblSubtitle = null!;
        private FlowLayoutPanel flowBadges = null!;
        private Label lblLicenseBadge = null!;
        private Label lblHwidBadge = null!;

        // UI Controls - Stats (Double-buffered custom paint)
        private StatsDashboardControl statsDashboard = null!;

        // UI Controls - Action Toolbar
        private Panel panelActions = null!;
        private FlowLayoutPanel flowToolbar = null!;
        private RoundedButton btnStart = null!;
        private RoundedButton btnStop = null!;
        private Panel pnlThreadsCapsule = null!;
        private Label lblThreadCount = null!;
        private RoundedButton btnThreadMinus = null!;
        private RoundedButton btnThreadPlus = null!;
        private RoundedButton btnToggleCaptcha = null!;
        private RoundedButton btnOpenAccounts = null!;
        private RoundedButton btnResetQueue = null!;
        private RoundedButton btnClearLogs = null!;

        private static readonly string StateFileName = "accounts_state.json";

        // UI Controls - Main Content
        private SplitContainer splitMain = null!;
        private DataGridView dgvAccounts = null!;
        private DataGridView dgvFailedAccounts = null!;
        private RoundedButton btnTabAll = null!;
        private RoundedButton btnTabFailed = null!;
        private RoundedButton btnRerunFailed = null!;
        private RichTextBox rtbLogs = null!;
        private Label lblAccountsCount = null!;
        private Panel pnlGridHeader = null!;

        public MainForm(string expiryText = "")
        {
            _botManager = new BotProcessManager();
            _config = ConfigManager.Load();

            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.DoubleBuffered = true;

            SetupUI();
            InitLicenseCountdown(expiryText);
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

            // Heartbeat Timer: 1 second interval for smooth real-time countdown & stats
            _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            // Background Batch Log Flush Timer: 50ms interval prevents UI freezes and message queue jams
            _logFlushTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _logFlushTimer.Tick += FlushLogs;
            _logFlushTimer.Start();

            // Auto reload accounts ONLY if accounts.txt was actually modified on disk
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
            return "v1.0.0";
        }

        private void SetupUI()
        {
            string verStr = GetAppVersionString();
            this.Text = $"FastLogin Suite {verStr} — Multi-Tab Auto Login Suite";
            this.Size = new Size(1160, 720);
            this.MinimumSize = new Size(860, 520);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9f);
            this.ForeColor = TextMain;
            this.ShowIcon = true;
            this.Padding = new Padding(0);

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

            // ═══════════════════════════════════════════
            //  1. TOP HEADER BAR (Compact Sleek 44px)
            // ═══════════════════════════════════════════
            panelTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = PanelDark,
                Padding = new Padding(14, 4, 14, 4)
            };
            panelTop.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1f);
                e.Graphics.DrawLine(pen, 0, panelTop.Height - 1, panelTop.Width, panelTop.Height - 1);
            };

            var pnlTitleGroup = new Panel
            {
                Dock = DockStyle.Left,
                Width = 540,
                BackColor = Color.Transparent
            };

            lblTitle = new Label
            {
                Text = $"⚡ FastLogin Suite {verStr}",
                Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(0, 9),
                UseMnemonic = false
            };

            lblSubtitle = new Label
            {
                Text = "• High-Performance Multi-Tab Engine",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(220, 13),
                UseMnemonic = false
            };

            pnlTitleGroup.Controls.Add(lblTitle);
            pnlTitleGroup.Controls.Add(lblSubtitle);

            // Right-aligned badges flow container
            flowBadges = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 6, 0, 0)
            };

            string hwid = CloudLicenseService.GetHWID();
            string shortHwid = hwid.Length > 12 ? hwid.Substring(0, 12) + "..." : hwid;
            lblHwidBadge = new Label
            {
                Text = $"HWID: {shortHwid} (คัดลอก)",
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold),
                ForeColor = Accent,
                BackColor = CardDark,
                AutoSize = true,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(6, 0, 0, 0),
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            lblHwidBadge.Paint += (s, e) => DrawRoundedChip(e.Graphics, lblHwidBadge, CardBorder);
            lblHwidBadge.Click += (s, e) =>
            {
                Clipboard.SetText(hwid);
                MessageBox.Show(this, $"HWID ของเครื่องนี้:\n\n{hwid}\n\n(คัดลอกลงคลิปบอร์ดเรียบร้อย)", "HWID Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            lblLicenseBadge = new Label
            {
                Text = "สิทธิ์: กำลังตรวจสอบ...",
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold),
                ForeColor = Success,
                BackColor = CardDark,
                AutoSize = true,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(6, 0, 0, 0),
                UseMnemonic = false
            };
            lblLicenseBadge.Paint += (s, e) => DrawRoundedChip(e.Graphics, lblLicenseBadge, CardBorder);

            flowBadges.Controls.Add(lblHwidBadge);
            flowBadges.Controls.Add(lblLicenseBadge);

            panelTop.Controls.Add(flowBadges);
            panelTop.Controls.Add(pnlTitleGroup);

            // ═══════════════════════════════════════════
            //  2. STATS DASHBOARD (Compact Modern 56px)
            // ═══════════════════════════════════════════
            statsDashboard = new StatsDashboardControl
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = BgDark,
                Padding = new Padding(12, 4, 12, 4)
            };

            // ═══════════════════════════════════════════
            //  3. ACTION TOOLBAR (Responsive Flow Layout)
            // ═══════════════════════════════════════════
            panelActions = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = PanelDark,
                Padding = new Padding(0)
            };
            panelActions.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1f);
                e.Graphics.DrawLine(pen, 0, panelActions.Height - 1, panelActions.Width, panelActions.Height - 1);
            };

            flowToolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = false,
                BackColor = PanelDark,
                Padding = new Padding(10, 5, 10, 4)
            };

            // 1. Start Button
            btnStart = new RoundedButton
            {
                Text = "▶  เริ่มทำงาน",
                Size = new Size(125, 34),
                BaseColor = Success,
                HoverColor = SuccessHover,
                BorderColor = Color.FromArgb(40, 180, 120),
                ForeColorNormal = Color.FromArgb(6, 24, 14),
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Margin = new Padding(0, 0, 6, 4)
            };
            btnStart.Click += async (s, e) =>
            {
                if (_isFailedTabActive)
                {
                    await RerunFailedAccountsAsync();
                }
                else
                {
                    await HandleStartButtonClickAsync();
                }
            };

            // 2. Stop Button
            btnStop = new RoundedButton
            {
                Text = "■  หยุด",
                Size = new Size(85, 34),
                BaseColor = Danger,
                HoverColor = DangerHover,
                BorderColor = Color.FromArgb(200, 70, 70),
                ForeColorNormal = Color.White,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Enabled = false,
                Margin = new Padding(0, 0, 6, 4)
            };
            btnStop.Click += (s, e) => StopBot();

            // 3. Operation Mode Capsule
            var pnlModeCapsule = new Panel
            {
                Size = new Size(215, 34),
                BackColor = CardDark,
                Padding = new Padding(4, 3, 4, 3),
                Margin = new Padding(0, 0, 6, 4)
            };
            pnlModeCapsule.Paint += (s, e) => DrawRoundedChip(e.Graphics, pnlModeCapsule, CardBorder);

            var lblModeTitle = new Label
            {
                Text = "โหมด:",
                Font = new Font("Segoe UI Semibold", 8.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(6, 8),
                UseMnemonic = false
            };

            var cmbMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(20, 24, 38),
                ForeColor = TextMain,
                Font = new Font("Segoe UI Semibold", 8.5f),
                Location = new Point(48, 5),
                Size = new Size(160, 24)
            };

            cmbMode.Items.Add("🎁 ล็อกอิน + รับของ (ปกติ)");
            cmbMode.Items.Add("🔑 ล็อกอินเก็บ Token");
            cmbMode.Items.Add("⚡ ยิงรับของ 100 จอ (Token)");

            if (_config.OperationMode == "harvest") cmbMode.SelectedIndex = 1;
            else if (_config.OperationMode == "redeem_only") cmbMode.SelectedIndex = 2;
            else cmbMode.SelectedIndex = 0;

            cmbMode.SelectedIndexChanged += (s, e) =>
            {
                if (cmbMode.SelectedIndex == 1) _config.OperationMode = "harvest";
                else if (cmbMode.SelectedIndex == 2) _config.OperationMode = "redeem_only";
                else _config.OperationMode = "all";
                ConfigManager.Save(_config);
                RefreshTokensFromStorage(showLog: true);
            };

            pnlModeCapsule.Controls.Add(lblModeTitle);
            pnlModeCapsule.Controls.Add(cmbMode);

            // 4. Threads Stepper Capsule
            pnlThreadsCapsule = new Panel
            {
                Size = new Size(112, 34),
                BackColor = CardDark,
                Padding = new Padding(4, 3, 4, 3),
                Margin = new Padding(0, 0, 6, 4)
            };
            pnlThreadsCapsule.Paint += (s, e) => DrawRoundedChip(e.Graphics, pnlThreadsCapsule, CardBorder);

            var lblThreadsTitle = new Label
            {
                Text = "บอท:",
                Font = new Font("Segoe UI Semibold", 8.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(6, 8),
                UseMnemonic = false
            };

            btnThreadMinus = new RoundedButton
            {
                Text = "–",
                Size = new Size(22, 22),
                Location = new Point(38, 6),
                BaseColor = Color.FromArgb(34, 40, 60),
                HoverColor = Color.FromArgb(45, 52, 78),
                BorderColor = CardBorder,
                CornerRadius = 4,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColorNormal = TextMain
            };
            btnThreadMinus.Click += (s, e) =>
            {
                if (_config.BotThreads > 1)
                {
                    _config.BotThreads--;
                    lblThreadCount.Text = _config.BotThreads.ToString();
                    ConfigManager.Save(_config);
                }
            };

            lblThreadCount = new Label
            {
                Text = Math.Max(1, Math.Min(50, _config.BotThreads)).ToString(),
                Size = new Size(24, 22),
                Location = new Point(60, 6),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.White,
                UseMnemonic = false
            };

            btnThreadPlus = new RoundedButton
            {
                Text = "+",
                Size = new Size(22, 22),
                Location = new Point(84, 6),
                BaseColor = Color.FromArgb(34, 40, 60),
                HoverColor = Color.FromArgb(45, 52, 78),
                BorderColor = CardBorder,
                CornerRadius = 4,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColorNormal = TextMain
            };
            btnThreadPlus.Click += (s, e) =>
            {
                if (_config.BotThreads < 50)
                {
                    _config.BotThreads++;
                    lblThreadCount.Text = _config.BotThreads.ToString();
                    ConfigManager.Save(_config);
                }
            };

            pnlThreadsCapsule.Controls.Add(lblThreadsTitle);
            pnlThreadsCapsule.Controls.Add(btnThreadMinus);
            pnlThreadsCapsule.Controls.Add(lblThreadCount);
            pnlThreadsCapsule.Controls.Add(btnThreadPlus);

            // 5. Captcha Toggle Button
            btnToggleCaptcha = new RoundedButton
            {
                Size = new Size(135, 34),
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 8.5f),
                EnableBorder = true,
                Margin = new Padding(0, 0, 8, 4)
            };
            UpdateCaptchaToggleButton();
            btnToggleCaptcha.Click += (s, e) =>
            {
                _config.AutoStartCaptcha = !_config.AutoStartCaptcha;
                ConfigManager.Save(_config);
                UpdateCaptchaToggleButton();
            };

            // 6. Open Accounts
            btnOpenAccounts = new RoundedButton
            {
                Text = "accounts.txt",
                Size = new Size(95, 34),
                BaseColor = CardDark,
                HoverColor = Color.FromArgb(34, 40, 60),
                BorderColor = CardBorder,
                ForeColorNormal = TextMain,
                Font = new Font("Segoe UI Semibold", 8.5f),
                Margin = new Padding(0, 0, 6, 4)
            };
            btnOpenAccounts.Click += (s, e) => OpenAccountsFile();

            // 7. Import txt
            var btnImportTxt = new RoundedButton
            {
                Text = "📁 ดึงไฟล์ .txt",
                Size = new Size(95, 34),
                BaseColor = CardDark,
                HoverColor = Color.FromArgb(34, 40, 60),
                BorderColor = CardBorder,
                ForeColorNormal = TextMain,
                Font = new Font("Segoe UI Semibold", 8.5f),
                Margin = new Padding(0, 0, 6, 4)
            };
            btnImportTxt.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog { Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*", Title = "เลือกไฟล์ไอดี accounts.txt" };
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    ImportAccountsFromTxtFile(ofd.FileName);
                }
            };

            // 8. Chrome Profiles
            var btnChromeProfiles = new RoundedButton
            {
                Text = "🌐 Profiles",
                Size = new Size(85, 34),
                BaseColor = Color.FromArgb(32, 36, 62),
                HoverColor = Color.FromArgb(45, 52, 90),
                BorderColor = Color.FromArgb(99, 102, 241),
                ForeColorNormal = Color.FromArgb(140, 200, 255),
                Font = new Font("Segoe UI Semibold", 8.5f),
                Margin = new Padding(0, 0, 6, 4)
            };
            btnChromeProfiles.Click += (s, e) =>
            {
                using var dlg = new ChromeProfileDialog(_config);
                dlg.ShowDialog(this);
            };

            // 9. Reset Queue
            btnResetQueue = new RoundedButton
            {
                Text = "🔄 รีเซ็ต",
                Size = new Size(78, 34),
                BaseColor = CardDark,
                HoverColor = Color.FromArgb(40, 36, 20),
                BorderColor = CardBorder,
                ForeColorNormal = Warning,
                Font = new Font("Segoe UI Semibold", 8.5f),
                Margin = new Padding(0, 0, 6, 4)
            };
            btnResetQueue.Click += (s, e) => ResetSelectedAccountsStatus();

            // 10. Clear Logs
            btnClearLogs = new RoundedButton
            {
                Text = "🗑️ ล้าง Log",
                Size = new Size(75, 34),
                BaseColor = CardDark,
                HoverColor = Color.FromArgb(34, 40, 60),
                BorderColor = CardBorder,
                ForeColorNormal = TextDim,
                Font = new Font("Segoe UI Semibold", 8.5f),
                Margin = new Padding(0, 0, 0, 4)
            };
            btnClearLogs.Click += (s, e) =>
            {
                while (_logQueue.TryDequeue(out _)) { }
                rtbLogs.Clear();
                rtbLogs.ClearUndo();
                _currentLogLines = 0;
            };

            flowToolbar.Controls.Add(btnStart);
            flowToolbar.Controls.Add(btnStop);
            flowToolbar.Controls.Add(pnlModeCapsule);
            flowToolbar.Controls.Add(pnlThreadsCapsule);
            flowToolbar.Controls.Add(btnToggleCaptcha);
            flowToolbar.Controls.Add(btnOpenAccounts);
            flowToolbar.Controls.Add(btnImportTxt);
            flowToolbar.Controls.Add(btnChromeProfiles);
            flowToolbar.Controls.Add(btnResetQueue);
            flowToolbar.Controls.Add(btnClearLogs);

            panelActions.Controls.Add(flowToolbar);

            // Responsive Toolbar Resize (expands to 2 rows if screen width < 1140px)
            this.Resize += (s, e) =>
            {
                if (panelActions != null)
                {
                    panelActions.Height = this.ClientSize.Width < 1140 ? 80 : 44;
                }
            };
            panelActions.Height = this.ClientSize.Width < 1140 ? 80 : 44;

            // ═══════════════════════════════════════════
            //  4. MAIN SPLIT CONTAINER (Grid & Log)
            // ═══════════════════════════════════════════
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 640,
                SplitterWidth = 6,
                BackColor = BgDark,
                Padding = new Padding(12, 6, 12, 8)
            };

            // Left Side: Accounts Grid Container
            var panelGridContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PanelDark,
                Padding = new Padding(1)
            };
            panelGridContainer.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, panelGridContainer.Width - 1, panelGridContainer.Height - 1);
            };

            pnlGridHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                BackColor = CardDark,
                Padding = new Padding(8, 5, 8, 5)
            };
            pnlGridHeader.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1f);
                e.Graphics.DrawLine(pen, 0, pnlGridHeader.Height - 1, pnlGridHeader.Width, pnlGridHeader.Height - 1);
            };

            btnTabAll = new RoundedButton
            {
                Text = "📋 บัญชีทั้งหมด (0)",
                Size = new Size(150, 30),
                Location = new Point(8, 6),
                BaseColor = Color.FromArgb(36, 42, 64),
                HoverColor = Color.FromArgb(46, 54, 80),
                BorderColor = Accent,
                ForeColorNormal = Color.White,
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold)
            };
            btnTabAll.Click += (s, e) => SwitchTab(false);

            btnTabFailed = new RoundedButton
            {
                Text = "❌ บัญชีที่ผิดพลาด (0)",
                Size = new Size(155, 30),
                Location = new Point(164, 6),
                BaseColor = CardDark,
                HoverColor = Color.FromArgb(45, 26, 32),
                BorderColor = CardBorder,
                ForeColorNormal = TextDim,
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold)
            };
            btnTabFailed.Click += (s, e) => SwitchTab(true);

            btnRerunFailed = new RoundedButton
            {
                Text = "⚡ รันเฉพาะไอดีที่ผิดพลาด",
                Size = new Size(175, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(pnlGridHeader.Width - 185, 6),
                BaseColor = Color.FromArgb(170, 40, 50),
                HoverColor = Color.FromArgb(200, 50, 62),
                BorderColor = Danger,
                ForeColorNormal = Color.White,
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                Visible = false
            };
            btnRerunFailed.Click += async (s, e) => await RerunFailedAccountsAsync();

            lblAccountsCount = new Label
            {
                Text = "0 บัญชี",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = Success,
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(pnlGridHeader.Width - 140, 12),
                UseMnemonic = false
            };

            pnlGridHeader.Controls.Add(btnTabAll);
            pnlGridHeader.Controls.Add(btnTabFailed);
            pnlGridHeader.Controls.Add(btnRerunFailed);
            pnlGridHeader.Controls.Add(lblAccountsCount);

            dgvAccounts = CreateStyledGridView(_accountsList, true);
            dgvFailedAccounts = CreateStyledGridView(_failedAccountsList, false);
            dgvFailedAccounts.Visible = false;

            var pnlGridBody = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PanelDark,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            pnlGridBody.Controls.Add(dgvAccounts);
            pnlGridBody.Controls.Add(dgvFailedAccounts);

            panelGridContainer.Controls.Add(pnlGridBody);
            panelGridContainer.Controls.Add(pnlGridHeader);
            pnlGridHeader.SendToBack();
            pnlGridBody.BringToFront();
            splitMain.Panel1.Controls.Add(panelGridContainer);

            // Right Side: Live Logs
            var panelLogContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PanelDark,
                Padding = new Padding(1)
            };
            panelLogContainer.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, panelLogContainer.Width - 1, panelLogContainer.Height - 1);
            };

            var pnlLogHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = CardDark,
                Padding = new Padding(10, 0, 10, 0)
            };

            var lblLogTitle = new Label
            {
                Text = "Live Console Logs & Network Events",
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(8, 8),
                UseMnemonic = false
            };

            var lblLogLimitHint = new Label
            {
                Text = $"(จำกัด: {MaxLogLines} บรรทัด)",
                Font = new Font("Segoe UI", 8f),
                ForeColor = TextMuted,
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(pnlLogHeader.Width - 120, 9),
                UseMnemonic = false
            };

            pnlLogHeader.Controls.Add(lblLogTitle);
            pnlLogHeader.Controls.Add(lblLogLimitHint);

            rtbLogs = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(9, 11, 17),
                ForeColor = Color.FromArgb(215, 222, 240),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                HideSelection = false,
                Margin = new Padding(0)
            };

            panelLogContainer.Controls.Add(pnlLogHeader);
            panelLogContainer.Controls.Add(rtbLogs);
            pnlLogHeader.SendToBack();
            rtbLogs.BringToFront();
            splitMain.Panel2.Controls.Add(panelLogContainer);

            // Top-to-bottom dock stacking order
            this.Controls.Add(splitMain);
            this.Controls.Add(panelActions);
            this.Controls.Add(statsDashboard);
            this.Controls.Add(panelTop);
        }

        private void UpdateCaptchaToggleButton()
        {
            if (_config.AutoStartCaptcha)
            {
                btnToggleCaptcha.Text = "● Auto Captcha: ON";
                btnToggleCaptcha.BaseColor = Color.FromArgb(20, 48, 36);
                btnToggleCaptcha.HoverColor = Color.FromArgb(28, 62, 46);
                btnToggleCaptcha.BorderColor = Color.FromArgb(40, 160, 100);
                btnToggleCaptcha.ForeColorNormal = Success;
            }
            else
            {
                btnToggleCaptcha.Text = "○ Auto Captcha: OFF";
                btnToggleCaptcha.BaseColor = Color.FromArgb(28, 32, 46);
                btnToggleCaptcha.HoverColor = Color.FromArgb(36, 42, 60);
                btnToggleCaptcha.BorderColor = CardBorder;
                btnToggleCaptcha.ForeColorNormal = TextDim;
            }
        }

        private static void DrawRoundedChip(Graphics g, Control ctrl, Color borderColor)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = GetRoundedPath(new Rectangle(0, 0, ctrl.Width - 1, ctrl.Height - 1), 6);
            using var pen = new Pen(borderColor, 1f);
            g.DrawPath(pen, path);
        }

        private static GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
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

            UpdateLicenseBadgeDisplay();
        }

        private void UpdateLicenseBadgeDisplay()
        {
            if (_licenseExpiryDate.HasValue)
            {
                var remaining = _licenseExpiryDate.Value - DateTime.Now;
                if (remaining.TotalSeconds > 0)
                {
                    if (remaining.TotalDays >= 1)
                    {
                        lblLicenseBadge.Text = $"สิทธิ์: เหลือ {remaining.Days} วัน {remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    }
                    else
                    {
                        lblLicenseBadge.Text = $"สิทธิ์: เหลือ {remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    }
                    lblLicenseBadge.ForeColor = Success;
                }
                else
                {
                    lblLicenseBadge.Text = "สิทธิ์: หมดอายุแล้ว";
                    lblLicenseBadge.ForeColor = Danger;

                    if (_botManager.IsRunning)
                    {
                        AppendLog("[License] ❌ สิทธิ์การใช้งานหมดอายุแล้ว ระบบทำการหยุดการทำงานอัตโนมัติ", Danger);
                        StopBot();
                    }
                }
            }
            else
            {
                string display = string.IsNullOrWhiteSpace(_rawExpiryText) || _rawExpiryText.Equals("Active", StringComparison.OrdinalIgnoreCase)
                    ? "ถาวร (Active)"
                    : _rawExpiryText;
                lblLicenseBadge.Text = $"สิทธิ์: {display}";
                lblLicenseBadge.ForeColor = Success;
            }
        }

        private void LoadExistingAccounts()
        {
            string workDir = BotProcessManager.GetWorkingDir();
            string accountsPath = Path.Combine(workDir, "accounts.txt");
            string statePath = Path.Combine(workDir, StateFileName);

            _accountsList.Clear();
            _accountMap.Clear();
            _failedAccountsList.Clear();
            _failedAccountMap.Clear();
            _totalRegisteredCount = 0;
            _successCount = 0;
            _failCount = 0;

            // Load saved state dictionary if exists (with .bak fallback for power cuts)
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

                            // Restore saved state if available
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

                            if (acc.IsCompleted)
                            {
                                _successCount++;
                            }
                            else if (acc.Status.Contains("ล้มเหลว") || acc.Status.Contains("ผิด"))
                            {
                                var failedAcc = new RegisteredAccount
                                {
                                    Index = _failedAccountsList.Count + 1,
                                    Username = acc.Username,
                                    Password = acc.Password,
                                    Status = acc.Status,
                                    RegisteredAt = acc.RegisteredAt,
                                    ResultDetail = acc.ResultDetail,
                                    SessionStatus = acc.SessionStatus
                                };
                                _failedAccountsList.Add(failedAcc);
                                _failedAccountMap[acc.Username] = failedAcc;
                            }
                        }
                    }

                    _failCount = _failedAccountsList.Count;
                    RefreshTokensFromStorage(showLog: false);
                    UpdateDashboardStats();
                    UpdateTabBadges();
                    string stateMsg = _successCount > 0 ? $" (จำสถานะเดิมสำเร็จแล้ว {_successCount} บัญชี)" : "";
                    AppendLog($"[System] โหลดข้อมูล accounts.txt สำเร็จ พบไอดีในคิวทั้งหมด {_totalRegisteredCount} บัญชี{stateMsg}", Color.FromArgb(140, 200, 255));
                }
                catch (Exception ex)
                {
                    AppendLog($"[System] โหลด accounts.txt ไม่สำเร็จ: {ex.Message}", Danger);
                }
            }
            else
            {
                UpdateDashboardStats();
                UpdateTabBadges();
            }

            if (dgvAccounts != null && !_isFailedTabActive)
            {
                dgvAccounts.DataSource = null;
                dgvAccounts.DataSource = _accountsList;
                dgvAccounts.Refresh();
                dgvAccounts.Invalidate();
            }
            else if (dgvFailedAccounts != null && _isFailedTabActive)
            {
                dgvFailedAccounts.DataSource = null;
                dgvFailedAccounts.DataSource = _failedAccountsList;
                dgvFailedAccounts.Refresh();
                dgvFailedAccounts.Invalidate();
            }
        }

        private DataGridView CreateStyledGridView(IBindingList source, bool isMainGrid = true)
        {
            var dgv = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = PanelDark,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.None,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoGenerateColumns = false,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 34,
                RowTemplate = { Height = 32 }
            };
            dgv.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            dgv.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;

            dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(16, 19, 28);
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(120, 130, 155);
            dgv.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);
            dgv.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            dgv.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
            dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(16, 19, 28);

            dgv.DefaultCellStyle.BackColor = PanelDark;
            dgv.DefaultCellStyle.ForeColor = TextMain;
            dgv.DefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 38, 58);
            dgv.DefaultCellStyle.SelectionForeColor = Color.White;
            dgv.DefaultCellStyle.Font = new Font("Consolas", 9.2f);
            dgv.DefaultCellStyle.Padding = new Padding(8, 0, 0, 0);

            dgv.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(14, 16, 25);
            dgv.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 38, 58);

            var colCheck = new DataGridViewCheckBoxColumn
            {
                DataPropertyName = "IsSelected",
                HeaderText = "☑",
                Width = 36,
                FlatStyle = FlatStyle.Flat,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter }
            };
            var colIndex = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Index",
                HeaderText = "#",
                Width = 45,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, ForeColor = TextDim }
            };
            var colUsername = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Username",
                HeaderText = "HOF ID (ไอดี)",
                Width = 145
            };
            var colToken = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "SessionStatus",
                HeaderText = "🔑 Token",
                Width = 95,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 8.8f) }
            };
            var colStatus = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Status",
                HeaderText = "สถานะ",
                Width = 115,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold) }
            };
            var colTime = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "RegisteredAt",
                HeaderText = "เวลา",
                Width = 70,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(140, 165, 200) }
            };
            var colDetail = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "ResultDetail",
                HeaderText = "รายละเอียด / ผลลัพธ์",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 180,
                DefaultCellStyle = { ForeColor = Color.FromArgb(200, 205, 220) }
            };

            dgv.Columns.AddRange(colCheck, colIndex, colUsername, colToken, colStatus, colTime, colDetail);
            dgv.DataSource = source;

            // Click header checkbox to toggle all selection
            dgv.ColumnHeaderMouseClick += (s, e) =>
            {
                if (e.ColumnIndex == 0)
                {
                    bool anyUnselected = source.Cast<RegisteredAccount>().Any(a => !a.IsSelected);
                    foreach (RegisteredAccount a in source)
                    {
                        a.IsSelected = anyUnselected;
                    }
                    dgv.Refresh();
                }
            };

            // ─── Mouse Drag Sweep Selection Engine (from WarZBotDaily) ───
            Point _dragStart = Point.Empty;
            int _dragStartRow = -1;
            bool _hasDragged = false;
            bool[]? _initialSelectionSnapshot = null;

            dgv.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var hitRight = dgv.HitTest(e.X, e.Y);
                    if (hitRight.RowIndex >= 0 && hitRight.RowIndex < source.Count)
                    {
                        dgv.CurrentCell = dgv.Rows[hitRight.RowIndex].Cells[Math.Max(0, hitRight.ColumnIndex)];
                        if (source[hitRight.RowIndex] is RegisteredAccount clickedAcc)
                        {
                            if (!source.Cast<RegisteredAccount>().Any(a => a.IsSelected))
                            {
                                clickedAcc.IsSelected = true;
                                dgv.Refresh();
                            }
                        }
                    }
                    return;
                }

                if (e.Button != MouseButtons.Left) return;
                _dragStart = e.Location;
                _hasDragged = false;

                var hit = dgv.HitTest(e.X, e.Y);
                _dragStartRow = hit.RowIndex;

                if (hit.RowIndex >= 0 && hit.RowIndex < source.Count)
                {
                    _initialSelectionSnapshot = source.Cast<RegisteredAccount>().Select(a => a.IsSelected).ToArray();
                }
                else
                {
                    _initialSelectionSnapshot = null;
                }
            };

            dgv.MouseMove += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || _dragStart == Point.Empty || _dragStartRow < 0) return;

                int dx = Math.Abs(e.X - _dragStart.X);
                int dy = Math.Abs(e.Y - _dragStart.Y);
                if (!_hasDragged && dx < 5 && dy < 5) return;

                _hasDragged = true;

                // Auto-scroll vertically when dragging near top or bottom
                if (e.Y < 25 && dgv.FirstDisplayedScrollingRowIndex > 0)
                {
                    try { dgv.FirstDisplayedScrollingRowIndex--; } catch { }
                }
                else if (e.Y > dgv.ClientSize.Height - 25 && dgv.FirstDisplayedScrollingRowIndex < source.Count - 1)
                {
                    try { dgv.FirstDisplayedScrollingRowIndex++; } catch { }
                }

                var hit = dgv.HitTest(e.X, e.Y);
                int currentRow = hit.RowIndex;
                if (currentRow < 0)
                {
                    if (e.Y < 0) currentRow = 0;
                    else if (e.Y >= dgv.ClientSize.Height) currentRow = source.Count - 1;
                    else return;
                }
                currentRow = Math.Clamp(currentRow, 0, source.Count - 1);

                int from = Math.Min(_dragStartRow, currentRow);
                int to = Math.Max(_dragStartRow, currentRow);

                bool isCtrl = (ModifierKeys & Keys.Control) == Keys.Control;

                for (int i = 0; i < source.Count; i++)
                {
                    bool inRange = (i >= from && i <= to);
                    bool shouldBeSelected = isCtrl
                        ? (inRange ? true : (_initialSelectionSnapshot != null && _initialSelectionSnapshot[i]))
                        : inRange;

                    if (source[i] is RegisteredAccount acc && acc.IsSelected != shouldBeSelected)
                    {
                        acc.IsSelected = shouldBeSelected;
                        dgv.InvalidateRow(i);
                    }
                }
            };

            dgv.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;

                if (_hasDragged)
                {
                    _dragStart = Point.Empty;
                    _dragStartRow = -1;
                    _hasDragged = false;
                    _initialSelectionSnapshot = null;
                    dgv.Refresh();
                    return;
                }

                if (_dragStart != Point.Empty)
                {
                    var hit = dgv.HitTest(e.X, e.Y);

                    if (hit.RowIndex < 0)
                    {
                        // Clicked empty background -> Deselect all
                        if (hit.ColumnIndex != 0)
                        {
                            bool hadSelections = source.Cast<RegisteredAccount>().Any(a => a.IsSelected);
                            if (hadSelections)
                            {
                                foreach (RegisteredAccount a in source) a.IsSelected = false;
                                dgv.Refresh();
                            }
                        }
                    }
                    else if (hit.RowIndex >= 0 && hit.RowIndex < source.Count)
                    {
                        bool isCtrl = (ModifierKeys & Keys.Control) == Keys.Control;
                        if (source[hit.RowIndex] is RegisteredAccount acc)
                        {
                            if (hit.ColumnIndex == 0)
                            {
                                // Clicked checkbox cell directly -> Toggle
                                acc.IsSelected = !acc.IsSelected;
                                dgv.Refresh();
                            }
                            else
                            {
                                if (isCtrl)
                                {
                                    acc.IsSelected = !acc.IsSelected;
                                }
                                else
                                {
                                    int totalSelected = source.Cast<RegisteredAccount>().Count(a => a.IsSelected);
                                    bool isCurrentSelected = acc.IsSelected;

                                    if (totalSelected == 1 && isCurrentSelected)
                                    {
                                        acc.IsSelected = false;
                                    }
                                    else
                                    {
                                        foreach (RegisteredAccount a in source) a.IsSelected = false;
                                        acc.IsSelected = true;
                                    }
                                }
                                dgv.Refresh();
                            }
                        }
                    }

                    _dragStart = Point.Empty;
                    _dragStartRow = -1;
                    _hasDragged = false;
                    _initialSelectionSnapshot = null;
                }
            };

            // ─── Context Menu (for Main Grid) ───
            if (isMainGrid)
            {
                var cms = new ContextMenuStrip
                {
                    BackColor = CardDark,
                    ForeColor = TextMain,
                    ShowImageMargin = false,
                    Font = new Font("Segoe UI Semibold", 9.2f)
                };

                var miRunFromHere = new ToolStripMenuItem("▶️  เริ่มรันตั้งแต่ไอดีนี้ (Start From This Row)") { ForeColor = Accent };
                miRunFromHere.Click += async (s, e) =>
                {
                    int clickedRow = dgv.CurrentCell?.RowIndex ?? -1;
                    if (clickedRow >= 0 && clickedRow < _accountsList.Count)
                    {
                        await RunFromAccountAsync(clickedRow);
                    }
                };

                var miRunSelected = new ToolStripMenuItem("🚀  รันเฉพาะไอดีที่เลือกไว้ (Run Selected)") { ForeColor = Success };
                miRunSelected.Click += async (s, e) =>
                {
                    await RunSelectedAccountsAsync();
                };

                var miSelectAll = new ToolStripMenuItem("☑️  เลือกทั้งหมด (Select All)");
                miSelectAll.Click += (s, e) =>
                {
                    foreach (var a in _accountsList) a.IsSelected = true;
                    dgv.Refresh();
                };

                var miDeselectAll = new ToolStripMenuItem("⬜  ยกเลิกการเลือกทั้งหมด (Deselect All)");
                miDeselectAll.Click += (s, e) =>
                {
                    foreach (var a in _accountsList) a.IsSelected = false;
                    dgv.Refresh();
                };

                var miResetStatus = new ToolStripMenuItem("🔄  รีเซ็ตสถานะเป็นรอคิว (Reset Status)") { ForeColor = Warning };
                miResetStatus.Click += (s, e) =>
                {
                    ResetSelectedAccountsStatus();
                };

                var miClearAllState = new ToolStripMenuItem("🗑️  ล้างประวัติสถานะทั้งหมด (Clear All State)") { ForeColor = Danger };
                miClearAllState.Click += (s, e) =>
                {
                    ClearAllState();
                };

                cms.Items.AddRange(new ToolStripItem[] {
                    miRunFromHere,
                    miRunSelected,
                    new ToolStripSeparator(),
                    miSelectAll,
                    miDeselectAll,
                    new ToolStripSeparator(),
                    miResetStatus,
                    miClearAllState
                });

                dgv.ContextMenuStrip = cms;
            }

            dgv.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= source.Count) return;
                if (source[e.RowIndex] is RegisteredAccount item)
                {
                    if (item.IsSelected)
                    {
                        e.CellStyle.BackColor = Color.FromArgb(28, 34, 54);
                    }

                    if (dgv.Columns[e.ColumnIndex].DataPropertyName == "SessionStatus")
                    {
                        if (item.SessionStatus.Contains("มี Token") || item.SessionStatus.Contains("🟢") || item.SessionStatus.Contains("พร้อม"))
                        {
                            e.CellStyle.ForeColor = Color.FromArgb(52, 211, 153); // Emerald Green
                            e.CellStyle.SelectionForeColor = Color.FromArgb(110, 231, 183);
                        }
                        else
                        {
                            e.CellStyle.ForeColor = Color.FromArgb(120, 130, 155);
                            e.CellStyle.SelectionForeColor = Color.FromArgb(160, 170, 195);
                        }
                    }

                    if (dgv.Columns[e.ColumnIndex].DataPropertyName == "Status")
                    {
                        if (item.Status.Contains("สำเร็จ") || item.Status.Contains("เก็บ Token"))
                        {
                            e.CellStyle.ForeColor = Success;
                            e.CellStyle.SelectionForeColor = SuccessHover;
                        }
                        else if (item.Status.Contains("Limit") || item.Status.Contains("รอรันซ้ำ"))
                        {
                            e.CellStyle.ForeColor = Warning;
                            e.CellStyle.SelectionForeColor = WarningHover;
                        }
                        else if (item.Status.Contains("ล้มเหลว") || item.Status.Contains("ผิด") || item.Status.Contains("ไม่ได้"))
                        {
                            e.CellStyle.ForeColor = Danger;
                            e.CellStyle.SelectionForeColor = DangerHover;
                        }
                        else if (item.Status.Contains("กำลัง"))
                        {
                            e.CellStyle.ForeColor = Accent;
                            e.CellStyle.SelectionForeColor = AccentHover;
                        }
                        else
                        {
                            e.CellStyle.ForeColor = TextDim;
                        }
                    }
                }
            };

            return dgv;
        }

        private void SwitchTab(bool showFailed)
        {
            _isFailedTabActive = showFailed;
            dgvAccounts.Visible = !showFailed;
            dgvFailedAccounts.Visible = showFailed;

            if (showFailed)
            {
                dgvFailedAccounts.DataSource = null;
                dgvFailedAccounts.DataSource = _failedAccountsList;
                dgvFailedAccounts.BringToFront();
                dgvFailedAccounts.Refresh();
                btnTabAll.BaseColor = CardDark;
                btnTabAll.BorderColor = CardBorder;
                btnTabAll.ForeColorNormal = TextDim;

                btnTabFailed.BaseColor = Color.FromArgb(50, 20, 26);
                btnTabFailed.BorderColor = Danger;
                btnTabFailed.ForeColorNormal = DangerHover;

                btnRerunFailed.Visible = true;
                lblAccountsCount.Text = $"{_failedAccountsList.Count} บัญชีล้มเหลว";
                lblAccountsCount.ForeColor = Danger;

                if (!_botManager.IsRunning)
                {
                    btnStart.Text = "▶  รันเฉพาะไอดีล้มเหลว";
                }
            }
            else
            {
                dgvAccounts.DataSource = null;
                dgvAccounts.DataSource = _accountsList;
                dgvAccounts.BringToFront();
                dgvAccounts.Refresh();
                btnTabAll.BaseColor = Color.FromArgb(36, 42, 64);
                btnTabAll.BorderColor = Accent;
                btnTabAll.ForeColorNormal = Color.White;

                btnTabFailed.BaseColor = CardDark;
                btnTabFailed.BorderColor = CardBorder;
                btnTabFailed.ForeColorNormal = TextDim;

                btnRerunFailed.Visible = false;
                lblAccountsCount.Text = $"{_successCount} / {_accountsList.Count} สำเร็จ";
                lblAccountsCount.ForeColor = Success;
                lblAccountsCount.Visible = true;

                if (!_botManager.IsRunning)
                {
                    btnStart.Text = "▶  เริ่มทำงาน (Start)";
                }
            }
        }

        private void UpdateTabBadges()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(UpdateTabBadges));
                return;
            }

            btnTabAll.Text = $"📋 ทั้งหมด ({_accountsList.Count})";
            btnTabFailed.Text = $"❌ ล้มเหลว ({_failedAccountsList.Count})";
            btnRerunFailed.Text = $"⚡ รันเฉพาะที่ผิดพลาด ({_failedAccountsList.Count})";

            if (_isFailedTabActive)
            {
                lblAccountsCount.Text = $"{_failedAccountsList.Count} บัญชีล้มเหลว";
                lblAccountsCount.ForeColor = Danger;
            }
            else
            {
                lblAccountsCount.Text = $"{_successCount} / {_accountsList.Count} สำเร็จ";
                lblAccountsCount.ForeColor = Success;
            }
        }

        private async Task RerunFailedAccountsAsync()
        {
            if (_failedAccountsList.Count == 0)
            {
                AppendLog("[System] ℹ️ ไม่มีรายการไอดีที่ผิดพลาดในคิว", Color.FromArgb(140, 200, 255));
                MessageBox.Show(this, "ไม่พบบัญชีที่ผิดพลาดในคิวขณะนี้", "ไม่มีไอดีผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_botManager.IsRunning) return;

            string workDir = BotProcessManager.GetWorkingDir();
            string failedPath = Path.Combine(workDir, "accounts_failed.txt");

            try
            {
                var lines = new List<string>();
                foreach (var acc in _failedAccountsList)
                {
                    string pass = string.IsNullOrEmpty(acc.Password) ? acc.Username : acc.Password;
                    lines.Add($"ID: {acc.Username} | PASS: {pass}");
                }
                File.WriteAllLines(failedPath, lines);
                AppendLog($"[System] ⚡ บันทึกไฟล์ accounts_failed.txt ({_failedAccountsList.Count} บัญชี) เรียบร้อย เริ่มต้นรันเฉพาะไอดีล้มเหลว...", Color.FromArgb(255, 180, 80));
            }
            catch (Exception ex)
            {
                AppendLog($"[System] ❌ สร้างไฟล์ accounts_failed.txt ไม่สำเร็จ: {ex.Message}", Danger);
                return;
            }

            await StartBotAsync("accounts_failed.txt");
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
            if (targets.Count == 0)
            {
                targets = _accountsList.ToList();
            }

            foreach (var acc in targets)
            {
                acc.Status = "⏳ รอคิว";
                acc.RegisteredAt = "-";
                acc.ResultDetail = "อยู่ในคิวรอการตรวจสอบ";

                if (_failedAccountMap.TryGetValue(acc.Username, out var failedAcc))
                {
                    _failedAccountsList.Remove(failedAcc);
                    _failedAccountMap.Remove(acc.Username);
                }
            }

            for (int i = 0; i < _failedAccountsList.Count; i++)
            {
                _failedAccountsList[i].Index = i + 1;
            }

            _successCount = _accountsList.Count(a => a.IsCompleted);
            _failCount = _failedAccountsList.Count;

            SaveState();
            dgvAccounts.Refresh();
            dgvFailedAccounts.Refresh();
            UpdateDashboardStats();
            UpdateTabBadges();
            AppendLog($"[System] 🔄 รีเซ็ตสถานะ {targets.Count} บัญชีเป็น 'รอคิว' เรียบร้อย", Color.FromArgb(251, 191, 36));
        }

        private void ClearAllState()
        {
            if (MessageBox.Show(this, "ต้องการล้างประวัติสถานะทั้งหมด (accounts_state.json) และรีเซ็ตทุกไอดีเป็นรอคิวใช่หรือไม่?", "ยืนยันการล้างสถานะ", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
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
                _failedAccountsList.Clear();
                _failedAccountMap.Clear();
                _successCount = 0;
                _failCount = 0;

                dgvAccounts.Refresh();
                dgvFailedAccounts.Refresh();
                UpdateDashboardStats();
                UpdateTabBadges();
                AppendLog("[System] 🗑️ ล้างประวัติสถานะและรีเซ็ตทุกบัญชีเรียบร้อย", Color.FromArgb(248, 113, 113));
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
                MessageBox.Show(this, "กรุณาติ๊กเลือกไอดีที่ต้องการรันก่อน (หรือคลิกลากคลุมแถวที่ต้องการ)", "ยังไม่ได้เลือกไอดี", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            await RunCustomAccountQueueAsync(selected, $"รันเฉพาะไอดีที่เลือกไว้ทั้งหมด {selected.Count} บัญชี");
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
                    // Set status for the accounts in queue to waiting
                    acc.Status = "⏳ รอคิว";
                    acc.ResultDetail = "อยู่ในคิวรอทำงาน";
                }
                File.WriteAllLines(queuePath, lines);
                AppendLog($"[System] ⚡ {logDescription}...", Color.FromArgb(255, 180, 80));
            }
            catch (Exception ex)
            {
                AppendLog($"[System] ❌ สร้างไฟล์คิว accounts_queue.txt ไม่สำเร็จ: {ex.Message}", Danger);
                return;
            }

            dgvAccounts.Refresh();
            UpdateDashboardStats();
            UpdateTabBadges();
            await StartBotAsync("accounts_queue.txt");
        }

        private async Task HandleStartButtonClickAsync()
        {
            if (_accountsList.Count == 0)
            {
                MessageBox.Show(this, "ไม่พบบัญชีในระบบ กรุณาใส่บัญชีใน accounts.txt หรือลากไฟล์ .txt เข้ามาในโปรแกรม", "ไม่มีบัญชี", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int selectedCount = _accountsList.Count(a => a.IsSelected);
            // If user explicitly selected a subset of accounts
            if (selectedCount > 0 && selectedCount < _accountsList.Count)
            {
                await RunSelectedAccountsAsync();
                return;
            }

            // Otherwise, check for uncompleted accounts
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
                    _failedAccountsList.Clear();
                    _failedAccountMap.Clear();
                    _successCount = 0;
                    _failCount = 0;
                    SaveState();
                    dgvAccounts.Refresh();
                    dgvFailedAccounts.Refresh();
                    UpdateDashboardStats();
                    UpdateTabBadges();
                    await StartBotAsync("accounts.txt");
                }
                return;
            }

            if (pendingAccounts.Count == _accountsList.Count)
            {
                await StartBotAsync("accounts.txt");
            }
            else
            {
                await RunCustomAccountQueueAsync(pendingAccounts, $"พบ {pendingAccounts.Count} บัญชีที่ยังไม่เสร็จ (ข้าม {_accountsList.Count - pendingAccounts.Count} บัญชีที่สำเร็จแล้ว) กำลังเริ่มรันเฉพาะบัญชีที่เหลือ");
            }
        }

        private async Task StartBotAsync(string accountsFile = "accounts.txt")
        {
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            btnThreadMinus.Enabled = false;
            btnThreadPlus.Enabled = false;
            btnToggleCaptcha.Enabled = false;
            btnRerunFailed.Enabled = false;

            _currentRunningFile = accountsFile;
            _uptimeTimer.Restart();

            if (accountsFile == "accounts_failed.txt")
            {
                // Reset status of failed list accounts for rerun
                foreach (var acc in _failedAccountsList)
                {
                    acc.Status = "⏳ รอคิว";
                    acc.ResultDetail = "อยู่ในคิวรันซ้ำ";
                }
            }

            UpdateDashboardStats();
            UpdateTabBadges();

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

            btnStart.Enabled = true;
            btnStop.Enabled = false;
            btnThreadMinus.Enabled = true;
            btnThreadPlus.Enabled = true;
            btnToggleCaptcha.Enabled = true;
            btnRerunFailed.Enabled = true;
            btnStart.Text = _isFailedTabActive ? "▶  รันเฉพาะไอดีล้มเหลว" : "▶  เริ่มทำงาน (Start)";
            UpdateDashboardStats();
            UpdateTabBadges();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int wMsg, int wParam, int lParam);
        private const int WM_SETREDRAW = 0x000B;

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

        // ═══════════════════════════════════════════════════════════════════
        //  NON-BLOCKING BATCH LOG FLUSH (Completely Prevents UI Thread Lock)
        // ═══════════════════════════════════════════════════════════════════
        private void FlushLogs(object? sender, EventArgs e)
        {
            if (rtbLogs == null || rtbLogs.IsDisposed || _logQueue.IsEmpty) return;

            var batch = new List<(string message, Color color, string time)>();
            while (_logQueue.TryDequeue(out var item) && batch.Count < 60)
            {
                batch.Add(item);
            }
            if (batch.Count == 0) return;

            try
            {
                SendMessage(rtbLogs.Handle, WM_SETREDRAW, 0, 0);

                foreach (var (message, color, time) in batch)
                {
                    _currentLogLines++;

                    if (_currentLogLines > MaxLogLines)
                    {
                        try
                        {
                            int charIndex = rtbLogs.GetFirstCharIndexFromLine(TrimBatchLines);
                            if (charIndex > 0)
                            {
                                rtbLogs.Select(0, charIndex);
                                rtbLogs.SelectedText = "";
                                rtbLogs.ClearUndo();
                                _currentLogLines -= TrimBatchLines;
                            }
                            else
                            {
                                rtbLogs.Clear();
                                rtbLogs.ClearUndo();
                                _currentLogLines = 0;
                            }
                        }
                        catch
                        {
                            rtbLogs.Clear();
                            rtbLogs.ClearUndo();
                            _currentLogLines = 0;
                        }
                    }

                    rtbLogs.SelectionStart = rtbLogs.TextLength;
                    rtbLogs.SelectionLength = 0;

                    rtbLogs.SelectionColor = Color.FromArgb(90, 100, 125);
                    rtbLogs.AppendText($"[{time}] ");

                    rtbLogs.SelectionColor = color;
                    rtbLogs.AppendText(message + Environment.NewLine);
                }

                rtbLogs.ClearUndo();
                rtbLogs.ScrollToCaret();
            }
            catch { }
            finally
            {
                SendMessage(rtbLogs.Handle, WM_SETREDRAW, 1, 0);
                rtbLogs.Invalidate();
            }
        }

        private void UpdateDashboardStats()
        {
            int tokenCount = _accountsList.Count(a => a.SessionStatus.Contains("มี Token") || a.SessionStatus.Contains("🟢"));
            lock (_cachedTokenUsers)
            {
                tokenCount = Math.Max(tokenCount, _cachedTokenUsers.Count);
            }
            statsDashboard.TotalAccounts = $"{_successCount} บัญชี";
            statsDashboard.Speed = $"{_failCount} บัญชี";
            statsDashboard.TokenStatus = $"{tokenCount} บัญชี";
            lblAccountsCount.Text = $"{_successCount} / {_accountsList.Count} สำเร็จ (🔑 Token ในคลัง: {tokenCount} บัญชี)";
        }

        private void BotManager_OnTokenCaptured(string username)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnTokenCaptured(username)));
                return;
            }

            if (string.IsNullOrWhiteSpace(username)) return;

            lock (_cachedTokenUsers)
            {
                _cachedTokenUsers.Add(username);
            }

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.SessionStatus = "🟢 มี Token";
            }
            if (_failedAccountMap.TryGetValue(username, out var failedAcc))
            {
                failedAcc.SessionStatus = "🟢 มี Token";
            }

            dgvAccounts?.Refresh();
            dgvFailedAccounts?.Refresh();
            UpdateDashboardStats();
        }

        private void BotManager_OnAccountRunning(string username)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnAccountRunning(username)));
                return;
            }

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = "⚡ กำลังล็อกอิน...";
                acc.RegisteredAt = DateTime.Now.ToString("HH:mm:ss");
                acc.ResultDetail = "กำลังเปิดแท็บและตรวจสอบ Captcha...";
            }
            else
            {
                var newAcc = new RegisteredAccount
                {
                    Index = _accountsList.Count + 1,
                    Username = username,
                    Password = username,
                    Status = "⚡ กำลังล็อกอิน...",
                    RegisteredAt = DateTime.Now.ToString("HH:mm:ss"),
                    ResultDetail = "กำลังเปิดแท็บและตรวจสอบ Captcha..."
                };
                _accountsList.Add(newAcc);
                _accountMap[username] = newAcc;
            }

            if (_failedAccountMap.TryGetValue(username, out var failedAcc))
            {
                failedAcc.Status = "⚡ กำลังล็อกอิน...";
                failedAcc.RegisteredAt = DateTime.Now.ToString("HH:mm:ss");
                failedAcc.ResultDetail = "กำลังเปิดแท็บและตรวจสอบ Captcha...";
            }

            dgvAccounts.Refresh();
            UpdateDashboardStats();
        }

        private void BotManager_OnAccountSuccess(string username, string elapsed, string detail)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnAccountSuccess(username, elapsed, detail)));
                return;
            }

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
            else
            {
                var newAcc = new RegisteredAccount
                {
                    Index = _accountsList.Count + 1,
                    Username = username,
                    Password = username,
                    Status = statusMsg,
                    SessionStatus = sessionTag,
                    RegisteredAt = elapsed,
                    ResultDetail = resultMsg
                };
                _accountsList.Add(newAcc);
                _accountMap[username] = newAcc;
            }

            // หากไอดีนี้เคยล้มเหลวมาก่อน แล้วรันซ้ำผ่าน ให้ดึงออกจากรายการล้มเหลว
            if (_failedAccountMap.TryGetValue(username, out var failedAcc))
            {
                _failedAccountsList.Remove(failedAcc);
                _failedAccountMap.Remove(username);
                for (int i = 0; i < _failedAccountsList.Count; i++)
                {
                    _failedAccountsList[i].Index = i + 1;
                }
            }

            _successCount++;
            _failCount = _failedAccountsList.Count;
            dgvAccounts.Refresh();
            dgvFailedAccounts.Refresh();
            UpdateDashboardStats();
            UpdateTabBadges();
            SaveState();
        }

        private void BotManager_OnAccountLimit(string username, string elapsed, string reason)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnAccountLimit(username, elapsed, reason)));
                return;
            }

            string limitDetail = string.IsNullOrWhiteSpace(reason) ? "ติด Limit Cloudflare ชั่วคราว (รอรันซ้ำ)" : reason;

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = "⏳ ติด Limit (รอรันซ้ำ)";
                acc.RegisteredAt = elapsed;
                acc.ResultDetail = limitDetail;
            }
            else
            {
                var newAcc = new RegisteredAccount
                {
                    Index = _accountsList.Count + 1,
                    Username = username,
                    Password = username,
                    Status = "⏳ ติด Limit (รอรันซ้ำ)",
                    RegisteredAt = elapsed,
                    ResultDetail = limitDetail
                };
                _accountsList.Add(newAcc);
                _accountMap[username] = newAcc;
            }

            dgvAccounts.Refresh();
            UpdateDashboardStats();
            SaveState();
        }

        private void BotManager_OnAccountRetry(string username, string elapsed, string reason)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnAccountRetry(username, elapsed, reason)));
                return;
            }

            string retryDetail = string.IsNullOrWhiteSpace(reason) ? "เกิดปัญหาชั่วคราว (รอรันซ้ำ)" : reason;

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = "🔄 รอรันซ้ำ";
                acc.RegisteredAt = elapsed;
                acc.ResultDetail = retryDetail;
            }
            else
            {
                var newAcc = new RegisteredAccount
                {
                    Index = _accountsList.Count + 1,
                    Username = username,
                    Password = username,
                    Status = "🔄 รอรันซ้ำ",
                    RegisteredAt = elapsed,
                    ResultDetail = retryDetail
                };
                _accountsList.Add(newAcc);
                _accountMap[username] = newAcc;
            }

            dgvAccounts.Refresh();
            UpdateDashboardStats();
            SaveState();
        }

        private void BotManager_OnAccountFail(string username, string elapsed, string reason)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnAccountFail(username, elapsed, reason)));
                return;
            }

            string failDetail = string.IsNullOrWhiteSpace(reason) ? "รหัสผ่านไม่ถูกต้อง หรือล็อกอินไม่ผ่าน" : reason;

            if (_accountMap.TryGetValue(username, out var acc))
            {
                acc.Status = "❌ ล้มเหลว";
                acc.RegisteredAt = elapsed;
                acc.ResultDetail = failDetail;
            }
            else
            {
                acc = new RegisteredAccount
                {
                    Index = _accountsList.Count + 1,
                    Username = username,
                    Password = username,
                    Status = "❌ ล้มเหลว",
                    RegisteredAt = elapsed,
                    ResultDetail = failDetail
                };
                _accountsList.Add(acc);
                _accountMap[username] = acc;
            }

            // เพิ่มหรืออัปเดตในตารางไอดีที่ผิดพลาด
            if (!_failedAccountMap.TryGetValue(username, out var failedAcc))
            {
                failedAcc = new RegisteredAccount
                {
                    Index = _failedAccountsList.Count + 1,
                    Username = acc.Username,
                    Password = acc.Password,
                    Status = acc.Status,
                    RegisteredAt = acc.RegisteredAt,
                    ResultDetail = acc.ResultDetail
                };
                _failedAccountsList.Add(failedAcc);
                _failedAccountMap[username] = failedAcc;
            }
            else
            {
                failedAcc.Status = acc.Status;
                failedAcc.RegisteredAt = acc.RegisteredAt;
                failedAcc.ResultDetail = acc.ResultDetail;
            }

            _failCount = _failedAccountsList.Count;
            UpdateDashboardStats();
            UpdateTabBadges();
            SaveState();
        }

        private void BotManager_OnStateChanged(bool isRunning)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnStateChanged(isRunning)));
                return;
            }

            btnStart.Enabled = !isRunning;
            btnStop.Enabled = isRunning;
            btnThreadMinus.Enabled = !isRunning;
            btnThreadPlus.Enabled = !isRunning;
            btnToggleCaptcha.Enabled = !isRunning;
            if (btnRerunFailed != null) btnRerunFailed.Enabled = !isRunning;

            if (!isRunning)
            {
                btnStart.Text = _isFailedTabActive ? "▶  รันเฉพาะไอดีล้มเหลว" : "▶  เริ่มทำงาน (Start)";
                GC.Collect(2, GCCollectionMode.Optimized);
            }
        }

        private void BotManager_OnVpnChanged(string ip)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnVpnChanged(ip)));
                return;
            }

            AppendLog($"[VPN] 🌐 ตรวจพบ IP ใหม่: {ip}", Color.FromArgb(80, 190, 255));
        }

        private int _uiTokenSyncCounter = 0;
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            // 1. Update Real-time License Expiry Countdown
            UpdateLicenseBadgeDisplay();

            // 2. Update Bot Uptime & Real-time Stats
            if (_uptimeTimer.IsRunning)
            {
                var ts = _uptimeTimer.Elapsed;
                statsDashboard.Uptime = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

                // 3. Periodic real-time storage check every 3 seconds while running
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

        public static (string? id, string? pass) ParseAccountLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return (null, null);

            string trimmed = line.Trim();
            if (trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                return (null, null);

            // 1. Labeled format: ID: x | PASS: y
            var labeled = LabeledFormatRegex.Match(trimmed);
            if (labeled.Success)
            {
                return (labeled.Groups["id"].Value.Trim(), labeled.Groups["pass"].Value.Trim());
            }

            // 2. Delimited format: |, :, ,, \t
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
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(pass))
                            return (id, pass);
                    }
                }
            }

            // 3. Space delimited (user pass)
            if (trimmed.Contains(' '))
            {
                var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    var id = parts[0].Trim();
                    var pass = parts[1].Trim();
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(pass))
                        return (id, pass);
                }
            }

            // 4. Single string format: ID and Password are the same! (e.g. Davidz1893400)
            return (trimmed, trimmed);
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

                AppendLog($"📁 ลากวาง/นำเข้าไฟล์ '{Path.GetFileName(filePath)}' สำเร็จ อ่านพบทั้งหมด {added} บัญชี (รองรับทุกรูปแบบ ID:PASS, ID|PASS, และ ID=PASS)!", Color.FromArgb(52, 211, 153));
                MessageBox.Show(this, $"นำเข้าคิวไอดีจากไฟล์ '{Path.GetFileName(filePath)}' สำเร็จทั้งหมด {added} บัญชี!\n\n(บันทึกลง accounts.txt พร้อมแสดงผลในตารางเรียบร้อยแล้ว)", "Import Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"ไม่สามารถอ่านไฟล์ได้: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        // Global Emergency Stop Hotkey F12
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID_F12 = 0x0F12;
        private const uint VK_F12 = 0x7B;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private HashSet<string> GetCachedTokenUsernames()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                                using var doc = System.Text.Json.JsonDocument.Parse(content);
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                {
                                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                        prop.Value.TryGetProperty("token", out var tok))
                                    {
                                        string? tokStr = tok.GetString();
                                        if (!string.IsNullOrWhiteSpace(tokStr) &&
                                            tokStr.Length > 50 &&
                                            tokStr.StartsWith("eyJ") &&
                                            tokStr.Count(c => c == '.') == 2)
                                        {
                                            result.Add(prop.Name.Trim());
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
            return result;
        }

        public void RefreshTokensFromStorage(bool showLog = false)
        {
            var tokenUsers = GetCachedTokenUsernames();
            lock (_cachedTokenUsers)
            {
                _cachedTokenUsers.Clear();
                foreach (var u in tokenUsers) _cachedTokenUsers.Add(u);
            }
            int hasTokenCount = 0;

            foreach (var acc in _accountsList)
            {
                if (tokenUsers.Contains(acc.Username))
                {
                    acc.SessionStatus = "🟢 มี Token";
                    hasTokenCount++;
                }
                else
                {
                    acc.SessionStatus = "⚪ ไม่มี";
                }
            }

            foreach (var failed in _failedAccountsList)
            {
                if (tokenUsers.Contains(failed.Username))
                {
                    failed.SessionStatus = "🟢 มี Token";
                }
                else
                {
                    failed.SessionStatus = "⚪ ไม่มี";
                }
            }

            dgvAccounts?.Refresh();
            dgvFailedAccounts?.Refresh();
            UpdateDashboardStats();

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

    /// <summary>
    /// Custom Double-Buffered Stats Dashboard to eliminate any nested panel scanline/flicker artifacts.
    /// </summary>
    public class StatsDashboardControl : Control
    {
        private string _totalAccounts = "0 บัญชี";
        private string _speed = "0 บัญชี";
        private string _tokenStatus = "0 บัญชี";
        private string _uptime = "00:00:00";

        public string TotalAccounts
        {
            get => _totalAccounts;
            set { _totalAccounts = value; Invalidate(); }
        }

        public string Speed
        {
            get => _speed;
            set { _speed = value; Invalidate(); }
        }

        public string TokenStatus
        {
            get => _tokenStatus;
            set { _tokenStatus = value; Invalidate(); }
        }

        public string VpnStatus
        {
            get => "";
            set { /* No-op, preserves TokenStatus */ }
        }

        public string Uptime
        {
            get => _uptime;
            set { _uptime = value; Invalidate(); }
        }

        public StatsDashboardControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var bgBrush = new SolidBrush(Parent?.BackColor ?? Color.FromArgb(12, 14, 22));
            g.FillRectangle(bgBrush, ClientRectangle);

            int paddingX = 12;
            int paddingY = 4;
            int gap = 8;
            int availableWidth = Math.Max(100, Width - (paddingX * 2) - (gap * 3));
            int cardWidth = Math.Max(60, availableWidth / 4);
            int cardHeight = Math.Max(30, Height - (paddingY * 2));

            var titles = new[] { "เข้าสู่ระบบสำเร็จ", "เข้าสู่ระบบไม่สำเร็จ", "🔑 มี Token ในคลัง", "เวลาทำงาน (Uptime)" };
            var values = new[] { _totalAccounts, _speed, _tokenStatus, _uptime };
            var accents = new[] {
                Color.FromArgb(52, 211, 153),  // Emerald (Success)
                Color.FromArgb(248, 113, 113), // Red (Fail)
                Color.FromArgb(168, 85, 247),  // Purple/Violet (Token Count)
                Color.FromArgb(250, 204, 21)   // Yellow (Uptime)
            };

            using var titleFont = new Font("Segoe UI Semibold", 8f, FontStyle.Bold);
            using var valFont = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            using var cardBg = new SolidBrush(Color.FromArgb(24, 28, 42));
            using var cardBorder = new Pen(Color.FromArgb(38, 44, 66), 1f);
            using var titleBrush = new SolidBrush(Color.FromArgb(140, 148, 175));
            using var valBrush = new SolidBrush(Color.FromArgb(245, 247, 255));

            for (int i = 0; i < 4; i++)
            {
                int x = paddingX + (i * (cardWidth + gap));
                var cardRect = new Rectangle(x, paddingY, cardWidth, cardHeight);

                using var path = GetRoundedPath(cardRect, 6);
                g.FillPath(cardBg, path);
                g.DrawPath(cardBorder, path);

                // Top colored accent bar
                using var accentPen = new Pen(accents[i], 2.5f);
                g.DrawLine(accentPen, x + 8, paddingY + 1, x + cardWidth - 8, paddingY + 1);

                // Title
                g.DrawString(titles[i], titleFont, titleBrush, x + 10, paddingY + 6);

                // Value (large bold)
                g.DrawString(values[i], valFont, valBrush, x + 10, paddingY + 23);
            }
        }

        private static GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

