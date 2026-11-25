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

            Text = "Authenticator Transfer — Save Recovery Required";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(540, 444);

            string errorDetail = String.IsNullOrWhiteSpace(initialErrorMessage)
                ? "The transferred authenticator could not be written to maFiles."
                : initialErrorMessage;
            string stagingNote = stagingFailed
                ? "WARNING: Fallback recovery staging in LocalAppData also failed. Do NOT close this window until you retry local save or save an emergency backup."
                : "A protected recovery copy was staged in LocalAppData so Astro SDA can recover it on restart, but saving now is strongly recommended.";

            // Critical alert callout with 4px muted rose-crimson left bar
            Panel alertBanner = new Panel
            {
                Location = new Point(22, 18),
                Size = new Size(496, 134),
                BackColor = AstroTheme.ErrorSurface
            };
            Panel alertBar = new Panel
            {
                BackColor = AstroTheme.ErrorAccent,
                Dock = DockStyle.Left,
                Width = 4
            };
            Label lblTitle = new Label
            {
                Text = "Steam replaced your old authenticator, but local save failed",
                Font = new Font("Segoe UI", 10.25F, FontStyle.Bold),
                ForeColor = AstroTheme.Error,
                BackColor = Color.Transparent,
                AutoSize = false,
                Location = new Point(16, 12),
                Size = new Size(466, 24)
            };
            Label lblBody = new Label
            {
                Text = errorDetail + "\n\n" + stagingNote,
                Font = new Font("Segoe UI", 8.75F, FontStyle.Regular),
                ForeColor = Color.FromArgb(215, 200, 205),
                BackColor = Color.Transparent,
                AutoSize = false,
                Location = new Point(16, 40),
                Size = new Size(466, 84)
            };
            alertBanner.Controls.Add(lblTitle);
            alertBanner.Controls.Add(lblBody);
            alertBanner.Controls.Add(alertBar);
            alertBanner.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(Color.FromArgb(95, 50, 56), 1f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, alertBanner.Width - 1, alertBanner.Height - 1);
                }
            };

            // Inset read-only revocation code strip
            Panel codeWell = new Panel
            {
                Location = new Point(22, 166),
                Size = new Size(496, 56),
                BackColor = AstroTheme.SurfaceContainerLowest
            };
            codeWell.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(AstroTheme.OutlineVariant, 1f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, codeWell.Width - 1, codeWell.Height - 1);
                }
            };
            Label lblCodeCaption = new Label
            {
                Text = "NEW REVOCATION CODE",
                Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
                ForeColor = AstroTheme.Outline,
                BackColor = Color.Transparent,
                Location = new Point(14, 8),
                AutoSize = true
            };
            Label lblCodeValue = new Label
            {
                Text = String.IsNullOrWhiteSpace(replacementAccount.RevocationCode)
                    ? "(none)"
                    : string.Join(" ", replacementAccount.RevocationCode.Trim().ToCharArray()),
                Font = new Font("Consolas", 13.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 248, 252),
                BackColor = Color.Transparent,
                Location = new Point(14, 24),
                Size = new Size(468, 26)
            };
            codeWell.Controls.Add(lblCodeCaption);
            codeWell.Controls.Add(lblCodeValue);

            lblStatus = new Label
            {
                Text = String.Empty,
                AutoSize = false,
                Location = new Point(22, 230),
                Size = new Size(496, 36),
                Font = new Font("Segoe UI", 8.75F, FontStyle.Regular),
                ForeColor = AstroTheme.Error,
                BackColor = Color.Transparent
            };

            Button btnRetrySave = new Button
            {
                Text = "Retry Local Save",
                Location = new Point(22, 274),
                Size = new Size(496, 38)
            };
            btnRetrySave.Click += BtnRetrySave_Click;

            Button btnEmergencyZip = new Button
            {
                Text = "Emergency .zip Backup",
                Location = new Point(22, 322),
                Size = new Size(243, 36)
            };
            btnEmergencyZip.Click += BtnEmergencyZip_Click;

            Button btnPlainBackup = new Button
            {
                Text = "Plain .maFile + Code",
                Location = new Point(275, 322),
                Size = new Size(243, 36)
            };
            btnPlainBackup.Click += BtnPlainBackup_Click;

            Panel footerDivider = AstroTheme.CreateDivider(496);
            footerDivider.Location = new Point(22, 374);

            Button btnClose = new Button
            {
                Text = "Close Without Saving",
                Location = new Point(358, 390),
                Size = new Size(160, 34)
            };
            btnClose.Click += (s, e) => Close();

            Controls.Add(alertBanner);
            Controls.Add(codeWell);
            Controls.Add(lblStatus);
            Controls.Add(btnRetrySave);
            Controls.Add(btnEmergencyZip);
            Controls.Add(btnPlainBackup);
            Controls.Add(footerDivider);
            Controls.Add(btnClose);

            AstroTheme.ApplyTheme(this);
            alertBanner.BackColor = AstroTheme.ErrorSurface;
            alertBar.BackColor = AstroTheme.ErrorAccent;
            lblTitle.ForeColor = AstroTheme.Error;
            lblBody.ForeColor = Color.FromArgb(215, 200, 205);
            codeWell.BackColor = AstroTheme.SurfaceContainerLowest;
            lblCodeCaption.ForeColor = AstroTheme.Outline;
            lblCodeValue.ForeColor = Color.FromArgb(245, 248, 252);
            lblStatus.ForeColor = AstroTheme.Error;
            AstroTheme.StylePrimaryButton(btnRetrySave);
            AstroTheme.StyleSurfaceButton(btnEmergencyZip);
            AstroTheme.StyleSurfaceButton(btnPlainBackup);
            AstroTheme.StyleDenyButton(btnClose);
            AcceptButton = btnRetrySave;
        }

        private void BtnRetrySave_Click(object sender, EventArgs e)
        {
            StorageResult result = retryLocalSave(replacementAccount);
            if (result.Succeeded)
            {
                LocalSaveSucceeded = true;
                savedOrBackedUp = true;
                bool markerCleared = replacementAccount.Session == null ||
                    replacementAccount.Session.SteamID == 0 ||
                    PendingTransferMarker.DeleteMarkerBestEffort(replacementAccount.Session.SteamID, transferAttemptId);
                if (markerCleared && !String.IsNullOrEmpty(stagedRecoveryPath))
                    PendingTransferMarker.DeleteStagedRecoveryBestEffort(stagedRecoveryPath);
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
                    if (passForm.Canceled || !passForm.UiReady || passForm.LoadFailed || String.IsNullOrEmpty(passForm.txtBox.Text))
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
                    lblStatus.ForeColor = AstroTheme.Secondary;
                    lblStatus.Text = "Encrypted emergency backup saved. You can import its .maFile + manifest.json after extracting.";
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Transfer emergency backup", ex, "Could not save encrypted emergency backup.");
                    lblStatus.ForeColor = AstroTheme.Error;
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
                    lblStatus.ForeColor = AstroTheme.Secondary;
                    lblStatus.Text = "Plain .maFile and recovery code saved.";
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Transfer emergency backup", ex, "Could not save plain emergency backup.");
                    lblStatus.ForeColor = AstroTheme.Error;
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
