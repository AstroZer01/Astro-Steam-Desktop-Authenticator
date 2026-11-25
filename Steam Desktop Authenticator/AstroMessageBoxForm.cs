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
        private readonly FlowLayoutPanel buttonPanel;
        private readonly CheckBox chkOption;
        private readonly Panel dividerPanel;
        private readonly Panel messageContainer;
        private readonly string primaryButtonText;
        private readonly string secondaryButtonText;
        private readonly string tertiaryButtonText;
        private readonly bool centerContent;
        private readonly bool useDestructivePrimary;

        public DialogResult Result { get; private set; } = DialogResult.None;
        public bool IsChecked => chkOption != null && chkOption.Checked;

        public AstroMessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, string checkboxText = null, string primaryButtonText = null, string secondaryButtonText = null, string tertiaryButtonText = null, bool centerContent = false)
        {
            this.primaryButtonText = primaryButtonText;
            this.secondaryButtonText = secondaryButtonText;
            this.tertiaryButtonText = tertiaryButtonText;
            this.centerContent = centerContent;
            this.useDestructivePrimary =
                (icon == MessageBoxIcon.Warning || icon == MessageBoxIcon.Error) &&
                buttons != MessageBoxButtons.OK;

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

            using (Font measureFont = new Font("Segoe UI", 9.5f, FontStyle.Regular))
            {
                Size singleLineSize = TextRenderer.MeasureText(
                    text ?? string.Empty,
                    measureFont,
                    new Size(MaxDialogClientWidth - 56, int.MaxValue),
                    TextFormatFlags.WordBreak);

                int targetClientWidth = Math.Max(
                    MinDialogClientWidth,
                    Math.Min(MaxDialogClientWidth, singleLineSize.Width + 68));

                int contentWidth = targetClientWidth - 40;

                lblMessage = new Label
                {
                    Text = text,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                    ForeColor = AstroTheme.OnSurface,
                    BackColor = Color.Transparent,
                    TextAlign = centerContent ? ContentAlignment.MiddleCenter : ContentAlignment.TopLeft,
                    AutoSize = true,
                    MaximumSize = new Size(centerContent ? contentWidth : contentWidth - 16, 0)
                };

                if (centerContent)
                {
                    lblMessage.Location = new Point(20, 18);
                    lblMessage.MinimumSize = new Size(contentWidth, 0);
                    Controls.Add(lblMessage);
                }
                else
                {
                    messageContainer = new Panel
                    {
                        Location = new Point(20, 18),
                        Width = contentWidth,
                        BackColor = Color.Transparent
                    };

                    Panel accentBar = new Panel
                    {
                        BackColor = ResolveAccentBarColor(icon),
                        Location = new Point(0, 2),
                        Width = 3
                    };

                    lblMessage.Location = new Point(14, 0);
                    Size msgSize = lblMessage.GetPreferredSize(new Size(contentWidth - 16, 0));
                    lblMessage.Size = msgSize;
                    accentBar.Height = Math.Max(18, msgSize.Height - 2);
                    messageContainer.Height = Math.Max(22, msgSize.Height);

                    messageContainer.Controls.Add(accentBar);
                    messageContainer.Controls.Add(lblMessage);
                    Controls.Add(messageContainer);
                }

                int nextTop = (centerContent ? lblMessage.Bottom : messageContainer.Bottom) + 14;

                if (hasCheckbox)
                {
                    chkOption = new CheckBox
                    {
                        Text = checkboxText,
                        AutoSize = true,
                        MaximumSize = new Size(contentWidth, 0),
                        Location = new Point(20, nextTop),
                        Font = new Font("Segoe UI", 8.75f, FontStyle.Regular)
                    };
                    AstroTheme.StyleCheckBox(chkOption);
                    chkOption.ForeColor = Color.FromArgb(184, 196, 212);
                    Controls.Add(chkOption);
                    nextTop = chkOption.Bottom + 12;
                }

                dividerPanel = new Panel
                {
                    BackColor = AstroTheme.Divider,
                    Location = new Point(20, nextTop),
                    Size = new Size(contentWidth, 1)
                };
                Controls.Add(dividerPanel);
                nextTop = dividerPanel.Bottom + 12;

                buttonPanel = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    AutoSize = false,
                    Location = new Point(20, nextTop),
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
                if (finalClientWidth != targetClientWidth)
                {
                    int newContentWidth = finalClientWidth - 40;
                    dividerPanel.Width = newContentWidth;
                    buttonPanel.Width = newContentWidth;
                    if (centerContent)
                    {
                        lblMessage.MaximumSize = new Size(newContentWidth, 0);
                        lblMessage.MinimumSize = new Size(newContentWidth, 0);
                    }
                    else
                    {
                        messageContainer.Width = newContentWidth;
                    }
                }

                if (centerContent)
                {
                    int totalBtnWidth = Math.Min(buttonPanel.Width, requiredButtonsWidth);
                    buttonPanel.Location = new Point(Math.Max(20, (finalClientWidth - totalBtnWidth) / 2), nextTop);
                    buttonPanel.Width = Math.Max(totalBtnWidth, 88);
                }

                int finalClientHeight = Math.Min(MaxDialogHeight, buttonPanel.Bottom + 16);
                ClientSize = new Size(finalClientWidth, finalClientHeight);
                MinimumSize = Size;
                MaximumSize = new Size(Math.Max(660, Width), MaxDialogHeight + 60);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            BringToFront();
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
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
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
                    AstroTheme.StyleDangerSolidButton(btn);
                else
                    AstroTheme.StylePrimaryButton(btn);
                AcceptButton = btn;
            }
            else
            {
                AstroTheme.StyleSurfaceButton(btn);
            }

            if (isCancel)
                CancelButton = btn;

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
