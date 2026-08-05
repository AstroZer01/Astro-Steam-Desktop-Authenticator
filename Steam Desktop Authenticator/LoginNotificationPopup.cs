using System;
using System.Drawing;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal sealed class LoginNotificationPopup : Form
    {
        private readonly Timer closeTimer = new Timer { Interval = 10000 };

        public event EventHandler NotificationClicked;

        public LoginNotificationPopup(string title, string message, ToolTipIcon icon, string clickHint = "Click to open Login Actions")
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(23, 31, 51);
            ClientSize = new Size(360, 116);
            ControlBox = false;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            var accent = new Panel
            {
                BackColor = icon == ToolTipIcon.Warning || icon == ToolTipIcon.Error
                    ? Color.FromArgb(255, 84, 73)
                    : Color.FromArgb(78, 222, 163),
                Dock = DockStyle.Left,
                Width = 4
            };
            Controls.Add(accent);

            var titleLabel = new Label
            {
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(218, 226, 253),
                Location = new Point(20, 16),
                Size = new Size(310, 24),
                Text = title
            };
            Controls.Add(titleLabel);

            var messageLabel = new Label
            {
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = Color.FromArgb(186, 201, 204),
                Location = new Point(20, 43),
                Size = new Size(310, 48),
                Text = message
            };
            Controls.Add(messageLabel);

            var hintLabel = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 8F, FontStyle.Regular),
                ForeColor = Color.FromArgb(186, 201, 204),
                Location = new Point(20, 94),
                Text = clickHint
            };
            Controls.Add(hintLabel);

            var closeButton = new Button
            {
                AccessibleName = "Dismiss notification",
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular),
                ForeColor = Color.FromArgb(186, 201, 204),
                Location = new Point(330, 8),
                Size = new Size(22, 22),
                TabStop = true,
                Text = "×",
                UseVisualStyleBackColor = false
            };
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 255, 255, 255);
            closeButton.Click += (sender, args) => Close();
            Controls.Add(closeButton);

            Click += Notification_Click;
            foreach (Control control in new Control[] { accent, titleLabel, messageLabel, hintLabel })
                control.Click += Notification_Click;

            closeTimer.Tick += (sender, args) => Close();
            FormClosed += (sender, args) => closeTimer.Dispose();
        }

        public void ShowAtBottomRight(int stackedOffset)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16 - stackedOffset);
            Show();
            closeTimer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        private void Notification_Click(object sender, EventArgs e)
        {
            NotificationClicked?.Invoke(this, EventArgs.Empty);
            Close();
        }
    }
}
