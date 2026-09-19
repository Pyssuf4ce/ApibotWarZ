using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using ApibotWarZ.UI.Controls;
using ApibotWarZ.UI.Models;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Forms
{
    public class ChromeProfileDialog : Form
    {
        // ── Cyber Dark Theme Colors ──
        private static readonly Color BgDark = Color.FromArgb(11, 15, 25);         // #0B0F19
        private static readonly Color CardBg = Color.FromArgb(19, 25, 43);         // #13192B
        private static readonly Color CardBgAlt = Color.FromArgb(24, 31, 53);      // #181F35
        private static readonly Color ItemBg = Color.FromArgb(22, 31, 54);         // #161F36
        private static readonly Color ItemBorder = Color.FromArgb(37, 49, 78);     // #25314E
        private static readonly Color BorderColor = Color.FromArgb(30, 41, 59);    // #1E293B
        private static readonly Color TextMain = Color.FromArgb(248, 250, 252);    // #F8FAFC
        private static readonly Color TextMuted = Color.FromArgb(148, 163, 184);   // #94A3B8
        private static readonly Color TextDim = Color.FromArgb(100, 116, 139);     // #64748B
        private static readonly Color PrimaryAccent = Color.FromArgb(99, 102, 241);// #6366F1 (Indigo)
        private static readonly Color PrimaryHover = Color.FromArgb(129, 140, 248); // #818CF8
        private static readonly Color SuccessGreen = Color.FromArgb(16, 185, 129); // #10B981
        private static readonly Color WarningAmber = Color.FromArgb(245, 158, 11); // #F59E0B
        private static readonly Color DangerRed = Color.FromArgb(244, 63, 94);     // #F43F5E

        private readonly AppConfig _config;
        private NumericUpDown numProfileCount = null!;
        private RoundedButton btnCreate = null!;
        private Label lblStatus = null!;
        private Label lblActiveBadge = null!;
        private FlowLayoutPanel pnlProfilesList = null!;

        public ChromeProfileDialog(AppConfig config)
        {
            _config = config;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            SetupUI();
            RefreshProfilesList();
        }

        private void SetupUI()
        {
            this.Text = "🌐 จัดการ Google Chrome Profiles สำหรับบอท (Auto-Sync Extension)";
            this.Size = new Size(740, 640);
            this.MinimumSize = new Size(740, 640);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9.5f);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;

            var pnlMain = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24, 20, 24, 20),
                BackColor = BgDark
            };

            // ── 1. Top Header Banner ──
            var pnlHeader = new RoundedPanel
            {
                Location = new Point(20, 16),
                Size = new Size(684, 84),
                BackColor = CardBg,
                BorderColor = BorderColor,
                CornerRadius = 10,
                Padding = new Padding(16, 12, 16, 12)
            };

            var lblHeaderIcon = new Label
            {
                Text = "🌐",
                Font = new Font("Segoe UI Emoji", 20f),
                ForeColor = PrimaryAccent,
                Size = new Size(42, 42),
                Location = new Point(14, 18),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var lblTitle = new Label
            {
                Text = "Google Chrome Profile Manager",
                Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(62, 14)
            };

            var lblEngineBadge = new Label
            {
                Text = "WORKER SYNC ENGINE",
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = PrimaryHover,
                BackColor = Color.FromArgb(30, 36, 68),
                Size = new Size(130, 18),
                Location = new Point(310, 18),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var lblSubtitle = new Label
            {
                Text = "สร้าง & จำลอง Chrome แยกโฟลเดอร์ 100% พร้อมติดตั้ง Extension เชื่อมต่อ Token อัตโนมัติ",
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(64, 40)
            };

            lblActiveBadge = new Label
            {
                Text = "● 0 Profiles Active",
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = SuccessGreen,
                BackColor = Color.FromArgb(16, 35, 30),
                Size = new Size(125, 28),
                Location = new Point(544, 26),
                TextAlign = ContentAlignment.MiddleCenter
            };

            pnlHeader.Controls.Add(lblHeaderIcon);
            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblEngineBadge);
            pnlHeader.Controls.Add(lblSubtitle);
            pnlHeader.Controls.Add(lblActiveBadge);

            // ── 2. Create & Manage Card ──
            var pnlCreateCard = new RoundedPanel
            {
                Location = new Point(20, 110),
                Size = new Size(684, 94),
                BackColor = CardBg,
                BorderColor = BorderColor,
                CornerRadius = 10
            };

            var lblNumTitle = new Label
            {
                Text = "⚙️ กำหนดจำนวน Profile บอท (Worker Slots):",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(16, 14)
            };

            // Custom Stepper Container
            var pnlStepper = new RoundedPanel
            {
                Location = new Point(16, 42),
                Size = new Size(110, 36),
                BackColor = Color.FromArgb(11, 15, 25),
                BorderColor = ItemBorder,
                CornerRadius = 6
            };

            var btnMinus = new RoundedButton
            {
                Text = "-",
                Size = new Size(30, 30),
                Location = new Point(3, 3),
                BaseColor = ItemBg,
                HoverColor = Color.FromArgb(40, 50, 80),
                BorderColor = Color.Transparent,
                CornerRadius = 4,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColorNormal = TextMain
            };

            numProfileCount = new NumericUpDown
            {
                Location = new Point(35, 6),
                Size = new Size(40, 26),
                Minimum = 1,
                Maximum = 20,
                Value = Math.Max(1, Math.Min(20, _config.ChromeBotProfileCount > 0 ? _config.ChromeBotProfileCount : 5)),
                BackColor = Color.FromArgb(11, 15, 25),
                ForeColor = PrimaryHover,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center
            };

            var btnPlus = new RoundedButton
            {
                Text = "+",
                Size = new Size(30, 30),
                Location = new Point(77, 3),
                BaseColor = ItemBg,
                HoverColor = Color.FromArgb(40, 50, 80),
                BorderColor = Color.Transparent,
                CornerRadius = 4,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColorNormal = TextMain
            };

            btnMinus.Click += (s, e) => { if (numProfileCount.Value > numProfileCount.Minimum) numProfileCount.Value--; };
            btnPlus.Click += (s, e) => { if (numProfileCount.Value < numProfileCount.Maximum) numProfileCount.Value++; };

            pnlStepper.Controls.Add(btnMinus);
            pnlStepper.Controls.Add(numProfileCount);
            pnlStepper.Controls.Add(btnPlus);

            btnCreate = new RoundedButton
            {
                Text = "➕ สร้าง / อัปเดต Profiles",
                Size = new Size(200, 36),
                Location = new Point(136, 42),
                BaseColor = PrimaryAccent,
                HoverColor = PrimaryHover,
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColorNormal = Color.White
            };
            btnCreate.Click += BtnCreate_Click;

            var btnDeleteAll = new RoundedButton
            {
                Text = "🗑️ ล้างบอททั้งหมด",
                Size = new Size(150, 36),
                Location = new Point(344, 42),
                BaseColor = Color.FromArgb(45, 20, 28),
                HoverColor = Color.FromArgb(65, 25, 38),
                BorderColor = DangerRed,
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColorNormal = Color.FromArgb(255, 140, 160)
            };
            btnDeleteAll.Click += (s, e) =>
            {
                var dr = MessageBox.Show(
                    "คุณต้องการลบ Profile Chrome ของบอททั้งหมดใช่หรือไม่?\n\n(การกระทำนี้จะไม่ส่งผลต่อ Google Chrome หลักของคุณ)",
                    "ยืนยันการล้าง Profile บอท",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                );
                if (dr == DialogResult.Yes)
                {
                    ChromeProfileService.DeleteBotProfiles();
                    RefreshProfilesList();
                    lblStatus.Text = "🗑️ ล้าง Profiles ทั้งหมดแล้ว";
                    lblStatus.ForeColor = DangerRed;
                }
            };

            lblStatus = new Label
            {
                Text = "",
                Font = new Font("Segoe UI Semibold", 9f),
                ForeColor = SuccessGreen,
                AutoSize = true,
                Location = new Point(504, 51)
            };

            pnlCreateCard.Controls.Add(lblNumTitle);
            pnlCreateCard.Controls.Add(pnlStepper);
            pnlCreateCard.Controls.Add(btnCreate);
            pnlCreateCard.Controls.Add(btnDeleteAll);
            pnlCreateCard.Controls.Add(lblStatus);

            // ── 3. Profiles List Container ──
            var lblListTitle = new Label
            {
                Text = "📋 รายการ Profile บอทที่พร้อมใช้งาน:",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(22, 214)
            };

            var btnLaunchAll = new RoundedButton
            {
                Text = "🚀 เปิดทดสอบทุก Profile พร้อมกัน",
                Size = new Size(220, 28),
                Location = new Point(484, 210),
                BaseColor = CardBgAlt,
                HoverColor = Color.FromArgb(40, 50, 80),
                BorderColor = PrimaryAccent,
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 8.8f),
                ForeColorNormal = TextMain
            };
            btnLaunchAll.Click += (s, e) =>
            {
                ChromeProfileService.LaunchAllProfiles();
            };

            var pnlListBorder = new RoundedPanel
            {
                Location = new Point(20, 244),
                Size = new Size(684, 280),
                BackColor = CardBg,
                BorderColor = BorderColor,
                CornerRadius = 10,
                Padding = new Padding(6)
            };

            pnlProfilesList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBg,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(4)
            };
            pnlListBorder.Controls.Add(pnlProfilesList);

            // ── 4. Bottom Footer ──
            var lblFooterTip = new Label
            {
                Text = "💡 แนะนำ: บอทใช้ Extension ซิงค์ Token อัตโนมัติ ปลอดภัย ไม่ชนแท็บ Chrome ปกติ",
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(22, 542)
            };

            var btnOpenFolder = new RoundedButton
            {
                Text = "📂 เปิดโฟลเดอร์ Profiles",
                Size = new Size(160, 36),
                Location = new Point(400, 534),
                BaseColor = CardBg,
                HoverColor = CardBgAlt,
                BorderColor = BorderColor,
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 9f),
                ForeColorNormal = TextMain
            };
            btnOpenFolder.Click += (s, e) =>
            {
                try
                {
                    string dir = ChromeProfileService.GetBotProfilesBaseDir();
                    if (Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = dir, UseShellExecute = true });
                    }
                }
                catch { }
            };

            var btnClose = new RoundedButton
            {
                Text = "ปิดหน้าต่าง",
                Size = new Size(130, 36),
                Location = new Point(574, 534),
                BaseColor = PrimaryAccent,
                HoverColor = PrimaryHover,
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColorNormal = Color.White
            };
            btnClose.Click += (s, e) => this.Close();

            pnlMain.Controls.Add(pnlHeader);
            pnlMain.Controls.Add(pnlCreateCard);
            pnlMain.Controls.Add(lblListTitle);
            pnlMain.Controls.Add(btnLaunchAll);
            pnlMain.Controls.Add(pnlListBorder);
            pnlMain.Controls.Add(lblFooterTip);
            pnlMain.Controls.Add(btnOpenFolder);
            pnlMain.Controls.Add(btnClose);

            this.Controls.Add(pnlMain);
        }

        private void BtnCreate_Click(object? sender, EventArgs e)
        {
            int count = (int)numProfileCount.Value;
            lblStatus.Text = "⏳ กำลังสร้าง Profiles...";
            lblStatus.ForeColor = WarningAmber;
            Application.DoEvents();

            bool ok = ChromeProfileService.CreateBotProfiles(count);
            if (ok)
            {
                _config.ChromeBotProfileCount = count;
                ConfigManager.Save(_config);

                lblStatus.Text = $"✅ สร้างสำเร็จ ({count} Profiles)";
                lblStatus.ForeColor = SuccessGreen;

                RefreshProfilesList();
                MessageBox.Show(
                    $"สร้าง Profile บอทจำนวน {count} ตัว พร้อม Extension เรียบร้อยแล้ว!\n\n" +
                    $"💡 คำแนะนำการใช้งาน:\n" +
                    $"- กดปุ่ม '🚀 เปิด' ที่แต่ละรายการเพื่อทดสอบหน้าต่างบอทเดี่ยว\n" +
                    $"- กดปุ่ม '🚀 เปิดทดสอบทุก Profile พร้อมกัน' เพื่อรันทุก Worker\n" +
                    $"- บอทในหน้าหลักจะดึง Profile เหล่านี้ไปรันอัตโนมัติ",
                    "สร้าง Profile สำเร็จ",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }

        private void RefreshProfilesList()
        {
            pnlProfilesList.SuspendLayout();
            pnlProfilesList.Controls.Clear();
            var profiles = ChromeProfileService.GetBotProfiles();

            lblActiveBadge.Text = $"● {profiles.Count} Profiles";
            lblActiveBadge.ForeColor = profiles.Count > 0 ? SuccessGreen : TextDim;

            if (profiles.Count == 0)
            {
                var pnlEmpty = new Panel
                {
                    Size = new Size(656, 240),
                    BackColor = Color.Transparent
                };

                var lblEmptyIcon = new Label
                {
                    Text = "📂",
                    Font = new Font("Segoe UI Emoji", 28f),
                    ForeColor = TextDim,
                    Size = new Size(656, 50),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(0, 60)
                };

                var lblEmpty = new Label
                {
                    Text = "ยังไม่มี Profile บอทในระบบ\nระบุจำนวน Worker ที่ต้องการแล้วกดปุ่ม '➕ สร้าง / อัปเดต Profiles' ด้านบน",
                    Font = new Font("Segoe UI", 10f),
                    ForeColor = TextMuted,
                    Size = new Size(656, 50),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(0, 115)
                };

                pnlEmpty.Controls.Add(lblEmptyIcon);
                pnlEmpty.Controls.Add(lblEmpty);
                pnlProfilesList.Controls.Add(pnlEmpty);
                pnlProfilesList.ResumeLayout();
                return;
            }

            foreach (var p in profiles)
            {
                var row = new RoundedPanel
                {
                    Size = new Size(656, 52),
                    BackColor = ItemBg,
                    BorderColor = ItemBorder,
                    CornerRadius = 8,
                    Margin = new Padding(0, 3, 0, 3)
                };

                var lblBadge = new Label
                {
                    Text = $"BOT #{p.WorkerId}",
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    ForeColor = WarningAmber,
                    BackColor = Color.FromArgb(40, 30, 15),
                    Size = new Size(70, 26),
                    Location = new Point(12, 13),
                    TextAlign = ContentAlignment.MiddleCenter
                };

                var lblName = new Label
                {
                    Text = p.DisplayName,
                    Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                    ForeColor = TextMain,
                    AutoSize = true,
                    Location = new Point(92, 10)
                };

                var lblDir = new Label
                {
                    Text = $"● พร้อมทำงาน (Extension: {ChromeProfileService.ExtensionId[..8]}...)",
                    Font = new Font("Segoe UI", 8.2f),
                    ForeColor = SuccessGreen,
                    AutoSize = true,
                    Location = new Point(93, 29)
                };

                var btnLaunch = new RoundedButton
                {
                    Text = "🚀 เปิดทดสอบ",
                    Size = new Size(95, 30),
                    Location = new Point(466, 11),
                    BaseColor = PrimaryAccent,
                    HoverColor = PrimaryHover,
                    CornerRadius = 5,
                    Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                    ForeColorNormal = Color.White
                };
                int wid = p.WorkerId;
                btnLaunch.Click += (s, e) =>
                {
                    ChromeProfileService.LaunchProfile(wid);
                };

                var btnDelRow = new RoundedButton
                {
                    Text = "🗑️ ลบ",
                    Size = new Size(68, 30),
                    Location = new Point(570, 11),
                    BaseColor = Color.FromArgb(45, 20, 28),
                    HoverColor = Color.FromArgb(65, 25, 38),
                    BorderColor = DangerRed,
                    CornerRadius = 5,
                    Font = new Font("Segoe UI Semibold", 8.5f),
                    ForeColorNormal = Color.FromArgb(255, 140, 160)
                };
                btnDelRow.Click += (s, e) =>
                {
                    ChromeProfileService.DeleteSingleBotProfile(wid);
                    RefreshProfilesList();
                };

                row.Controls.Add(lblBadge);
                row.Controls.Add(lblName);
                row.Controls.Add(lblDir);
                row.Controls.Add(btnLaunch);
                row.Controls.Add(btnDelRow);

                pnlProfilesList.Controls.Add(row);
            }

            pnlProfilesList.ResumeLayout();
        }

        // ── Helper Rounded Panel Control ──
        private class RoundedPanel : Panel
        {
            public Color BorderColor { get; set; } = Color.FromArgb(30, 41, 59);
            public int CornerRadius { get; set; } = 8;

            public RoundedPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                if (rect.Width <= 2 || rect.Height <= 2) return;

                using var path = CreateRoundedRect(rect, CornerRadius);
                using var brush = new SolidBrush(BackColor);
                g.FillPath(brush, path);

                using var pen = new Pen(BorderColor, 1f);
                g.DrawPath(pen, path);
            }

            private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
            {
                radius = Math.Max(2, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
                int d = radius * 2;
                var path = new GraphicsPath();
                path.StartFigure();
                path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
                path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
                path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
                path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
