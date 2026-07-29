using System;
using System.Drawing;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    public class AstroMessageBoxForm : Form
    {
        private Label lblMessage;
        private FlowLayoutPanel buttonPanel;
        
        public DialogResult Result { get; private set; } = DialogResult.None;

        public AstroMessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            this.Text = caption;
            this.Size = new Size(400, 200);
            this.MinimumSize = new Size(400, 150);
            this.MaximumSize = new Size(600, 800);
            this.AutoSize = true;
            this.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.BackColor = Color.FromArgb(11, 19, 38);
            this.ForeColor = Color.FromArgb(218, 226, 253);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.AutoSize = true;
            layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            layout.Dock = DockStyle.Fill;
            layout.RowCount = 2;
            layout.ColumnCount = 1;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));
            layout.Padding = new Padding(15);
            this.Controls.Add(layout);

            lblMessage = new Label();
            lblMessage.Text = text;
            lblMessage.Dock = DockStyle.Fill;
            lblMessage.TextAlign = ContentAlignment.MiddleLeft;
            lblMessage.Font = new Font("Segoe UI", 10f, FontStyle.Regular);
            lblMessage.AutoSize = true;
            lblMessage.MaximumSize = new Size(350, 0); // Allow text wrapping
            layout.Controls.Add(lblMessage, 0, 0);

            buttonPanel = new FlowLayoutPanel();
            buttonPanel.Dock = DockStyle.Fill;
            buttonPanel.FlowDirection = FlowDirection.RightToLeft;
            buttonPanel.WrapContents = false;
            layout.Controls.Add(buttonPanel, 0, 1);

            SetupButtons(buttons);
        }

        private void SetupButtons(MessageBoxButtons buttons)
        {
            switch (buttons)
            {
                case MessageBoxButtons.OK:
                    AddButton("OK", DialogResult.OK, true);
                    break;
                case MessageBoxButtons.OKCancel:
                    AddButton("Cancel", DialogResult.Cancel, false);
                    AddButton("OK", DialogResult.OK, true);
                    break;
                case MessageBoxButtons.YesNo:
                    AddButton("No", DialogResult.No, false);
                    AddButton("Yes", DialogResult.Yes, true);
                    break;
                case MessageBoxButtons.YesNoCancel:
                    AddButton("Cancel", DialogResult.Cancel, false);
                    AddButton("No", DialogResult.No, false);
                    AddButton("Yes", DialogResult.Yes, true);
                    break;
                case MessageBoxButtons.RetryCancel:
                    AddButton("Cancel", DialogResult.Cancel, false);
                    AddButton("Retry", DialogResult.Retry, true);
                    break;
                case MessageBoxButtons.AbortRetryIgnore:
                    AddButton("Ignore", DialogResult.Ignore, false);
                    AddButton("Retry", DialogResult.Retry, false);
                    AddButton("Abort", DialogResult.Abort, true);
                    break;
            }
        }

        private void AddButton(string text, DialogResult result, bool isPrimary)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Size = new Size(80, 32);
            btn.FlatStyle = FlatStyle.Flat;
            btn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            btn.Margin = new Padding(10, 0, 0, 0);
            
            if (isPrimary)
            {
                btn.BackColor = Color.FromArgb(0, 229, 255); // #00E5FF
                btn.ForeColor = Color.FromArgb(0, 54, 61); // #00363D
                btn.FlatAppearance.BorderColor = Color.FromArgb(0, 229, 255);
            }
            else
            {
                btn.BackColor = Color.FromArgb(45, 52, 73); // surface-variant
                btn.ForeColor = Color.FromArgb(218, 226, 253);
                btn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 90);
            }

            btn.FlatAppearance.BorderSize = 0;
            btn.BackColor = Color.Transparent;

            btn.Paint += (sender, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(Color.FromArgb(11, 19, 38)); // Match the form background
                
                System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
                int radius = 6;
                path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
                path.AddArc(btn.Width - 1 - radius * 2, 0, radius * 2, radius * 2, 270, 90);
                path.AddArc(btn.Width - 1 - radius * 2, btn.Height - 1 - radius * 2, radius * 2, radius * 2, 0, 90);
                path.AddArc(0, btn.Height - 1 - radius * 2, radius * 2, radius * 2, 90, 90);
                path.CloseFigure();

                Color bgColor = isPrimary ? Color.FromArgb(0, 229, 255) : Color.FromArgb(45, 52, 73);
                Color fgColor = isPrimary ? Color.FromArgb(0, 54, 61) : Color.FromArgb(218, 226, 253);

                using (SolidBrush brush = new SolidBrush(bgColor))
                {
                    e.Graphics.FillPath(brush, path);
                }
                
                TextRenderer.DrawText(e.Graphics, btn.Text, btn.Font, btn.ClientRectangle, fgColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            btn.Click += (sender, e) =>
            {
                this.Result = result;
                this.DialogResult = result;
                this.Close();
            };

            buttonPanel.Controls.Add(btn);
        }
    }
}
