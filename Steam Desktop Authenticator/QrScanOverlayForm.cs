using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal sealed class QrScanOverlayForm : Form
    {
        private const int VisualSize = 350;
        private const int InstructionsHeight = 48;
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

            using (Pen borderPen = new Pen(Color.FromArgb(153, 153, 153), 3))
            {
                e.Graphics.DrawRectangle(borderPen, 2, 2, VisualSize - 5, VisualSize - 5);
            }

            DrawOutlinedInstruction(e.Graphics, "Press Right Ctrl to scan the QR code",
                new Rectangle(0, VisualSize + 2, VisualSize, 20));
            DrawOutlinedInstruction(e.Graphics, "Press Esc to cancel",
                new Rectangle(0, VisualSize + 22, VisualSize, 20));
        }

        private void DrawOutlinedInstruction(Graphics graphics, string text, Rectangle bounds)
        {
            using (Font boldFont = new Font(Font, FontStyle.Bold))
            using (StringFormat textFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
            using (Brush outlineBrush = new SolidBrush(Color.Black))
            using (Brush textBrush = new SolidBrush(Color.FromArgb(204, 204, 204)))
            {
                TextRenderingHint originalRenderingHint = graphics.TextRenderingHint;
                graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                foreach (Point offset in new[]
                {
                    new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1)
                })
                {
                    graphics.DrawString(text, boldFont, outlineBrush,
                        new Rectangle(bounds.X + offset.X, bounds.Y + offset.Y, bounds.Width, bounds.Height),
                        textFormat);
                }

                graphics.DrawString(text, boldFont, textBrush, bounds, textFormat);
                graphics.TextRenderingHint = originalRenderingHint;
            }
        }
    }
}
