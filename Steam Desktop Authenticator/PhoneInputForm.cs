using SteamAuth;
using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Drawing;

namespace Steam_Desktop_Authenticator
{
    public partial class PhoneInputForm : Form
    {
        private SteamGuardAccount Account;
        public string PhoneNumber;
        public string CountryCode;
        public bool Canceled;

        public PhoneInputForm(SteamGuardAccount account)
        {
            this.Account = account;
            InitializeComponent();
            AstroTheme.ApplyTheme(this);
            this.Size = new Size(400, 250);
            this.MinimumSize = new Size(400, 250);
            this.MaximumSize = new Size(400, 250);
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
        }

        private void btnSubmit_Click(object sender, EventArgs e)
        {
            this.PhoneNumber = txtPhoneNumber.Text;
            this.CountryCode = txtCountryCode.Text;

            if (this.PhoneNumber[0] != '+')
            {
                AstroMessageBox.Show("Phone number must start with + and country code.", "Phone Number", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            this.Close();
        }

        private void txtPhoneNumber_KeyPress(object sender, KeyPressEventArgs e)
        {
            // Allow pasting
            if (Char.IsControl(e.KeyChar))
                return;

            // Only allow numbers, spaces, and +
            var regex = new Regex(@"[^0-9\s\+]");
            if (regex.IsMatch(e.KeyChar.ToString()))
            {
                e.Handled = true;
            }
        }

        private void txtCountryCode_KeyPress(object sender, KeyPressEventArgs e)
        {
            // Allow pasting
            if (Char.IsControl(e.KeyChar))
                return;

            // Only allow letters
            var regex = new Regex(@"[^a-zA-Z]");
            if (regex.IsMatch(e.KeyChar.ToString()))
            {
                e.Handled = true;
            }
        }

        private void txtCountryCode_Leave(object sender, EventArgs e)
        {
            // Always uppercase
            txtCountryCode.Text = txtCountryCode.Text.ToUpper();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Canceled = true;
            this.Close();
        }
    }
}
