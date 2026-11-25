using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal sealed class QrScanOverlayForm : Form
    {
        private const int VisualSize = 350;
        private const int InstructionsHeight = 52;
        private readonly Timer followCursorTimer = new Timer { Interval = 16 };

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        public QrScanOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Size = new Size(VisualSize, VisualSize + InstructionsHeight);
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            followCursorTimer.Tick += (sender, args) => FollowCursor();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExTransparent = 0x00000020;
                const int WsExNoActivate = 0x08000000;

                CreateParams createParams = base.CreateParams;
                createParams.ExStyle |= WsExTransparent | WsExNoActivate;
                return createParams;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void FollowCursor()
        {
            if (GetCursorPos(out Point cursorPosition))
            {
                Location = new Point(
                    cursorPosition.X - VisualSize / 2,
                    cursorPosition.Y - VisualSize / 2);
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (Visible)
            {
                FollowCursor();
                followCursorTimer.Start();
            }
            else
            {
                followCursorTimer.Stop();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                followCursorTimer.Dispose();

            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message message)
        {
            const int WmNcHitTest = 0x0084;
            const int HtTransparent = -1;

            if (message.Msg == WmNcHitTest)
            {
                message.Result = new IntPtr(HtTransparent);
                return;
            }

            base.WndProc(ref message);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            // Strict non-antialiased geometry avoids blending against TransparencyKey (Magenta).
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;

            Color darkBacking = Color.FromArgb(9, 14, 25);     // #090E19
            Color cyanAccent = AstroTheme.PrimaryAccent;        // #4CC3D9
            Color cyanLight = AstroTheme.PrimaryLight;          // #85D6E6
            Color pillBg = Color.FromArgb(11, 17, 30);          // #0B111E
            Color pillBorder = AstroTheme.OutlineVariant;       // #334155

            int left = 3;
            int top = 3;
            int right = VisualSize - 4;
            int bottom = VisualSize - 4;
            int frameWidth = right - left;
            int frameHeight = bottom - top;

            // 1. Exact 350x350 capture perimeter: dark hairline + dashed glacier cyan (zero interior occlusion).
            using (Pen backingPen = new Pen(darkBacking, 3f))
            {
                g.DrawRectangle(backingPen, left, top, frameWidth, frameHeight);
            }
            using (Pen dashedPen = new Pen(cyanAccent, 1f))
            {
                dashedPen.DashStyle = DashStyle.Custom;
                dashedPen.DashPattern = new[] { 6f, 5f };
                g.DrawRectangle(dashedPen, left, top, frameWidth, frameHeight);
            }

            // 2. Outer-edge midpoint alignment ticks (strictly on the perimeter, never inside the QR area).
            int midX = VisualSize / 2;
            int midY = VisualSize / 2;
            DrawTick(g, darkBacking, cyanAccent, new Rectangle(midX - 6, top - 1, 12, 3));
            DrawTick(g, darkBacking, cyanAccent, new Rectangle(midX - 6, bottom - 1, 12, 3));
            DrawTick(g, darkBacking, cyanAccent, new Rectangle(left - 1, midY - 6, 3, 12));
            DrawTick(g, darkBacking, cyanAccent, new Rectangle(right - 1, midY - 6, 3, 12));

            // 3. Bold 24px corner L-brackets with 1px dark outline for contrast over light & dark Steam UIs.
            const int arm = 24;
            const int thick = 3;
            DrawCornerBracket(g, darkBacking, cyanAccent, 1, 1, arm, thick, 1, 1);
            DrawCornerBracket(g, darkBacking, cyanAccent, VisualSize - 2, 1, arm, thick, -1, 1);
            DrawCornerBracket(g, darkBacking, cyanAccent, 1, VisualSize - 2, arm, thick, 1, -1);
            DrawCornerBracket(g, darkBacking, cyanAccent, VisualSize - 2, VisualSize - 2, arm, thick, -1, -1);

            // 4. Solid dark instruction pill below the capture frame (opaque bg = crisp text, zero magenta halo).
            const int pillWidth = 282;
            const int pillHeight = 32;
            Rectangle pillRect = new Rectangle((VisualSize - pillWidth) / 2, VisualSize + 10, pillWidth, pillHeight);
            using (SolidBrush bgBrush = new SolidBrush(pillBg))
            using (Pen borderPen = new Pen(pillBorder, 1f))
            {
                g.FillRectangle(bgBrush, pillRect);
                g.DrawRectangle(borderPen, pillRect.X, pillRect.Y, pillRect.Width - 1, pillRect.Height - 1);
            }

            DrawInstructionPillText(g, pillRect, pillBg, cyanLight, AstroTheme.OnSurfaceVariant);
        }

        private static void DrawTick(Graphics g, Color backing, Color fill, Rectangle rect)
        {
            using (SolidBrush backBrush = new SolidBrush(backing))
            using (SolidBrush fillBrush = new SolidBrush(fill))
            {
                g.FillRectangle(backBrush, rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2);
                g.FillRectangle(fillBrush, rect);
            }
        }

        private static void DrawCornerBracket(Graphics g, Color backing, Color fill, int cornerX, int cornerY, int armLength, int thickness, int dirX, int dirY)
        {
            int hX = dirX > 0 ? cornerX : cornerX - armLength + 1;
            int hY = dirY > 0 ? cornerY : cornerY - thickness + 1;
            Rectangle horizontal = new Rectangle(hX, hY, armLength, thickness);

            int vX = dirX > 0 ? cornerX : cornerX - thickness + 1;
            int vY = dirY > 0 ? cornerY : cornerY - armLength + 1;
            Rectangle vertical = new Rectangle(vX, vY, thickness, armLength);

            using (SolidBrush backBrush = new SolidBrush(backing))
            using (SolidBrush fillBrush = new SolidBrush(fill))
            {
                g.FillRectangle(backBrush, horizontal.X - 1, horizontal.Y - 1, horizontal.Width + 2, horizontal.Height + 2);
                g.FillRectangle(backBrush, vertical.X - 1, vertical.Y - 1, vertical.Width + 2, vertical.Height + 2);
                g.FillRectangle(fillBrush, horizontal);
                g.FillRectangle(fillBrush, vertical);
            }
        }

        private static void DrawInstructionPillText(Graphics g, Rectangle pillRect, Color pillBg, Color keyColor, Color mutedColor)
        {
            var segments = new (string Text, bool Bold, Color Color)[]
            {
                ("Right Ctrl", true, keyColor),
                (" to scan   ·   ", false, mutedColor),
                ("Esc", true, keyColor),
                (" to cancel", false, mutedColor)
            };

            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

            using (Font regularFont = new Font("Segoe UI", 8.5f, FontStyle.Regular))
            using (Font boldFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            {
                int totalWidth = 0;
                int[] widths = new int[segments.Length];
                for (int i = 0; i < segments.Length; i++)
                {
                    Font f = segments[i].Bold ? boldFont : regularFont;
                    widths[i] = TextRenderer.MeasureText(g, segments[i].Text, f, Size.Empty, flags).Width;
                    totalWidth += widths[i];
                }

                int cursorX = pillRect.X + Math.Max(8, (pillRect.Width - totalWidth) / 2);
                for (int i = 0; i < segments.Length; i++)
                {
                    Font f = segments[i].Bold ? boldFont : regularFont;
                    Rectangle segRect = new Rectangle(cursorX, pillRect.Y, widths[i] + 2, pillRect.Height);
                    TextRenderer.DrawText(g, segments[i].Text, f, segRect, segments[i].Color, pillBg, flags);
                    cursorX += widths[i];
                }
            }
        }
    }
}
