using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ApibotWarZ.UI.Controls;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Forms
{
    public class DiscordSettingsDialog : Form
    {
        private static readonly Color BgDark = Color.FromArgb(13, 15, 26);
        private static readonly Color PanelDark = Color.FromArgb(22, 24, 42);
        private static readonly Color CardDark = Color.FromArgb(26, 29, 50);
        private static readonly Color TextMain = Color.FromArgb(240, 240, 250);
        private static readonly Color TextDim = Color.FromArgb(135, 140, 165);
        private static readonly Color Accent = Color.FromArgb(124, 106, 255);
        private static readonly Color AccentHover = Color.FromArgb(144, 126, 255);
        private static readonly Color Success = Color.FromArgb(16, 185, 129);
        private static readonly Color Danger = Color.FromArgb(244, 63, 94);

        private TextBox txtWebhook1 = null!;
        private TextBox txtWebhook2 = null!;
        private Label lblStatus = null!;

        public DiscordSettingsDialog()
        {
            SetupUI();
            LoadCurrentWebhook();
        }

        private void SetupUI()
        {
            this.Text = "🔔 ตั้งค่า Discord Webhook Notification (รองรับ 2 Webhooks)";
            this.Size = new Size(620, 420);
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
                Text = "🔔 Discord Webhook การแจ้งเตือน",
                Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(20, 16)
            };

            var lblDesc = new Label
            {
                Text = "ระบบจะส่งภาพหน้าจอและรายงานแจ้งเตือน (เข้าสู่ระบบสำเร็จ / Error) เข้า Discord ทั้ง 2 ห้องพร้อมกัน",
                Font = new Font("Segoe UI", 9f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(22, 46)
            };

            var pnlCard = new Panel
            {
                Location = new Point(20, 76),
                Size = new Size(560, 215),
                BackColor = CardDark
            };

            // ─── Webhook 1 ───
            var lblInput1 = new Label
            {
                Text = "📌 URL Discord Webhook 1 (ห้องหลัก):",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(90, 180, 255),
                AutoSize = true,
                Location = new Point(14, 12)
            };

            txtWebhook1 = new TextBox
            {
                Location = new Point(14, 38),
                Size = new Size(530, 26),
                BackColor = PanelDark,
                ForeColor = TextMain,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f)
            };

            // ─── Webhook 2 ───
            var lblInput2 = new Label
            {
                Text = "📌 URL Discord Webhook 2 (ห้องสำรอง / ถ้ามี):",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 202, 90),
                AutoSize = true,
                Location = new Point(14, 76)
            };

            txtWebhook2 = new TextBox
            {
                Location = new Point(14, 102),
                Size = new Size(530, 26),
                BackColor = PanelDark,
                ForeColor = TextMain,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f)
            };

            lblStatus = new Label
            {
                Text = "💡 วางลิงก์ Discord Webhook (ขึ้นต้นด้วย https://discord.com/api/webhooks/...)",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(14, 145)
            };

            var lblTip = new Label
            {
                Text = "📝 หากต้องการใช้เพียง 1 Webhook ให้กรอกเฉพาะช่องแรกและเว้นช่องที่ 2 ว่างไว้",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(14, 172)
            };

            pnlCard.Controls.Add(lblInput1);
            pnlCard.Controls.Add(txtWebhook1);
            pnlCard.Controls.Add(lblInput2);
            pnlCard.Controls.Add(txtWebhook2);
            pnlCard.Controls.Add(lblStatus);
            pnlCard.Controls.Add(lblTip);

            // Buttons
            var btnSave = new RoundedButton
            {
                Text = "💾 บันทึก",
                Size = new Size(110, 36),
                Location = new Point(20, 310),
                BaseColor = Success,
                HoverColor = Color.FromArgb(120, 230, 140),
                ForeColorNormal = Color.Black
            };
            btnSave.Click += (s, e) => SaveWebhook();

            var btnTest = new RoundedButton
            {
                Text = "🚀 ทดสอบส่ง Alert",
                Size = new Size(150, 36),
                Location = new Point(140, 310),
                BaseColor = Accent,
                HoverColor = AccentHover,
                ForeColorNormal = Color.White
            };
            btnTest.Click += (s, e) => TestWebhook();

            var btnOpenFile = new RoundedButton
            {
                Text = "📁 เปิดไฟล์ webhook.txt",
                Size = new Size(160, 36),
                Location = new Point(300, 310),
                BaseColor = PanelDark,
                HoverColor = Color.FromArgb(46, 46, 62),
                ForeColorNormal = TextMain
            };
            btnOpenFile.Click += (s, e) => OpenWebhookFile();

            pnlMain.Controls.Add(lblTitle);
            pnlMain.Controls.Add(lblDesc);
            pnlMain.Controls.Add(pnlCard);
            pnlMain.Controls.Add(btnSave);
            pnlMain.Controls.Add(btnTest);
            pnlMain.Controls.Add(btnOpenFile);

            this.Controls.Add(pnlMain);
        }

        private void LoadCurrentWebhook()
        {
            var list = DiscordNotifier.GetCustomWebhookUrls();
            if (list.Count > 0)
            {
                txtWebhook1.Text = list[0];
                if (list.Count > 1)
                {
                    txtWebhook2.Text = list[1];
                }
                lblStatus.Text = $"✅ เชื่อมต่อกับ Webhook เรียบร้อยแล้ว (ตรวจพบ {list.Count} ลิงก์)";
                lblStatus.ForeColor = Success;
            }
            else
            {
                lblStatus.Text = "⚠️ ยังไม่ได้ระบุ Webhook (ปล่อยว่างไว้หากไม่ต้องการส่งเข้า Discord ส่วนตัว)";
                lblStatus.ForeColor = TextDim;
            }
        }

        private void SaveWebhook()
        {
            string url1 = txtWebhook1.Text.Trim();
            string url2 = txtWebhook2.Text.Trim();
            string workDir = BotProcessManager.GetWorkingDir();
            string path = Path.Combine(workDir, "webhook.txt");

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# ==============================================================");
                sb.AppendLine("# วาง URL Discord Webhook ของคุณในบรรทัดด้านล่างนี้ (ใส่ได้สูงสุด 2 ลิงก์ บรรทัดละ 1 ลิงก์)");
                sb.AppendLine("# ตัวอย่าง: https://discord.com/api/webhooks/123456789/abcdefgh...");
                sb.AppendLine("# (หากไม่ต้องการใช้งาน ให้ปล่อยว่างไว้)");
                sb.AppendLine("# ==============================================================");
                sb.AppendLine();
                if (!string.IsNullOrEmpty(url1))
                {
                    sb.AppendLine(url1);
                }
                if (!string.IsNullOrEmpty(url2) && !string.Equals(url2, url1, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(url2);
                }
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

                int count = 0;
                if (!string.IsNullOrEmpty(url1)) count++;
                if (!string.IsNullOrEmpty(url2)) count++;

                lblStatus.Text = $"✅ บันทึก Webhook สำเร็จ ({count} ลิงก์)!";
                lblStatus.ForeColor = Success;
                MessageBox.Show(this, $"บันทึก Discord Webhook สำเร็จเรียบร้อยแล้วครับ! ({count} ลิงก์)", "สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"ไม่สามารถบันทึกไฟล์ได้: {ex.Message}", "ข้อผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void TestWebhook()
        {
            SaveWebhook();
            lblStatus.Text = "🚀 กำลังทดสอบส่งภาพหน้าจอและข้อความเข้า Discord Webhooks...";
            lblStatus.ForeColor = Accent;

            DiscordNotifier.SendSuccessAlert("TestAccount", "ทดสอบการเชื่อมต่อจากหน้าต่างตั้งค่า Discord Webhook", "0.5s", "VIP Diamond x1");
            MessageBox.Show(this, "ส่งข้อความและภาพหน้าจอทดสอบเข้า Discord เรียบร้อยแล้วครับ!\nกรุณาเปิดดูในห้อง Discord ของคุณ", "ทดสอบสำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Information);
            lblStatus.Text = "✅ ส่งข้อความทดสอบสำเร็จแล้ว!";
            lblStatus.ForeColor = Success;
        }

        private void OpenWebhookFile()
        {
            string workDir = BotProcessManager.GetWorkingDir();
            string path = Path.Combine(workDir, "webhook.txt");
            if (!File.Exists(path))
            {
                SaveWebhook();
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"ไม่สามารถเปิดไฟล์ได้: {ex.Message}", "ข้อผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
