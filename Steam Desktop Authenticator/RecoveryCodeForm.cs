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
        private Label recoveryCodeValueLabel;
        private Label warningLabel;
        private Label backupRequirementLabel;
        private Button downloadButton;
        private bool continuationAllowed;

        public RecoveryCodeForm(SteamGuardAccount account, string statusMessage, bool requireBackupBeforeContinue = false)
        {
            this.account = account ?? throw new ArgumentNullException(nameof(account));
            if (String.IsNullOrWhiteSpace(this.account.RevocationCode))
                throw new ArgumentException("The account does not have a Steam Guard recovery code.", nameof(account));
            this.requireBackupBeforeContinue = requireBackupBeforeContinue;

            Text = "Steam Guard Recovery Code";
            MinimumSize = new Size(500, 330);
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
                Location = new Point(20, 20),
                MaximumSize = new Size(440, 0),
                Font = new Font("Segoe UI", 10f),
                ForeColor = AstroTheme.OnSurface
            };

            recoveryCodeLabel = new Label
            {
                Text = "Recovery code for " + account.AccountName + ":",
                AutoSize = true,
                Location = new Point(20, 75),
                Font = new Font("Segoe UI", 10f),
                ForeColor = AstroTheme.OnSurfaceVariant
            };

            recoveryCodeValueLabel = new Label
            {
                Text = account.RevocationCode,
                AutoSize = false,
                Location = new Point(20, 101),
                Size = new Size(440, 36),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 16f, FontStyle.Bold),
                BackColor = AstroTheme.SurfaceVariant,
                ForeColor = AstroTheme.Primary
            };

            warningLabel = new Label
            {
                Text = "Keep this code private. Steam may require it to recover your account or move Steam Guard to another device.",
                AutoSize = true,
                Location = new Point(20, 148),
                MaximumSize = new Size(440, 0),
                Font = new Font("Segoe UI", 9f),
                ForeColor = AstroTheme.OnSurfaceVariant
            };

            backupRequirementLabel = new Label
            {
                Text = requireBackupBeforeContinue
                    ? "For safety, download the recovery code to continue, or wait 60 seconds."
                    : "Keep this code private and stored somewhere safe.",
                AutoSize = true,
                Location = new Point(20, 188),
                MaximumSize = new Size(440, 0),
                Font = new Font("Segoe UI", 9f),
                ForeColor = AstroTheme.Primary
            };

            downloadButton = new Button
            {
                Text = "Download recovery code",
                Location = new Point(20, 222),
                Size = new Size(230, 34)
            };
            AstroTheme.StylePrimaryButton(downloadButton);
            downloadButton.Click += DownloadRecoveryCode_Click;

            continueButton = new Button
            {
                Text = "Continue",
                Location = new Point(275, 222),
                Size = new Size(185, 34),
                Enabled = !requireBackupBeforeContinue,
                DialogResult = DialogResult.OK
            };
            AstroTheme.StyleSecondaryButton(continueButton);

            Controls.Add(statusLabel);
            Controls.Add(recoveryCodeLabel);
            Controls.Add(recoveryCodeValueLabel);
            Controls.Add(warningLabel);
            Controls.Add(backupRequirementLabel);
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
            int contentWidth = Math.Max(300, ClientSize.Width - 40);
            statusLabel.MaximumSize = new Size(contentWidth, 0);
            warningLabel.MaximumSize = new Size(contentWidth, 0);
            backupRequirementLabel.MaximumSize = new Size(contentWidth, 0);

            int nextTop = 20;
            statusLabel.Location = new Point(20, nextTop);
            nextTop = statusLabel.Bottom + 12;
            recoveryCodeLabel.Location = new Point(20, nextTop);
            nextTop = recoveryCodeLabel.Bottom + 8;
            recoveryCodeValueLabel.Location = new Point(20, nextTop);
            recoveryCodeValueLabel.Size = new Size(contentWidth, 36);
            nextTop = recoveryCodeValueLabel.Bottom + 11;
            warningLabel.Location = new Point(20, nextTop);
            nextTop = warningLabel.Bottom + 10;
            backupRequirementLabel.Location = new Point(20, nextTop);
            nextTop = backupRequirementLabel.Bottom + 14;
            downloadButton.Location = new Point(20, nextTop);
            continueButton.Location = new Point(Math.Max(20, ClientSize.Width - 20 - continueButton.Width), nextTop);

            int requiredHeight = continueButton.Bottom + 50;
            if (MinimumSize.Height < requiredHeight)
                MinimumSize = new Size(MinimumSize.Width, requiredHeight);
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
