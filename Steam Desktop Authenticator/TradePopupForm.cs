using SteamAuth;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    public partial class TradePopupForm : Form
    {
        private SteamGuardAccount acc;
        private List<Confirmation> confirms = new List<Confirmation>();
        private bool deny2, accept2;

        public TradePopupForm()
        {
            InitializeComponent();
            AstroTheme.ApplyTheme(this);

            // Style trade buttons with Stitch accept/deny design
            AstroTheme.StyleAcceptButton(btnAccept);
            AstroTheme.StyleDenyButton(btnDeny);

            lblStatus.Text = "";
        }

        public SteamGuardAccount Account
        {
            get { return acc; }
            set { acc = value; lblAccount.Text = acc.AccountName; }
        }

        public Confirmation[] Confirmations
        {
            get { return confirms.ToArray(); }
            set { confirms = new List<Confirmation>(value); }
        }

        private void TradePopupForm_Load(object sender, EventArgs e)
        {
            this.Location = (Point)Size.Subtract(Screen.GetWorkingArea(this).Size, this.Size);
        }

        // Prevent the form from being disposed when the user closes it.
        // If it were disposed, subsequent attempts to show it again would throw
        // "Cannot access a disposed object".
        private void TradePopupForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private void btnAccept_Click(object sender, EventArgs e)
        {
            if (!accept2)
            {
                // Allow user to confirm first
                lblStatus.Text = "Press Accept again to confirm";
                btnAccept.BackColor = AstroTheme.SecondaryContainer;
                accept2 = true;
            }
            else
            {
                lblStatus.Text = "Accepting...";
                acc.AcceptConfirmation(confirms[0]);
                confirms.RemoveAt(0);
                Reset();
            }
        }

        private void btnDeny_Click(object sender, EventArgs e)
        {
            if (!deny2)
            {
                lblStatus.Text = "Press Deny again to confirm";
                btnDeny.BackColor = AstroTheme.ErrorContainer;
                deny2 = true;
            }
            else
            {
                lblStatus.Text = "Denying...";
                acc.DenyConfirmation(confirms[0]);
                confirms.RemoveAt(0);
                Reset();
            }
        }

        private void Reset()
        {
            deny2 = false;
            accept2 = false;
            AstroTheme.StyleAcceptButton(btnAccept);
            AstroTheme.StyleDenyButton(btnDeny);

            btnAccept.Text = "Accept";
            btnDeny.Text = "Deny";
            lblStatus.Text = "";

            if (confirms.Count == 0)
            {
                this.Hide();
            }
            else
            {
                var conf = confirms[0];

                // Line 1: trader/offer name from Headline
                lblAccount.Text = !string.IsNullOrEmpty(conf.Headline) ? conf.Headline.Trim() : "New Confirmation";

                // Remaining lines: summary with overflow ellipsis
                if (conf.Summary != null && conf.Summary.Count > 0)
                {
                    lblDesc.Text = string.Join(" · ", conf.Summary);
                }
                else
                {
                    lblDesc.Text = "";
                }
            }
        }

        public void Popup()
        {
            Reset();
            this.Show();
        }
    }
}
