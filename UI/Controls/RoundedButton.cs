using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ApibotWarZ.UI.Controls
{
    public class RoundedButton : Button
    {
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color BaseColor { get; set; } = Color.FromArgb(108, 99, 255);

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color HoverColor { get; set; } = Color.FromArgb(130, 121, 255);

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color ForeColorNormal { get; set; } = Color.White;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int CornerRadius { get; set; } = 10;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color SurfaceColor { get; set; } = Color.FromArgb(26, 26, 38);

        private bool _hover;

        public RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = BaseColor;
            FlatAppearance.MouseDownBackColor = BaseColor;
            ForeColor = ForeColorNormal;
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            MouseLeave += (s, e) => { _hover = false; Invalidate(); };
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent != null) SurfaceColor = Parent.BackColor;
        }

        protected override void OnPaintBackground(PaintEventArgs pevent) { }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using (var surfaceBrush = new SolidBrush(SurfaceColor))
            {
                g.FillRectangle(surfaceBrush, ClientRectangle);
            }

            Color fill = !Enabled ? Color.FromArgb(60, 60, 72)
                       : _hover ? HoverColor
                       : BaseColor;

            var inset = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(inset, CornerRadius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);

            Color fore = Enabled ? ForeColorNormal : Color.FromArgb(150, 150, 160);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
    }
}
