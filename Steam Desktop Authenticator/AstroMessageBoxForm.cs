using System;
using System.Drawing;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    public class AstroMessageBoxForm : Form
    {
        private const int MinDialogClientWidth = 360;
        private const int MaxDialogClientWidth = 480;
        private const int MaxDialogHeight = 680;

        private readonly Label lblMessage;
        private readonly Panel messageScrollHost;
        private readonly FlowLayoutPanel buttonPanel;
        private readonly CheckBox chkOption;
        private readonly Panel dividerPanel;
        private readonly Panel messageContainer;
        private readonly string primaryButtonText;
        private readonly string secondaryButtonText;
        private readonly string tertiaryButtonText;
        private readonly bool centerContent;
        private readonly bool useDestructivePrimary;
        private Button initialFocusButton;

        public DialogResult Result { get; private set; } = DialogResult.None;
        public bool IsChecked => chkOption != null && chkOption.Checked;
        internal Button InitialFocusButton => initialFocusButton;
        internal FlowLayoutPanel ButtonPanelForTests => buttonPanel;
        internal bool ProcessDialogKeyForTests(Keys keyData) => ProcessDialogKey(keyData);

        public AstroMessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, string checkboxText = null, string primaryButtonText = null, string secondaryButtonText = null, string tertiaryButtonText = null, bool centerContent = false, bool? destructivePrimary = null)
        {
            this.primaryButtonText = primaryButtonText;
            this.secondaryButtonText = secondaryButtonText;
            this.tertiaryButtonText = tertiaryButtonText;
            this.centerContent = centerContent;
            // Treat Warning/Error YesNo, AbortRetryIgnore, and standard YesNoCancel (without custom safe
            // primary label like "Save Changes") as destructive so Enter/Escape resolve to the safe button.
            this.useDestructivePrimary = destructivePrimary ??
                ((icon == MessageBoxIcon.Warning || icon == MessageBoxIcon.Error) &&
                 (buttons == MessageBoxButtons.YesNo ||
                  buttons == MessageBoxButtons.AbortRetryIgnore ||
                  (buttons == MessageBoxButtons.YesNoCancel && string.IsNullOrWhiteSpace(primaryButtonText))));

            this.Text = caption;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.BackColor = AstroTheme.Background;
            this.ForeColor = AstroTheme.OnSurface;
            AstroTheme.ApplyDarkTitleBar(this);

            bool hasCheckbox = !string.IsNullOrEmpty(checkboxText);

            Size singleLineSize = TextRenderer.MeasureText(
                text ?? string.Empty,
                AstroTheme.FontBody,
                new Size(MaxDialogClientWidth - 56, int.MaxValue),
                TextFormatFlags.WordBreak);

            int targetClientWidth = Math.Max(
                MinDialogClientWidth,
                Math.Min(MaxDialogClientWidth, singleLineSize.Width + 68));

            int contentWidth = targetClientWidth - 40;

            lblMessage = new Label
            {
                Text = text,
                Font = AstroTheme.FontBody,
                ForeColor = AstroTheme.OnSurface,
                BackColor = Color.Transparent,
                TextAlign = centerContent ? ContentAlignment.MiddleCenter : ContentAlignment.TopLeft,
                AutoSize = true,
                MaximumSize = new Size(centerContent ? contentWidth : contentWidth - 16, 0)
            };

            messageScrollHost = new Panel
            {
                Location = new Point(20, 18),
                Width = contentWidth,
                BackColor = Color.Transparent,
                AutoScroll = false
            };

            int desiredMessageHeight;
            Panel accentBar = null;
            if (centerContent)
            {
                lblMessage.Location = new Point(0, 0);
                lblMessage.MinimumSize = new Size(contentWidth, 0);
                Size msgSize = lblMessage.GetPreferredSize(new Size(contentWidth, 0));
                lblMessage.Size = msgSize;
                desiredMessageHeight = Math.Max(22, msgSize.Height);
                messageScrollHost.Controls.Add(lblMessage);
            }
            else
            {
                messageContainer = new Panel
                {
                    Location = new Point(0, 0),
                    Width = contentWidth,
                    BackColor = Color.Transparent
                };

                accentBar = new Panel
                {
                    BackColor = ResolveAccentBarColor(icon),
                    Location = new Point(0, 2),
                    Width = 3
                };

                lblMessage.Location = new Point(14, 0);
                Size msgSize = lblMessage.GetPreferredSize(new Size(contentWidth - 16, 0));
                lblMessage.Size = msgSize;
                accentBar.Height = Math.Max(18, msgSize.Height - 2);
                desiredMessageHeight = Math.Max(22, msgSize.Height);
                messageContainer.Height = desiredMessageHeight;

                messageContainer.Controls.Add(accentBar);
                messageContainer.Controls.Add(lblMessage);
                messageScrollHost.Controls.Add(messageContainer);
            }

            Controls.Add(messageScrollHost);

            if (hasCheckbox)
            {
                chkOption = new CheckBox
                {
                    Text = checkboxText,
                    AutoSize = true,
                    MaximumSize = new Size(contentWidth, 0),
                    Font = AstroTheme.FontBodySmall
                };
                AstroTheme.StyleCheckBox(chkOption);
                chkOption.ForeColor = Color.FromArgb(184, 196, 212);
                Controls.Add(chkOption);
            }

            dividerPanel = new Panel
            {
                BackColor = AstroTheme.Divider,
                Size = new Size(contentWidth, 1)
            };
            Controls.Add(dividerPanel);

            buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = false,
                Size = new Size(contentWidth, 34),
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent
            };
            Controls.Add(buttonPanel);

            SetupButtons(buttons);

            // Expand dialog width cleanly if custom button labels need extra horizontal room.
            int requiredButtonsWidth = 0;
            foreach (Control control in buttonPanel.Controls)
                requiredButtonsWidth += control.Width + control.Margin.Horizontal;

            int finalClientWidth = Math.Max(targetClientWidth, Math.Min(640, requiredButtonsWidth + 40));
            int finalContentWidth = finalClientWidth - 40;
            if (finalClientWidth != targetClientWidth)
            {
                dividerPanel.Width = finalContentWidth;
                buttonPanel.Width = finalContentWidth;
                messageScrollHost.Width = finalContentWidth;
                if (centerContent)
                {
                    lblMessage.MaximumSize = new Size(finalContentWidth, 0);
                    lblMessage.MinimumSize = new Size(finalContentWidth, 0);
                    Size msgSize = lblMessage.GetPreferredSize(new Size(finalContentWidth, 0));
                    lblMessage.Size = msgSize;
                    desiredMessageHeight = Math.Max(22, msgSize.Height);
                }
                else
                {
                    messageContainer.Width = finalContentWidth;
                    lblMessage.MaximumSize = new Size(finalContentWidth - 16, 0);
                    Size msgSize = lblMessage.GetPreferredSize(new Size(finalContentWidth - 16, 0));
                    lblMessage.Size = msgSize;
                    accentBar.Height = Math.Max(18, msgSize.Height - 2);
                    desiredMessageHeight = Math.Max(22, msgSize.Height);
                    messageContainer.Height = desiredMessageHeight;
                }
                if (chkOption != null)
                    chkOption.MaximumSize = new Size(finalContentWidth, 0);
            }

            // Cap the scrollable message region so footer divider + buttons never clip past MaxDialogHeight.
            int checkboxHeight = chkOption != null ? chkOption.GetPreferredSize(new Size(finalContentWidth, 0)).Height + 12 : 0;
            int footerChromeHeight = 18 + 14 + checkboxHeight + 1 + 12 + buttonPanel.Height + 16;
            int maxMessageViewportHeight = Math.Max(80, MaxDialogHeight - footerChromeHeight);
            bool needsScroll = desiredMessageHeight > maxMessageViewportHeight;

            if (needsScroll)
            {
                int scrollTextWidth = Math.Max(120, finalContentWidth - SystemInformation.VerticalScrollBarWidth - 6);
                if (centerContent)
                {
                    lblMessage.MaximumSize = new Size(scrollTextWidth, 0);
                    lblMessage.MinimumSize = new Size(scrollTextWidth, 0);
                    Size msgSize = lblMessage.GetPreferredSize(new Size(scrollTextWidth, 0));
                    lblMessage.Size = msgSize;
                }
                else
                {
                    messageContainer.Width = scrollTextWidth;
                    lblMessage.MaximumSize = new Size(Math.Max(100, scrollTextWidth - 16), 0);
                    Size msgSize = lblMessage.GetPreferredSize(new Size(Math.Max(100, scrollTextWidth - 16), 0));
                    lblMessage.Size = msgSize;
                    accentBar.Height = Math.Max(18, msgSize.Height - 2);
                    messageContainer.Height = Math.Max(22, msgSize.Height);
                }
                messageScrollHost.AutoScroll = true;
                messageScrollHost.Height = maxMessageViewportHeight;
            }
            else
            {
                messageScrollHost.AutoScroll = false;
                messageScrollHost.Height = desiredMessageHeight;
            }

            int nextTop = messageScrollHost.Bottom + 14;
            if (chkOption != null)
            {
                chkOption.Location = new Point(20, nextTop);
                nextTop = chkOption.Bottom + 12;
            }

            dividerPanel.Location = new Point(20, nextTop);
            nextTop = dividerPanel.Bottom + 12;
            buttonPanel.Location = new Point(20, nextTop);

            if (centerContent)
            {
                int totalBtnWidth = Math.Min(buttonPanel.Width, requiredButtonsWidth);
                buttonPanel.Location = new Point(Math.Max(20, (finalClientWidth - totalBtnWidth) / 2), nextTop);
                buttonPanel.Width = Math.Max(totalBtnWidth, 88);
            }

            if (initialFocusButton != null)
                ActiveControl = initialFocusButton;

            int finalClientHeight = Math.Min(MaxDialogHeight, buttonPanel.Bottom + 16);
            ClientSize = new Size(finalClientWidth, finalClientHeight);
            MinimumSize = Size;
            MaximumSize = new Size(Math.Max(660, Width), MaxDialogHeight + 60);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            BringToFront();
            if (initialFocusButton != null && initialFocusButton.CanSelect)
                initialFocusButton.Select();
        }

        private static Color ResolveAccentBarColor(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Error:
                    return AstroTheme.ErrorAccent;
                case MessageBoxIcon.Warning:
                    return AstroTheme.Warning;
                default:
                    return AstroTheme.PrimaryAccent;
            }
        }

        private void SetupButtons(MessageBoxButtons buttons)
        {
            // FlowDirection.RightToLeft places the first added button on the far right,
            // so primary/confirm always sits cleanly on the right with Cancel/No to its left.
            switch (buttons)
            {
                case MessageBoxButtons.OK:
                    AddButton("OK", DialogResult.OK, isPrimary: true, isCancel: true);
                    break;
                case MessageBoxButtons.OKCancel:
                    AddButton(CustomLabelOrDefault(primaryButtonText, "OK"), DialogResult.OK, isPrimary: true);
                    AddButton(CustomLabelOrDefault(secondaryButtonText, "Cancel"), DialogResult.Cancel, isPrimary: false, isCancel: true);
                    break;
                case MessageBoxButtons.YesNo:
                    AddButton("Yes", DialogResult.Yes, isPrimary: true);
                    AddButton("No", DialogResult.No, isPrimary: false, isCancel: true);
                    break;
                case MessageBoxButtons.YesNoCancel:
                    AddButton(CustomLabelOrDefault(primaryButtonText, "Yes"), DialogResult.Yes, isPrimary: true);
                    AddButton(CustomLabelOrDefault(secondaryButtonText, "No"), DialogResult.No, isPrimary: false);
                    AddButton(CustomLabelOrDefault(tertiaryButtonText, "Cancel"), DialogResult.Cancel, isPrimary: false, isCancel: true);
                    break;
                case MessageBoxButtons.RetryCancel:
                    AddButton("Retry", DialogResult.Retry, isPrimary: true);
                    AddButton("Cancel", DialogResult.Cancel, isPrimary: false, isCancel: true);
                    break;
                case MessageBoxButtons.AbortRetryIgnore:
                    AddButton("Abort", DialogResult.Abort, isPrimary: true);
                    AddButton("Retry", DialogResult.Retry, isPrimary: false);
                    AddButton("Ignore", DialogResult.Ignore, isPrimary: false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "The dialog button set is not supported.");
            }
        }

        private static string CustomLabelOrDefault(string label, string fallback)
        {
            return String.IsNullOrWhiteSpace(label) ? fallback : label;
        }

        private void AddButton(string text, DialogResult result, bool isPrimary, bool isCancel = false)
        {
            Button btn = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Font = AstroTheme.FontButton,
                Cursor = Cursors.Hand
            };
            int buttonWidth = Math.Max(82, TextRenderer.MeasureText(text, btn.Font, Size.Empty, TextFormatFlags.SingleLine).Width + 26);
            btn.Size = new Size(buttonWidth, 32);
            btn.Margin = buttonPanel.Controls.Count == 0
                ? new Padding(0)
                : new Padding(0, 0, 8, 0);

            if (isPrimary)
            {
                if (useDestructivePrimary)
                {
                    AstroTheme.StyleDangerSolidButton(btn);
                }
                else
                {
                    AstroTheme.StylePrimaryButton(btn);
                    AcceptButton = btn;
                    initialFocusButton = btn;
                }
            }
            else
            {
                AstroTheme.StyleSurfaceButton(btn);
            }

            if (isCancel)
            {
                CancelButton = btn;
                if (useDestructivePrimary)
                {
                    AcceptButton = btn;
                    initialFocusButton = btn;
                }
            }

            // For destructive prompts without a dedicated cancel button (e.g., AbortRetryIgnore),
            // prefer the first non-destructive button as initial safe focus.
            if (useDestructivePrimary && !isPrimary && initialFocusButton == null)
            {
                AcceptButton = btn;
                initialFocusButton = btn;
            }

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
