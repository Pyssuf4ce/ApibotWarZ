using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ApibotWarZ.UI.Controls;
using ApibotWarZ.UI.Models;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Forms
{
    public class ChromeProfileDialog : Form
    {
        private static readonly Color BgDark = Color.FromArgb(13, 15, 26);
        private static readonly Color PanelDark = Color.FromArgb(22, 24, 42);
        private static readonly Color CardDark = Color.FromArgb(26, 29, 50);
        private static readonly Color CardBorder = Color.FromArgb(45, 52, 78);
        private static readonly Color TextMain = Color.FromArgb(240, 240, 250);
        private static readonly Color TextDim = Color.FromArgb(135, 140, 165);
        private static readonly Color Accent = Color.FromArgb(99, 102, 241);
        private static readonly Color AccentHover = Color.FromArgb(129, 140, 248);
        private static readonly Color Success = Color.FromArgb(16, 185, 129);
        private static readonly Color BotBadgeColor = Color.FromArgb(245, 158, 11);

        private readonly AppConfig _config;
        private NumericUpDown numProfileCount = null!;
        private RoundedButton btnCreate = null!;
        private Label lblStatus = null!;
        private Panel pnlProfilesList = null!;

        public ChromeProfileDialog(AppConfig config)
        {
            _config = config;
            SetupUI();
            RefreshProfilesList();
        }

        private void SetupUI()
        {
            this.Text = "🌐 จัดการ Google Chrome Profiles สำหรับบอท (Auto-Sync Extension)";
            this.Size = new Size(680, 560);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9.5f);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var pnlMain = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24, 20, 24, 20),
                BackColor = BgDark
            };

            var lblTitle = new Label
            {
                Text = "🌐 ระบบสร้าง Profile Chrome พร้อมติดตั้ง Extension",
                Font = new Font("Segoe UI Semibold", 13.5f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(20, 16)
            };

            var lblDesc = new Label
            {
                Text = "ระบบจะสร้าง Profile บอทอิสระ (ไม่กระทบกับ Chrome ส่วนตัวของคุณ ไม่ค้าง ไม่ชนกับแท็บเดิม)\nพร้อมติดตั้ง Extension สำหรับ Sync เมื่อรันบอท เพื่อให้ Cloudflare Turnstile ตรวจเป็นคนแท้ 100%",
                Font = new Font("Segoe UI", 9f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(22, 48)
            };

            // ─── Card 1: Create Profiles ───
            var pnlCreateCard = new Panel
            {
                Location = new Point(20, 96),
                Size = new Size(624, 90),
                BackColor = CardDark
            };

            var lblNum = new Label
            {
                Text = "จำนวน Profile บอทที่ต้องการสร้าง:",
                Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(14, 18)
            };

            numProfileCount = new NumericUpDown
            {
                Location = new Point(16, 45),
                Size = new Size(80, 28),
                Minimum = 1,
                Maximum = 20,
                Value = Math.Max(1, Math.Min(20, _config.ChromeBotProfileCount > 0 ? _config.ChromeBotProfileCount : 5)),
                BackColor = PanelDark,
                ForeColor = TextMain,
                Font = new Font("Segoe UI Semibold", 10f)
            };

            btnCreate = new RoundedButton
            {
                Text = "➕ สร้าง Profile บอท",
                Size = new Size(230, 36),
                Location = new Point(106, 41),
                BaseColor = Accent,
                HoverColor = AccentHover,
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColorNormal = Color.White
            };
            btnCreate.Click += BtnCreate_Click;

            var btnDelete = new RoundedButton
            {
                Text = "🗑️ ล้าง Profile บอททั้งหมด",
                Size = new Size(210, 36),
                Location = new Point(346, 41),
                BaseColor = Color.FromArgb(50, 20, 30),
                HoverColor = Color.FromArgb(75, 25, 40),
                BorderColor = Color.FromArgb(244, 63, 94),
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColorNormal = Color.FromArgb(255, 130, 150)
            };
            btnDelete.Click += (s, e) =>
            {
                var dr = MessageBox.Show(
                    "คุณต้องการลบ Profile บอททั้งหมดใช่หรือไม่?",
                    "ยืนยันการล้าง Profile",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );
                if (dr == DialogResult.Yes)
                {
                    ChromeProfileService.DeleteBotProfiles();
                    RefreshProfilesList();
                    MessageBox.Show("ล้าง Profile บอททั้งหมดเรียบร้อยแล้ว!", "สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            lblStatus = new Label
            {
                Text = "",
                Font = new Font("Segoe UI Semibold", 9f),
                ForeColor = Success,
                AutoSize = true,
                Location = new Point(562, 48)
            };

            pnlCreateCard.Controls.Add(lblNum);
            pnlCreateCard.Controls.Add(numProfileCount);
            pnlCreateCard.Controls.Add(btnCreate);
            pnlCreateCard.Controls.Add(btnDelete);
            pnlCreateCard.Controls.Add(lblStatus);

            // ─── Card 2: Profiles List ───
            var lblListTitle = new Label
            {
                Text = "📋 รายการ Profile บอท (กดเปิดทดสอบได้ทันที):",
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(20, 198)
            };

            var btnLaunchAll = new RoundedButton
            {
                Text = "🚀 เปิดทดสอบทุก Profile",
                Size = new Size(170, 26),
                Location = new Point(474, 195),
                BaseColor = Color.FromArgb(40, 45, 75),
                HoverColor = AccentHover,
                BorderColor = Accent,
                CornerRadius = 5,
                Font = new Font("Segoe UI Semibold", 8.5f),
                ForeColorNormal = TextMain
            };
            btnLaunchAll.Click += (s, e) =>
            {
                ChromeProfileService.LaunchAllProfiles();
            };

            pnlProfilesList = new Panel
            {
                Location = new Point(20, 226),
                Size = new Size(624, 240),
                BackColor = CardDark,
                AutoScroll = true
            };

            // ─── Bottom Actions ───
            var btnOpenFolder = new RoundedButton
            {
                Text = "📂 เปิดโฟลเดอร์ Profiles",
                Size = new Size(160, 38),
                Location = new Point(356, 476),
                BaseColor = PanelDark,
                HoverColor = Color.FromArgb(34, 40, 60),
                BorderColor = CardBorder,
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 9.5f),
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
                Size = new Size(120, 38),
                Location = new Point(524, 476),
                BaseColor = PanelDark,
                HoverColor = Color.FromArgb(34, 40, 60),
                BorderColor = CardBorder,
                CornerRadius = 6,
                Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColorNormal = TextMain
            };
            btnClose.Click += (s, e) => this.Close();

            var lblTip = new Label
            {
                Text = "💡 หมายเหตุ: บอทจะเปิดหน้าต่าง Chrome โดยแยกโฟลเดอร์เป็นอิสระจาก Chrome หลักของคุณ 100%",
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(20, 484)
            };

            pnlMain.Controls.Add(lblTitle);
            pnlMain.Controls.Add(lblDesc);
            pnlMain.Controls.Add(pnlCreateCard);
            pnlMain.Controls.Add(lblListTitle);
            pnlMain.Controls.Add(btnLaunchAll);
            pnlMain.Controls.Add(pnlProfilesList);
            pnlMain.Controls.Add(lblTip);
            pnlMain.Controls.Add(btnOpenFolder);
            pnlMain.Controls.Add(btnClose);

            this.Controls.Add(pnlMain);
        }

        private void BtnCreate_Click(object? sender, EventArgs e)
        {
            int count = (int)numProfileCount.Value;
            lblStatus.Text = "⏳ กำลังสร้าง...";
            lblStatus.ForeColor = Color.FromArgb(245, 158, 11);
            Application.DoEvents();

            bool ok = ChromeProfileService.CreateBotProfiles(count);
            if (ok)
            {
                _config.ChromeBotProfileCount = count;
                ConfigManager.Save(_config);

                lblStatus.Text = $"✅ สำเร็จ ({count})";
                lblStatus.ForeColor = Success;

                RefreshProfilesList();
                MessageBox.Show(
                    $"สร้าง Profile บอทจำนวน {count} ตัว พร้อม Extension เรียบร้อยแล้ว!\n\n" +
                    $"💡 คำแนะนำการใช้งาน:\n" +
                    $"- กดปุ่ม '🚀 เปิด' ที่รายการด้านล่าง เพื่อเปิดหน้าต่าง Chrome พร้อม Extension อัตโนมัติ\n" +
                    $"- หรือกดปุ่ม '🚀 เปิดทดสอบทุก Profile' เพื่อเปิดพร้อมกันทุกหน้าต่าง\n" +
                    $"- หรือกดปุ่ม '📂 เปิดโฟลเดอร์ Profiles' แล้วดับเบิลคลิกไฟล์ Launch_Worker_X.bat ได้เลย",
                    "สร้าง Profile สำเร็จ",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }

        private void RefreshProfilesList()
        {
            pnlProfilesList.Controls.Clear();
            var profiles = ChromeProfileService.GetBotProfiles();

            if (profiles.Count == 0)
            {
                var lblEmpty = new Label
                {
                    Text = "📂 ยังไม่มี Profile บอทในระบบ\n(กรุณากำหนดจำนวนที่ต้องการแล้วกดปุ่ม '➕ สร้าง Profile บอท' ด้านบน)",
                    Font = new Font("Segoe UI", 10f),
                    ForeColor = TextDim,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill
                };
                pnlProfilesList.Controls.Add(lblEmpty);
                return;
            }

            int y = 8;
            foreach (var p in profiles)
            {
                var row = new Panel
                {
                    Location = new Point(8, y),
                    Size = new Size(590, 42),
                    BackColor = Color.FromArgb(32, 36, 62)
                };

                var lblBadge = new Label
                {
                    Text = "🤖 BOT",
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    ForeColor = BotBadgeColor,
                    Size = new Size(55, 24),
                    Location = new Point(10, 10),
                    TextAlign = ContentAlignment.MiddleCenter
                };

                var lblName = new Label
                {
                    Text = p.DisplayName,
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    ForeColor = TextMain,
                    AutoSize = true,
                    Location = new Point(70, 11)
                };

                var lblDir = new Label
                {
                    Text = $"({p.DirectoryName})",
                    Font = new Font("Segoe UI", 8.5f),
                    ForeColor = TextDim,
                    AutoSize = true,
                    Location = new Point(220, 12)
                };

                var btnLaunch = new RoundedButton
                {
                    Text = "🚀 เปิด",
                    Size = new Size(75, 28),
                    Location = new Point(440, 7),
                    BaseColor = Accent,
                    HoverColor = AccentHover,
                    CornerRadius = 5,
                    Font = new Font("Segoe UI Semibold", 8.8f),
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
                    Size = new Size(62, 28),
                    Location = new Point(520, 7),
                    BaseColor = Color.FromArgb(45, 20, 28),
                    HoverColor = Color.FromArgb(65, 25, 38),
                    BorderColor = Color.FromArgb(244, 63, 94),
                    CornerRadius = 5,
                    Font = new Font("Segoe UI Semibold", 8.5f),
                    ForeColorNormal = Color.FromArgb(255, 130, 150)
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
                y += 48;
            }
        }
    }
}
