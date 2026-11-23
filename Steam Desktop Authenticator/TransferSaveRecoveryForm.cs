using SteamAuth;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal sealed class TransferSaveRecoveryForm : Form
    {
        private readonly SteamGuardAccount replacementAccount;
        private readonly Func<SteamGuardAccount, StorageResult> retryLocalSave;
        private readonly string manifestPassKey;
        private readonly string stagedRecoveryPath;
        private readonly string transferAttemptId;
        private readonly Label lblStatus;
        private bool savedOrBackedUp;
        public bool LocalSaveSucceeded { get; private set; }

        public TransferSaveRecoveryForm(
            SteamGuardAccount replacementAccount,
            string initialErrorMessage,
            string stagedRecoveryPath,
            bool stagingFailed,
            string manifestPassKey,
            Func<SteamGuardAccount, StorageResult> retryLocalSave,
            string transferAttemptId = null)
        {
            this.replacementAccount = replacementAccount ?? throw new ArgumentNullException(nameof(replacementAccount));
            this.retryLocalSave = retryLocalSave ?? throw new ArgumentNullException(nameof(retryLocalSave));
            this.manifestPassKey = manifestPassKey;
            this.stagedRecoveryPath = stagedRecoveryPath;
            this.transferAttemptId = transferAttemptId;

            Text = "Authenticator Transfer - Save Recovery Required";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 390);

            Label lblTitle = new Label
            {
                Text = "Steam replaced your old authenticator, but local save failed",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                AutoSize = false,
                Location = new Point(20, 18),
                Size = new Size(480, 28)
            };

            string stagingNote = stagingFailed
                ? "WARNING: Fallback recovery staging in LocalAppData also failed. Do NOT close this window until you retry local save or save an emergency backup."
                : "A protected recovery copy was staged in LocalAppData so Astro SDA can recover it on restart, but saving now is strongly recommended.";

            Label lblBody = new Label
            {
                Text = (String.IsNullOrWhiteSpace(initialErrorMessage) ? "The transferred authenticator could not be written to maFiles." : initialErrorMessage) +
                    "\n\nRecovery Code: " + (replacementAccount.RevocationCode ?? "(none)") +
                    "\n\n" + stagingNote,
                AutoSize = false,
                Location = new Point(20, 54),
                Size = new Size(480, 130)
            };

            lblStatus = new Label
            {
                Text = String.Empty,
                AutoSize = false,
                Location = new Point(20, 192),
                Size = new Size(480, 44),
                ForeColor = Color.FromArgb(255, 180, 171)
            };

            Button btnRetrySave = new Button
            {
                Text = "Retry Local Save",
                Location = new Point(20, 246),
                Size = new Size(480, 36)
            };
            btnRetrySave.Click += BtnRetrySave_Click;

            Button btnEmergencyZip = new Button
            {
                Text = "Save Protected Emergency Backup (.zip)",
                Location = new Point(20, 290),
                Size = new Size(235, 36)
            };
            btnEmergencyZip.Click += BtnEmergencyZip_Click;

            Button btnPlainBackup = new Button
            {
                Text = "Save Plain Backup (.maFile + Code)",
                Location = new Point(265, 290),
                Size = new Size(235, 36)
            };
            btnPlainBackup.Click += BtnPlainBackup_Click;

            Button btnClose = new Button
            {
                Text = "Close",
                Location = new Point(380, 338),
                Size = new Size(120, 32)
            };
            btnClose.Click += (s, e) => Close();

            Controls.Add(lblTitle);
            Controls.Add(lblBody);
            Controls.Add(lblStatus);
            Controls.Add(btnRetrySave);
            Controls.Add(btnEmergencyZip);
            Controls.Add(btnPlainBackup);
            Controls.Add(btnClose);

            AstroTheme.ApplyTheme(this);
            AstroTheme.StylePrimaryButton(btnRetrySave);
            AstroTheme.StyleSurfaceButton(btnEmergencyZip);
            AstroTheme.StyleSurfaceButton(btnPlainBackup);
            AstroTheme.StyleSurfaceButton(btnClose);
        }

        private void BtnRetrySave_Click(object sender, EventArgs e)
        {
            StorageResult result = retryLocalSave(replacementAccount);
            if (result.Succeeded)
            {
                LocalSaveSucceeded = true;
                savedOrBackedUp = true;
                if (!String.IsNullOrEmpty(stagedRecoveryPath))
                    PendingTransferMarker.DeleteStagedRecoveryBestEffort(stagedRecoveryPath);
                if (replacementAccount.Session != null && replacementAccount.Session.SteamID != 0)
                    PendingTransferMarker.DeleteMarkerBestEffort(replacementAccount.Session.SteamID, transferAttemptId);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            lblStatus.Text = result.UserMessage ?? "Local save failed again. Try saving an emergency backup.";
        }

        private void BtnEmergencyZip_Click(object sender, EventArgs e)
        {
            string exportPassKey = manifestPassKey;
            if (String.IsNullOrEmpty(exportPassKey))
            {
                using (InputForm passForm = new InputForm("Enter a passkey to encrypt this emergency backup ZIP:", true))
                {
                    passForm.ShowInputDialog(this);
                    if (passForm.Canceled || String.IsNullOrEmpty(passForm.txtBox.Text))
                        return;
                    exportPassKey = passForm.txtBox.Text;
                }
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "Encrypted Backup ZIP (*.zip)|*.zip";
                ulong steamId = replacementAccount.Session?.SteamID ?? 0;
                sfd.FileName = AccountExportService.SanitizeExportFilename(
                    (replacementAccount.AccountName ?? "account") + "_emergency_backup",
                    steamId,
                    extension: ".zip");
                if (sfd.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    AccountExportService.ExportEncryptedZip(new[] { replacementAccount }, sfd.FileName, exportPassKey, includeSession: true);
                    savedOrBackedUp = true;
                    lblStatus.ForeColor = Color.FromArgb(78, 222, 163);
                    lblStatus.Text = "Encrypted emergency backup saved. You can import its .maFile + manifest.json after extracting.";
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Transfer emergency backup", ex, "Could not save encrypted emergency backup.");
                    lblStatus.ForeColor = Color.FromArgb(255, 180, 171);
                    lblStatus.Text = ex.Message;
                }
            }
        }

        private void BtnPlainBackup_Click(object sender, EventArgs e)
        {
            DialogResult warn = AstroMessageBox.Show(
                "A plain .maFile contains your unencrypted authenticator secrets (shared_secret, identity_secret, and revocation_code). Save only to a secure location. Continue?",
                "Plaintext Emergency Backup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (warn != DialogResult.Yes)
                return;

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "Steam Authenticator File (*.maFile)|*.maFile";
                ulong steamId = replacementAccount.Session?.SteamID ?? 0;
                sfd.FileName = AccountExportService.SanitizeExportFilename(replacementAccount.AccountName, steamId, extension: ".maFile");
                if (sfd.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    AccountExportService.ExportPlainMaFile(replacementAccount, sfd.FileName, includeSession: true);
                    if (!String.IsNullOrWhiteSpace(replacementAccount.RevocationCode))
                    {
                        string codePath = Path.ChangeExtension(sfd.FileName, ".recovery.txt");
                        bool shouldWriteCodeFile = true;
                        if (File.Exists(codePath))
                        {
                            DialogResult overwriteCode = AstroMessageBox.Show(
                                "The recovery code file already exists:\n" + codePath + "\n\nOverwrite it? Choose No to save the recovery code to a different filename.",
                                "Recovery Code File Exists",
                                MessageBoxButtons.YesNoCancel,
                                MessageBoxIcon.Warning);
                            if (overwriteCode == DialogResult.Cancel)
                            {
                                shouldWriteCodeFile = false;
                            }
                            else if (overwriteCode == DialogResult.No)
                            {
                                using (SaveFileDialog codeSfd = new SaveFileDialog())
                                {
                                    codeSfd.Filter = "Recovery Code Text (*.txt)|*.txt";
                                    codeSfd.FileName = Path.GetFileName(codePath);
                                    if (codeSfd.ShowDialog(this) == DialogResult.OK)
                                        codePath = codeSfd.FileName;
                                    else
                                        shouldWriteCodeFile = false;
                                }
                            }
                        }

                        if (shouldWriteCodeFile)
                        {
                            AccountExportService.ValidateExportDestination(codePath);
                            PendingTransferMarker.WriteAllTextAtomicallyFlushed(
                                codePath,
                                "Account: " + (replacementAccount.AccountName ?? String.Empty) + Environment.NewLine +
                                "SteamID: " + steamId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                                "Revocation Code: " + replacementAccount.RevocationCode + Environment.NewLine);
                        }
                    }
                    savedOrBackedUp = true;
                    lblStatus.ForeColor = Color.FromArgb(78, 222, 163);
                    lblStatus.Text = "Plain .maFile and recovery code saved.";
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Transfer emergency backup", ex, "Could not save plain emergency backup.");
                    lblStatus.ForeColor = Color.FromArgb(255, 180, 171);
                    lblStatus.Text = ex.Message;
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!savedOrBackedUp && e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult firstConfirm = AstroMessageBox.Show(
                    "You have NOT saved this transferred authenticator locally or exported a backup yet. Closing now could lock you out of your Steam account if recovery fails.\n\nAre you sure you want to close without saving?",
                    "Confirm Close Without Saving",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (firstConfirm != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                DialogResult secondConfirm = AstroMessageBox.Show(
                    "Final confirmation: I understand my old authenticator was already replaced on Steam and I still want to close without saving.",
                    "Final Confirmation",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Error);
                if (secondConfirm != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            base.OnFormClosing(e);
        }
    }
}
