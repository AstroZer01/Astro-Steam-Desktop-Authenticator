using SteamAuth;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal sealed class ExportQrForm : Form
    {
        private readonly SteamGuardAccount account;
        private readonly RadioButton rbKeePassXC;
        private readonly RadioButton rbBitwarden;
        private readonly PictureBox picQr;
        private readonly Label lblFormatNote;
        private readonly Label lblError;
        private Bitmap currentQrBitmap;

        public ExportQrForm(SteamGuardAccount account)
        {
            this.account = account ?? throw new ArgumentNullException(nameof(account));

            Text = "Export Secret QR Code - " + (account.AccountName ?? "Account");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 560);

            Label lblWarning = new Label
            {
                Text = "SECURITY WARNING: Anyone who scans this QR code can generate your Steam Guard login codes.\n\n" +
                    "QR codes transfer only shared_secret (5-character login codes). They do NOT include identity_secret (trade/market confirmations) or revocation_code — export a .maFile or encrypted .zip for a complete backup.",
                AutoSize = false,
                Location = new Point(20, 14),
                Size = new Size(380, 96),
                ForeColor = Color.FromArgb(255, 200, 140)
            };

            Label lblFormatTitle = new Label
            {
                Text = "Steam TOTP Format:",
                AutoSize = true,
                Location = new Point(20, 118),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };

            rbKeePassXC = new RadioButton
            {
                Text = "KeePassXC (otpauth Steam encoder)",
                Location = new Point(20, 142),
                Size = new Size(210, 24),
                Checked = true
            };
            rbKeePassXC.CheckedChanged += FormatSelectionChanged;

            rbBitwarden = new RadioButton
            {
                Text = "Bitwarden (steam://)",
                Location = new Point(236, 142),
                Size = new Size(164, 24)
            };
            rbBitwarden.CheckedChanged += FormatSelectionChanged;

            Panel qrContainer = new Panel
            {
                Location = new Point(60, 174),
                Size = new Size(300, 300),
                BackColor = Color.White,
                Padding = new Padding(10)
            };

            picQr = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White
            };
            qrContainer.Controls.Add(picQr);

            lblFormatNote = new Label
            {
                Text = String.Empty,
                AutoSize = false,
                Location = new Point(20, 482),
                Size = new Size(380, 28),
                TextAlign = ContentAlignment.MiddleCenter
            };

            lblError = new Label
            {
                Text = String.Empty,
                AutoSize = false,
                Location = new Point(20, 482),
                Size = new Size(380, 28),
                ForeColor = Color.FromArgb(255, 180, 171),
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };

            Button btnClose = new Button
            {
                Text = "Close",
                Location = new Point(150, 516),
                Size = new Size(120, 32),
                DialogResult = DialogResult.OK
            };
            btnClose.Click += (s, e) => Close();
            CancelButton = btnClose;

            Controls.Add(lblWarning);
            Controls.Add(lblFormatTitle);
            Controls.Add(rbKeePassXC);
            Controls.Add(rbBitwarden);
            Controls.Add(qrContainer);
            Controls.Add(lblFormatNote);
            Controls.Add(lblError);
            Controls.Add(btnClose);

            AstroTheme.ApplyTheme(this);
            // Preserve crisp white quiet-zone around QR code after applying dark theme.
            qrContainer.BackColor = Color.White;
            picQr.BackColor = Color.White;
            lblWarning.ForeColor = Color.FromArgb(255, 200, 140);
            AstroTheme.StyleSurfaceButton(btnClose);

            RenderSelectedFormat();
        }

        private void FormatSelectionChanged(object sender, EventArgs e)
        {
            if (sender is RadioButton rb && rb.Checked)
                RenderSelectedFormat();
        }

        private void RenderSelectedFormat()
        {
            SecretQrFormat format = rbBitwarden.Checked ? SecretQrFormat.Bitwarden : SecretQrFormat.KeePassXC;
            Bitmap previous = currentQrBitmap;
            picQr.Image = null;
            currentQrBitmap = null;
            previous?.Dispose();

            try
            {
                string payload = SecretQrBuilder.BuildSecretUri(account, format);
                currentQrBitmap = SecretQrBuilder.RenderQrBitmap(payload, 280);
                picQr.Image = currentQrBitmap;
                lblError.Visible = false;
                lblFormatNote.Visible = true;
                lblFormatNote.Text = format == SecretQrFormat.KeePassXC
                    ? "Format: KeePassXC Steam TOTP (30s, 5 chars, encoder=steam)"
                    : "Format: Bitwarden Steam Authenticator (steam://)";
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Secret QR export", ex, "Could not render the Steam secret QR code.");
                lblFormatNote.Visible = false;
                lblError.Visible = true;
                lblError.Text = "Could not generate QR code for this account's shared secret.";
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Bitmap toDispose = currentQrBitmap;
            picQr.Image = null;
            currentQrBitmap = null;
            toDispose?.Dispose();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Bitmap toDispose = currentQrBitmap;
                if (picQr != null)
                    picQr.Image = null;
                currentQrBitmap = null;
                toDispose?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
