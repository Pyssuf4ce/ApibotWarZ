using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        // ---------- Palette ----------
        private static readonly Color BgDark = Color.FromArgb(16, 18, 27);
        private static readonly Color PanelDark = Color.FromArgb(24, 27, 40);
        private static readonly Color CardDark = Color.FromArgb(32, 36, 52);
        private static readonly Color CardBorder = Color.FromArgb(44, 49, 72);
        private static readonly Color Accent = Color.FromArgb(108, 99, 255);
        private static readonly Color AccentHover = Color.FromArgb(130, 121, 255);
        private static readonly Color Success = Color.FromArgb(60, 215, 125);
        private static readonly Color SuccessHover = Color.FromArgb(80, 235, 145);
        private static readonly Color Danger = Color.FromArgb(239, 83, 80);
        private static readonly Color DangerHover = Color.FromArgb(255, 105, 100);
        private static readonly Color TextMain = Color.FromArgb(240, 240, 250);
        private static readonly Color TextDim = Color.FromArgb(145, 150, 175);
        private static readonly Color GridLine = Color.FromArgb(40, 44, 65);

        private readonly BotProcessManager _botManager;
        private readonly AppConfig _config;
        private readonly BindingList<RegisteredAccount> _accountsList = new();
        private readonly Stopwatch _uptimeTimer = new();
        private System.Windows.Forms.Timer _uiTimer = null!;
        private int _totalRegisteredCount = 0;

        // UI Controls
        private Panel panelTop = null!;
        private Label lblTitle = null!;
        private Label lblSubtitle = null!;
        private Label lblLicenseBadge = null!;
        private Label lblHwidBadge = null!;

        // Stats Controls
        private Panel panelStats = null!;
        private Label lblStatTotal = null!;
        private Label lblStatSpeed = null!;
        private Label lblStatVpn = null!;
        private Label lblStatUptime = null!;

        // Action Controls
        private Panel panelActions = null!;
        private RoundedButton btnStart = null!;
        private RoundedButton btnStop = null!;
        private NumericUpDown numThreads = null!;
        private CheckBox chkAutoCaptcha = null!;
        private RoundedButton btnOpenAccounts = null!;
        private RoundedButton btnClearLogs = null!;

        // Grid & Logs
        private DataGridView dgvAccounts = null!;
        private RichTextBox rtbLogs = null!;

        public MainForm(string expiryText = "")
        {
            _botManager = new BotProcessManager();
            _config = ConfigManager.Load();

            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer, true);
            this.DoubleBuffered = true;

            SetupUI(expiryText);
            LoadExistingAccounts();

            _botManager.OnLog += BotManager_OnLog;
            _botManager.OnAccountRegistered += BotManager_OnAccountRegistered;
            _botManager.OnStateChanged += BotManager_OnStateChanged;
            _botManager.OnVpnChanged += BotManager_OnVpnChanged;

            _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick += UiTimer_Tick;
        }

        private void SetupUI(string expiryText)
        {
            this.Text = "⚡ Apibot WarZ — Auto Register & VPN Coordinator";
            this.Size = new Size(1060, 680);
            this.MinimumSize = new Size(920, 580);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9.5f);
            this.ForeColor = TextMain;
            this.ShowIcon = true;

            // ═══════════════════════════════════════════
            //  1. TOP HEADER PANEL
            // ═══════════════════════════════════════════
            panelTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70,
                BackColor = PanelDark,
                Padding = new Padding(20, 10, 20, 10)
            };

            lblTitle = new Label
            {
                Text = "⚡ Apibot WarZ",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(18, 12)
            };

            lblSubtitle = new Label
            {
                Text = "High-Performance Auto Register & Cloudflare Bypass Engine",
                Font = new Font("Segoe UI", 9f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(20, 42)
            };

            string expiryDisplay = string.IsNullOrWhiteSpace(expiryText) ? "Active" : expiryText;
            lblLicenseBadge = new Label
            {
                Text = $"🟢 สิทธิ์การใช้งาน: {expiryDisplay}",
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = Success,
                BackColor = CardDark,
                AutoSize = true,
                Padding = new Padding(8, 5, 8, 5),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            lblLicenseBadge.Location = new Point(panelTop.Width - 360, 20);

            string hwid = CloudLicenseService.GetHWID();
            string shortHwid = hwid.Length > 12 ? hwid.Substring(0, 12) + "..." : hwid;
            lblHwidBadge = new Label
            {
                Text = $"🛡️ HWID: {shortHwid}",
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = Accent,
                BackColor = CardDark,
                AutoSize = true,
                Padding = new Padding(8, 5, 8, 5),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            lblHwidBadge.Location = new Point(panelTop.Width - 170, 20);
            lblHwidBadge.Click += (s, e) =>
            {
                Clipboard.SetText(hwid);
                MessageBox.Show(this, $"HWID ของเครื่องนี้:\n\n{hwid}\n\n(คัดลอกลงคลิปบอร์ดแล้ว)", "HWID Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            panelTop.Controls.Add(lblTitle);
            panelTop.Controls.Add(lblSubtitle);
            panelTop.Controls.Add(lblLicenseBadge);
            panelTop.Controls.Add(lblHwidBadge);
            this.Controls.Add(panelTop);

            // ═══════════════════════════════════════════
            //  2. STATS CARDS PANEL
            // ═══════════════════════════════════════════
            panelStats = new Panel
            {
                Dock = DockStyle.Top,
                Height = 74,
                BackColor = BgDark,
                Padding = new Padding(16, 8, 16, 8)
            };

            var tableStats = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = Color.Transparent
            };
            tableStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            tableStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            tableStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            tableStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));

            var card1 = CreateStatCard("🌟 บัญชีที่สมัครได้รวม", "0 บัญชี", out lblStatTotal);
            var card2 = CreateStatCard("⚡ ความเร็วเฉลี่ย", "0 ไอดี/ชม.", out lblStatSpeed);
            var card3 = CreateStatCard("🌐 เครือข่าย VPN / IP", "ยังไม่ได้เริ่ม", out lblStatVpn);
            var card4 = CreateStatCard("⏱️ เวลาทำงาน (Uptime)", "00:00:00", out lblStatUptime);

            tableStats.Controls.Add(card1, 0, 0);
            tableStats.Controls.Add(card2, 1, 0);
            tableStats.Controls.Add(card3, 2, 0);
            tableStats.Controls.Add(card4, 3, 0);

            panelStats.Controls.Add(tableStats);
            this.Controls.Add(panelStats);

            // ═══════════════════════════════════════════
            //  3. ACTION TOOLBAR
            // ═══════════════════════════════════════════
            panelActions = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                BackColor = PanelDark,
                Padding = new Padding(18, 10, 18, 10)
            };

            btnStart = new RoundedButton
            {
                Text = "🚀  เริ่มทำงาน (Start Bot)",
                Size = new Size(180, 40),
                Location = new Point(18, 10),
                BaseColor = Success,
                HoverColor = SuccessHover,
                ForeColorNormal = Color.FromArgb(10, 30, 15),
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold)
            };
            btnStart.Click += async (s, e) => await StartBotAsync();

            btnStop = new RoundedButton
            {
                Text = "🛑  หยุดทำงาน (Stop)",
                Size = new Size(150, 40),
                Location = new Point(206, 10),
                BaseColor = Danger,
                HoverColor = DangerHover,
                ForeColorNormal = Color.White,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Enabled = false
            };
            btnStop.Click += (s, e) => StopBot();

            var lblThreads = new Label
            {
                Text = "จำนวนบอท (Threads):",
                Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(375, 19)
            };

            numThreads = new NumericUpDown
            {
                Location = new Point(515, 17),
                Size = new Size(55, 26),
                Minimum = 1,
                Maximum = 20,
                Value = Math.Max(1, Math.Min(20, _config.BotThreads)),
                BackColor = CardDark,
                ForeColor = TextMain,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center
            };
            numThreads.ValueChanged += (s, e) =>
            {
                _config.BotThreads = (int)numThreads.Value;
                ConfigManager.Save(_config);
            };

            chkAutoCaptcha = new CheckBox
            {
                Text = "เริ่ม Captcha Worker อัตโนมัติ",
                Checked = _config.AutoStartCaptcha,
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(585, 18),
                Font = new Font("Segoe UI", 9.5f)
            };
            chkAutoCaptcha.CheckedChanged += (s, e) =>
            {
                _config.AutoStartCaptcha = chkAutoCaptcha.Checked;
                ConfigManager.Save(_config);
            };

            btnOpenAccounts = new RoundedButton
            {
                Text = "📁  เปิด accounts.txt",
                Size = new Size(140, 38),
                Location = new Point(panelActions.Width - 250, 11),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BaseColor = CardDark,
                HoverColor = CardBorder,
                ForeColorNormal = TextMain,
                Font = new Font("Segoe UI Semibold", 9f)
            };
            btnOpenAccounts.Click += (s, e) => OpenAccountsFile();

            btnClearLogs = new RoundedButton
            {
                Text = "🧹  ล้าง Log",
                Size = new Size(90, 38),
                Location = new Point(panelActions.Width - 100, 11),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BaseColor = CardDark,
                HoverColor = CardBorder,
                ForeColorNormal = TextDim,
                Font = new Font("Segoe UI Semibold", 9f)
            };
            btnClearLogs.Click += (s, e) => rtbLogs.Clear();

            panelActions.Controls.Add(btnStart);
            panelActions.Controls.Add(btnStop);
            panelActions.Controls.Add(lblThreads);
            panelActions.Controls.Add(numThreads);
            panelActions.Controls.Add(chkAutoCaptcha);
            panelActions.Controls.Add(btnOpenAccounts);
            panelActions.Controls.Add(btnClearLogs);
            this.Controls.Add(panelActions);

            // ═══════════════════════════════════════════
            //  4. MAIN SPLIT CONTAINER (Grid & Console)
            // ═══════════════════════════════════════════
            var splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 420,
                SplitterWidth = 6,
                BackColor = BgDark,
                Padding = new Padding(16, 12, 16, 16)
            };

            // Left Side: Accounts Grid
            var panelGridContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PanelDark,
                Padding = new Padding(1)
            };
            var lblGridHeader = new Label
            {
                Text = "📋 รายชื่อไอดีที่สมัครสำเร็จ (Registered Accounts)",
                Dock = DockStyle.Top,
                Height = 32,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = TextMain,
                BackColor = CardDark,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0)
            };

            dgvAccounts = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = PanelDark,
                BorderStyle = BorderStyle.None,
                GridColor = GridLine,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoGenerateColumns = false,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 32,
                RowTemplate = { Height = 28 }
            };

            dgvAccounts.ColumnHeadersDefaultCellStyle.BackColor = CardDark;
            dgvAccounts.ColumnHeadersDefaultCellStyle.ForeColor = TextDim;
            dgvAccounts.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f);
            dgvAccounts.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            dgvAccounts.DefaultCellStyle.BackColor = PanelDark;
            dgvAccounts.DefaultCellStyle.ForeColor = TextMain;
            dgvAccounts.DefaultCellStyle.SelectionBackColor = CardBorder;
            dgvAccounts.DefaultCellStyle.SelectionForeColor = Color.White;
            dgvAccounts.DefaultCellStyle.Font = new Font("Consolas", 9.5f);

            var colUsername = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Username",
                HeaderText = "Username (ไอดี)",
                Width = 170,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            };
            var colTime = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "RegisteredAt",
                HeaderText = "เวลา",
                Width = 90
            };
            var colStatus = new DataGridViewTextBoxColumn
            {
                DataPropertyName = "Status",
                HeaderText = "สถานะ",
                Width = 140
            };

            dgvAccounts.Columns.AddRange(colUsername, colTime, colStatus);
            dgvAccounts.DataSource = _accountsList;

            panelGridContainer.Controls.Add(dgvAccounts);
            panelGridContainer.Controls.Add(lblGridHeader);
            splitMain.Panel1.Controls.Add(panelGridContainer);

            // Right Side: Live Logs
            var panelLogContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PanelDark,
                Padding = new Padding(1)
            };
            var lblLogHeader = new Label
            {
                Text = "🖥️ Live Console Logs & Network Events",
                Dock = DockStyle.Top,
                Height = 32,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = TextMain,
                BackColor = CardDark,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0)
            };

            rtbLogs = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(12, 13, 19),
                ForeColor = Color.FromArgb(210, 215, 230),
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                HideSelection = false
            };

            panelLogContainer.Controls.Add(rtbLogs);
            panelLogContainer.Controls.Add(lblLogHeader);
            splitMain.Panel2.Controls.Add(panelLogContainer);

            this.Controls.Add(splitMain);

            // Make sure panel order is correct for docking
            panelTop.SendToBack();
            panelStats.BringToFront();
            panelActions.BringToFront();
            splitMain.BringToFront();
        }

        private Panel CreateStatCard(string title, string initialVal, out Label valLabel)
        {
            var p = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = CardDark,
                Padding = new Padding(12, 8, 12, 8),
                Margin = new Padding(4)
            };
            p.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1.2f);
                e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            };

            var lblT = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(10, 8)
            };

            valLabel = new Label
            {
                Text = initialVal,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(10, 28)
            };

            p.Controls.Add(lblT);
            p.Controls.Add(valLabel);
            return p;
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

                    lblStatTotal.Text = $"{_totalRegisteredCount} บัญชี";
                    AppendLog($"[System] 📂 โหลดข้อมูล accounts.txt สำเร็จ พบไอดีเดิมทั้งหมด {_totalRegisteredCount} บัญชี", Color.FromArgb(140, 200, 255));
                }
                catch { }
            }
        }

        private async Task StartBotAsync()
        {
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            numThreads.Enabled = false;
            chkAutoCaptcha.Enabled = false;

            _uptimeTimer.Restart();
            _uiTimer.Start();

            int threads = (int)numThreads.Value;
            bool autoCaptcha = chkAutoCaptcha.Checked;

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
            _uiTimer.Stop();

            btnStart.Enabled = true;
            btnStop.Enabled = false;
            numThreads.Enabled = true;
            chkAutoCaptcha.Enabled = true;
            lblStatVpn.Text = "หยุดทำงาน";
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

        private void AppendLog(string message, Color color)
        {
            if (rtbLogs.IsDisposed) return;

            string time = DateTime.Now.ToString("HH:mm:ss");
            rtbLogs.SelectionStart = rtbLogs.TextLength;
            rtbLogs.SelectionLength = 0;

            rtbLogs.SelectionColor = Color.FromArgb(100, 105, 130);
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
            lblStatTotal.Text = $"{_totalRegisteredCount} บัญชี";

            // Calculate Speed
            double hours = _uptimeTimer.Elapsed.TotalHours;
            if (hours > 0.005)
            {
                double speed = _accountsList.Count / hours;
                lblStatSpeed.Text = $"{speed:F1} ไอดี/ชม.";
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
            numThreads.Enabled = !isRunning;
            chkAutoCaptcha.Enabled = !isRunning;
        }

        private void BotManager_OnVpnChanged(string ip)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => BotManager_OnVpnChanged(ip)));
                return;
            }

            lblStatVpn.Text = $"IP: {ip}";
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (_uptimeTimer.IsRunning)
            {
                var ts = _uptimeTimer.Elapsed;
                lblStatUptime.Text = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
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
                AppendLog("[Emergency Hotkey] 🛑 กดปุ่ม [F12] สั่งหยุดการทำงานทันที!", Color.FromArgb(255, 80, 80));
                StopBot();
            }
            base.WndProc(ref m);
        }
    }
}
