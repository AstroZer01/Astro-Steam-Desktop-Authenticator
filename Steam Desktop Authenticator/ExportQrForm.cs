using SteamAuth;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal sealed class ExportQrForm : Form
    {
        private readonly SteamGuardAccount account;
        private readonly Button btnKeePassXC;
        private readonly Button btnBitwarden;
        private readonly PictureBox picQr;
        private readonly Label lblFormatNote;
        private readonly Label lblError;
        private SecretQrFormat selectedFormat = SecretQrFormat.KeePassXC;
        private Bitmap currentQrBitmap;

        public ExportQrForm(SteamGuardAccount account)
        {
            this.account = account ?? throw new ArgumentNullException(nameof(account));

            Text = "Export Secret QR Code — " + (account.AccountName ?? "Account");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(440, 588);

            // Muted gold left-accent security warning callout
            Panel warningCallout = new Panel
            {
                Location = new Point(22, 16),
                Size = new Size(396, 98),
                BackColor = AstroTheme.WarningSurface
            };
            Panel warningBar = new Panel
            {
                BackColor = AstroTheme.Warning,
                Dock = DockStyle.Left,
                Width = 4
            };
            Label lblWarning = new Label
            {
                Text = "SECURITY WARNING: Anyone who scans this QR code can generate your Steam Guard login codes.\n\n" +
                    "QR codes transfer only shared_secret (5-character codes). Export a .maFile or encrypted .zip for a complete backup.",
                AutoSize = false,
                Location = new Point(14, 9),
                Size = new Size(370, 82),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                ForeColor = AstroTheme.WarningSoft,
                BackColor = Color.Transparent
            };
            warningCallout.Controls.Add(lblWarning);
            warningCallout.Controls.Add(warningBar);
            warningCallout.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(Color.FromArgb(62, 54, 42), 1f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, warningCallout.Width - 1, warningCallout.Height - 1);
                }
            };

            Label lblFormatTitle = new Label
            {
                Text = "STEAM TOTP FORMAT",
                AutoSize = true,
                Location = new Point(22, 126),
                Font = new Font("Segoe UI", 7.75F, FontStyle.Bold),
                ForeColor = AstroTheme.Outline,
                BackColor = Color.Transparent
            };

            // Flat segmented format toggle buttons (replaces legacy RadioButtons)
            Panel formatTrack = new Panel
            {
                Location = new Point(22, 146),
                Size = new Size(396, 36),
                BackColor = AstroTheme.SurfaceContainerLowest,
                Padding = new Padding(3)
            };
            formatTrack.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(AstroTheme.Divider, 1f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, formatTrack.Width - 1, formatTrack.Height - 1);
                }
            };

            btnKeePassXC = new Button
            {
                Text = "KeePassXC (otpauth)",
                Location = new Point(3, 3),
                Size = new Size(194, 30)
            };
            btnKeePassXC.Click += (s, e) => SelectFormat(SecretQrFormat.KeePassXC);

            btnBitwarden = new Button
            {
                Text = "Bitwarden (steam://)",
                Location = new Point(199, 3),
                Size = new Size(194, 30)
            };
            btnBitwarden.Click += (s, e) => SelectFormat(SecretQrFormat.Bitwarden);

            formatTrack.Controls.Add(btnKeePassXC);
            formatTrack.Controls.Add(btnBitwarden);

            Panel qrContainer = new Panel
            {
                Location = new Point(68, 196),
                Size = new Size(304, 304),
                BackColor = Color.White,
                Padding = new Padding(14)
            };
            qrContainer.Paint += (s, e) =>
            {
                using (Pen borderPen = new Pen(AstroTheme.OutlineVariant, 1f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, qrContainer.Width - 1, qrContainer.Height - 1);
                }
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
                Location = new Point(22, 506),
                Size = new Size(396, 22),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                ForeColor = AstroTheme.OnSurfaceVariant,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };

            lblError = new Label
            {
                Text = String.Empty,
                AutoSize = false,
                Location = new Point(22, 506),
                Size = new Size(396, 22),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                ForeColor = AstroTheme.Error,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };

            Panel footerDivider = AstroTheme.CreateDivider(396);
            footerDivider.Location = new Point(22, 534);

            Button btnClose = new Button
            {
                Text = "Close",
                Location = new Point(314, 546),
                Size = new Size(104, 34),
                DialogResult = DialogResult.OK
            };
            btnClose.Click += (s, e) => Close();
            CancelButton = btnClose;

            Controls.Add(warningCallout);
            Controls.Add(lblFormatTitle);
            Controls.Add(formatTrack);
            Controls.Add(qrContainer);
            Controls.Add(lblFormatNote);
            Controls.Add(lblError);
            Controls.Add(footerDivider);
            Controls.Add(btnClose);

            AstroTheme.ApplyTheme(this);
            // Preserve crisp white quiet-zone & callout colors after applying dark theme.
            warningCallout.BackColor = AstroTheme.WarningSurface;
            warningBar.BackColor = AstroTheme.Warning;
            lblWarning.ForeColor = AstroTheme.WarningSoft;
            lblFormatTitle.ForeColor = AstroTheme.Outline;
            formatTrack.BackColor = AstroTheme.SurfaceContainerLowest;
            qrContainer.BackColor = Color.White;
            picQr.BackColor = Color.White;
            lblFormatNote.ForeColor = AstroTheme.OnSurfaceVariant;
            lblError.ForeColor = AstroTheme.Error;
            AstroTheme.StyleSurfaceButton(btnClose);

            UpdateFormatToggleStyles();
            RenderSelectedFormat();
        }

        private void SelectFormat(SecretQrFormat format)
        {
            if (selectedFormat == format)
                return;
            selectedFormat = format;
            UpdateFormatToggleStyles();
            RenderSelectedFormat();
        }

        private void UpdateFormatToggleStyles()
        {
            StyleSegmentedButton(btnKeePassXC, selectedFormat == SecretQrFormat.KeePassXC);
            StyleSegmentedButton(btnBitwarden, selectedFormat == SecretQrFormat.Bitwarden);
        }

        private static void StyleSegmentedButton(Button button, bool active)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Segoe UI", 8.75F, active ? FontStyle.Bold : FontStyle.Regular);
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderSize = active ? 1 : 0;
            if (active)
            {
                button.BackColor = AstroTheme.SurfaceContainerHigh;
                button.ForeColor = AstroTheme.PrimaryLight;
                button.FlatAppearance.BorderColor = Color.FromArgb(58, 125, 146);
                button.FlatAppearance.MouseOverBackColor = AstroTheme.SurfaceBright;
                button.FlatAppearance.MouseDownBackColor = AstroTheme.SurfaceContainerHigh;
            }
            else
            {
                button.BackColor = AstroTheme.SurfaceContainerLowest;
                button.ForeColor = AstroTheme.OnSurfaceVariant;
                button.FlatAppearance.MouseOverBackColor = AstroTheme.SurfaceContainerLow;
                button.FlatAppearance.MouseDownBackColor = AstroTheme.SurfaceContainer;
            }
        }

        private void RenderSelectedFormat()
        {
            SecretQrFormat format = selectedFormat;
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
