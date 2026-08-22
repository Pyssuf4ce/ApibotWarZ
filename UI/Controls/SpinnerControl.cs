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
        public Color TrailColor { get; set; } = Color.FromArgb(38, 43, 64);

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Thickness { get; set; } = 3;

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
                g.DrawArc(arcPen, rect, _angle, 90f);
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
