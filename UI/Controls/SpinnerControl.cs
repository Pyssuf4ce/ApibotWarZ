using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ApibotWarZ.UI.Controls
{
    public class SpinnerControl : Control
    {
        private System.Windows.Forms.Timer _timer = null!;
        private float _angle = 0f;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color SpinnerColor { get; set; } = Color.FromArgb(108, 99, 255);

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color SpinnerColorTail { get; set; } = Color.FromArgb(180, 121, 255); // brighter tip color for the gradient trail

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color TrailColor { get; set; } = Color.FromArgb(38, 43, 64);

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Thickness { get; set; } = 3;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float SweepAngle { get; set; } = 100f; // slightly longer arc looks smoother than 90°

        public SpinnerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
            this.DoubleBuffered = true;
            this.Size = new Size(32, 32);

            _timer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
            _timer.Tick += (s, e) =>
            {
                _angle = (_angle + 5f) % 360f; // slightly slower = feels smoother than a fast snap
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

            // Static faint trail ring.
            using (var trailPen = new Pen(TrailColor, Thickness))
            {
                g.DrawEllipse(trailPen, rect);
            }

            // ── Comet-tail arc: draw it as many thin segments with fading alpha
            //    to fake a smooth gradient along the stroke (GDI+ has no native
            //    "gradient along a path" for arcs). ──
            const int segments = 24;
            for (int i = 0; i < segments; i++)
            {
                float segStart = _angle + (SweepAngle * i / segments);
                float segSweep = (SweepAngle / segments) + 0.5f; // tiny overlap avoids gaps
                float t = i / (float)(segments - 1); // 0 = tail, 1 = leading edge

                Color segColor = LerpColor(SpinnerColor, SpinnerColorTail, t);
                int alpha = (int)(60 + 195 * t); // tail fades out, head is fully opaque

                using var arcPen = new Pen(Color.FromArgb(alpha, segColor), Thickness)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawArc(arcPen, rect, segStart, segSweep);
            }

            // Small bright dot at the leading edge for extra polish.
            double leadRad = (_angle + SweepAngle) * Math.PI / 180.0;
            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;
            float rx = rect.Width / 2f;
            float ry = rect.Height / 2f;
            float dotX = cx + rx * (float)Math.Cos(leadRad);
            float dotY = cy + ry * (float)Math.Sin(leadRad);
            float dotR = Thickness * 0.9f;
            using var dotBrush = new SolidBrush(SpinnerColorTail);
            g.FillEllipse(dotBrush, dotX - dotR, dotY - dotR, dotR * 2, dotR * 2);
        }

        private static Color LerpColor(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            int r = (int)(a.R + (b.R - a.R) * t);
            int g = (int)(a.G + (b.G - a.G) * t);
            int bl = (int)(a.B + (b.B - a.B) * t);
            return Color.FromArgb(255, r, g, bl);
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
