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
            lblAccount.Text = "";
            lblStatus.Text = "";

            if (confirms.Count == 0)
            {
                this.Hide();
            }
            else
            {
                string description = confirms[0].Headline;
                if (confirms[0].Summary != null && confirms[0].Summary.Count > 0)
                {
                    description += "\n" + string.Join("\n", confirms[0].Summary);
                }
                lblDesc.Text = !string.IsNullOrEmpty(description) ? description.Trim() : "Confirmation";
            }
        }

        public void Popup()
        {
            Reset();
            this.Show();
        }
    }
}
