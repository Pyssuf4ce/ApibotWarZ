using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
        private static readonly Color Danger = Color.FromArgb(248, 113, 113);
        private static readonly Color DangerHover = Color.FromArgb(252, 140, 140);
        private static readonly Color TextMain = Color.FromArgb(245, 247, 255);
        private static readonly Color TextDim = Color.FromArgb(140, 148, 175);
        private static readonly Color TextMuted = Color.FromArgb(90, 98, 122);
        private static readonly Color GridLine = Color.FromArgb(26, 30, 46);

        private readonly BotProcessManager _botManager;
        private readonly AppConfig _config;
        private readonly BindingList<RegisteredAccount> _accountsList = new();
        private readonly Stopwatch _uptimeTimer = new();
        private readonly System.Windows.Forms.Timer _uiTimer;
        private int _totalRegisteredCount = 0;

        // License & Expiry State
        private DateTime? _licenseExpiryDate;
        private string _rawExpiryText = "";

        // Log Buffer Limit Constants (prevents UI freezing/lag)
        private const int MaxLogLines = 500;
        private const int TrimBatchLines = 100;

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
        private FlowLayoutPanel flowActionsLeft = null!;
        private FlowLayoutPanel flowActionsRight = null!;
        private RoundedButton btnStart = null!;
        private RoundedButton btnStop = null!;
        private Panel pnlThreadsCapsule = null!;
        private Label lblThreadCount = null!;
        private RoundedButton btnThreadMinus = null!;
        private RoundedButton btnThreadPlus = null!;
        private RoundedButton btnToggleCaptcha = null!;
        private RoundedButton btnOpenAccounts = null!;
        private RoundedButton btnClearLogs = null!;

        // UI Controls - Main Content
        private SplitContainer splitMain = null!;
        private DataGridView dgvAccounts = null!;
        private RichTextBox rtbLogs = null!;
        private Label lblAccountsCount = null!;

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
            _botManager.OnAccountRegistered += BotManager_OnAccountRegistered;
            _botManager.OnStateChanged += BotManager_OnStateChanged;
            _botManager.OnVpnChanged += BotManager_OnVpnChanged;

            // Heartbeat Timer: 1 second interval for smooth real-time countdown & stats
            _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
        }

        private void SetupUI()
        {
            this.Text = "Apibot WarZ — Auto Register & VPN Coordinator";
            this.Size = new Size(1120, 740);
            this.MinimumSize = new Size(980, 640);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9.5f);
            this.ForeColor = TextMain;
            this.ShowIcon = true;

            // ═══════════════════════════════════════════
            //  1. TOP HEADER (Responsive Anti-Overlap)
            // ═══════════════════════════════════════════
            panelTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 72,
                BackColor = PanelDark,
                Padding = new Padding(22, 8, 22, 8)
            };
            panelTop.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1f);
                e.Graphics.DrawLine(pen, 0, panelTop.Height - 1, panelTop.Width, panelTop.Height - 1);
            };

            var pnlTitleGroup = new Panel
            {
                Dock = DockStyle.Left,
                Width = 520,
                BackColor = Color.Transparent
            };

            lblTitle = new Label
            {
                Text = "⚡ Apibot WarZ",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(0, 8),
                UseMnemonic = false
            };

            lblSubtitle = new Label
            {
                Text = "High-Performance Multi-Threaded Auto Register & Captcha Bypass",
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(2, 38),
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
                Padding = new Padding(0, 16, 0, 0)
            };

            string hwid = CloudLicenseService.GetHWID();
            string shortHwid = hwid.Length > 12 ? hwid.Substring(0, 12) + "..." : hwid;
            lblHwidBadge = new Label
            {
                Text = $"HWID: {shortHwid} (คัดลอก)",
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                ForeColor = Accent,
                BackColor = CardDark,
                AutoSize = true,
                Padding = new Padding(10, 6, 10, 6),
                Margin = new Padding(8, 0, 0, 0),
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
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                ForeColor = Success,
                BackColor = CardDark,
                AutoSize = true,
                Padding = new Padding(10, 6, 10, 6),
                Margin = new Padding(8, 0, 0, 0),
                UseMnemonic = false
            };
            lblLicenseBadge.Paint += (s, e) => DrawRoundedChip(e.Graphics, lblLicenseBadge, CardBorder);

            flowBadges.Controls.Add(lblHwidBadge);
            flowBadges.Controls.Add(lblLicenseBadge);

            panelTop.Controls.Add(flowBadges);
            panelTop.Controls.Add(pnlTitleGroup);
            this.Controls.Add(panelTop);

            // ═══════════════════════════════════════════
            //  2. STATS DASHBOARD (Custom Zero-Artifact Paint)
            // ═══════════════════════════════════════════
            statsDashboard = new StatsDashboardControl
            {
                Dock = DockStyle.Top,
                Height = 88,
                BackColor = BgDark,
                Padding = new Padding(18, 10, 18, 6)
            };
            this.Controls.Add(statsDashboard);

            // ═══════════════════════════════════════════
            //  3. ACTION TOOLBAR (Modern Ergonomic Controls)
            // ═══════════════════════════════════════════
            panelActions = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = PanelDark,
                Padding = new Padding(18, 10, 18, 10)
            };
            panelActions.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1f);
                e.Graphics.DrawLine(pen, 0, panelActions.Height - 1, panelActions.Width, panelActions.Height - 1);
            };

            // Left actions group
            flowActionsLeft = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = PanelDark
            };

            btnStart = new RoundedButton
            {
                Text = "▶  เริ่มทำงาน (Start)",
                Size = new Size(160, 40),
                BaseColor = Success,
                HoverColor = SuccessHover,
                BorderColor = Color.FromArgb(40, 180, 120),
                ForeColorNormal = Color.FromArgb(6, 24, 14),
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Margin = new Padding(0, 0, 10, 0)
            };
            btnStart.Click += async (s, e) => await StartBotAsync();

            btnStop = new RoundedButton
            {
                Text = "■  หยุดทำงาน (Stop)",
                Size = new Size(140, 40),
                BaseColor = Danger,
                HoverColor = DangerHover,
                BorderColor = Color.FromArgb(200, 70, 70),
                ForeColorNormal = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Enabled = false,
                Margin = new Padding(0, 0, 12, 0)
            };
            btnStop.Click += (s, e) => StopBot();

            // Custom Modern Threads Stepper Capsule
            pnlThreadsCapsule = new Panel
            {
                Size = new Size(330, 40),
                BackColor = CardDark,
                Padding = new Padding(6, 4, 6, 4),
                Margin = new Padding(0, 0, 10, 0)
            };
            pnlThreadsCapsule.Paint += (s, e) => DrawRoundedChip(e.Graphics, pnlThreadsCapsule, CardBorder);

            var lblThreadsTitle = new Label
            {
                Text = "บอท:",
                Font = new Font("Segoe UI Semibold", 9f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(10, 11),
                UseMnemonic = false
            };

            btnThreadMinus = new RoundedButton
            {
                Text = "–",
                Size = new Size(28, 28),
                Location = new Point(48, 6),
                BaseColor = Color.FromArgb(34, 40, 60),
                HoverColor = Color.FromArgb(45, 52, 78),
                BorderColor = CardBorder,
                CornerRadius = 6,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
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
                Text = Math.Max(1, Math.Min(20, _config.BotThreads)).ToString(),
                Size = new Size(30, 28),
                Location = new Point(78, 6),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = Color.White,
                UseMnemonic = false
            };

            btnThreadPlus = new RoundedButton
            {
                Text = "+",
                Size = new Size(28, 28),
                Location = new Point(110, 6),
                BaseColor = Color.FromArgb(34, 40, 60),
                HoverColor = Color.FromArgb(45, 52, 78),
                BorderColor = CardBorder,
                CornerRadius = 6,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColorNormal = TextMain
            };
            btnThreadPlus.Click += (s, e) =>
            {
                if (_config.BotThreads < 20)
                {
                    _config.BotThreads++;
                    lblThreadCount.Text = _config.BotThreads.ToString();
                    ConfigManager.Save(_config);
                }
            };

            // Custom Modern Captcha Toggle Button
            btnToggleCaptcha = new RoundedButton
            {
                Size = new Size(165, 28),
                Location = new Point(152, 6),
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 8.8f),
                EnableBorder = true
            };
            UpdateCaptchaToggleButton();
            btnToggleCaptcha.Click += (s, e) =>
            {
                _config.AutoStartCaptcha = !_config.AutoStartCaptcha;
                ConfigManager.Save(_config);
                UpdateCaptchaToggleButton();
            };

            pnlThreadsCapsule.Controls.Add(lblThreadsTitle);
            pnlThreadsCapsule.Controls.Add(btnThreadMinus);
            pnlThreadsCapsule.Controls.Add(lblThreadCount);
            pnlThreadsCapsule.Controls.Add(btnThreadPlus);
            pnlThreadsCapsule.Controls.Add(btnToggleCaptcha);

            flowActionsLeft.Controls.Add(btnStart);
            flowActionsLeft.Controls.Add(btnStop);
            flowActionsLeft.Controls.Add(pnlThreadsCapsule);

            // Right actions group
            flowActionsRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = PanelDark
            };

            btnClearLogs = new RoundedButton
            {
                Text = "ล้าง Log",
                Size = new Size(90, 40),
                BaseColor = CardDark,
                HoverColor = Color.FromArgb(34, 40, 60),
                BorderColor = CardBorder,
                ForeColorNormal = TextDim,
                Font = new Font("Segoe UI Semibold", 9f),
                Margin = new Padding(8, 0, 0, 0)
            };
            btnClearLogs.Click += (s, e) => rtbLogs.Clear();

            btnOpenAccounts = new RoundedButton
            {
                Text = "accounts.txt",
                Size = new Size(125, 40),
                BaseColor = CardDark,
                HoverColor = Color.FromArgb(34, 40, 60),
                BorderColor = CardBorder,
                ForeColorNormal = TextMain,
                Font = new Font("Segoe UI Semibold", 9f),
                Margin = new Padding(0, 0, 0, 0)
            };
            btnOpenAccounts.Click += (s, e) => OpenAccountsFile();

            flowActionsRight.Controls.Add(btnClearLogs);
            flowActionsRight.Controls.Add(btnOpenAccounts);

            panelActions.Controls.Add(flowActionsLeft);
            panelActions.Controls.Add(flowActionsRight);
            this.Controls.Add(panelActions);

            // ═══════════════════════════════════════════
            //  4. MAIN SPLIT CONTAINER (Grid & Log)
            // ═══════════════════════════════════════════
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 450,
                SplitterWidth = 6,
                BackColor = BgDark,
                Padding = new Padding(18, 10, 18, 14)
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

            var pnlGridHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                BackColor = CardDark,
                Padding = new Padding(12, 0, 12, 0)
            };

            var lblGridTitle = new Label
            {
                Text = "บัญชีที่สมัครสำเร็จ (Accounts)",
                Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(10, 10),
                UseMnemonic = false
            };

            lblAccountsCount = new Label
            {
                Text = "0 บัญชี",
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                ForeColor = Success,
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(pnlGridHeader.Width - 80, 10),
                UseMnemonic = false
            };

            pnlGridHeader.Controls.Add(lblGridTitle);
            pnlGridHeader.Controls.Add(lblAccountsCount);

            dgvAccounts = new DataGridView
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
            dgvAccounts.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            dgvAccounts.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;

            dgvAccounts.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(16, 19, 28);
            dgvAccounts.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(120, 130, 155);
            dgvAccounts.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);
            dgvAccounts.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            dgvAccounts.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
            dgvAccounts.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(16, 19, 28);

            dgvAccounts.DefaultCellStyle.BackColor = PanelDark;
            dgvAccounts.DefaultCellStyle.ForeColor = TextMain;
            dgvAccounts.DefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 38, 58);
            dgvAccounts.DefaultCellStyle.SelectionForeColor = Color.White;
            dgvAccounts.DefaultCellStyle.Font = new Font("Consolas", 9.2f);
            dgvAccounts.DefaultCellStyle.Padding = new Padding(8, 0, 0, 0);

            // Alternating Row Color
            dgvAccounts.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(14, 16, 25);
            dgvAccounts.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 38, 58);

            var colUsername = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Username",
                HeaderText = "USERNAME (ไอดี)",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 140
            };
            var colTime = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "RegisteredAt",
                HeaderText = "เวลา",
                Width = 85,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(140, 165, 200) }
            };
            var colStatus = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Status",
                HeaderText = "สถานะ",
                Width = 130,
                DefaultCellStyle = { ForeColor = Success, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold) }
            };

            dgvAccounts.Columns.AddRange(colUsername, colTime, colStatus);
            dgvAccounts.DataSource = _accountsList;

            panelGridContainer.Controls.Add(dgvAccounts);
            panelGridContainer.Controls.Add(pnlGridHeader);
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
                Height = 36,
                BackColor = CardDark,
                Padding = new Padding(12, 0, 12, 0)
            };

            var lblLogTitle = new Label
            {
                Text = "Live Console Logs & Network Events",
                Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(10, 9),
                UseMnemonic = false
            };

            var lblLogLimitHint = new Label
            {
                Text = $"(จำกัด: {MaxLogLines} บรรทัด)",
                Font = new Font("Segoe UI", 8.2f),
                ForeColor = TextMuted,
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(pnlLogHeader.Width - 125, 10),
                UseMnemonic = false
            };

            pnlLogHeader.Controls.Add(lblLogTitle);
            pnlLogHeader.Controls.Add(lblLogLimitHint);

            rtbLogs = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(9, 11, 17),
                ForeColor = Color.FromArgb(215, 222, 240),
                Font = new Font("Consolas", 9.2f),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                HideSelection = false,
                Margin = new Padding(0)
            };

            panelLogContainer.Controls.Add(rtbLogs);
            panelLogContainer.Controls.Add(pnlLogHeader);
            splitMain.Panel2.Controls.Add(panelLogContainer);

            this.Controls.Add(splitMain);

            // Dock stacking order
            panelTop.SendToBack();
            statsDashboard.BringToFront();
            panelActions.BringToFront();
            splitMain.BringToFront();
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

            if (File.Exists(accountsPath))
            {
                try
                {
                    var lines = File.ReadAllLines(accountsPath);
                    _totalRegisteredCount = 0;
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            _totalRegisteredCount++;
                        }
                    }

                    statsDashboard.TotalAccounts = $"{_totalRegisteredCount} บัญชี";
                    lblAccountsCount.Text = $"{_totalRegisteredCount} บัญชี";
                    AppendLog($"[System] โหลดข้อมูล accounts.txt สำเร็จ พบไอดีเดิมทั้งหมด {_totalRegisteredCount} บัญชี", Color.FromArgb(140, 200, 255));
                }
                catch { }
            }
        }

        private async Task StartBotAsync()
        {
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            btnThreadMinus.Enabled = false;
            btnThreadPlus.Enabled = false;
            btnToggleCaptcha.Enabled = false;

            _uptimeTimer.Restart();

            int threads = _config.BotThreads;
            bool autoCaptcha = _config.AutoStartCaptcha;

            bool started = await _botManager.StartAsync(threads, autoCaptcha);
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
            statsDashboard.VpnStatus = "หยุดทำงาน";
        }

        private void BotManager_OnLog(string message, Color color)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnLog(message, color)));
                return;
            }

            AppendLog(message, color);
        }

        // ═══════════════════════════════════════════
        //  LOG BUFFER LIMIT (Prevents Memory & UI Lag)
        // ═══════════════════════════════════════════
        private void AppendLog(string message, Color color)
        {
            if (rtbLogs.IsDisposed) return;

            string time = DateTime.Now.ToString("HH:mm:ss");

            // Check and prune log buffer if exceeding limit
            if (rtbLogs.Lines.Length > MaxLogLines)
            {
                try
                {
                    int charIndex = rtbLogs.GetFirstCharIndexFromLine(TrimBatchLines);
                    if (charIndex > 0)
                    {
                        rtbLogs.Select(0, charIndex);
                        rtbLogs.SelectedText = "";
                    }
                }
                catch { }
            }

            rtbLogs.SelectionStart = rtbLogs.TextLength;
            rtbLogs.SelectionLength = 0;

            rtbLogs.SelectionColor = Color.FromArgb(90, 100, 125);
            rtbLogs.AppendText($"[{time}] ");

            rtbLogs.SelectionColor = color;
            rtbLogs.AppendText(message + Environment.NewLine);
            rtbLogs.ScrollToCaret();
        }

        private void BotManager_OnAccountRegistered(RegisteredAccount acc)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnAccountRegistered(acc)));
                return;
            }

            _accountsList.Insert(0, acc);
            _totalRegisteredCount++;
            statsDashboard.TotalAccounts = $"{_totalRegisteredCount} บัญชี";
            lblAccountsCount.Text = $"{_totalRegisteredCount} บัญชี";

            // Calculate Speed
            double hours = _uptimeTimer.Elapsed.TotalHours;
            if (hours > 0.005)
            {
                double speed = _accountsList.Count / hours;
                statsDashboard.Speed = $"{speed:F1} ไอดี/ชม.";
            }
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
        }

        private void BotManager_OnVpnChanged(string ip)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnVpnChanged(ip)));
                return;
            }

            statsDashboard.VpnStatus = $"IP: {ip}";
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            // 1. Update Real-time License Expiry Countdown
            UpdateLicenseBadgeDisplay();

            // 2. Update Bot Uptime
            if (_uptimeTimer.IsRunning)
            {
                var ts = _uptimeTimer.Elapsed;
                statsDashboard.Uptime = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RegisterHotKey(this.Handle, HOTKEY_ID_F12, 0, VK_F12);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnregisterHotKey(this.Handle, HOTKEY_ID_F12);
            StopBot();
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
        private string _speed = "0.0 ไอดี/ชม.";
        private string _vpnStatus = "พร้อมทำงาน";
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

        public string VpnStatus
        {
            get => _vpnStatus;
            set { _vpnStatus = value; Invalidate(); }
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

            int paddingX = 18;
            int paddingY = 8;
            int gap = 10;
            int availableWidth = Width - (paddingX * 2) - (gap * 3);
            int cardWidth = availableWidth / 4;
            int cardHeight = Height - (paddingY * 2);

            var titles = new[] { "ไอดีที่สมัครสำเร็จ", "ความเร็วเฉลี่ย", "เครือข่าย VPN / IP", "เวลาทำงาน (Uptime)" };
            var values = new[] { _totalAccounts, _speed, _vpnStatus, _uptime };
            var accents = new[] {
                Color.FromArgb(52, 211, 153),  // Emerald
                Color.FromArgb(167, 139, 250), // Purple
                Color.FromArgb(56, 189, 248),  // Sky Blue
                Color.FromArgb(250, 204, 21)   // Yellow
            };

            using var titleFont = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold);
            using var valFont = new Font("Segoe UI", 13.5f, FontStyle.Bold);
            using var cardBg = new SolidBrush(Color.FromArgb(24, 28, 42));
            using var cardBorder = new Pen(Color.FromArgb(38, 44, 66), 1f);
            using var titleBrush = new SolidBrush(Color.FromArgb(140, 148, 175));
            using var valBrush = new SolidBrush(Color.FromArgb(245, 247, 255));

            for (int i = 0; i < 4; i++)
            {
                int x = paddingX + (i * (cardWidth + gap));
                var cardRect = new Rectangle(x, paddingY, cardWidth, cardHeight);

                using var path = GetRoundedPath(cardRect, 8);
                g.FillPath(cardBg, path);
                g.DrawPath(cardBorder, path);

                // Top colored accent bar
                using var accentPen = new Pen(accents[i], 2.5f);
                g.DrawLine(accentPen, x + 10, paddingY + 1, x + cardWidth - 10, paddingY + 1);

                // Title
                g.DrawString(titles[i], titleFont, titleBrush, x + 14, paddingY + 12);

                // Value (large bold)
                g.DrawString(values[i], valFont, valBrush, x + 14, paddingY + 34);
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
