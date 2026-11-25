using SteamAuth;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    public sealed class RecoveryCodeForm : Form
    {
        private const int ContinueDelaySeconds = 60;
        private readonly SteamGuardAccount account;
        private readonly bool requireBackupBeforeContinue;
        private readonly Timer continueTimer;
        private readonly Stopwatch continueStopwatch = new Stopwatch();
        private Button continueButton;
        private Label statusLabel;
        private Label recoveryCodeLabel;
        private Panel recoveryCodeWell;
        private Label recoveryCodeCaptionLabel;
        private Label recoveryCodeValueLabel;
        private Panel warningCallout;
        private Label warningBodyLabel;
        private Panel footerDivider;
        private Button downloadButton;
        private bool continuationAllowed;

        public RecoveryCodeForm(SteamGuardAccount account, string statusMessage, bool requireBackupBeforeContinue = false)
        {
            this.account = account ?? throw new ArgumentNullException(nameof(account));
            if (String.IsNullOrWhiteSpace(this.account.RevocationCode))
                throw new ArgumentException("The account does not have a Steam Guard recovery code.", nameof(account));
            this.requireBackupBeforeContinue = requireBackupBeforeContinue;

            Text = "Steam Guard Recovery Code";
            MinimumSize = new Size(504, 372);
            ClientSize = new Size(504, 372);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = !requireBackupBeforeContinue;
            ShowInTaskbar = true;
            BackColor = AstroTheme.Background;
            ForeColor = AstroTheme.OnSurface;
            AstroTheme.ApplyDarkTitleBar(this);

            AutoScroll = true;
            statusLabel = new Label
            {
                Text = statusMessage,
                AutoSize = true,
                Location = new Point(22, 20),
                MaximumSize = new Size(460, 0),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = AstroTheme.OnSurface,
                BackColor = Color.Transparent
            };

            recoveryCodeLabel = new Label
            {
                Text = "Account: " + account.AccountName,
                AutoSize = true,
                Location = new Point(22, 54),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = AstroTheme.OnSurfaceVariant,
                BackColor = Color.Transparent
            };

            // Calm inset read-only display well (clearly non-editable, crisp white monospace)
            recoveryCodeWell = new Panel
            {
                Location = new Point(22, 80),
                Size = new Size(460, 74),
                BackColor = Color.FromArgb(10, 16, 29)
            };
            recoveryCodeWell.Paint += (sender, e) =>
            {
                using (Pen borderPen = new Pen(AstroTheme.OutlineVariant, 1f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, recoveryCodeWell.Width - 1, recoveryCodeWell.Height - 1);
                }
            };

            recoveryCodeCaptionLabel = new Label
            {
                Text = "READ-ONLY REVOCATION CODE",
                AutoSize = false,
                Location = new Point(12, 10),
                Size = new Size(436, 16),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = AstroTheme.Outline,
                BackColor = Color.Transparent
            };

            recoveryCodeValueLabel = new Label
            {
                Text = string.Join(" ", account.RevocationCode.Trim().ToCharArray()),
                AutoSize = false,
                Location = new Point(12, 28),
                Size = new Size(436, 36),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 17f, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 248, 252),
                BackColor = Color.Transparent
            };
            recoveryCodeWell.Controls.Add(recoveryCodeCaptionLabel);
            recoveryCodeWell.Controls.Add(recoveryCodeValueLabel);

            string calloutText = "Keep this code private. Steam may require it to recover your account or move Steam Guard to another device.\n\n" +
                (requireBackupBeforeContinue
                    ? "For safety, download the recovery code (.txt) to unlock Continue immediately, or wait 60 seconds."
                    : "Store a backup in a secure offline location.");

            warningCallout = new Panel
            {
                Location = new Point(22, 168),
                Width = 460,
                BackColor = AstroTheme.WarningSurface,
                Padding = new Padding(14, 10, 12, 10)
            };
            Panel warningAccent = new Panel
            {
                BackColor = AstroTheme.Warning,
                Dock = DockStyle.Left,
                Width = 4
            };
            warningBodyLabel = new Label
            {
                Text = calloutText,
                AutoSize = true,
                MaximumSize = new Size(426, 0),
                Font = new Font("Segoe UI", 8.85f, FontStyle.Regular),
                ForeColor = AstroTheme.WarningSoft,
                BackColor = Color.Transparent,
                Location = new Point(14, 10)
            };
            warningCallout.Controls.Add(warningBodyLabel);
            warningCallout.Controls.Add(warningAccent);
            warningCallout.Paint += (sender, e) =>
            {
                using (Pen borderPen = new Pen(Color.FromArgb(62, 54, 42), 1f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, warningCallout.Width - 1, warningCallout.Height - 1);
                }
            };

            footerDivider = AstroTheme.CreateDivider(460);

            downloadButton = new Button
            {
                Text = "Download Code (.txt)",
                Location = new Point(22, 286),
                Size = new Size(184, 34)
            };
            AstroTheme.StyleOutlineAccentButton(downloadButton);
            downloadButton.Click += DownloadRecoveryCode_Click;

            continueButton = new Button
            {
                Text = requireBackupBeforeContinue ? $"Continue ({ContinueDelaySeconds}s)" : "Continue",
                Location = new Point(312, 286),
                Size = new Size(170, 34),
                Enabled = !requireBackupBeforeContinue,
                DialogResult = DialogResult.OK
            };
            if (requireBackupBeforeContinue)
                AstroTheme.StyleDisabledGlassButton(continueButton);
            else
                AstroTheme.StylePrimaryButton(continueButton);

            Controls.Add(statusLabel);
            Controls.Add(recoveryCodeLabel);
            Controls.Add(recoveryCodeWell);
            Controls.Add(warningCallout);
            Controls.Add(footerDivider);
            Controls.Add(downloadButton);
            Controls.Add(continueButton);
            AcceptButton = continueButton;
            LayoutRecoveryControls();

            continuationAllowed = !requireBackupBeforeContinue;
            continueTimer = new Timer { Interval = 250 };
            continueTimer.Tick += ContinueTimer_Tick;
            Shown += RecoveryCodeForm_Shown;
            Activated += RecoveryCodeForm_Activated;
            FormClosing += RecoveryCodeForm_FormClosing;
            FormClosed += RecoveryCodeForm_FormClosed;
            Resize += (sender, args) => LayoutRecoveryControls();
        }

        private void LayoutRecoveryControls()
        {
            int contentWidth = Math.Max(320, ClientSize.Width - 44);
            statusLabel.MaximumSize = new Size(contentWidth, 0);
            warningBodyLabel.MaximumSize = new Size(Math.Max(260, contentWidth - 28), 0);

            int nextTop = 20;
            statusLabel.Location = new Point(22, nextTop);
            nextTop = statusLabel.Bottom + 6;
            recoveryCodeLabel.Location = new Point(22, nextTop);
            nextTop = recoveryCodeLabel.Bottom + 12;

            recoveryCodeWell.Location = new Point(22, nextTop);
            recoveryCodeWell.Size = new Size(contentWidth, 74);
            recoveryCodeCaptionLabel.Size = new Size(Math.Max(100, contentWidth - 24), 16);
            recoveryCodeValueLabel.Size = new Size(Math.Max(100, contentWidth - 24), 36);
            nextTop = recoveryCodeWell.Bottom + 14;

            warningCallout.Location = new Point(22, nextTop);
            warningCallout.Width = contentWidth;
            warningCallout.Height = warningBodyLabel.PreferredHeight + 20;
            nextTop = warningCallout.Bottom + 16;

            footerDivider.Location = new Point(22, nextTop);
            footerDivider.Width = contentWidth;
            nextTop = footerDivider.Bottom + 14;

            downloadButton.Location = new Point(22, nextTop);
            continueButton.Location = new Point(Math.Max(22, ClientSize.Width - 22 - continueButton.Width), nextTop);

            int requiredClientHeight = continueButton.Bottom + 20;
            int requiredFormHeight = SizeFromClientSize(new Size(ClientSize.Width, requiredClientHeight)).Height;
            if (MinimumSize.Height < requiredFormHeight)
                MinimumSize = new Size(MinimumSize.Width, requiredFormHeight);
        }

        private void DownloadRecoveryCode_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog directoryDialog = new FolderBrowserDialog())
            {
                directoryDialog.Description = "Choose where to save your Steam Guard recovery code";
                directoryDialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (directoryDialog.ShowDialog(this) != DialogResult.OK || String.IsNullOrWhiteSpace(directoryDialog.SelectedPath))
                    return;

                string filename = "ASDA-" + SanitizeFileNamePart(account.AccountName) + "-recovery-code.txt";
                string destination = Path.Combine(directoryDialog.SelectedPath, filename);
                if (File.Exists(destination))
                {
                    DialogResult overwrite = AstroMessageBox.Show(
                        "A recovery-code file with this name already exists. Replace it?",
                        "Replace Recovery Code File",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (overwrite != DialogResult.Yes)
                        return;
                }

                try
                {
                    File.WriteAllText(destination, BuildRecoveryCodeText());
                    EnableContinuation();
                    AstroMessageBox.Show("Recovery code saved to:\n" + destination, "Recovery Code Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Recovery code export", ex, "The recovery code file could not be saved.");
                    AstroMessageBox.Show("Unable to save the recovery code. Check that the selected folder is available and that you have permission to write there.", "Recovery Code Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RecoveryCodeForm_Shown(object sender, EventArgs e)
        {
            if (!requireBackupBeforeContinue)
                return;

            continueStopwatch.Start();
            UpdateContinueState();
            continueTimer.Start();
        }

        private void RecoveryCodeForm_Activated(object sender, EventArgs e)
        {
            UpdateContinueState();
        }

        private void ContinueTimer_Tick(object sender, EventArgs e)
        {
            UpdateContinueState();
        }

        private void UpdateContinueState()
        {
            if (continuationAllowed || !requireBackupBeforeContinue)
                return;

            int elapsedSeconds = (int)Math.Floor(continueStopwatch.Elapsed.TotalSeconds);
            int remainingSeconds = Math.Max(0, ContinueDelaySeconds - elapsedSeconds);
            if (remainingSeconds == 0)
            {
                EnableContinuation();
                return;
            }

            continueButton.Text = "Continue (" + remainingSeconds + "s)";
        }

        private void EnableContinuation()
        {
            if (continuationAllowed)
                return;

            continuationAllowed = true;
            continueStopwatch.Stop();
            continueTimer.Stop();
            continueButton.Text = "Continue";
            continueButton.Enabled = true;
            AstroTheme.StylePrimaryButton(continueButton);
        }

        private void RecoveryCodeForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            UpdateContinueState();
            if (!continuationAllowed && e.CloseReason == CloseReason.UserClosing)
                e.Cancel = true;
        }

        private void RecoveryCodeForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            continueTimer.Dispose();
        }

        private string BuildRecoveryCodeText()
        {
            return "Astro Steam Desktop Authenticator" + Environment.NewLine +
                Environment.NewLine +
                "Recovery code for " + account.AccountName + ": " + account.RevocationCode + Environment.NewLine +
                Environment.NewLine +
                "Keep this code private and stored somewhere safe. Steam may require it to recover your account or transfer Steam Guard to another device." + Environment.NewLine +
                Environment.NewLine +
                "Losing this code can make account recovery more difficult." + Environment.NewLine;
        }

        private static string SanitizeFileNamePart(string value)
        {
            string sanitized = String.IsNullOrWhiteSpace(value) ? "account" : value;
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                sanitized = sanitized.Replace(invalidCharacter, '_');

            return sanitized;
        }
    }
}
