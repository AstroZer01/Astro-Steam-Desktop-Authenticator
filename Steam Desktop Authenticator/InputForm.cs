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
        private int navigationGeneration = 0;
        private ulong? activeNavigationId;
        internal bool IsPasswordMasked { get; }
        internal bool Accepted { get; private set; }
        internal bool ExplicitCancel { get; private set; }
        internal bool UiReady { get; private set; }
        internal bool LoadFailed { get; private set; }

        public InputForm(string label, bool password = false)
            : this(label, password, initializeModernUi: true)
        {
        }

        internal InputForm(string label, bool password, bool initializeModernUi)
        {
            InitializeComponent();
            AstroTheme.ApplyTheme(this);
            StartPosition = FormStartPosition.CenterScreen;

            this.labelText.Text = label;
            this.IsPasswordMasked = password;
            this.UiReady = !initializeModernUi;

            if (password)
            {
                this.txtBox.PasswordChar = '*';
            }

            if (initializeModernUi)
            {
                SetupModernUI(label, password);
            }
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
        private bool uiCancellationSourceDisposed;

        private bool IsUiCanceledOrDisposed()
        {
            return IsDisposed || Disposing || uiCancellationSourceDisposed || uiCancellationSource.IsCancellationRequested;
        }

        private void DisposeUiCancellationSource()
        {
            if (uiCancellationSourceDisposed)
                return;

            uiCancellationSourceDisposed = true;
            uiCancellationSource.Dispose();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!uiCancellationSourceDisposed)
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

            DisposeUiCancellationSource();
            base.OnFormClosed(e);
        }

        internal static bool IsScriptTrueResult(string scriptResult)
        {
            return String.Equals(scriptResult, "true", StringComparison.Ordinal);
        }

        private async Task<bool> ExecuteScriptSafelyAsync(string script, string operation, bool requireTrueResult = false)
        {
            if (IsDisposed || Disposing || webView == null)
                return false;

            try
            {
                CoreWebView2 coreWebView = webView.CoreWebView2;
                if (coreWebView == null)
                    return false;
                string result = await coreWebView.ExecuteScriptAsync(script);
                if (requireTrueResult && !IsScriptTrueResult(result))
                {
                    DiagnosticErrorLogger.Log(
                        operation,
                        new InvalidOperationException("The WebView2 script did not report successful completion."),
                        "The WebView2 operation could not be completed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log(operation, ex, "The WebView2 operation could not be completed.");
                return false;
            }
        }

        internal void DisableNativeFallbackControls()
        {
            this.AcceptButton = null;
            this.CancelButton = null;
            this.txtBox.Enabled = false;
            this.btnAccept.Enabled = false;
            this.btnCancel.Enabled = false;
        }

        internal bool CanAcceptWebMessage(bool isWebViewVisible)
        {
            return UiReady && !LoadFailed && isWebViewVisible;
        }

        private void ShowLoadingState(Panel loadingPanel, Label lblLoading, string message)
        {
            if (!IsDisposed && !Disposing)
            {
                if (webView != null)
                    webView.Visible = false;
                if (lblLoading != null)
                    lblLoading.Text = message;
                if (loadingPanel != null)
                {
                    loadingPanel.Visible = true;
                    loadingPanel.BringToFront();
                }
            }
        }

        internal void ResetModernUiState(Panel loadingPanel = null, Label lblLoading = null)
        {
            activeNavigationId = null;
            navigationGeneration++;
            this.UiReady = false;
            this.LoadFailed = false;
            ShowLoadingState(loadingPanel, lblLoading, "Loading...");
        }

        internal void BeginModernUiNavigation(ulong navigationId, Panel loadingPanel = null, Label lblLoading = null)
        {
            activeNavigationId = navigationId;
            navigationGeneration++;
            this.UiReady = false;
            this.LoadFailed = false;
            ShowLoadingState(loadingPanel, lblLoading, "Loading...");
        }

        internal bool ShouldHandleNavigationCompleted(ulong navigationId, out int expectedGeneration)
        {
            expectedGeneration = navigationGeneration;
            return activeNavigationId.HasValue && activeNavigationId.Value == navigationId;
        }

        internal bool TryHandleNavigationCompletedResult(ulong navigationId, int expectedGeneration, bool succeeded, Panel loadingPanel = null, Label lblLoading = null)
        {
            if (!activeNavigationId.HasValue || activeNavigationId.Value != navigationId || navigationGeneration != expectedGeneration)
                return false;

            if (!succeeded)
            {
                MarkModernUiLoadFailed(loadingPanel, lblLoading);
                return true;
            }

            CompleteModernUiLoad(loadingPanel);
            return true;
        }

        internal void CompleteModernUiLoad(Panel loadingPanel = null)
        {
            this.LoadFailed = false;
            this.UiReady = true;
            if (!IsDisposed && !Disposing)
            {
                if (loadingPanel != null)
                    loadingPanel.Visible = false;
                foreach (Control c in this.Controls)
                {
                    if (c != webView && c != loadingPanel)
                        c.Visible = false;
                }
                if (webView != null)
                {
                    webView.Visible = true;
                    webView.Focus();
                }
            }
        }

        internal static bool IsTerminalProcessFailure(CoreWebView2ProcessFailedKind failureKind)
        {
            return failureKind == CoreWebView2ProcessFailedKind.BrowserProcessExited ||
                failureKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                failureKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive;
        }

        internal bool HandleModernUiProcessFailed(CoreWebView2ProcessFailedKind failureKind, Panel loadingPanel = null, Label lblLoading = null)
        {
            if (!IsTerminalProcessFailure(failureKind))
                return false;

            DiagnosticErrorLogger.Log(
                "Input UI",
                new InvalidOperationException("The WebView2 input process failed (" + failureKind + ")."),
                "The WebView2 input dialog process ended unexpectedly.");
            MarkModernUiLoadFailed(loadingPanel, lblLoading, "The input UI stopped responding. Close this window and try again.");
            return true;
        }

        internal void MarkModernUiLoadFailed(
            Panel loadingPanel = null,
            Label lblLoading = null,
            string failureMessage = "Input UI could not be loaded. Restore the complete release folder and try again.")
        {
            activeNavigationId = null;
            navigationGeneration++;
            this.UiReady = false;
            this.LoadFailed = true;
            ShowLoadingState(loadingPanel, lblLoading, failureMessage);
        }

        internal void SubmitInput(string value)
        {
            this.txtBox.Text = value ?? String.Empty;
            btnAccept_Click(this, EventArgs.Empty);
        }

        internal void CancelInput()
        {
            btnCancel_Click(this, EventArgs.Empty);
        }

        internal void SimulateWindowClose()
        {
            InputForm_FormClosing(this, new FormClosingEventArgs(CloseReason.UserClosing, cancel: false));
        }

        internal Manifest.PassKeyPromptResult ToPassKeyPromptResult()
        {
            return new Manifest.PassKeyPromptResult(
                canceled: this.Canceled,
                value: this.txtBox.Text,
                uiReady: this.UiReady,
                loadFailed: this.LoadFailed,
                accepted: this.Accepted,
                explicitCancel: this.ExplicitCancel);
        }

        internal static bool TryParseWebMessagePayload(string message, out JObject payload, out string action)
        {
            payload = null;
            action = null;
            if (String.IsNullOrEmpty(message) || message.Length > 64 * 1024)
                return false;

            JObject parsedPayload;
            try
            {
                using (StringReader stringReader = new StringReader(message))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                {
                    parsedPayload = JObject.Load(jsonReader);
                }
            }
            catch (JsonException)
            {
                return false;
            }

            JToken actionToken = parsedPayload["action"];
            if (actionToken == null || actionToken.Type != JTokenType.String)
                return false;

            string parsedAction = (string)actionToken;
            if (parsedAction == null || parsedAction.Length > 32)
                return false;

            payload = parsedPayload;
            action = parsedAction;
            return true;
        }

        internal void HandleWebMessageJson(string message, bool isWebViewVisible)
        {
            if (!TryParseWebMessagePayload(message, out JObject payload, out string action))
                return;

            if (action == "accept")
            {
                if (!CanAcceptWebMessage(isWebViewVisible))
                    return;
                if (payload["value"]?.Type != JTokenType.String)
                    return;
                string value = (string)payload["value"];
                if (value == null || value.Length > 4096) return;

                this.txtBox.Text = value;
                btnAccept_Click(this, EventArgs.Empty);
            }
            else if (action == "cancel")
            {
                if (!CanAcceptWebMessage(isWebViewVisible))
                    return;
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
        }

        private async void SetupModernUI(string label, bool isPassword)
        {
            DisableNativeFallbackControls();
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

            ResetModernUiState(loadingPanel, lblLoading);

            try
            {
                CoreWebView2Environment environment = await WebViewEnvironmentProvider.GetAsync();
                if (IsUiCanceledOrDisposed() || webView == null)
                    return;
                await webView.EnsureCoreWebView2Async(environment);
            }
            catch (Exception ex)
            {
                if (IsUiCanceledOrDisposed())
                    return;
                DiagnosticErrorLogger.Log("Input UI", ex, "The WebView2 input dialog could not be initialized.");
                MarkModernUiLoadFailed(loadingPanel, lblLoading);
                return;
            }

            if (IsUiCanceledOrDisposed())
                return;
            if (webView?.CoreWebView2 == null)
            {
                MarkModernUiLoadFailed(loadingPanel, lblLoading);
                return;
            }

            string htmlPath = Path.GetFullPath(Path.Combine(ApplicationPaths.UiDirectory, "input.html"));

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            webView.CoreWebView2.ProcessFailed += (sender, args) =>
            {
                if (IsUiCanceledOrDisposed())
                    return;

                HandleModernUiProcessFailed(args.ProcessFailedKind, loadingPanel, lblLoading);
            };

            webView.NavigationStarting += (sender, args) =>
            {
                if (WebViewSecurityPolicy.IsTrustedLocalDocument(args.Uri, htmlPath))
                {
                    BeginModernUiNavigation(args.NavigationId, loadingPanel, lblLoading);
                    return;
                }

                args.Cancel = true;
                DiagnosticErrorLogger.Log(
                    "Input UI",
                    new InvalidOperationException("An untrusted WebView2 navigation was blocked."),
                    "The input dialog attempted to navigate away from its packaged local document.");
            };

            webView.CoreWebView2.WebMessageReceived += (sender, e) =>
            {
                if (IsUiCanceledOrDisposed() ||
                    !WebViewSecurityPolicy.IsTrustedLocalDocument(e.Source, htmlPath))
                    return;

                HandleWebMessageJson(e.WebMessageAsJson, webView != null && webView.Visible);
            };

            webView.NavigationCompleted += async (sender, args) =>
            {
                if (IsUiCanceledOrDisposed() || webView?.CoreWebView2 == null)
                    return;
                if (!ShouldHandleNavigationCompleted(args.NavigationId, out int expectedGeneration))
                    return;
                if (!args.IsSuccess)
                {
                    TryHandleNavigationCompletedResult(args.NavigationId, expectedGeneration, succeeded: false, loadingPanel, lblLoading);
                    return;
                }
                if (!WebViewSecurityPolicy.IsTrustedLocalDocument(webView.CoreWebView2.Source, htmlPath))
                {
                    TryHandleNavigationCompletedResult(args.NavigationId, expectedGeneration, succeeded: false, loadingPanel, lblLoading);
                    DiagnosticErrorLogger.Log(
                        "Input UI",
                        new InvalidOperationException("WebView2 completed navigation to an untrusted document."),
                        "The input dialog refused to process an untrusted document.");
                    return;
                }

                string jsLabel = JsonConvert.SerializeObject(label);
                string isPassStr = isPassword ? "true" : "false";
                bool setupSucceeded = await ExecuteScriptSafelyAsync($"setupInput({jsLabel}, {isPassStr})", "Input dialog UI", requireTrueResult: true);
                if (IsUiCanceledOrDisposed() || webView == null)
                    return;

                TryHandleNavigationCompletedResult(args.NavigationId, expectedGeneration, setupSucceeded, loadingPanel, lblLoading);
            };

            if (IsUiCanceledOrDisposed() || webView == null)
                return;
            try
            {
                ResetModernUiState(loadingPanel, lblLoading);
                webView.Source = new Uri(htmlPath);
            }
            catch (Exception ex)
            {
                if (IsUiCanceledOrDisposed())
                    return;
                DiagnosticErrorLogger.Log("Input UI", ex, "The WebView2 input dialog could not navigate to its packaged UI.");
                MarkModernUiLoadFailed(loadingPanel, lblLoading);
            }
        }

        private void btnAccept_Click(object sender, EventArgs e)
        {
            this.Accepted = true;
            this.ExplicitCancel = false;
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
            this.Accepted = false;
            this.ExplicitCancel = true;
            this.Canceled = true;
            this.userClosed = false;
            this.Close();
        }

        private void InputForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (this.userClosed)
            {
                // Set Canceled = true when the user hits the X button.
                this.Accepted = false;
                this.ExplicitCancel = false;
                this.Canceled = true;
            }
        }
    }
}
