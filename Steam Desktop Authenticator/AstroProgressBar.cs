using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    /// <summary>
    /// A custom-drawn progress bar that supports gradient fills 
    /// matching the Astro dark theme design system.
    /// Guaranteed to render correctly regardless of Windows theme settings.
    /// </summary>
    public class AstroProgressBar : Control
    {
        private int _minimum = 0;
        private int _maximum = 30;
        private int _value = 30;

        public int Minimum
        {
            get => _minimum;
            set { _minimum = value; Invalidate(); }
        }

        public int Maximum
        {
            get => _maximum;
            set { _maximum = value; Invalidate(); }
        }

        public int Value
        {
            get => _value;
            set
            {
                _value = Math.Max(_minimum, Math.Min(_maximum, value));
                Invalidate();
            }
        }

        /// <summary>
        /// The color used for the filled portion of the progress bar (start of gradient).
        /// Defaults to AstroTheme.SecondaryContainer (#00A572).
        /// </summary>
        public Color BarColorStart { get; set; } = AstroTheme.SecondaryContainer;

        /// <summary>
        /// The color used for the filled portion of the progress bar (end of gradient).
        /// Defaults to AstroTheme.Secondary (#4EDEA3).
        /// </summary>
        public Color BarColorEnd { get; set; } = AstroTheme.Secondary;

        /// <summary>
        /// The background (track) color of the progress bar.
        /// Defaults to AstroTheme.SurfaceVariant (#2D3449).
        /// </summary>
        public Color TrackColor { get; set; } = AstroTheme.SurfaceVariant;

        /// <summary>
        /// The border color of the progress bar.
        /// Defaults to AstroTheme.OutlineVariant.
        /// </summary>
        public Color BorderColor { get; set; } = AstroTheme.OutlineVariant;

        /// <summary>
        /// Corner radius for the rounded rectangle.
        /// </summary>
        public int CornerRadius { get; set; } = 8;

        public AstroProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 19;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);

            // Draw track (background)
            using (GraphicsPath trackPath = CreateRoundedRect(rect, CornerRadius))
            using (SolidBrush trackBrush = new SolidBrush(TrackColor))
            {
                g.FillPath(trackBrush, trackPath);
            }

            // Draw filled portion
            if (_maximum > _minimum && _value > _minimum)
            {
                float fraction = (float)(_value - _minimum) / (_maximum - _minimum);
                int barWidth = (int)(rect.Width * fraction);

                if (barWidth > 0)
                {
                    Rectangle barRect = new Rectangle(rect.X, rect.Y, barWidth, rect.Height);

                    using (GraphicsPath barPath = CreateRoundedRect(barRect, CornerRadius))
                    using (LinearGradientBrush barBrush = new LinearGradientBrush(
                        barRect, BarColorStart, BarColorEnd, LinearGradientMode.Horizontal))
                    {
                        g.FillPath(barBrush, barPath);
                    }
                }
            }

            // Draw border
            using (GraphicsPath borderPath = CreateRoundedRect(rect, CornerRadius))
            using (Pen borderPen = new Pen(BorderColor, 1f))
            {
                g.DrawPath(borderPen, borderPath);
            }
        }

        /// <summary>
        /// Creates a rounded rectangle GraphicsPath.
        /// </summary>
        private static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;

            if (diameter > rect.Height) diameter = rect.Height;
            if (diameter > rect.Width) diameter = rect.Width;

            Rectangle arcRect = new Rectangle(rect.Location, new Size(diameter, diameter));

            // Top left
            path.AddArc(arcRect, 180, 90);

            // Top right
            arcRect.X = rect.Right - diameter;
            path.AddArc(arcRect, 270, 90);

            // Bottom right
            arcRect.Y = rect.Bottom - diameter;
            path.AddArc(arcRect, 0, 90);

            // Bottom left
            arcRect.X = rect.Left;
            path.AddArc(arcRect, 90, 90);

            path.CloseFigure();
            return path;
        }
    }
}
