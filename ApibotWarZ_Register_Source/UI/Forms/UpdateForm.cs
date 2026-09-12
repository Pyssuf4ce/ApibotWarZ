using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using ApibotWarZ.UI.Controls;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Forms
{
    public class UpdateForm : Form
    {
        private static readonly Color BgDark = Color.FromArgb(16, 18, 27);
        private static readonly Color CardDark = Color.FromArgb(24, 27, 40);
        private static readonly Color Accent = Color.FromArgb(108, 99, 255);
        private static readonly Color AccentHover = Color.FromArgb(130, 121, 255);
        private static readonly Color TextMain = Color.FromArgb(240, 240, 250);
        private static readonly Color TextDim = Color.FromArgb(135, 140, 165);
        private static readonly Color Success = Color.FromArgb(60, 215, 125);
        private static readonly Color Danger = Color.FromArgb(240, 80, 80);

        private readonly AutoUpdater.UpdateInfo _updateInfo;
        private ProgressBar prgDownload = null!;
        private Label lblProgress = null!;
        private RoundedButton btnStartUpdate = null!;
        private RoundedButton btnSkip = null!;

        public UpdateForm(AutoUpdater.UpdateInfo updateInfo)
        {
            _updateInfo = updateInfo;
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "Apibot WarZ — Software Update";
            this.Size = new Size(480, 350);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9.5f);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ControlBox = true;

            var lblTitle = new Label
            {
                Text = "🚀  ตรวจพบการอัปเดตเวอร์ชันใหม่!",
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(28, 24)
            };

            var lblVersion = new Label
            {
                Text = $"เวอร์ชันปัจจุบัน: v{AutoUpdater.CurrentVersion}  ➔  เวอร์ชันใหม่: v{_updateInfo.LatestVersion}",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Accent,
                AutoSize = true,
                Location = new Point(30, 62)
            };

            var txtChangelog = new TextBox
            {
                Location = new Point(30, 96),
                Size = new Size(405, 90),
                BackColor = CardDark,
                ForeColor = TextDim,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Text = string.IsNullOrEmpty(_updateInfo.Changelog)
                    ? "• ปรับปรุงประสิทธิภาพการทำงานและระบบแก้ Captcha"
                    : _updateInfo.Changelog
            };

            prgDownload = new ProgressBar
            {
                Location = new Point(30, 200),
                Size = new Size(405, 14),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Visible = false
            };

            lblProgress = new Label
            {
                Text = "คุณสามารถเลือกอัปเดตทันที หรือข้ามไปก่อนเพื่อเข้าใช้งานเวอร์ชันเดิมได้",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(30, 222)
            };

            btnStartUpdate = new RoundedButton
            {
                Text = "📥  อัปเดตทันที (Update Now)",
                Size = new Size(260, 42),
                Location = new Point(30, 248),
                BaseColor = Accent,
                HoverColor = AccentHover,
                ForeColorNormal = Color.White,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnStartUpdate.Click += async (s, e) => await StartDownloadAndApplyAsync();

            btnSkip = new RoundedButton
            {
                Text = "⏳ ข้ามไปก่อน",
                Size = new Size(135, 42),
                Location = new Point(300, 248),
                BaseColor = CardDark,
                HoverColor = Color.FromArgb(45, 48, 70),
                ForeColorNormal = TextDim,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnSkip.Click += (s, e) =>
            {
                this.DialogResult = DialogResult.Ignore;
                this.Close();
            };

            this.Controls.Add(lblTitle);
            this.Controls.Add(lblVersion);
            this.Controls.Add(txtChangelog);
            this.Controls.Add(prgDownload);
            this.Controls.Add(lblProgress);
            this.Controls.Add(btnStartUpdate);
            this.Controls.Add(btnSkip);
        }

        private async Task StartDownloadAndApplyAsync()
        {
            btnStartUpdate.Enabled = false;
            btnSkip.Enabled = false;
            prgDownload.Visible = true;
            lblProgress.Text = "กำลังเชื่อมต่อดาวน์โหลดไฟล์...";
            lblProgress.ForeColor = Color.FromArgb(90, 180, 255);

            try
            {
                string downloadUrl = AutoUpdater.NormalizeDownloadUrl(_updateInfo.DownloadUrl);
                string tempRawPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ApibotWarZ_update.tmp");

                if (File.Exists(tempRawPath)) File.Delete(tempRawPath);

                using var handler = new HttpClientHandler { AllowAutoRedirect = true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempRawPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        int progressPercent = (int)((totalRead * 100) / totalBytes);
                        prgDownload.Value = Math.Min(100, Math.Max(0, progressPercent));
                        double mbRead = (double)totalRead / (1024 * 1024);
                        double mbTotal = (double)totalBytes / (1024 * 1024);
                        lblProgress.Text = $"กำลังดาวน์โหลด: {progressPercent}% ({mbRead:F1} MB / {mbTotal:F1} MB)...";
                    }
                    else
                    {
                        double mbRead = (double)totalRead / (1024 * 1024);
                        lblProgress.Text = $"กำลังดาวน์โหลด: {mbRead:F1} MB...";
                    }
                }

                fileStream.Close();

                byte[] header = new byte[4];
                using (var fsCheck = File.OpenRead(tempRawPath))
                {
                    fsCheck.ReadExactly(header, 0, header.Length);
                }

                bool isMZ = header[0] == 0x4D && header[1] == 0x5A;
                bool isPK = header[0] == 0x50 && header[1] == 0x4B;

                if (!isMZ && !isPK)
                {
                    File.Delete(tempRawPath);
                    throw new Exception("ไฟล์ที่โหลดมาไม่ใช่ไฟล์โปรแกรมจริง กรุณาตรวจสอบลิงก์ดาวน์โหลด");
                }

                string finalTempPath;
                if (isPK)
                {
                    finalTempPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ApibotWarZ_update.zip");
                    if (File.Exists(finalTempPath)) File.Delete(finalTempPath);
                    File.Move(tempRawPath, finalTempPath);
                }
                else
                {
                    finalTempPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ApibotWarZ_update.exe");
                    if (File.Exists(finalTempPath)) File.Delete(finalTempPath);
                    File.Move(tempRawPath, finalTempPath);
                }

                lblProgress.Text = "✅ ดาวน์โหลดเสร็จสมบูรณ์! กำลังแตกไฟล์และเปิดโปรแกรม...";
                lblProgress.ForeColor = Success;
                prgDownload.Value = 100;

                await Task.Delay(1000);
                AutoUpdater.ApplyUpdateAndRestart(finalTempPath);
            }
            catch (Exception ex)
            {
                btnStartUpdate.Enabled = true;
                btnSkip.Enabled = true;
                lblProgress.Text = $"❌ {ex.Message}";
                lblProgress.ForeColor = Danger;
            }
        }
    }
}
