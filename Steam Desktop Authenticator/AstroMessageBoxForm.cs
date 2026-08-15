using System;
using System.Drawing;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    public class AstroMessageBoxForm : Form
    {
        private Label lblMessage;
        private FlowLayoutPanel buttonPanel;
        private CheckBox chkOption;
        private readonly string primaryButtonText;
        private readonly string secondaryButtonText;
        
        public DialogResult Result { get; private set; } = DialogResult.None;
        public bool IsChecked => chkOption != null && chkOption.Checked;

        public AstroMessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, string checkboxText = null, string primaryButtonText = null, string secondaryButtonText = null)
        {
            this.primaryButtonText = primaryButtonText;
            this.secondaryButtonText = secondaryButtonText;
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
            this.ShowInTaskbar = true;
            this.BackColor = AstroTheme.Background;
            this.ForeColor = AstroTheme.OnSurface;
            AstroTheme.ApplyDarkTitleBar(this);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.AutoSize = true;
            layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            layout.Dock = DockStyle.Fill;
            layout.RowCount = string.IsNullOrEmpty(checkboxText) ? 2 : 3;
            layout.ColumnCount = 1;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            
            if (!string.IsNullOrEmpty(checkboxText))
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            
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

            int currentRow = 1;

            if (!string.IsNullOrEmpty(checkboxText))
            {
                chkOption = new CheckBox();
                chkOption.Text = checkboxText;
                chkOption.AutoSize = true;
                chkOption.Dock = DockStyle.Left;
                chkOption.Margin = new Padding(10, 10, 0, 0);
                chkOption.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
                layout.Controls.Add(chkOption, 0, currentRow);
                currentRow++;
            }

            buttonPanel = new FlowLayoutPanel();
            buttonPanel.Dock = DockStyle.Fill;
            buttonPanel.FlowDirection = FlowDirection.RightToLeft;
            buttonPanel.WrapContents = false;
            buttonPanel.AutoScroll = true;
            layout.Controls.Add(buttonPanel, 0, currentRow);

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
                    AddButton(secondaryButtonText ?? "Cancel", DialogResult.Cancel, false);
                    AddButton(primaryButtonText ?? "OK", DialogResult.OK, true);
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
            btn.FlatStyle = FlatStyle.Flat;
            btn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            int buttonWidth = Math.Max(80, TextRenderer.MeasureText(text, btn.Font, Size.Empty, TextFormatFlags.SingleLine).Width + 24);
            btn.Size = new Size(buttonWidth, 32);
            btn.Cursor = Cursors.Hand;
            btn.Margin = new Padding(10, 0, 0, 0);
            
            if (isPrimary)
            {
                AstroTheme.StylePrimaryButton(btn);
            }
            else
            {
                AstroTheme.StyleSecondaryButton(btn);
            }
            btn.Click += (sender, e) =>
            {
                this.Result = result;
                this.DialogResult = result;
                this.Close();
            };

            buttonPanel.Controls.Add(btn);
            EnsureButtonPanelWidth();
        }

        private void EnsureButtonPanelWidth()
        {
            int requiredPanelWidth = 0;
            foreach (Control control in buttonPanel.Controls)
                requiredPanelWidth += control.Width + control.Margin.Horizontal;

            int requiredDialogWidth = Math.Min(MaximumSize.Width, Math.Max(Width, requiredPanelWidth + 50));
            if (Width < requiredDialogWidth)
                Width = requiredDialogWidth;

            buttonPanel.AutoScroll = requiredPanelWidth + 30 > ClientSize.Width;
        }
    }
}
