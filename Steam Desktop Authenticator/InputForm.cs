using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
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

        public DialogResult ShowInputDialog()
        {
            return ShowDialogWithCurrentOwner(null);
        }

        public DialogResult ShowInputDialog(IWin32Window owner)
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
        private readonly CancellationTokenSource uiCancellationSource = new CancellationTokenSource();

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            uiCancellationSource.Cancel();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (webView != null)
            {
                webView.Dispose();
                webView = null;
            }

            uiCancellationSource.Dispose();
            base.OnFormClosed(e);
        }

        private async Task ExecuteScriptSafelyAsync(string script, string operation)
        {
            if (IsDisposed || Disposing || webView == null)
                return;

            try
            {
                CoreWebView2 coreWebView = webView.CoreWebView2;
                if (coreWebView == null)
                    return;
                await coreWebView.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log(operation, ex, "The WebView2 operation could not be completed.");
            }
        }

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
                if (!IsDisposed && !Disposing)
                    lblLoading.Text = "Input UI could not be loaded. Restore the complete release folder and try again.";
                return;
            }

            if (IsDisposed || uiCancellationSource.IsCancellationRequested || webView?.CoreWebView2 == null)
                return;

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            webView.CoreWebView2.WebMessageReceived += (sender, e) =>
            {
                string message = e.WebMessageAsJson;
                if (IsDisposed || uiCancellationSource.IsCancellationRequested || String.IsNullOrEmpty(message) || message.Length > 64 * 1024) return;

                JObject payload;
                try
                {
                    using (StringReader stringReader = new StringReader(message))
                    using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                    {
                        payload = JObject.Load(jsonReader);
                    }
                }
                catch (JsonException)
                {
                    return;
                }

                string action = payload.Value<string>("action");
                if (payload["action"]?.Type != JTokenType.String || action == null || action.Length > 32)
                    return;

                if (action == "accept")
                {
                    if (payload["value"]?.Type != JTokenType.String)
                        return;
                    string value = (string)payload["value"];
                    if (value == null || value.Length > 4096) return;

                    this.txtBox.Text = value;
                    btnAccept_Click(this, EventArgs.Empty);
                }
                else if (action == "cancel")
                {
                    btnCancel_Click(this, EventArgs.Empty);
                }
                else if (action == "resize")
                {
                    if (payload["height"]?.Type != JTokenType.Integer ||
                        !long.TryParse(payload["height"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long requestedHeight))
                        return;

                    int height = (int)Math.Clamp(requestedHeight, 100L, 1000L);
                    this.ClientSize = new Size(this.ClientSize.Width, height);
                }
            };

            webView.NavigationCompleted += (sender, args) =>
            {
                if (IsDisposed || uiCancellationSource.IsCancellationRequested || webView?.CoreWebView2 == null)
                    return;
                if (!args.IsSuccess)
                {
                    if (!IsDisposed && !Disposing)
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

                string jsLabel = JsonConvert.SerializeObject(label);
                string isPassStr = isPassword ? "true" : "false";
                _ = ExecuteScriptSafelyAsync($"setupInput({jsLabel}, {isPassStr})", "Input dialog UI");
            };

            string htmlPath = System.IO.Path.Combine(ApplicationPaths.UiDirectory, "input.html");
            if (IsDisposed || uiCancellationSource.IsCancellationRequested || webView == null)
                return;
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
