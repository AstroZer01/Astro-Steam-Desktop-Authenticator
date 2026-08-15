using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace Steam_Desktop_Authenticator
{
    public partial class InputForm : Form
    {
        public bool Canceled = false;
        private bool userClosed = true;

        public InputForm(string label, bool password = false)
        {
            InitializeComponent();
            AstroTheme.ApplyTheme(this);
            StartPosition = FormStartPosition.CenterScreen;

            this.labelText.Text = label;

            if (password)
            {
                this.txtBox.PasswordChar = '*';
            }
            
            SetupModernUI(label, password);
        }

        public new DialogResult ShowDialog()
        {
            return ShowDialogWithCurrentOwner(null);
        }

        public new DialogResult ShowDialog(IWin32Window owner)
        {
            return ShowDialogWithCurrentOwner(owner);
        }

        private DialogResult ShowDialogWithCurrentOwner(IWin32Window requestedOwner)
        {
            IWin32Window owner = requestedOwner;
            if (!IsValidOwner(owner))
                owner = Form.ActiveForm;

            if (IsValidOwner(owner))
            {
                StartPosition = FormStartPosition.CenterParent;
                return base.ShowDialog(owner);
            }
            StartPosition = FormStartPosition.CenterScreen;
            return base.ShowDialog();
        }

        private bool IsValidOwner(IWin32Window owner)
        {
            if (owner == null || ReferenceEquals(owner, this))
                return false;

            if (owner is Form form)
                return form.Visible && !form.Disposing && !form.IsDisposed;
            if (owner is Control control)
                return control.Visible && !control.Disposing && !control.IsDisposed && control.IsHandleCreated;

            try
            {
                return owner.Handle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private WebView2 webView;
        private async void SetupModernUI(string label, bool isPassword)
        {
            this.Size = new Size(400, 300);
            this.MinimumSize = new Size(400, 100);
            this.MaximumSize = new Size(400, 1000);
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.BackColor = Color.FromArgb(11, 19, 38);
            this.Text = "Astro SDA - Input";

            Panel loadingPanel = new Panel();
            loadingPanel.Dock = DockStyle.Fill;
            loadingPanel.BackColor = Color.FromArgb(11, 19, 38);
            Label lblLoading = new Label() { Text = "Loading...", ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            loadingPanel.Controls.Add(lblLoading);
            this.Controls.Add(loadingPanel);
            loadingPanel.BringToFront();

            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            webView.Visible = false;
            this.Controls.Add(webView);
            webView.BringToFront();

            try
            {
                await webView.EnsureCoreWebView2Async(await WebViewEnvironmentProvider.GetAsync());
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Input UI", ex, "The WebView2 input dialog could not be initialized.");
                lblLoading.Text = "Input UI could not be loaded. Restore the complete release folder and try again.";
                return;
            }

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            webView.CoreWebView2.WebMessageReceived += (sender, e) =>
            {
                string message = e.WebMessageAsJson;
                if (string.IsNullOrEmpty(message)) return;

                JObject payload = JObject.Parse(message);
                string action = (string)payload["action"];

                if (action == "accept")
                {
                    this.txtBox.Text = (string)payload["value"];
                    btnAccept_Click(this, EventArgs.Empty);
                }
                else if (action == "cancel")
                {
                    btnCancel_Click(this, EventArgs.Empty);
                }
                else if (action == "resize")
                {
                    int height = (int)payload["height"];
                    this.ClientSize = new Size(this.ClientSize.Width, height);
                }
            };

            webView.NavigationCompleted += (sender, args) =>
            {
                if (!args.IsSuccess)
                {
                    lblLoading.Text = "Input UI could not be loaded. Restore the complete release folder and try again.";
                    return;
                }

                loadingPanel.Visible = false;
                foreach (Control c in this.Controls)
                {
                    if (c != webView && c != loadingPanel)
                        c.Visible = false;
                }
                webView.Visible = true;

                string jsLabel = label.Replace("'", "\\'");
                string isPassStr = isPassword ? "true" : "false";
                webView.CoreWebView2.ExecuteScriptAsync($"setupInput('{jsLabel}', {isPassStr})");
            };

            string htmlPath = System.IO.Path.Combine(ApplicationPaths.UiDirectory, "input.html");
            webView.Source = new Uri(htmlPath);
        }

        private void btnAccept_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(this.txtBox.Text))
            {
                this.Canceled = true;
                this.userClosed = false;
                this.Close();
            }
            else
            {
                this.Canceled = false;
                this.userClosed = false;
                this.Close();
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Canceled = true;
            this.userClosed = false;
            this.Close();
        }

        private void InputForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (this.userClosed)
            {
                // Set Canceled = true when the user hits the X button.
                this.Canceled = true;
            }
        }
    }
}
