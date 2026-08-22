using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using ApibotWarZ.UI.Controls;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Forms
{
    public class LicenseForm : Form
    {
        private static readonly Color BgDark = Color.FromArgb(16, 18, 27);
        private static readonly Color CardDark = Color.FromArgb(24, 27, 40);
        private static readonly Color CardBorder = Color.FromArgb(38, 43, 64);
        private static readonly Color Accent = Color.FromArgb(108, 99, 255);
        private static readonly Color AccentHover = Color.FromArgb(130, 121, 255);
        private static readonly Color TextMain = Color.FromArgb(240, 240, 250);
        private static readonly Color TextDim = Color.FromArgb(135, 140, 165);
        private static readonly Color Danger = Color.FromArgb(240, 80, 80);
        private static readonly Color Success = Color.FromArgb(60, 215, 125);

        private readonly CloudLicenseService _licenseService;

        // ─── Loading View Controls ───
        private Label lblAppName = null!;
        private Label lblVersion = null!;
        private SpinnerControl spinner = null!;
        private Label lblLoadingStatus = null!;

        // ─── Key Input View Controls ───
        private Panel panelKeyInput = null!;
        private Label lblSubtitle = null!;
        private Panel panelInput = null!;
        private TextBox txtKey = null!;
        private RoundedButton btnActivate = null!;
        private Label lblStatus = null!;
        private Label lblHwidDisplay = null!;

        public bool IsAuthenticated { get; private set; } = false;
        public string ExpiryText { get; private set; } = "";

        public LicenseForm(CloudLicenseService licenseService)
        {
            _licenseService = licenseService;
            SetupUI();
        }

        private void SetupUI()
        {
            this.Text = "Apibot WarZ - License Authentication";
            this.Size = new Size(440, 320);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9.5f);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = true;

            // ═══════════════════════════════════════════
            //  LOADING VIEW (Pure HWID check on startup)
            // ═══════════════════════════════════════════
            lblAppName = new Label
            {
                Text = "⚡ Apibot WarZ",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Anchor = AnchorStyles.None
            };

            lblVersion = new Label
            {
                Text = $"v{AutoUpdater.CurrentVersion} • Cloud Security",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = Accent,
                AutoSize = true,
                Anchor = AnchorStyles.None
            };

            spinner = new SpinnerControl
            {
                Size = new Size(34, 34),
                SpinnerColor = Accent,
                Anchor = AnchorStyles.None
            };

            lblLoadingStatus = new Label
            {
                Text = "กำลังตรวจสอบสิทธิ์เครื่องกับ Cloud Server...",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Anchor = AnchorStyles.None
            };

            this.Controls.Add(lblAppName);
            this.Controls.Add(lblVersion);
            this.Controls.Add(spinner);
            this.Controls.Add(lblLoadingStatus);

            // ═══════════════════════════════════════════
            //  KEY INPUT VIEW (shown only if HWID not active)
            // ═══════════════════════════════════════════
            panelKeyInput = new Panel
            {
                Location = new Point(0, 0),
                Size = this.ClientSize,
                BackColor = BgDark,
                Visible = false
            };

            var lblTitle2 = new Label
            {
                Text = "⚡ Apibot WarZ",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(36, 20)
            };

            lblSubtitle = new Label
            {
                Text = "กรุณากรอก License Key เพื่อเปิดใช้งานเครื่องนี้",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = TextDim,
                AutoSize = true,
                Location = new Point(38, 52)
            };

            panelInput = new Panel
            {
                Location = new Point(36, 86),
                Size = new Size(350, 42),
                BackColor = CardDark,
                Padding = new Padding(12, 10, 12, 10)
            };
            panelInput.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1.5f);
                e.Graphics.DrawRectangle(pen, 0, 0, panelInput.Width - 1, panelInput.Height - 1);
            };

            txtKey = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = CardDark,
                ForeColor = TextMain,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 11f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center
            };
            txtKey.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await DoActivateAsync(txtKey.Text);
                }
            };
            panelInput.Controls.Add(txtKey);

            btnActivate = new RoundedButton
            {
                Text = "🔓  เปิดใช้งานเครื่อง (Activate)",
                Size = new Size(350, 44),
                Location = new Point(36, 138),
                BaseColor = Accent,
                HoverColor = AccentHover,
                ForeColorNormal = Color.White,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnActivate.Click += async (s, e) => await DoActivateAsync(txtKey.Text);

            lblStatus = new Label
            {
                Text = "",
                ForeColor = TextDim,
                Font = new Font("Segoe UI", 9f),
                AutoSize = false,
                Size = new Size(350, 35),
                Location = new Point(36, 190),
                TextAlign = ContentAlignment.TopCenter
            };

            string hwid = CloudLicenseService.GetHWID();
            lblHwidDisplay = new Label
            {
                Text = $"HWID: {hwid.Substring(0, Math.Min(16, hwid.Length))}...",
                ForeColor = Color.FromArgb(90, 95, 120),
                Font = new Font("Consolas", 8.5f),
                AutoSize = false,
                Size = new Size(350, 20),
                Location = new Point(36, 235),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            lblHwidDisplay.Click += (s, e) =>
            {
                Clipboard.SetText(hwid);
                lblStatus.Text = "📋 คัดลอก HWID เต็มลงคลิปบอร์ดแล้ว!";
                lblStatus.ForeColor = Success;
            };

            panelKeyInput.Controls.Add(lblTitle2);
            panelKeyInput.Controls.Add(lblSubtitle);
            panelKeyInput.Controls.Add(panelInput);
            panelKeyInput.Controls.Add(btnActivate);
            panelKeyInput.Controls.Add(lblStatus);
            panelKeyInput.Controls.Add(lblHwidDisplay);
            this.Controls.Add(panelKeyInput);

            // ─── Layout ───
            CenterLoadingControls();
            this.Resize += (s, e) => CenterLoadingControls();

            // ─── Pure HWID Startup Flow ───
            this.Shown += async (s, e) => await OnFormShownAsync();
        }

        private void CenterLoadingControls()
        {
            int cx = this.ClientSize.Width / 2;
            int baseY = 65;

            lblAppName.Location = new Point(cx - lblAppName.Width / 2, baseY);
            lblVersion.Location = new Point(cx - lblVersion.Width / 2, baseY + 42);
            spinner.Location = new Point(cx - spinner.Width / 2, baseY + 86);
            lblLoadingStatus.Location = new Point(cx - lblLoadingStatus.Width / 2, baseY + 130);
        }

        private async Task OnFormShownAsync()
        {
            ShowLoadingView(true);
            spinner.Start();

            var (success, message, boundKey, expiry) = await _licenseService.CheckHwidAsync();

            if (success)
            {
                lblLoadingStatus.Text = "✅ ยืนยันสิทธิ์เครื่องสำเร็จ!";
                lblLoadingStatus.ForeColor = Success;

                IsAuthenticated = true;
                ExpiryText = expiry;

                _ = DiscordNotifier.SendLoginAlertAsync(boundKey, ExpiryText, CloudLicenseService.GetHWID());

                await Task.Delay(400);
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                spinner.Stop();
                ShowKeyInputView(message);
            }
        }

        private void ShowLoadingView(bool visible)
        {
            lblAppName.Visible = visible;
            lblVersion.Visible = visible;
            spinner.Visible = visible;
            lblLoadingStatus.Visible = visible;
            panelKeyInput.Visible = !visible;
        }

        private void ShowKeyInputView(string? errorMessage)
        {
            ShowLoadingView(false);
            panelKeyInput.Visible = true;

            if (!string.IsNullOrEmpty(errorMessage) && errorMessage != "ไม่พบเครื่องนี้ในระบบ กรุณากรอก License Key เพื่อเปิดใช้งาน")
            {
                lblSubtitle.Text = "สิทธิ์ของเครื่องไม่ถูกต้อง กรุณากรอก License Key ใหม่";
                lblSubtitle.ForeColor = Color.FromArgb(255, 180, 90);
                lblStatus.Text = $"❌  {errorMessage}";
                lblStatus.ForeColor = Danger;
            }
            else
            {
                lblSubtitle.Text = "กรุณากรอก License Key เพื่อเปิดใช้งานเครื่องนี้";
                lblSubtitle.ForeColor = TextDim;
                lblStatus.Text = "";
            }

            txtKey.Text = "";
            txtKey.Focus();
        }

        private async Task DoActivateAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                lblStatus.Text = "❌  กรุณากรอก License Key";
                lblStatus.ForeColor = Danger;
                txtKey.Focus();
                return;
            }

            btnActivate.Enabled = false;
            txtKey.Enabled = false;
            lblStatus.Text = "⏳  กำลังผูกสิทธิ์เครื่องกับ Cloud Server...";
            lblStatus.ForeColor = Color.FromArgb(90, 180, 255);

            var (success, message, expiry) = await _licenseService.ActivateKeyAsync(key);
            if (success)
            {
                IsAuthenticated = true;
                ExpiryText = expiry;
                lblStatus.Text = $"✅  ยืนยันสำเร็จ! อายุการใช้งาน: {ExpiryText}";
                lblStatus.ForeColor = Success;

                _ = DiscordNotifier.SendLoginAlertAsync(key.Trim(), ExpiryText, CloudLicenseService.GetHWID());

                await Task.Delay(600);
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                btnActivate.Enabled = true;
                txtKey.Enabled = true;
                lblStatus.Text = $"❌  {message}";
                lblStatus.ForeColor = Danger;
                txtKey.Focus();
                txtKey.SelectAll();
            }
        }
    }
}
