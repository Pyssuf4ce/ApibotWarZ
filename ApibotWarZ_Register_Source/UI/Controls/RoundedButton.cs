using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ApibotWarZ.UI.Controls
{
    public class RoundedButton : Button
    {
        public Color BaseColor { get; set; } = Color.FromArgb(108, 99, 255);
        public Color HoverColor { get; set; } = Color.FromArgb(130, 121, 255);
        public Color ForeColorNormal { get; set; } = Color.White;
        public Color BorderColor { get; set; } = Color.FromArgb(42, 48, 72);
        public Color DisabledBaseColor { get; set; } = Color.FromArgb(24, 27, 40);
        public Color DisabledForeColor { get; set; } = Color.FromArgb(90, 96, 120);
        public Color DisabledBorderColor { get; set; } = Color.FromArgb(34, 38, 56);
        public int CornerRadius { get; set; } = 8;
        public bool EnableBorder { get; set; } = true;

        private bool _hover;
        private bool _pressed;

        private float _hoverProgress;     // 0 = idle, 1 = fully hovered
        private float _pressProgress;     // 0 = idle, 1 = fully pressed
        private readonly System.Windows.Forms.Timer _animTimer;
        private const float AnimStep = 0.25f;

        public RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            ForeColor = ForeColorNormal;
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);

            Padding = new Padding(0);

            _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animTimer.Tick += (s, e) => StepAnimation();

            MouseEnter += (s, e) => { if (Enabled) { _hover = true; EnsureAnimRunning(); } };
            MouseLeave += (s, e) => { _hover = false; _pressed = false; EnsureAnimRunning(); };
            MouseDown += (s, e) => { if (Enabled && e.Button == MouseButtons.Left) { _pressed = true; EnsureAnimRunning(); } };
            MouseUp += (s, e) => { _pressed = false; EnsureAnimRunning(); };
        }

        protected override bool ShowFocusCues => false;

        private void EnsureAnimRunning()
        {
            if (!_animTimer.Enabled) _animTimer.Start();
        }

        private void StepAnimation()
        {
            float hoverTarget = (_hover && Enabled) ? 1f : 0f;
            float pressTarget = (_pressed && Enabled) ? 1f : 0f;

            _hoverProgress = Lerp(_hoverProgress, hoverTarget, AnimStep);
            _pressProgress = Lerp(_pressProgress, pressTarget, AnimStep);

            Invalidate();

            bool settled = Math.Abs(_hoverProgress - hoverTarget) < 0.01f &&
                            Math.Abs(_pressProgress - pressTarget) < 0.01f;
            if (settled)
            {
                _hoverProgress = hoverTarget;
                _pressProgress = pressTarget;
                _animTimer.Stop();
                Invalidate();
            }
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

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            // Do not paint default background to prevent flickering, we fill clean in OnPaint
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            // 1. Clear background completely with parent color to avoid any corner bleeding/artifacts
            Color parentBg = (Parent != null && Parent.BackColor != Color.Transparent) 
                ? Parent.BackColor 
                : Color.FromArgb(18, 21, 32);

            using (var bgBrush = new SolidBrush(parentBg))
            {
                g.FillRectangle(bgBrush, ClientRectangle);
            }

            int pressOffset = (int)Math.Round(_pressProgress * 1.5f);
            var rect = new Rectangle(0, pressOffset, Width - 1, Height - 1 - pressOffset);

            if (rect.Width <= 2 || rect.Height <= 2) return;

            using var path = RoundedRect(rect, CornerRadius);

            if (!Enabled)
            {
                // Disabled State: Clean flat dark surface, crisp muted border
                using (var disabledBrush = new SolidBrush(DisabledBaseColor))
                {
                    g.FillPath(disabledBrush, path);
                }

                if (EnableBorder)
                {
                    using var disabledPen = new Pen(DisabledBorderColor, 1f);
                    g.DrawPath(disabledPen, path);
                }

                var disabledTextRect = new Rectangle(0, 0, Width, Height);
                TextRenderer.DrawText(g, Text, Font, disabledTextRect, DisabledForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
                return;
            }

            // Enabled State: Smooth transition
            Color fill = LerpColor(BaseColor, HoverColor, _hoverProgress);

            if (_pressProgress > 0f)
            {
                fill = LerpColor(fill, Color.FromArgb(
                    Math.Max(fill.R - 20, 0),
                    Math.Max(fill.G - 20, 0),
                    Math.Max(fill.B - 20, 0)), _pressProgress);
            }

            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            // Subtle border
            if (EnableBorder)
            {
                Color curBorder = LerpColor(BorderColor, Color.FromArgb(Math.Min(BorderColor.R + 45, 255), Math.Min(BorderColor.G + 45, 255), Math.Min(BorderColor.B + 45, 255)), _hoverProgress);
                using var borderPen = new Pen(curBorder, 1f);
                g.DrawPath(borderPen, path);
            }

            // Centered crisp text
            var textRect = new Rectangle(0, pressOffset, Width, Height - pressOffset);
            TextRenderer.DrawText(g, Text, Font, textRect, ForeColorNormal,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animTimer?.Stop();
                _animTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
