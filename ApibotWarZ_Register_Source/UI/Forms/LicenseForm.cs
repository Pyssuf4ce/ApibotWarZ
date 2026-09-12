using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
        private Panel panelLoading = null!; // NEW: wraps the loading view so it can be crossfaded as one unit

        // ─── Key Input View Controls ───
        private Panel panelKeyInput = null!;
        private Label lblSubtitle = null!;
        private Panel panelInput = null!;
        private TextBox txtKey = null!;
        private RoundedButton btnActivate = null!;
        private Label lblStatus = null!;
        private Label lblHwidDisplay = null!;

        // ─── NEW: transition / focus animation state ───
        private PictureBox _fadeOverlay = null!;
        private System.Windows.Forms.Timer _fadeTimer = null!;
        private float _inputFocusProgress; // 0 = unfocused, 1 = focused
        private System.Windows.Forms.Timer _focusTimer = null!;
        private float _statusAlpha = 1f;   // status label fade-in
        private System.Windows.Forms.Timer _statusFadeTimer = null!;

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
            panelLoading = new Panel
            {
                Location = new Point(0, 0),
                Size = this.ClientSize,
                BackColor = BgDark
            };

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
                SpinnerColorTail = AccentHover,
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

            panelLoading.Controls.Add(lblAppName);
            panelLoading.Controls.Add(lblVersion);
            panelLoading.Controls.Add(spinner);
            panelLoading.Controls.Add(lblLoadingStatus);
            this.Controls.Add(panelLoading);

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
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                // Border color/width animate smoothly between idle and focused.
                Color idle = CardBorder;
                Color focused = Accent;
                Color borderColor = LerpColor(idle, focused, _inputFocusProgress);
                float width = 1.5f + 1.0f * _inputFocusProgress;

                // Soft glow that grows in as the field gains focus.
                if (_inputFocusProgress > 0.01f)
                {
                    int glowAlpha = (int)(90 * _inputFocusProgress);
                    using var glowPen = new Pen(Color.FromArgb(glowAlpha, Accent), width + 3f);
                    e.Graphics.DrawRectangle(glowPen, 1, 1, panelInput.Width - 3, panelInput.Height - 3);
                }

                using var pen = new Pen(borderColor, width);
                e.Graphics.DrawRectangle(pen, 0, 0, panelInput.Width - 1, panelInput.Height - 1);
            };

            _focusTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _focusTimer.Tick += (s, e) =>
            {
                float target = txtKey.Focused ? 1f : 0f;
                _inputFocusProgress = Lerp(_inputFocusProgress, target, 0.2f);
                panelInput.Invalidate();

                if (Math.Abs(_inputFocusProgress - target) < 0.01f)
                {
                    _inputFocusProgress = target;
                    _focusTimer.Stop();
                    panelInput.Invalidate();
                }
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
            txtKey.GotFocus += (s, e) => { if (!_focusTimer.Enabled) _focusTimer.Start(); };
            txtKey.LostFocus += (s, e) => { if (!_focusTimer.Enabled) _focusTimer.Start(); };
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
                SetStatusText("📋 คัดลอก HWID เต็มลงคลิปบอร์ดแล้ว!", Success);
            };

            panelKeyInput.Controls.Add(lblTitle2);
            panelKeyInput.Controls.Add(lblSubtitle);
            panelKeyInput.Controls.Add(panelInput);
            panelKeyInput.Controls.Add(btnActivate);
            panelKeyInput.Controls.Add(lblStatus);
            panelKeyInput.Controls.Add(lblHwidDisplay);
            this.Controls.Add(panelKeyInput);

            // ─── Fade overlay used for crossfading between views ───
            _fadeOverlay = new PictureBox
            {
                Location = new Point(0, 0),
                Size = this.ClientSize,
                SizeMode = PictureBoxSizeMode.Normal,
                Visible = false,
                BackColor = Color.Transparent
            };
            this.Controls.Add(_fadeOverlay);
            _fadeOverlay.BringToFront();

            _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };

            // ─── Layout ───
            CenterLoadingControls();
            this.Resize += (s, e) => CenterLoadingControls();

            // ─── Pure HWID Startup Flow ───
            this.Shown += async (s, e) => await OnFormShownAsync();
        }

        private static float Lerp(float current, float target, float step)
            => current + (target - current) * step;

        private static Color LerpColor(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            int r = (int)(a.R + (b.R - a.R) * t);
            int g = (int)(a.G + (b.G - a.G) * t);
            int bl = (int)(a.B + (b.B - a.B) * t);
            return Color.FromArgb(255, r, g, bl);
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
                await CrossFadeToKeyInputViewAsync(message);
            }
        }

        private void ShowLoadingView(bool visible)
        {
            panelLoading.Visible = visible;
            panelKeyInput.Visible = !visible;
        }

        /// <summary>
        /// Smoothly crossfades from the loading view to the key-input view instead of
        /// an abrupt Visible-flag swap. Captures both views as bitmaps and blends the
        /// alpha of the "old" snapshot down to 0 over ~280ms while the real key-input
        /// panel sits underneath already fully rendered.
        /// </summary>
        private async Task CrossFadeToKeyInputViewAsync(string? errorMessage)
        {
            // Prepare the key input view's content/state first (but keep it hidden).
            ApplyKeyInputMessage(errorMessage);

            // Snapshot the current (loading) view.
            Bitmap oldSnapshot = new Bitmap(panelLoading.Width, panelLoading.Height);
            panelLoading.DrawToBitmap(oldSnapshot, new Rectangle(Point.Empty, panelLoading.Size));

            // Swap visibility so the new view is the "real" one underneath.
            panelLoading.Visible = false;
            panelKeyInput.Visible = true;
            txtKey.Text = "";

            _fadeOverlay.Size = this.ClientSize;
            _fadeOverlay.Image = oldSnapshot;
            _fadeOverlay.Visible = true;
            _fadeOverlay.BringToFront();

            var tcs = new TaskCompletionSource<bool>();
            float alpha = 1f;

            EventHandler? tick = null;
            tick = (s, e) =>
            {
                alpha -= 0.09f; // ~280ms total at 15ms interval
                if (alpha <= 0f)
                {
                    alpha = 0f;
                    _fadeTimer.Tick -= tick;
                    _fadeTimer.Stop();
                    _fadeOverlay.Visible = false;
                    _fadeOverlay.Image?.Dispose();
                    _fadeOverlay.Image = null;
                    txtKey.Focus();
                    tcs.TrySetResult(true);
                    return;
                }
                _fadeOverlay.Image = ApplyAlpha(oldSnapshot, alpha);
            };
            _fadeTimer.Tick += tick;
            _fadeTimer.Start();

            await tcs.Task;
        }

        /// <summary>Returns a copy of the bitmap rendered at the given alpha (0..1).</summary>
        private static Bitmap ApplyAlpha(Bitmap source, float alpha)
        {
            var result = new Bitmap(source.Width, source.Height);
            using var g = Graphics.FromImage(result);
            var matrix = new ColorMatrix { Matrix33 = alpha };
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            return result;
        }

        private void ApplyKeyInputMessage(string? errorMessage)
        {
            if (!string.IsNullOrEmpty(errorMessage) && errorMessage != "ไม่พบเครื่องนี้ในระบบ กรุณากรอก License Key เพื่อเปิดใช้งาน")
            {
                lblSubtitle.Text = "สิทธิ์ของเครื่องไม่ถูกต้อง กรุณากรอก License Key ใหม่";
                lblSubtitle.ForeColor = Color.FromArgb(255, 180, 90);
                SetStatusText($"❌  {errorMessage}", Danger);
            }
            else
            {
                lblSubtitle.Text = "กรุณากรอก License Key เพื่อเปิดใช้งานเครื่องนี้";
                lblSubtitle.ForeColor = TextDim;
                SetStatusText("", TextDim);
            }
        }

        /// <summary>Sets lblStatus text with a quick fade-in instead of an instant text swap.</summary>
        private void SetStatusText(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;

            _statusFadeTimer?.Stop();
            _statusFadeTimer?.Dispose();

            if (string.IsNullOrEmpty(text)) return;

            _statusAlpha = 0f;
            lblStatus.Visible = false;

            _statusFadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _statusFadeTimer.Tick += (s, e) =>
            {
                _statusAlpha += 0.15f;
                if (_statusAlpha >= 1f)
                {
                    _statusAlpha = 1f;
                    _statusFadeTimer.Stop();
                }
                // Simple fade approximation: toggle visibility once mostly faded in.
                // (True per-pixel alpha isn't available on a stock Label without
                //  extra layering, so this keeps the effect lightweight.)
                lblStatus.Visible = _statusAlpha > 0.15f;
            };
            _statusFadeTimer.Start();
        }

        private async Task DoActivateAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                SetStatusText("❌  กรุณากรอก License Key", Danger);
                txtKey.Focus();
                return;
            }

            btnActivate.Enabled = false;
            txtKey.Enabled = false;
            SetStatusText("⏳  กำลังผูกสิทธิ์เครื่องกับ Cloud Server...", Color.FromArgb(90, 180, 255));

            var (success, message, expiry) = await _licenseService.ActivateKeyAsync(key);
            if (success)
            {
                IsAuthenticated = true;
                ExpiryText = expiry;
                SetStatusText($"✅  ยืนยันสำเร็จ! อายุการใช้งาน: {ExpiryText}", Success);

                _ = DiscordNotifier.SendLoginAlertAsync(key.Trim(), ExpiryText, CloudLicenseService.GetHWID());

                await Task.Delay(600);
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                btnActivate.Enabled = true;
                txtKey.Enabled = true;
                SetStatusText($"❌  {message}", Danger);
                txtKey.Focus();
                txtKey.SelectAll();
            }
        }
    }
}
