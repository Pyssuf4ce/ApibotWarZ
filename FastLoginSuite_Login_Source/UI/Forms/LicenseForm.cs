using ApibotWarZ.UI.Controls;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI.Forms
{
    public class LicenseForm : Form
    {
        private static readonly Color BgDark = Color.FromArgb(14, 16, 26);
        private static readonly Color CardDark = Color.FromArgb(22, 25, 42);
        private static readonly Color CardBorder = Color.FromArgb(42, 48, 75);
        private static readonly Color Accent = Color.FromArgb(115, 95, 255);
        private static readonly Color AccentHover = Color.FromArgb(135, 118, 255);
        private static readonly Color CyberBlue = Color.FromArgb(56, 189, 248);
        private static readonly Color CyberBlueHover = Color.FromArgb(90, 205, 255);
        private static readonly Color TextMain = Color.FromArgb(245, 245, 255);
        private static readonly Color TextDim = Color.FromArgb(140, 146, 175);
        private static readonly Color Danger = Color.FromArgb(244, 63, 94);
        private static readonly Color Success = Color.FromArgb(52, 211, 153);

        private readonly CloudLicenseService _licenseService;

        // Loading Controls
        private Panel pnlLoading = null!;
        private Label lblAppTitle = null!;
        private Label lblVersionBadge = null!;
        private SpinnerControl spinner = null!;
        private Label lblLoadingStep = null!;
        private Label lblLoadingSub = null!;
        private RoundedButton btnManualKeyInput = null!;

        // Activation Controls
        private Panel pnlActivation = null!;
        private Label lblActTitle = null!;
        private Label lblActSubtitle = null!;
        private Panel pnlInputCard = null!;
        private TextBox txtKey = null!;
        private RoundedButton btnActivate = null!;
        private RoundedButton btnDiscoverHost = null!;
        private Label lblStatus = null!;
        private Panel pnlHwidFooter = null!;
        private Label lblHwidText = null!;
        private RoundedButton btnCopyHwid = null!;

        public bool IsAuthenticated { get; private set; } = false;
        public string ExpiryText { get; private set; } = "";
        public string RawExpiryText { get; private set; } = "";

        public LicenseForm(CloudLicenseService licenseService)
        {
            _licenseService = licenseService;
            SetupModernUI();
        }

        private void SetupModernUI()
        {
            this.Text = "WarZ Bot Daily — Authentication Gateway";
            this.Size = new Size(500, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.Font = new Font("Segoe UI", 9.5f);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = true;
            this.DoubleBuffered = true;

            // ═══════════════════════════════════════════
            //  1. LOADING VIEW
            // ═══════════════════════════════════════════
            pnlLoading = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark
            };

            var lblLogo = new Label
            {
                Text = "⚡",
                Font = new Font("Segoe UI Emoji", 32f),
                ForeColor = Accent,
                AutoSize = true,
                Location = new Point(220, 45)
            };

            lblAppTitle = new Label
            {
                Text = "WARZ BOT DAILY",
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(135, 105)
            };

            lblVersionBadge = new Label
            {
                Text = $"ENTERPRISE • v{AutoUpdater.CurrentVersion}",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = CyberBlue,
                AutoSize = true,
                Location = new Point(175, 142)
            };

            spinner = new SpinnerControl
            {
                Size = new Size(42, 42),
                SpinnerColor = Accent,
                TrailColor = Color.FromArgb(35, 40, 65),
                Thickness = 3,
                Location = new Point(225, 195)
            };

            lblLoadingStep = new Label
            {
                Text = "กำลังตรวจสอบสิทธิ์เครื่อง...",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = false,
                Size = new Size(440, 24),
                Location = new Point(26, 255),
                TextAlign = ContentAlignment.MiddleCenter
            };

            lblLoadingSub = new Label
            {
                Text = "เชื่อมต่อระบบรักษาความปลอดภัย Cloud Security...",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextDim,
                AutoSize = false,
                Size = new Size(440, 20),
                Location = new Point(26, 280),
                TextAlign = ContentAlignment.MiddleCenter
            };

            btnManualKeyInput = new RoundedButton
            {
                Text = "🔑  กรอก License Key (สำหรับเครื่องแม่)",
                Size = new Size(260, 30),
                Location = new Point(120, 385),
                BaseColor = Color.FromArgb(30, 34, 55),
                HoverColor = Color.FromArgb(45, 52, 85),
                ForeColorNormal = TextDim,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Visible = false
            };
            btnManualKeyInput.Click += (s, e) => ShowKeyInputView("กรุณากรอก License Key เพื่อเปิดใช้งานเครื่องแม่");

            pnlLoading.Controls.Add(lblLogo);
            pnlLoading.Controls.Add(lblAppTitle);
            pnlLoading.Controls.Add(lblVersionBadge);
            pnlLoading.Controls.Add(spinner);
            pnlLoading.Controls.Add(lblLoadingStep);
            pnlLoading.Controls.Add(lblLoadingSub);
            pnlLoading.Controls.Add(btnManualKeyInput);
            this.Controls.Add(pnlLoading);

            // ═══════════════════════════════════════════
            //  2. ACTIVATION VIEW
            // ═══════════════════════════════════════════
            pnlActivation = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                Visible = false,
                Padding = new Padding(24)
            };

            var pnlCard = new Panel
            {
                Size = new Size(436, 320),
                Location = new Point(26, 20),
                BackColor = CardDark
            };
            pnlCard.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(CardBorder, 1.2f);
                var r = pnlCard.ClientRectangle;
                r.Width -= 1;
                r.Height -= 1;
                g.DrawRectangle(pen, r);
            };

            var lblShield = new Label
            {
                Text = "🛡️",
                Font = new Font("Segoe UI Emoji", 20f),
                AutoSize = true,
                Location = new Point(198, 12)
            };

            lblActTitle = new Label
            {
                Text = "การยืนยันสิทธิ์การใช้งาน (Activation)",
                Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = false,
                Size = new Size(400, 26),
                Location = new Point(18, 48),
                TextAlign = ContentAlignment.MiddleCenter
            };

            lblActSubtitle = new Label
            {
                Text = "กรุณากรอก License Key หรือรับสิทธิ์ผ่านระบบเครือข่าย VM",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextDim,
                AutoSize = false,
                Size = new Size(400, 20),
                Location = new Point(18, 74),
                TextAlign = ContentAlignment.MiddleCenter
            };

            pnlInputCard = new Panel
            {
                Size = new Size(380, 44),
                Location = new Point(28, 102),
                BackColor = Color.FromArgb(16, 18, 30)
            };
            pnlInputCard.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(60, 68, 105), 1.2f);
                var r = pnlInputCard.ClientRectangle;
                r.Width -= 1;
                r.Height -= 1;
                e.Graphics.DrawRectangle(pen, r);
            };

            txtKey = new TextBox
            {
                Size = new Size(350, 24),
                Location = new Point(15, 10),
                BackColor = Color.FromArgb(16, 18, 30),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 12f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                PlaceholderText = "กรอก LICENSE KEY ที่นี่..."
            };
            txtKey.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await DoActivateAsync(txtKey.Text);
                }
            };
            pnlInputCard.Controls.Add(txtKey);

            btnActivate = new RoundedButton
            {
                Text = "🔓  เปิดใช้งานสิทธิ์ (Activate License)",
                Size = new Size(380, 40),
                Location = new Point(28, 156),
                BaseColor = Accent,
                HoverColor = AccentHover,
                ForeColorNormal = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnActivate.Click += async (s, e) => await DoActivateAsync(txtKey.Text);

            btnDiscoverHost = new RoundedButton
            {
                Text = "📡  เชื่อมต่อและรับสิทธิ์จากคอมหลัก (VM Guest Mode)",
                Size = new Size(380, 36),
                Location = new Point(28, 204),
                BaseColor = Color.FromArgb(30, 42, 68),
                HoverColor = Color.FromArgb(42, 58, 92),
                ForeColorNormal = CyberBlue,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnDiscoverHost.Click += async (s, e) => await DoDiscoverHostAsync();

            lblStatus = new Label
            {
                Text = "",
                ForeColor = TextDim,
                Font = new Font("Segoe UI", 8.5f),
                AutoSize = false,
                Size = new Size(380, 50),
                Location = new Point(28, 250),
                TextAlign = ContentAlignment.MiddleCenter
            };

            pnlCard.Controls.Add(lblShield);
            pnlCard.Controls.Add(lblActTitle);
            pnlCard.Controls.Add(lblActSubtitle);
            pnlCard.Controls.Add(pnlInputCard);
            pnlCard.Controls.Add(btnActivate);
            pnlCard.Controls.Add(btnDiscoverHost);
            pnlCard.Controls.Add(lblStatus);
            pnlActivation.Controls.Add(pnlCard);

            // HWID Footer Card
            pnlHwidFooter = new Panel
            {
                Size = new Size(436, 44),
                Location = new Point(26, 352),
                BackColor = CardDark
            };
            pnlHwidFooter.Paint += (s, e) =>
            {
                using var pen = new Pen(CardBorder, 1);
                var r = pnlHwidFooter.ClientRectangle;
                r.Width -= 1;
                r.Height -= 1;
                e.Graphics.DrawRectangle(pen, r);
            };

            string currentHwid = CloudLicenseService.GetHWID();
            lblHwidText = new Label
            {
                Text = $"HWID: {currentHwid}",
                Font = new Font("Consolas", 8.5f),
                ForeColor = TextDim,
                Location = new Point(14, 13),
                Size = new Size(300, 20),
                AutoEllipsis = true
            };

            btnCopyHwid = new RoundedButton
            {
                Text = "📋 คัดลอก",
                Size = new Size(80, 28),
                Location = new Point(344, 8),
                BaseColor = Color.FromArgb(35, 40, 60),
                HoverColor = Color.FromArgb(50, 58, 85),
                ForeColorNormal = TextMain,
                Font = new Font("Segoe UI", 8.5f),
                Cursor = Cursors.Hand
            };
            btnCopyHwid.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(currentHwid);
                    btnCopyHwid.Text = "✅ คัดลอกแล้ว";
                    Task.Delay(1500).ContinueWith(_ =>
                    {
                        if (!btnCopyHwid.IsDisposed)
                            btnCopyHwid.Invoke(new Action(() => btnCopyHwid.Text = "📋 คัดลอก"));
                    });
                }
                catch { }
            };

            pnlHwidFooter.Controls.Add(lblHwidText);
            pnlHwidFooter.Controls.Add(btnCopyHwid);
            pnlActivation.Controls.Add(pnlHwidFooter);

            this.Controls.Add(pnlActivation);

            // Startup Flow
            this.Shown += async (s, e) => await OnFormShownAsync();
        }

        private async Task OnFormShownAsync()
        {
            ShowLoadingView(true);
            spinner.Start();

            // ─── 1. Check Direct Firebase HWID (Host PC Mode) ───
            lblLoadingStep.Text = "กำลังตรวจสอบสิทธิ์เครื่องกับ Cloud Server...";
            lblLoadingSub.Text = "กำลังเชื่อมต่อไปยัง Firebase Security Gateway...";
            var (success, message, boundKey, expiry, rawExpiry) = await _licenseService.CheckHwidAsync();

            if (success)
            {
                lblLoadingStep.Text = "✅ ยืนยันสิทธิ์เครื่องหลักสำเร็จ (Host Mode)!";
                lblLoadingStep.ForeColor = Success;
                lblLoadingSub.Text = $"อายุการใช้งาน: {expiry}";

                IsAuthenticated = true;
                ExpiryText = expiry;
                RawExpiryText = rawExpiry;

                await Task.Delay(400);
                this.DialogResult = DialogResult.OK;
                this.Close();
                return;
            }

            // ─── 2. If check fails, show Key Input Activation View ───
            spinner.Stop();
            ShowKeyInputView(message);
        }

        private void ShowLoadingView(bool visible)
        {
            pnlLoading.Visible = visible;
            pnlActivation.Visible = !visible;
        }

        private void ShowKeyInputView(string? errorMessage)
        {
            ShowLoadingView(false);

            if (!string.IsNullOrEmpty(errorMessage) && errorMessage != "ไม่พบเครื่องนี้ในระบบ กรุณากรอก License Key เพื่อเปิดใช้งาน")
            {
                lblActSubtitle.Text = "สิทธิ์ของเครื่องไม่ถูกต้อง กรุณากรอก License Key ใหม่";
                lblActSubtitle.ForeColor = Color.FromArgb(255, 180, 90);
                lblStatus.Text = $"❌  {errorMessage}";
                lblStatus.ForeColor = Danger;
            }
            else
            {
                lblActSubtitle.Text = "กรุณากรอก License Key หรือเชื่อมต่อกับเครื่องแม่";
                lblActSubtitle.ForeColor = TextDim;
                lblStatus.Text = "";
            }

            txtKey.Text = "";
            txtKey.Focus();
        }

        private async Task DoDiscoverHostAsync()
        {
            await Task.CompletedTask;
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
            btnDiscoverHost.Enabled = false;
            txtKey.Enabled = false;
            lblStatus.Text = "⏳  กำลังผูกสิทธิ์เครื่องกับ Cloud Server...";
            lblStatus.ForeColor = CyberBlue;

            var (success, message, expiry, rawExpiry) = await _licenseService.ActivateKeyAsync(key);
            if (success)
            {
                IsAuthenticated = true;
                ExpiryText = expiry;
                RawExpiryText = rawExpiry;
                // LocalLicenseClient.CurrentMode = LicenseMode.DirectHost;

                // Start Local License Server on Physical PC
                // LocalLicenseServer.Start(key.Trim(), ExpiryText, CloudLicenseService.GetHWID());

                lblStatus.Text = $"✅  ยืนยันสำเร็จ! อายุการใช้งาน: {ExpiryText}";
                lblStatus.ForeColor = Success;

//                 _ = DiscordNotifier.SendLoginAlertAsync(key.Trim(), ExpiryText, CloudLicenseService.GetHWID());

                await Task.Delay(600);
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                btnActivate.Enabled = true;
                btnDiscoverHost.Enabled = true;
                txtKey.Enabled = true;
                lblStatus.Text = $"❌  {message}";
                lblStatus.ForeColor = Danger;
                txtKey.Focus();
                txtKey.SelectAll();
            }
        }
    }

    public class SpinnerControl : Control
    {
        private System.Windows.Forms.Timer _timer = null!;
        private float _angle = 0f;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color SpinnerColor { get; set; } = Color.FromArgb(115, 95, 255);

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color TrailColor { get; set; } = Color.FromArgb(35, 40, 65);

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Thickness { get; set; } = 3;

        public SpinnerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
            this.DoubleBuffered = true;
            this.Size = new Size(36, 36);

            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += (s, e) =>
            {
                _angle = (_angle + 6f) % 360f;
                Invalidate();
            };
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int pad = Thickness + 1;
            var rect = new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2);

            if (rect.Width <= 0 || rect.Height <= 0) return;

            using (var trailPen = new Pen(TrailColor, Thickness))
            {
                g.DrawEllipse(trailPen, rect);
            }

            using (var arcPen = new Pen(SpinnerColor, Thickness))
            {
                arcPen.StartCap = LineCap.Round;
                arcPen.EndCap = LineCap.Round;
                g.DrawArc(arcPen, rect, _angle, 100f);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop();
                _timer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}




