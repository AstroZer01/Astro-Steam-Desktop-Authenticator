using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal sealed class LoginNotificationPopup : Form
    {
        private const int LifetimeMs = 10000;
        private readonly Timer countdownTimer = new Timer { Interval = 100 };
        private readonly Stopwatch lifetimeStopwatch = new Stopwatch();
        private readonly Color accentColor;
        private readonly Label countdownLabel;
        private float progressFraction = 1f;

        public event EventHandler NotificationClicked;

        public LoginNotificationPopup(string title, string message, ToolTipIcon icon, string clickHint = "Click to open Login Actions")
        {
            bool isAlert = icon == ToolTipIcon.Warning || icon == ToolTipIcon.Error;
            accentColor = isAlert ? AstroTheme.ErrorAccent : AstroTheme.PrimaryAccent;

            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = AstroTheme.SurfaceContainer;
            ClientSize = new Size(368, 122);
            ControlBox = false;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            var accent = new Panel
            {
                BackColor = accentColor,
                Dock = DockStyle.Left,
                Width = 4
            };
            Controls.Add(accent);

            var titleLabel = new Label
            {
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = isAlert ? AstroTheme.Error : Color.FromArgb(243, 246, 251),
                Location = new Point(18, 14),
                Size = new Size(310, 22),
                Text = title
            };
            Controls.Add(titleLabel);

            var messageLabel = new Label
            {
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.75F, FontStyle.Regular),
                ForeColor = Color.FromArgb(184, 196, 212),
                Location = new Point(18, 39),
                Size = new Size(326, 46),
                Text = message
            };
            Controls.Add(messageLabel);

            string formattedHint = string.IsNullOrWhiteSpace(clickHint)
                ? string.Empty
                : (clickHint.TrimEnd().EndsWith("→", StringComparison.Ordinal) ? clickHint : clickHint + " →");

            var hintLabel = new Label
            {
                AutoSize = true,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = isAlert ? AstroTheme.Error : AstroTheme.PrimaryLight,
                Location = new Point(18, 92),
                Text = formattedHint
            };
            Controls.Add(hintLabel);

            countdownLabel = new Label
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = new Font("Consolas", 7.5F, FontStyle.Regular),
                ForeColor = AstroTheme.Outline,
                Location = new Point(314, 92),
                Size = new Size(38, 18),
                Text = "10s",
                TextAlign = ContentAlignment.MiddleRight
            };
            Controls.Add(countdownLabel);

            var closeButton = new Button
            {
                AccessibleName = "Dismiss notification",
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular),
                ForeColor = AstroTheme.OnSurfaceVariant,
                Location = new Point(336, 8),
                Size = new Size(24, 24),
                TabStop = true,
                Text = "×",
                UseVisualStyleBackColor = false
            };
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.FlatAppearance.MouseOverBackColor = AstroTheme.SurfaceContainerHigh;
            closeButton.Click += (sender, args) => Close();
            Controls.Add(closeButton);

            Cursor = Cursors.Hand;
            Click += Notification_Click;
            foreach (Control control in new Control[] { accent, titleLabel, messageLabel, hintLabel, countdownLabel })
            {
                control.Cursor = Cursors.Hand;
                control.Click += Notification_Click;
            }

            countdownTimer.Tick += CountdownTimer_Tick;
            FormClosed += (sender, args) =>
            {
                countdownTimer.Stop();
                countdownTimer.Dispose();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Crisp outer 1px border
            using (Pen borderPen = new Pen(Color.FromArgb(46, 59, 82), 1f))
            {
                e.Graphics.DrawRectangle(borderPen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }

            // Bottom countdown track + animated bar
            const int barHeight = 3;
            int barX = 4;
            int barY = ClientSize.Height - barHeight - 1;
            int totalWidth = Math.Max(0, ClientSize.Width - barX - 1);

            using (SolidBrush trackBrush = new SolidBrush(AstroTheme.SurfaceContainerLow))
            {
                e.Graphics.FillRectangle(trackBrush, barX, barY, totalWidth, barHeight);
            }

            int fillWidth = (int)Math.Round(totalWidth * Math.Max(0f, Math.Min(1f, progressFraction)));
            if (fillWidth > 0)
            {
                using (SolidBrush fillBrush = new SolidBrush(accentColor))
                {
                    e.Graphics.FillRectangle(fillBrush, barX, barY, fillWidth, barHeight);
                }
            }
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            double elapsed = lifetimeStopwatch.Elapsed.TotalMilliseconds;
            if (elapsed >= LifetimeMs)
            {
                Close();
                return;
            }

            double remaining = LifetimeMs - elapsed;
            progressFraction = (float)(remaining / LifetimeMs);
            int remainingSec = Math.Max(1, (int)Math.Ceiling(remaining / 1000.0));
            string nextLabel = remainingSec + "s";
            if (!string.Equals(countdownLabel.Text, nextLabel, StringComparison.Ordinal))
                countdownLabel.Text = nextLabel;

            Invalidate(new Rectangle(4, ClientSize.Height - 6, ClientSize.Width - 4, 6));
        }

        public void ShowAtBottomRight(int stackedOffset)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16 - stackedOffset);
            Show();
            lifetimeStopwatch.Restart();
            countdownTimer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        private void Notification_Click(object sender, EventArgs e)
        {
            NotificationClicked?.Invoke(this, EventArgs.Empty);
            Close();
        }
    }
}
