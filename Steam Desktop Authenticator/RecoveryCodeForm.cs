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
        private bool continuationAllowed;

        public RecoveryCodeForm(SteamGuardAccount account, string statusMessage, bool requireBackupBeforeContinue = false)
        {
            this.account = account ?? throw new ArgumentNullException(nameof(account));
            this.requireBackupBeforeContinue = requireBackupBeforeContinue;

            Text = "Steam Guard Recovery Code";
            Size = new Size(500, 330);
            MinimumSize = new Size(500, 330);
            MaximumSize = new Size(500, 330);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = !requireBackupBeforeContinue;
            ShowInTaskbar = true;
            BackColor = AstroTheme.Background;
            ForeColor = AstroTheme.OnSurface;
            AstroTheme.ApplyDarkTitleBar(this);

            Label message = new Label
            {
                Text = statusMessage,
                AutoSize = false,
                Location = new Point(20, 20),
                Size = new Size(440, 42),
                Font = new Font("Segoe UI", 10f),
                ForeColor = AstroTheme.OnSurface
            };

            Label codeLabel = new Label
            {
                Text = "Recovery code for " + account.AccountName + ":",
                AutoSize = true,
                Location = new Point(20, 75),
                Font = new Font("Segoe UI", 10f),
                ForeColor = AstroTheme.OnSurfaceVariant
            };

            Label code = new Label
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

            Label warning = new Label
            {
                Text = "Keep this code private. Steam may require it to recover your account or move Steam Guard to another device.",
                AutoSize = false,
                Location = new Point(20, 148),
                Size = new Size(440, 34),
                Font = new Font("Segoe UI", 9f),
                ForeColor = AstroTheme.OnSurfaceVariant
            };

            Label backupRequirement = new Label
            {
                Text = requireBackupBeforeContinue
                    ? "For safety, download the recovery code to continue, or wait 60 seconds."
                    : "Keep this code private and stored somewhere safe.",
                AutoSize = false,
                Location = new Point(20, 188),
                Size = new Size(440, 20),
                Font = new Font("Segoe UI", 9f),
                ForeColor = AstroTheme.Primary
            };

            Button download = new Button
            {
                Text = "Download recovery code",
                Location = new Point(20, 222),
                Size = new Size(230, 34)
            };
            AstroTheme.StylePrimaryButton(download);
            download.Click += DownloadRecoveryCode_Click;

            continueButton = new Button
            {
                Text = "Continue",
                Location = new Point(275, 222),
                Size = new Size(185, 34),
                Enabled = !requireBackupBeforeContinue,
                DialogResult = DialogResult.OK
            };
            AstroTheme.StyleSecondaryButton(continueButton);

            Controls.Add(message);
            Controls.Add(codeLabel);
            Controls.Add(code);
            Controls.Add(warning);
            Controls.Add(backupRequirement);
            Controls.Add(download);
            Controls.Add(continueButton);
            AcceptButton = continueButton;

            continuationAllowed = !requireBackupBeforeContinue;
            continueTimer = new Timer { Interval = 250 };
            continueTimer.Tick += ContinueTimer_Tick;
            Shown += RecoveryCodeForm_Shown;
            Activated += RecoveryCodeForm_Activated;
            FormClosing += RecoveryCodeForm_FormClosing;
            FormClosed += RecoveryCodeForm_FormClosed;
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
