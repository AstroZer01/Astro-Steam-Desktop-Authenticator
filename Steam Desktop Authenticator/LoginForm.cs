using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using SteamAuth;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Drawing;
using System.IO;
using System.Threading;

namespace Steam_Desktop_Authenticator
{
    public partial class LoginForm : Form
    {
        public SteamGuardAccount account;
        public LoginType LoginReason;
        public SessionData Session;
        public bool RefreshSucceeded { get; private set; }
        internal string ResolvedPassKey { get; private set; }

        internal const string CurrentPassKeyPromptLabel = "Please enter your current encryption passkey.";
        internal const string SetupPassKeyPromptLabel = "Please enter an encryption passkey. Leave blank or hit cancel to not encrypt (VERY INSECURE).";

        private readonly string suppliedPassKey;
        private readonly long? expectedStorageRevision;
        private readonly Func<ulong, CancellationToken, Task<IDisposable>> beginTransferFenceAsync;
        private readonly Action<SteamGuardAccount, string> onAccountCommitted;
        private readonly Action<bool> onIrreversibleTransferStateChanged;
        private readonly Func<bool> isDeferredExitRequested;
        private bool savePasswordRequested;
        public bool SavedPasswordUpdated { get; private set; }
        public ulong? SavedPasswordUpdatedSteamId { get; private set; }

        internal static string GetRefreshLoginExplanation(SteamGuardAccount targetAccount)
        {
            return targetAccount != null && !targetAccount.FullyEnrolled
                ? "Sign in to Steam to verify whether this pending authenticator is active."
                : "Your Steam credentials have expired. For trade and market confirmations to work properly, please login again.";
        }

        internal static bool ShouldEncryptAccountWrite(Manifest manifest, string passKey)
        {
            return (manifest != null && manifest.Entries != null && manifest.Entries.Count > 0 && manifest.Encrypted) ||
                !String.IsNullOrEmpty(passKey);
        }

        internal static bool ShouldDeletePendingOnExplicitCancel(bool finalizationAcceptedBySteam)
        {
            return !finalizationAcceptedBySteam;
        }

        internal static bool RequiresLegacyDeviceCodeProof(LoginType loginReason, SteamGuardAccount targetAccount)
        {
            return loginReason == LoginType.Refresh &&
                targetAccount != null &&
                !targetAccount.FullyEnrolled &&
                String.IsNullOrWhiteSpace(targetAccount.TokenGID) &&
                String.IsNullOrWhiteSpace(targetAccount.DeviceID) &&
                !String.IsNullOrWhiteSpace(targetAccount.SharedSecret);
        }

        internal readonly struct LegacyDeviceCodeLoginResult
        {
            public LegacyDeviceCodeLoginResult(AuthPollResult pollResult, bool deviceCodeAffirmativelyAccepted)
            {
                PollResult = pollResult;
                DeviceCodeAffirmativelyAccepted = deviceCodeAffirmativelyAccepted;
            }

            public AuthPollResult PollResult { get; }
            public bool DeviceCodeAffirmativelyAccepted { get; }
        }

        internal static bool TryApplyVerifiedIdentifiers(
            SteamGuardAccount account,
            AuthenticatorActivationVerificationResult verification,
            out string previousTokenGid,
            out string previousDeviceId)
        {
            previousTokenGid = account?.TokenGID;
            previousDeviceId = account?.DeviceID;
            if (account == null)
                return false;

            if (String.IsNullOrWhiteSpace(account.TokenGID) &&
                !String.IsNullOrWhiteSpace(verification.RemoteTokenGid))
            {
                account.TokenGID = verification.RemoteTokenGid.Trim();
            }

            if (String.IsNullOrWhiteSpace(account.DeviceID) &&
                !String.IsNullOrWhiteSpace(verification.RemoteDeviceIdentifier))
            {
                account.DeviceID = verification.RemoteDeviceIdentifier.Trim();
            }

            // Steam trade confirmations require DeviceID. If neither the local record nor
            // Steam's verified QueryStatus response supplied one, keep the record pending
            // instead of promoting an incomplete authenticator that fails on confirmations.
            if (String.IsNullOrWhiteSpace(account.DeviceID))
            {
                RestoreVerifiedIdentifiers(account, previousTokenGid, previousDeviceId);
                return false;
            }

            return true;
        }

        internal static void RestoreVerifiedIdentifiers(
            SteamGuardAccount account,
            string previousTokenGid,
            string previousDeviceId)
        {
            if (account == null)
                return;

            account.TokenGID = previousTokenGid;
            account.DeviceID = previousDeviceId;
        }

        internal static bool IsExpectedLoginCancellation(Exception ex, CancellationToken cancellationToken)
        {
            return ex is AuthenticatorAlreadyLinkedException ||
                ex is LoginPromptCanceledException ||
                (ex is OperationCanceledException && cancellationToken.IsCancellationRequested);
        }

        internal static async Task<LegacyDeviceCodeLoginResult> CompleteLegacyDeviceCodeLoginAsync(
            Func<CancellationToken, Task<string>> generateDeviceCodeAsync,
            Func<string, CancellationToken, Task<EResult>> submitDeviceCodeAsync,
            Func<CancellationToken, Task<AuthPollResult>> pollAuthSessionStatusAsync,
            TimeSpan pollingInterval,
            CancellationToken cancellationToken,
            TimeSpan? deadline = null,
            int maxMismatchSubmissions = 3,
            Func<TimeSpan, CancellationToken, Task> delayAsync = null,
            Func<TimeSpan, CancellationToken, CancellationTokenSource> createTimeoutCts = null)
        {
            if (generateDeviceCodeAsync == null || submitDeviceCodeAsync == null || pollAuthSessionStatusAsync == null)
                return new LegacyDeviceCodeLoginResult(null, false);

            int effectiveMaxMismatchSubmissions = Math.Max(1, maxMismatchSubmissions);
            bool hasExplicitOverallDeadline = deadline.HasValue && deadline.Value > TimeSpan.Zero;
            DateTime explicitOverallDeadlineUtc = hasExplicitOverallDeadline
                ? DateTime.UtcNow.Add(deadline.Value)
                : DateTime.MaxValue;
            TimeSpan effectiveSubmissionDeadline = hasExplicitOverallDeadline
                ? deadline.Value
                : TimeSpan.FromSeconds(Math.Max(30, 35 * effectiveMaxMismatchSubmissions));
            TimeSpan effectiveInterval = pollingInterval > TimeSpan.Zero
                ? pollingInterval
                : TimeSpan.FromSeconds(1);
            Func<TimeSpan, CancellationToken, Task> effectiveDelayAsync = delayAsync ?? ((delay, ct) => Task.Delay(delay, ct));
            Func<TimeSpan, CancellationToken, CancellationTokenSource> effectiveCreateTimeoutCts = createTimeoutCts ??
                ((duration, parentToken) =>
                {
                    CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
                    cts.CancelAfter(duration);
                    return cts;
                });

            bool deviceCodeAffirmativelyAccepted = false;

            // Phase 1: submit/retry the local Steam Guard code, bounded strictly to the submission CTS.
            using (CancellationTokenSource submissionCts = effectiveCreateTimeoutCts(effectiveSubmissionDeadline, cancellationToken))
            {
                CancellationToken submissionToken = submissionCts.Token;

                try
                {
                    bool readyToPoll = false;
                    int mismatchCount = 0;
                    string lastSubmittedCode = null;

                    while (!readyToPoll)
                    {
                        submissionToken.ThrowIfCancellationRequested();
                        string code = await generateDeviceCodeAsync(submissionToken);
                        if (String.IsNullOrWhiteSpace(code))
                            return new LegacyDeviceCodeLoginResult(null, false);

                        if (lastSubmittedCode != null && String.Equals(code, lastSubmittedCode, StringComparison.Ordinal))
                        {
                            AuthPollResult racedPoll = await pollAuthSessionStatusAsync(submissionToken);
                            if (racedPoll != null)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                return new LegacyDeviceCodeLoginResult(racedPoll, deviceCodeAffirmativelyAccepted: false);
                            }

                            await effectiveDelayAsync(effectiveInterval, submissionToken);
                            continue;
                        }

                        lastSubmittedCode = code;
                        EResult submitResult = await submitDeviceCodeAsync(code, submissionToken);

                        if (submitResult == EResult.OK)
                        {
                            // Only an affirmative EResult.OK proves Steam accepted this local record's TOTP code.
                            deviceCodeAffirmativelyAccepted = true;
                            readyToPoll = true;
                            break;
                        }

                        if (submitResult == EResult.DuplicateRequest)
                        {
                            // Another confirmation (such as mobile approval) already raced ahead.
                            // Poll directly without resubmitting a code and without treating this as local TOTP proof.
                            deviceCodeAffirmativelyAccepted = false;
                            readyToPoll = true;
                            break;
                        }

                        if (submitResult == EResult.TwoFactorCodeMismatch)
                        {
                            mismatchCount++;
                            AuthPollResult racedPoll = await pollAuthSessionStatusAsync(submissionToken);
                            if (racedPoll != null)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                return new LegacyDeviceCodeLoginResult(racedPoll, deviceCodeAffirmativelyAccepted: false);
                            }

                            if (mismatchCount >= effectiveMaxMismatchSubmissions)
                                throw new AuthenticationException("Steam rejected the generated Steam Guard code.", EResult.TwoFactorCodeMismatch);

                            await effectiveDelayAsync(effectiveInterval, submissionToken);
                            continue;
                        }

                        if (submitResult == EResult.RateLimitExceeded ||
                            submitResult == EResult.AccountLoginDeniedThrottle)
                        {
                            throw new AuthenticationException("Steam rate-limited this sign-in attempt.", submitResult);
                        }

                        // Steam returned a non-device-code error (for example when the account uses EmailCode or has no mobile authenticator).
                        // Fall back to standard SteamKit polling without marking local device-code proof.
                        return new LegacyDeviceCodeLoginResult(null, deviceCodeAffirmativelyAccepted: false);
                    }
                }
                catch (OperationCanceledException) when (submissionCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Steam did not finalize authentication before the deadline.");
                }
            }

            // Phase 2: poll for tokens with an independent 30-second budget on the default production path
            // (or the remaining explicit overall deadline when a caller supplied one).
            TimeSpan effectivePollDeadline;
            if (hasExplicitOverallDeadline)
            {
                effectivePollDeadline = explicitOverallDeadlineUtc - DateTime.UtcNow;
                if (effectivePollDeadline <= TimeSpan.Zero)
                    throw new TimeoutException("Steam did not finalize authentication before the deadline.");
            }
            else
            {
                effectivePollDeadline = TimeSpan.FromSeconds(30);
            }

            using (CancellationTokenSource pollDeadlineCts = effectiveCreateTimeoutCts(effectivePollDeadline, cancellationToken))
            {
                CancellationToken pollToken = pollDeadlineCts.Token;

                try
                {
                    while (true)
                    {
                        pollToken.ThrowIfCancellationRequested();
                        AuthPollResult pollResult = await pollAuthSessionStatusAsync(pollToken);
                        if (pollResult != null)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return new LegacyDeviceCodeLoginResult(pollResult, deviceCodeAffirmativelyAccepted);
                        }

                        await effectiveDelayAsync(effectiveInterval, pollToken);
                    }
                }
                catch (OperationCanceledException) when (pollDeadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Steam did not finalize authentication before the deadline.");
                }
            }
        }

        public LoginForm(
            LoginType loginReason = LoginType.Initial,
            SteamGuardAccount account = null,
            string passKey = null,
            long? expectedStorageRevision = null,
            Func<ulong, CancellationToken, Task<IDisposable>> beginTransferFenceAsync = null,
            Action<SteamGuardAccount, string> onAccountCommitted = null,
            Action<bool> onIrreversibleTransferStateChanged = null,
            Func<bool> isDeferredExitRequested = null)
        {
            InitializeComponent();
            this.LoginReason = loginReason;
            this.account = account;
            this.suppliedPassKey = passKey;
            this.expectedStorageRevision = expectedStorageRevision;
            this.beginTransferFenceAsync = beginTransferFenceAsync;
            this.onAccountCommitted = onAccountCommitted;
            this.onIrreversibleTransferStateChanged = onIrreversibleTransferStateChanged;
            this.isDeferredExitRequested = isDeferredExitRequested;

            try
            {
                if (this.account != null)
                {
                    txtUsername.Text = account.AccountName;
                    txtUsername.Enabled = false;
                }

                if (this.LoginReason == LoginType.Refresh)
                {
                    labelLoginExplanation.Text = GetRefreshLoginExplanation(account);
                }
                else if (this.LoginReason == LoginType.Import)
                {
                    labelLoginExplanation.Text = "Please login to your Steam account import it.";
                }
                else if (this.LoginReason == LoginType.Transfer)
                {
                    labelLoginExplanation.Text = "Transfer your existing Steam Mobile Authenticator to Astro SDA via SMS verification. Steam applies a 2-day trade and market hold after transfer.";
                }
            }
            catch (Exception)
            {
                AstroMessageBox.Show("Failed to find your account. Try closing and re-opening SDA.", "Login Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }

            SetupModernUI();
        }

        private WebView2 webView;
        private string trustedUiDocumentPath;
        private const int SteamConnectionTimeoutMilliseconds = 20000;
        private CancellationTokenSource loginCancellationSource;
        private SteamClient activeSteamClient;
        private volatile bool transferContinueInFlight;
        private volatile bool deferredCloseRequested;

        internal bool ShouldCloseForDeferredExit()
        {
            return deferredCloseRequested || (isDeferredExitRequested?.Invoke() ?? false);
        }

        private void CompleteTransferNonClosingOutcome(CancellationToken attemptCancellationToken)
        {
            if (ShouldCloseForDeferredExit())
            {
                if (!IsDisposed && !Disposing)
                    this.Close();
                return;
            }

            ResetLoginButton(attemptCancellationToken);
        }

        internal void SetIrreversibleTransferInFlight(bool inFlight)
        {
            bool changed = transferContinueInFlight != inFlight;
            transferContinueInFlight = inFlight;
            if (changed)
                onIrreversibleTransferStateChanged?.Invoke(inFlight);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (transferContinueInFlight &&
                e.CloseReason != CloseReason.WindowsShutDown &&
                e.CloseReason != CloseReason.TaskManagerClosing)
            {
                e.Cancel = true;
                if (e.CloseReason == CloseReason.ApplicationExitCall || e.CloseReason == CloseReason.FormOwnerClosing)
                    deferredCloseRequested = true;
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    AstroMessageBox.Show(
                        "Completing authenticator transfer with Steam — please wait a few seconds so your replacement secrets are received and staged safely.",
                        "Authenticator Transfer In Progress",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return;
            }

            loginCancellationSource?.Cancel();
            activeSteamClient?.Disconnect();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (webView != null)
            {
                webView.Dispose();
                webView = null;
            }

            if (loginCancellationSource != null)
            {
                loginCancellationSource.Dispose();
                loginCancellationSource = null;
            }

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

        private async void SetupModernUI()
        {
            this.Size = new Size(450, 750);
            this.MinimumSize = new Size(450, 750);
            this.MaximumSize = new Size(450, 750);
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.BackColor = Color.FromArgb(11, 19, 38);
            this.Text = "Astro SDA - Login";

            Panel loadingPanel = new Panel();
            loadingPanel.Dock = DockStyle.Fill;
            loadingPanel.BackColor = Color.FromArgb(11, 19, 38);
            Label lblLoading = new Label() { Text = "Loading Astro UI...", ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
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
                DiagnosticErrorLogger.Log("Login UI", ex, "The WebView2 login dialog could not be initialized.");
                if (!IsDisposed && !Disposing)
                    lblLoading.Text = "Login UI could not be loaded. Restore the complete release folder and try again.";
                return;
            }

            if (IsDisposed || loginCancellationSource?.IsCancellationRequested == true || webView?.CoreWebView2 == null)
                return;

            string htmlPath = Path.GetFullPath(Path.Combine(ApplicationPaths.UiDirectory, "login.html"));
            trustedUiDocumentPath = htmlPath;

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            webView.NavigationStarting += (sender, args) =>
            {
                if (WebViewSecurityPolicy.IsTrustedLocalDocument(args.Uri, htmlPath))
                    return;

                args.Cancel = true;
                DiagnosticErrorLogger.Log(
                    "Login UI",
                    new InvalidOperationException("An untrusted WebView2 navigation was blocked."),
                    "The login dialog attempted to navigate away from its packaged local document.");
            };

            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            webView.NavigationCompleted += (sender, args) =>
            {
                if (IsDisposed || loginCancellationSource?.IsCancellationRequested == true || webView?.CoreWebView2 == null)
                    return;
                if (!args.IsSuccess)
                {
                    if (!IsDisposed && !Disposing)
                        lblLoading.Text = "Login UI could not be loaded. Restore the complete release folder and try again.";
                    return;
                }
                if (!WebViewSecurityPolicy.IsTrustedLocalDocument(webView.CoreWebView2.Source, htmlPath))
                {
                    if (!IsDisposed && !Disposing)
                        lblLoading.Text = "Login UI could not be loaded. Restore the complete release folder and try again.";
                    DiagnosticErrorLogger.Log(
                        "Login UI",
                        new InvalidOperationException("WebView2 completed navigation to an untrusted document."),
                        "The login dialog refused to expose credentials to an untrusted document.");
                    return;
                }

                loadingPanel.Visible = false;
                foreach (Control c in this.Controls)
                {
                    if (c != webView && c != loadingPanel)
                        c.Visible = false;
                }
                webView.Visible = true;

                // Push initial values to JS
                string jsExp = Newtonsoft.Json.JsonConvert.SerializeObject(labelLoginExplanation.Text);
                _ = ExecuteScriptSafelyAsync($"setExplanation({jsExp})", "Login explanation UI");
                
                if (this.account != null)
                {
                    string jsAccountName = Newtonsoft.Json.JsonConvert.SerializeObject(account.AccountName ?? String.Empty);
                    _ = ExecuteScriptSafelyAsync($"setUsername({jsAccountName}, true)", "Login account UI");
                }

                Manifest currentManifest = Manifest.GetManifest();
                bool allowSavePassword = currentManifest != null &&
                    currentManifest.Encrypted &&
                    currentManifest.AllowSavedSteamPasswords &&
                    this.LoginReason != LoginType.Import;
                _ = ExecuteScriptSafelyAsync($"setSavePasswordOption({allowSavePassword.ToString().ToLowerInvariant()})", "Login save password UI");
            };

            if (IsDisposed || loginCancellationSource?.IsCancellationRequested == true || webView == null)
                return;
            webView.Source = new Uri(trustedUiDocumentPath);
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson;
            if (IsDisposed ||
                !WebViewSecurityPolicy.IsTrustedLocalDocument(e.Source, trustedUiDocumentPath) ||
                !InputForm.TryParseWebMessagePayload(message, out JObject payload, out string action))
                return;

            if (action == "login")
            {
                if (transferContinueInFlight || !btnSteamLogin.Enabled)
                    return;

                if (payload["user"]?.Type != JTokenType.String || payload["pass"]?.Type != JTokenType.String)
                    return;

                // The WebView is untrusted input. During refresh the account name
                // is part of the selected managed account and cannot be changed by
                // editing or tampering with the request payload.
                string username = LoginReason == LoginType.Refresh
                    ? account?.AccountName
                    : payload.Value<string>("user");
                string password = payload.Value<string>("pass");
                if (username == null || password == null || username.Length > 256 || password.Length > 4096)
                    return;

                savePasswordRequested = payload["savePassword"]?.Type == JTokenType.Boolean && payload.Value<bool>("savePassword");
                txtUsername.Text = username;
                txtPassword.Text = password;
                btnSteamLogin_Click(this, EventArgs.Empty);
            }
        }

        public void SetUsername(string username)
        {
            txtUsername.Text = username;
        }

        public string FilterPhoneNumber(string phoneNumber)
        {
            return phoneNumber.Replace("-", "").Replace("(", "").Replace(")", "");
        }

        public bool PhoneNumberOkay(string phoneNumber)
        {
            if (phoneNumber == null || phoneNumber.Length == 0) return false;
            if (phoneNumber[0] != '+') return false;
            return true;
        }

        private void ResetLoginButton(CancellationToken attemptCancellationToken)
        {
            if (IsDisposed || loginCancellationSource == null)
                return;

            try
            {
                if (loginCancellationSource.Token != attemptCancellationToken)
                    return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            btnSteamLogin.Enabled = true;
            btnSteamLogin.Text = "Login";
            _ = ExecuteScriptSafelyAsync("setButtonState('LOGIN', false)", "Login button UI");
        }

        private static async Task WaitForSteamConnectionAsync(SteamClient steamClient, CancellationToken cancellationToken)
        {
            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(SteamConnectionTimeoutMilliseconds);
            while (!steamClient.IsConnected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadlineUtc)
                    throw new TimeoutException("SteamKit did not establish a connection before the login deadline.");

                await Task.Delay(250, cancellationToken);
            }
        }

        private async void btnSteamLogin_Click(object sender, EventArgs e)
        {
            if (transferContinueInFlight || !btnSteamLogin.Enabled)
                return;

            // Disable button while we login
            btnSteamLogin.Enabled = false;
            btnSteamLogin.Text = "Logging in...";
            _ = ExecuteScriptSafelyAsync("setButtonState('LOGGING IN...', true)", "Login button UI");

            string username = txtUsername.Text;
            string password = txtPassword.Text;

            // A prior connection attempt can still be waiting on SteamKit. Cancel and
            // disconnect it before replacing the active client, but do not dispose its
            // source while that attempt may still be observing the token.
            loginCancellationSource?.Cancel();
            activeSteamClient?.Disconnect();
            loginCancellationSource = new CancellationTokenSource();
            CancellationToken cancellationToken = loginCancellationSource.Token;

            // Start a new SteamClient instance and bound the connection wait.
            SteamClient steamClient;
            try
            {
                steamClient = new SteamClient(ProxyService.CreateSteamKitConfiguration());
            }
            catch (InvalidOperationException ex)
            {
                AstroMessageBox.Show(ex.Message, "Steam Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ResetLoginButton(cancellationToken);
                return;
            }
            activeSteamClient = steamClient;
            try
            {
                steamClient.Connect();
                await WaitForSteamConnectionAsync(steamClient, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DiagnosticErrorLogger.Log("Steam login", new OperationCanceledException(), "The sign-in was canceled before Steam connected.");
                ResetLoginButton(cancellationToken);
                return;
            }
            catch (TimeoutException ex)
            {
                DiagnosticErrorLogger.Log("Steam login", ex, "Steam did not connect before the configured deadline.");
                AstroMessageBox.Show("Steam did not connect in time. Check your connection and try again.", "Steam Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ResetLoginButton(cancellationToken);
                return;
            }

            // Create a new auth session
            CredentialsAuthSession authSession;
            try
            {
                authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    DeviceFriendlyName = "AstroSDA",
                    IsPersistentSession = LoginReason == LoginType.Transfer,
                    PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                    ClientOSType = EOSType.Android9,
                    Authenticator = new UserFormAuthenticator(this.account, this, cancellationToken, allowInteractiveDeviceAuth: LoginReason == LoginType.Transfer),
                });
            }
            catch (Exception ex) when (IsExpectedLoginCancellation(ex, cancellationToken))
            {
                ResetLoginButton(cancellationToken);
                return;
            }
            catch (AuthenticationException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ResetLoginButton(cancellationToken);
                    return;
                }
                DiagnosticErrorLogger.Log("Steam login polling", ex, "Steam rejected the sign-in or confirmation code.");
                AstroMessageBox.Show(GetLoginErrorMessage(ex), "Steam Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ResetLoginButton(cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ResetLoginButton(cancellationToken);
                    return;
                }
                DiagnosticErrorLogger.Log("Steam login start", ex, "Steam could not start the login request.");
                AstroMessageBox.Show("Steam could not start the sign-in request. Check your connection and try again.", "Steam Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ResetLoginButton(cancellationToken);
                return;
            }

            // Starting polling Steam for authentication response
            bool legacyDeviceCodeAffirmativelyAccepted = false;
            AuthPollResult pollResponse = null;
            try
            {
                if (RequiresLegacyDeviceCodeProof(LoginReason, this.account) &&
                    this.account?.Session != null &&
                    authSession.SteamID.ConvertToUInt64() == this.account.Session.SteamID)
                {
                    SteamKit2.Internal.Authentication authService = steamClient
                        .GetHandler<SteamUnifiedMessages>()
                        ?.CreateService<SteamKit2.Internal.Authentication>();
                    if (authService != null)
                    {
                        LegacyDeviceCodeLoginResult legacyResult = await CompleteLegacyDeviceCodeLoginAsync(
                            ct => this.account.GenerateSteamGuardCodeAsync(ct),
                            async (code, ct) =>
                            {
                                var response = await authService.UpdateAuthSessionWithSteamGuardCode(
                                    new CAuthentication_UpdateAuthSessionWithSteamGuardCode_Request
                                    {
                                        client_id = authSession.ClientID,
                                        steamid = authSession.SteamID.ConvertToUInt64(),
                                        code = code,
                                        code_type = EAuthSessionGuardType.k_EAuthSessionGuardType_DeviceCode
                                    }).ToTask().WaitAsync(ct);
                                return response.Result;
                            },
                            ct => authSession.PollAuthSessionStatusAsync().WaitAsync(ct),
                            authSession.PollingInterval,
                            cancellationToken);

                        pollResponse = legacyResult.PollResult;
                        legacyDeviceCodeAffirmativelyAccepted = legacyResult.DeviceCodeAffirmativelyAccepted;
                    }
                }

                if (pollResponse == null)
                {
                    legacyDeviceCodeAffirmativelyAccepted = false;
                    pollResponse = await authSession.PollingWaitForResultAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (IsExpectedLoginCancellation(ex, cancellationToken))
            {
                ResetLoginButton(cancellationToken);
                return;
            }
            catch (AuthenticationException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ResetLoginButton(cancellationToken);
                    return;
                }
                AstroMessageBox.Show(GetLoginErrorMessage(ex), "Steam Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ResetLoginButton(cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ResetLoginButton(cancellationToken);
                    return;
                }
                DiagnosticErrorLogger.Log("Steam login polling", ex, "Steam could not complete the login request.");
                AstroMessageBox.Show("Steam could not complete the sign-in. Check your connection and try again.", "Steam Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ResetLoginButton(cancellationToken);
                return;
            }

            // Build a SessionData object
            SessionData sessionData = new SessionData()
            {
                SteamID = authSession.SteamID.ConvertToUInt64(),
                AccessToken = pollResponse.AccessToken,
                RefreshToken = pollResponse.RefreshToken,
            };

            //Login succeeded
            this.Session = sessionData;

            // If we're only logging in for an account import, stop here
            if (LoginReason == LoginType.Import)
            {
                this.Close();
                return;
            }

            // If we're only logging in for a session refresh (or verifying a pending account) then save it and exit
            if (LoginReason == LoginType.Refresh)
            {
                if (account?.Session == null || sessionData.SteamID == 0 || sessionData.SteamID != account.Session.SteamID)
                {
                    AstroMessageBox.Show("The Steam account that authenticated does not match the account being renewed. The existing session was kept.", "Steam Login Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ResetLoginButton(cancellationToken);
                    return;
                }

                Manifest man = Manifest.GetManifest();
                if (!account.FullyEnrolled)
                {
                    if (!TryResolveManifestPassKey(man, suppliedPassKey, out string resolvedKey))
                    {
                        ResetLoginButton(cancellationToken);
                        return;
                    }

                    PendingAuthenticatorRecoveryResult pendingRecovery;
                    try
                    {
                        pendingRecovery = await TryRecoverPendingAccountWithFreshSessionAsync(
                            man,
                            account,
                            sessionData,
                            resolvedKey,
                            (candidate, ct) => candidate.VerifyAuthenticatorActivationDetailsAsync(
                                ct,
                                allowUncorroboratedActiveWhenDeviceCodeVerified: legacyDeviceCodeAffirmativelyAccepted),
                            cancellationToken,
                            expectedStorageRevision);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        ResetLoginButton(cancellationToken);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested || IsDisposed || Disposing)
                        {
                            ResetLoginButton(cancellationToken);
                            return;
                        }
                        DiagnosticErrorLogger.Log("Authenticator recovery", ex, "Unexpected failure while verifying pending authenticator activation.");
                        AstroMessageBox.Show(
                            "Steam could not verify whether this pending authenticator is active right now. Your pending record was kept safely; try again later.",
                            "Steam Guard Setup",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        ResetLoginButton(cancellationToken);
                        return;
                    }
                    if (IsDisposed || Disposing)
                        return;

                    switch (pendingRecovery.Status)
                    {
                        case PendingAuthenticatorRecoveryStatus.Recovered:
                            bool recoveredEncrypt = ShouldEncryptAccountWrite(man, resolvedKey);
                            ResolvedPassKey = recoveredEncrypt ? resolvedKey : null;
                            TrySaveVerifiedPasswordAfterCommit(man, sessionData.SteamID, password, ResolvedPassKey, showRefreshNotice: false);
                            RefreshSucceeded = true;
                            AstroMessageBox.Show(
                                "Steam confirmed this authenticator is active. Your saved authenticator record has been recovered and activated.",
                                "Steam Guard Setup",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                            this.Close();
                            return;

                        case PendingAuthenticatorRecoveryStatus.SaveFailed:
                            DiagnosticErrorLogger.Log(
                                "Authenticator storage",
                                pendingRecovery.SaveResult?.Exception,
                                "Steam confirmed the pending authenticator is active, but the updated record could not be saved.");
                            AstroMessageBox.Show(
                                pendingRecovery.SaveResult?.UserMessage ?? "Steam confirmed this authenticator is active, but the updated local record could not be saved.",
                                "Steam Guard Setup",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            ResetLoginButton(cancellationToken);
                            return;

                        case PendingAuthenticatorRecoveryStatus.Inconclusive:
                            AstroMessageBox.Show(
                                "Steam could not verify whether this pending authenticator is active right now. Your pending record was kept safely; try again later.",
                                "Steam Guard Setup",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                            ResetLoginButton(cancellationToken);
                            return;

                        case PendingAuthenticatorRecoveryStatus.ConfirmedMismatch:
                        default:
                            string mismatchMessage = pendingRecovery.ActivationStatus == AuthenticatorActivationStatus.ActiveForDifferentAuthenticator
                                ? "Steam reports that a different authenticator is active on this account, so this pending record was kept inactive."
                                : "Steam reports that this authenticator has not been activated yet. Remove this pending record or complete Steam Guard setup.";
                            AstroMessageBox.Show(mismatchMessage, "Steam Guard Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            this.Close();
                            return;
                    }
                }

                string usedPassKey = null;
                if (!ApplyRefreshedSession(account, sessionData, () => SaveRefreshedSession(man, suppliedPassKey, expectedStorageRevision, out usedPassKey)))
                {
                    ResetLoginButton(cancellationToken);
                    return;
                }
                TrySaveVerifiedPasswordAfterCommit(man, account.Session.SteamID, password, usedPassKey, showRefreshNotice: true);
                RefreshSucceeded = true;
                this.Close();
                return;
            }

            if (LoginReason == LoginType.Transfer)
            {
                await ExecuteAuthenticatorTransferAsync(sessionData, username, password, cancellationToken);
                return;
            }

            // Resolve encryption passkey before starting Steam authenticator enrollment
            // so closing or failing the prompt never abandons a newly issued revocation
            // code, while still deferring first-account encryption commit until SaveAccount.
            Manifest manifest = Manifest.GetManifest();
            AuthenticatorLinker linker = new AuthenticatorLinker(sessionData);
            linker.FinalizationProgress += UpdateFinalizationProgress;

            AuthenticatorEnrollmentCoordinator coordinator = new AuthenticatorEnrollmentCoordinator(
                linker,
                new LoginPhoneEnrollmentInteraction(account, this));
            InitialEnrollmentPreparation preparation;
            try
            {
                preparation = await PrepareInitialEnrollmentAsync(
                    () =>
                    {
                        bool resolved = TryResolveManifestPassKey(manifest, suppliedPassKey, out string resolvedKey);
                        return (resolved, resolvedKey);
                    },
                    ct => coordinator.StartAsync(ct),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ResetLoginButton(cancellationToken);
                return;
            }
            if (IsDisposed || Disposing)
                return;
            if (!preparation.PassKeyResolved)
            {
                this.Close();
                return;
            }

            string passKey = preparation.PassKey;
            AuthenticatorEnrollmentOutcome enrollmentOutcome = preparation.EnrollmentOutcome;
            switch (enrollmentOutcome.Result)
            {
                case AuthenticatorEnrollmentResult.AwaitingFinalization:
                    break;

                case AuthenticatorEnrollmentResult.AuthenticatorPresent:
                    PendingAuthenticatorRecoveryResult recoveryResult;
                    try
                    {
                        recoveryResult = await TryRecoverExistingPendingAuthenticatorAsync(
                            manifest,
                            sessionData,
                            passKey,
                            (candidate, ct) => candidate.VerifyAuthenticatorActivationDetailsAsync(ct),
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        ResetLoginButton(cancellationToken);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested || IsDisposed || Disposing)
                        {
                            ResetLoginButton(cancellationToken);
                            return;
                        }
                        DiagnosticErrorLogger.Log("Authenticator recovery", ex, "Unexpected failure while checking for an existing pending authenticator.");
                        AstroMessageBox.Show(
                            "Steam could not verify whether your existing pending authenticator is active right now. Your pending record was kept safely; try again later.",
                            "Steam Guard Setup",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        ResetLoginButton(cancellationToken);
                        return;
                    }
                    if (IsDisposed || Disposing)
                        return;
                    if (recoveryResult.Status == PendingAuthenticatorRecoveryStatus.Recovered)
                    {
                        bool recoveredEncrypt = ShouldEncryptAccountWrite(manifest, passKey);
                        ResolvedPassKey = recoveredEncrypt ? passKey : null;
                        TrySaveVerifiedPasswordAfterCommit(manifest, sessionData.SteamID, password, ResolvedPassKey, showRefreshNotice: false);
                        AstroMessageBox.Show(
                            "This authenticator is already active on Steam. Your saved authenticator record has been recovered and activated.",
                            "Steam Guard Setup",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        this.Close();
                        return;
                    }
                    if (recoveryResult.Status == PendingAuthenticatorRecoveryStatus.SaveFailed)
                    {
                        DiagnosticErrorLogger.Log(
                            "Authenticator storage",
                            recoveryResult.SaveResult?.Exception,
                            "Steam confirmed the pending authenticator is active, but the updated record could not be saved.");
                        AstroMessageBox.Show(
                            recoveryResult.SaveResult?.UserMessage ?? "Steam confirmed this authenticator is active, but the updated local record could not be saved.",
                            "Steam Guard Setup",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        this.Close();
                        return;
                    }
                    if (recoveryResult.Status == PendingAuthenticatorRecoveryStatus.Inconclusive)
                    {
                        AstroMessageBox.Show(
                            "Steam reported an existing authenticator, and Astro SDA could not verify whether your saved pending record is already active. Your pending record was kept safely; try again later before removing Steam Guard.",
                            "Steam Guard Setup",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        ResetLoginButton(cancellationToken);
                        return;
                    }
                    AstroMessageBox.Show("This account already has an authenticator linked. You must remove that authenticator to add SDA as your authenticator.", "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    this.Close();
                    return;

                case AuthenticatorEnrollmentResult.Canceled:
                    ResetLoginButton(cancellationToken);
                    return;

                case AuthenticatorEnrollmentResult.Failed:
                default:
                    string enrollmentError = String.IsNullOrWhiteSpace(enrollmentOutcome.ErrorMessage)
                        ? "Steam did not accept the authenticator enrollment request. Please try again later."
                        : enrollmentOutcome.ErrorMessage;
                    DiagnosticErrorLogger.Log(
                        "Authenticator enrollment",
                        new InvalidOperationException(enrollmentError),
                        "Steam rejected or could not complete authenticator enrollment.");
                    AstroMessageBox.Show("Error adding your authenticator: " + enrollmentError, "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ResetLoginButton(cancellationToken);
                    return;
            }

            //Save the file immediately; losing this would be bad.
            bool encrypt = ShouldEncryptAccountWrite(manifest, passKey);
            StorageResult initialSaveResult = manifest.SaveAccount(linker.LinkedAccount, encrypt, passKey);
            if (IsDisposed || Disposing)
                return;
            if (!initialSaveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", initialSaveResult.Exception, "The initial authenticator record could not be saved.");
                AstroMessageBox.Show(initialSaveResult.UserMessage ?? "Unable to save the mobile authenticator file. The setup was stopped before finalization.");
                this.Close();
                return;
            }
            long? pendingStorageRevision = initialSaveResult.CommittedStorageRevision;
            ResolvedPassKey = encrypt ? passKey : null;

            ShowRecoveryCode(linker.LinkedAccount, "The mobile authenticator is not linked yet. Save this recovery code before continuing.", true);

            AuthenticatorLinker.FinalizeResult finalizeResponse = AuthenticatorLinker.FinalizeResult.GeneralFailure;
            bool previousFinalizationCodeWasInvalid = false;
            while (finalizeResponse != AuthenticatorLinker.FinalizeResult.Success &&
                finalizeResponse != AuthenticatorLinker.FinalizeResult.FinalizedStatusUnverified &&
                finalizeResponse != AuthenticatorLinker.FinalizeResult.FinalizedIdentityMismatch)
            {
                string confirmationDestination = linker.FinalizationConfirmationType == AuthenticatorLinker.ConfirmationCodeType.Email
                    ? "email address associated with your Steam account"
                    : linker.FinalizationConfirmationType == AuthenticatorLinker.ConfirmationCodeType.SMS
                        ? "phone number associated with your Steam account"
                        : "confirmation method selected by Steam";
                string confirmationCode;
                using (InputForm confirmationCodeForm = new InputForm(
                    previousFinalizationCodeWasInvalid
                        ? "That confirmation code was not accepted. Please enter the correct code sent to your " + confirmationDestination + "."
                        : "Please input the confirmation code sent to your " + confirmationDestination + "."))
                {
                    confirmationCodeForm.ShowInputDialog(this);
                    Manifest.PassKeyPromptResult confirmationPromptResult = confirmationCodeForm.ToPassKeyPromptResult();
                    ConfirmationCodePromptDecision promptDecision = EvaluateConfirmationCodePrompt(
                        confirmationPromptResult,
                        () => AstroMessageBox.Show(
                            GetConfirmationCodeRetryPromptMessage(confirmationPromptResult),
                            "Steam Guard Setup",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning) == DialogResult.Yes);

                    if (promptDecision.Action == ConfirmationCodePromptAction.RetryPrompt)
                        continue;

                    if (promptDecision.Action == ConfirmationCodePromptAction.KeepPendingAndStop)
                    {
                        this.Close();
                        return;
                    }

                    if (promptDecision.Action == ConfirmationCodePromptAction.CancelAndRemovePending)
                    {
                        if (!ShouldDeletePendingOnExplicitCancel(linker.FinalizationAcceptedBySteam))
                        {
                            AstroMessageBox.Show(
                                "Steam already accepted authenticator finalization, so your pending authenticator record and recovery code were kept safely. Use \"Verify Steam activation\" on the account row to finish activating it.",
                                "Steam Guard Setup",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                            this.Close();
                            return;
                        }
                        if (!manifest.RemoveAccount(linker.LinkedAccount, passKey, expectedStorageRevision: pendingStorageRevision))
                        {
                            const string removalError = "The unfinished authenticator record could not be removed safely. It may still be present in the app; restart the app before making further account changes.";
                            DiagnosticErrorLogger.Log("Authenticator storage", new InvalidOperationException(removalError), "Removing the unfinished authenticator record after setup cancellation failed.");
                            AstroMessageBox.Show(removalError, "Steam Guard Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            ResetLoginButton(cancellationToken);
                            return;
                        }
                        if (!manifest.Encrypted)
                            ResolvedPassKey = null;
                        this.Close();
                        return;
                    }

                    confirmationCode = promptDecision.ConfirmationCode;
                }
                try
                {
                    finalizeResponse = await linker.FinalizeAddAuthenticator(confirmationCode, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ResetLoginButton(cancellationToken);
                    return;
                }

                if (finalizeResponse != AuthenticatorLinker.FinalizeResult.Success &&
                    finalizeResponse != AuthenticatorLinker.FinalizeResult.FinalizedStatusUnverified &&
                    finalizeResponse != AuthenticatorLinker.FinalizeResult.FinalizedIdentityMismatch)
                {
                    DiagnosticErrorLogger.Log(
                        "Authenticator finalization",
                        new InvalidOperationException(linker.LastErrorMessage ?? "Steam did not finalize the authenticator."),
                        "Steam rejected or could not confirm authenticator finalization.");
                }

                switch (finalizeResponse)
                {
                    case AuthenticatorLinker.FinalizeResult.BadConfirmationCode:
                        AstroMessageBox.Show(
                            String.IsNullOrWhiteSpace(linker.LastErrorMessage)
                                ? "Steam did not accept that confirmation code. Enter the current code sent to your email or phone and try again."
                                : linker.LastErrorMessage,
                            "Invalid Confirmation Code",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        previousFinalizationCodeWasInvalid = true;
                        continue;

                    case AuthenticatorLinker.FinalizeResult.UnableToGenerateCorrectCodes:
                        AstroMessageBox.Show("Unable to generate the proper codes to finalize this authenticator. The authenticator should not have been linked. In the off-chance it was, please write down your revocation code, as this is the last chance to see it: " + linker.LinkedAccount.RevocationCode);
                        if (ShouldDeletePendingOnExplicitCancel(linker.FinalizationAcceptedBySteam))
                        {
                            manifest.RemoveAccount(linker.LinkedAccount, passKey, expectedStorageRevision: pendingStorageRevision);
                            if (!manifest.Encrypted)
                                ResolvedPassKey = null;
                        }
                        this.Close();
                        return;

                    case AuthenticatorLinker.FinalizeResult.GeneralFailure:
                        AstroMessageBox.Show(String.IsNullOrWhiteSpace(linker.LastErrorMessage)
                            ? "Steam could not finalize the authenticator yet. This may be temporary; enter a current confirmation code to try again, or cancel to stop setup."
                            : linker.LastErrorMessage, "Steam Guard Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        previousFinalizationCodeWasInvalid = false;
                        continue;

                    case AuthenticatorLinker.FinalizeResult.RateLimited:
                    case AuthenticatorLinker.FinalizeResult.NotFinalized:
                        AstroMessageBox.Show(
                            String.IsNullOrWhiteSpace(linker.LastErrorMessage)
                                ? "Steam could not finalize the authenticator yet. This may be temporary; enter a current confirmation code to try again, or cancel to stop setup."
                                : linker.LastErrorMessage,
                            "Steam Guard Setup",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        previousFinalizationCodeWasInvalid = false;
                        continue;
                }
            }

            // Linked, finally. Do not report success until the finalized record is durable,
            // and bind to the initial pending revision so a concurrent replacement or deletion
            // cannot be overwritten or resurrected.
            StorageResult finalSaveResult = manifest.SaveAccount(
                linker.LinkedAccount,
                encrypt,
                passKey,
                expectedStorageRevision: pendingStorageRevision,
                allowCreate: false);
            if (IsDisposed || Disposing)
                return;
            if (!finalSaveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", finalSaveResult.Exception, "Steam finalized the authenticator, but the finalized local record could not be saved.");
                AstroMessageBox.Show(
                    (finalSaveResult.UserMessage ?? "The finalized authenticator record could not be saved.") +
                    " Steam may have linked the authenticator already. Keep the recovery code you downloaded and try opening the app again before making further account changes.",
                    "Steam Guard Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                this.Close();
                return;
            }
            if (finalizeResponse == AuthenticatorLinker.FinalizeResult.Success && linker.LinkedAccount.FullyEnrolled)
            {
                TrySaveVerifiedPasswordAfterCommit(manifest, linker.LinkedAccount.Session.SteamID, password, encrypt ? passKey : null, showRefreshNotice: false);
            }
            string finalRecoveryMessage = finalizeResponse == AuthenticatorLinker.FinalizeResult.FinalizedIdentityMismatch
                ? "Steam accepted authenticator finalization, but reported a different authenticator identity. Your saved pending record was kept inactive; keep this recovery code safe and verify your Steam Guard status."
                : finalizeResponse == AuthenticatorLinker.FinalizeResult.FinalizedStatusUnverified
                    ? "Steam accepted the authenticator finalization, but its activation status could not be verified yet. Your record was kept as pending activation until Steam confirms it; keep this recovery code safe."
                    : "Mobile authenticator successfully linked. Keep this recovery code safe.";
            ShowRecoveryCode(linker.LinkedAccount, finalRecoveryMessage);
            this.Close();
        }

        private static string GetLoginErrorMessage(AuthenticationException exception)
        {
            switch (exception.Result)
            {
                case EResult.InvalidPassword:
                    return "Steam rejected the username or password. Check both and try again.";

                case EResult.RateLimitExceeded:
                case EResult.AccountLoginDeniedThrottle:
                    return "Steam is rate limiting sign-in attempts. Wait a while before trying again.";

                case EResult.InvalidLoginAuthCode:
                case EResult.TwoFactorCodeMismatch:
                    return "Steam did not accept the email or Steam Guard code. Request or enter a newer code, then try again.";

                case EResult.AccountLogonDenied:
                case EResult.AccessDenied:
                    return "Steam denied this sign-in attempt. Wait a while and check Steam Guard or account-security notifications before trying again.";

                default:
                    return "Steam rejected the sign-in request (" + exception.Result + "). Please try again later.";
            }
        }

        private void UpdateFinalizationProgress(string message)
        {
            if (IsDisposed || String.IsNullOrWhiteSpace(message))
                return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)(() => UpdateFinalizationProgress(message)));
                }
                catch (InvalidOperationException)
                {
                    // The form was closed while the cancellation request was in flight.
                }
                return;
            }

            labelLoginExplanation.Text = message;
            _ = ExecuteScriptSafelyAsync("setExplanation(" + Newtonsoft.Json.JsonConvert.SerializeObject(message) + ");", "Login progress UI");
        }

        private sealed class LoginPhoneEnrollmentInteraction : IPhoneEnrollmentInteraction
        {
            private readonly SteamGuardAccount account;
            private readonly Form owner;

            public LoginPhoneEnrollmentInteraction(SteamGuardAccount account, Form owner)
            {
                this.account = account;
                this.owner = owner;
            }

            public PhoneEnrollmentDetails RequestPhoneNumber()
            {
                return RunOnOwnerThread(() =>
                {
                    using (PhoneInputForm phoneInputForm = new PhoneInputForm(account))
                    {
                        phoneInputForm.ShowDialog(owner);
                        return phoneInputForm.Canceled
                            ? null
                            : new PhoneEnrollmentDetails(phoneInputForm.PhoneNumber, phoneInputForm.CountryCode, phoneInputForm.ContinueWithoutPhone);
                    }
                }, null);
            }

            public bool ConfirmPhoneRequired()
            {
                return RunOnOwnerThread(() =>
                {
                    DialogResult result = AstroMessageBox.ShowWithCustomButtons(
                        "Steam requires a verified phone number to set up a mobile authenticator for this account. Continue to add and verify a phone number, or cancel setup.",
                        "Phone Number Required",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information,
                        "Continue",
                        "Cancel");
                    return result == DialogResult.OK;
                }, false);
            }

            public bool ConfirmEmail(string confirmationEmailAddress)
            {
                string destination = string.IsNullOrWhiteSpace(confirmationEmailAddress)
                    ? "your Steam account email address"
                    : confirmationEmailAddress;
                return RunOnOwnerThread(() =>
                {
                    DialogResult result = AstroMessageBox.Show(
                        "Steam sent a confirmation link to " + destination + ". Click that link, then select OK to send the SMS verification code.",
                        "Confirm Phone Number",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information);
                    return result == DialogResult.OK;
                }, false);
            }

            public string RequestSmsCode(bool previousCodeWasInvalid)
            {
                string message = previousCodeWasInvalid
                    ? "That SMS code was not accepted. Please enter the new code sent to your phone."
                    : "Please enter the SMS code sent to your phone to verify the phone number.";
                return RunOnOwnerThread(() =>
                {
                    using (InputForm smsCodeForm = new InputForm(message))
                    {
                        smsCodeForm.ShowInputDialog(owner);
                        return (smsCodeForm.Canceled || !smsCodeForm.UiReady || smsCodeForm.LoadFailed) ? null : smsCodeForm.txtBox.Text;
                    }
                }, null);
            }

            private T RunOnOwnerThread<T>(Func<T> action, T unavailableResult)
            {
                if (owner == null || owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated)
                    return unavailableResult;

                try
                {
                    return owner.InvokeRequired ? (T)owner.Invoke(action) : action();
                }
                catch (InvalidOperationException)
                {
                    return unavailableResult;
                }
            }
        }

        internal readonly struct InitialEnrollmentPreparation
        {
            public InitialEnrollmentPreparation(
                bool passKeyResolved,
                string passKey,
                AuthenticatorEnrollmentOutcome enrollmentOutcome)
            {
                PassKeyResolved = passKeyResolved;
                PassKey = passKey;
                EnrollmentOutcome = enrollmentOutcome;
            }

            public bool PassKeyResolved { get; }
            public string PassKey { get; }
            public AuthenticatorEnrollmentOutcome EnrollmentOutcome { get; }
        }

        internal static async Task<InitialEnrollmentPreparation> PrepareInitialEnrollmentAsync(
            Func<(bool Resolved, string PassKey)> resolvePassKey,
            Func<CancellationToken, Task<AuthenticatorEnrollmentOutcome>> startEnrollmentAsync,
            CancellationToken cancellationToken)
        {
            if (resolvePassKey == null)
                throw new ArgumentNullException(nameof(resolvePassKey));
            if (startEnrollmentAsync == null)
                throw new ArgumentNullException(nameof(startEnrollmentAsync));

            cancellationToken.ThrowIfCancellationRequested();
            (bool resolved, string passKey) = resolvePassKey();
            if (!resolved)
                return new InitialEnrollmentPreparation(false, null, null);

            cancellationToken.ThrowIfCancellationRequested();
            AuthenticatorEnrollmentOutcome enrollmentOutcome = await startEnrollmentAsync(cancellationToken);
            return new InitialEnrollmentPreparation(true, passKey, enrollmentOutcome);
        }

        private bool TryResolveManifestPassKey(Manifest manifest, string currentPassKey, out string resolvedPassKey)
        {
            return TryResolveManifestPassKey(
                manifest,
                currentPassKey,
                prompt =>
                {
                    Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(prompt, commitImmediately: false, out string passKey);
                    return (status, passKey);
                },
                PromptForCurrentPassKey,
                message => AstroMessageBox.Show(message, "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Error),
                out resolvedPassKey);
        }

        internal static InputForm CreateCurrentPassKeyPrompt(string promptLabel = CurrentPassKeyPromptLabel, bool initializeModernUi = true)
        {
            return new InputForm(promptLabel, password: true, initializeModernUi: initializeModernUi);
        }

        private (bool Canceled, string Value) PromptForCurrentPassKey(string promptLabel, bool maskInput)
        {
            using (InputForm passKeyForm = maskInput
                ? CreateCurrentPassKeyPrompt(promptLabel)
                : new InputForm(promptLabel, false))
            {
                passKeyForm.ShowInputDialog(this);
                return (passKeyForm.Canceled || !passKeyForm.UiReady || passKeyForm.LoadFailed, passKeyForm.txtBox.Text);
            }
        }

        internal static bool TryResolveManifestPassKey(
            Manifest manifest,
            string suppliedPassKey,
            Func<string, (Manifest.SetupPassKeyStatus Status, string PassKey)> promptSetupPassKey,
            Func<string, bool, (bool Canceled, string Value)> promptCurrentPassKey,
            Action<string> showInvalidPassKeyMessage,
            out string resolvedPassKey)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (promptSetupPassKey == null)
                throw new ArgumentNullException(nameof(promptSetupPassKey));
            if (promptCurrentPassKey == null)
                throw new ArgumentNullException(nameof(promptCurrentPassKey));
            if (showInvalidPassKeyMessage == null)
                throw new ArgumentNullException(nameof(showInvalidPassKeyMessage));

            resolvedPassKey = null;
            if (manifest.Entries.Count == 0)
            {
                (Manifest.SetupPassKeyStatus status, string setupPassKey) = promptSetupPassKey(SetupPassKeyPromptLabel);
                if (status != Manifest.SetupPassKeyStatus.Set && status != Manifest.SetupPassKeyStatus.DeclinedUnencrypted)
                    return false;

                resolvedPassKey = status == Manifest.SetupPassKeyStatus.Set ? setupPassKey : null;
                return true;
            }

            if (!manifest.Encrypted)
            {
                return true;
            }

            if (!String.IsNullOrEmpty(suppliedPassKey) && manifest.VerifyPasskey(suppliedPassKey))
            {
                resolvedPassKey = suppliedPassKey;
                return true;
            }

            while (true)
            {
                (bool Canceled, string Value) promptResult = promptCurrentPassKey(CurrentPassKeyPromptLabel, true);
                if (promptResult.Canceled)
                {
                    resolvedPassKey = null;
                    return false;
                }

                if (!String.IsNullOrEmpty(promptResult.Value) && manifest.VerifyPasskey(promptResult.Value))
                {
                    resolvedPassKey = promptResult.Value;
                    return true;
                }

                showInvalidPassKeyMessage("That passkey is invalid. Please enter the same passkey you used for your other accounts.");
            }
        }

        internal enum ConfirmationCodePromptAction
        {
            SubmitCode,
            RetryPrompt,
            KeepPendingAndStop,
            CancelAndRemovePending
        }

        internal readonly struct ConfirmationCodePromptDecision
        {
            public ConfirmationCodePromptDecision(ConfirmationCodePromptAction action, string confirmationCode = null)
            {
                Action = action;
                ConfirmationCode = confirmationCode;
            }

            public ConfirmationCodePromptAction Action { get; }
            public string ConfirmationCode { get; }
        }

        internal static string GetConfirmationCodeRetryPromptMessage(Manifest.PassKeyPromptResult promptResult)
        {
            if (promptResult.LoadFailed)
                return "The confirmation code window could not be displayed. Your pending authenticator and recovery code have been kept safely.\n\nDo you want to reopen the confirmation code prompt?";
            if (!promptResult.UiReady)
                return "The confirmation code window was closed before it finished loading. Your pending authenticator and recovery code have been kept safely.\n\nDo you want to reopen the confirmation code prompt?";
            if (promptResult.Accepted)
                return "No confirmation code was entered. Your pending authenticator and recovery code have been kept safely.\n\nDo you want to reopen the confirmation code prompt?";

            return "The confirmation code window was closed without canceling setup. Your pending authenticator and recovery code have been kept safely.\n\nDo you want to reopen the confirmation code prompt?";
        }

        internal static ConfirmationCodePromptDecision EvaluateConfirmationCodePrompt(
            Manifest.PassKeyPromptResult promptResult,
            Func<bool> promptRetryOnNonExplicitDismissal)
        {
            if (promptRetryOnNonExplicitDismissal == null)
                throw new ArgumentNullException(nameof(promptRetryOnNonExplicitDismissal));

            if (promptResult.LoadFailed || !promptResult.UiReady || (promptResult.Canceled && !promptResult.ExplicitCancel))
            {
                return promptRetryOnNonExplicitDismissal()
                    ? new ConfirmationCodePromptDecision(ConfirmationCodePromptAction.RetryPrompt)
                    : new ConfirmationCodePromptDecision(ConfirmationCodePromptAction.KeepPendingAndStop);
            }

            if (promptResult.Canceled)
                return new ConfirmationCodePromptDecision(ConfirmationCodePromptAction.CancelAndRemovePending);

            return new ConfirmationCodePromptDecision(ConfirmationCodePromptAction.SubmitCode, promptResult.Value);
        }

        internal enum PendingAuthenticatorRecoveryStatus
        {
            NotApplicable,
            ConfirmedMismatch,
            Inconclusive,
            Recovered,
            SaveFailed
        }

        internal readonly struct PendingAuthenticatorRecoveryResult
        {
            public PendingAuthenticatorRecoveryResult(
                PendingAuthenticatorRecoveryStatus status,
                StorageResult saveResult = null,
                AuthenticatorActivationStatus activationStatus = AuthenticatorActivationStatus.Unverified)
            {
                Status = status;
                SaveResult = saveResult;
                ActivationStatus = activationStatus;
            }

            public PendingAuthenticatorRecoveryStatus Status { get; }
            public StorageResult SaveResult { get; }
            public AuthenticatorActivationStatus ActivationStatus { get; }
        }

        internal static Task<PendingAuthenticatorRecoveryResult> TryRecoverPendingAccountWithFreshSessionAsync(
            Manifest manifest,
            SteamGuardAccount pendingAccount,
            SessionData freshSession,
            string passKey,
            Func<SteamGuardAccount, CancellationToken, Task<AuthenticatorActivationStatus>> verifyActivationAsync,
            CancellationToken cancellationToken,
            long? expectedStorageRevision = null)
        {
            if (verifyActivationAsync == null)
                return Task.FromResult(new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.NotApplicable));

            return TryRecoverPendingAccountWithFreshSessionAsync(
                manifest,
                pendingAccount,
                freshSession,
                passKey,
                async (candidate, ct) => new AuthenticatorActivationVerificationResult(await verifyActivationAsync(candidate, ct)),
                cancellationToken,
                expectedStorageRevision);
        }

        internal static async Task<PendingAuthenticatorRecoveryResult> TryRecoverPendingAccountWithFreshSessionAsync(
            Manifest manifest,
            SteamGuardAccount pendingAccount,
            SessionData freshSession,
            string passKey,
            Func<SteamGuardAccount, CancellationToken, Task<AuthenticatorActivationVerificationResult>> verifyActivationAsync,
            CancellationToken cancellationToken,
            long? expectedStorageRevision = null)
        {
            if (manifest == null || pendingAccount == null || pendingAccount.FullyEnrolled ||
                freshSession == null || freshSession.SteamID == 0 || verifyActivationAsync == null)
            {
                return new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.NotApplicable);
            }

            long revisionToCheck = expectedStorageRevision ?? manifest.GetAccountStorageRevision(freshSession.SteamID);
            if (expectedStorageRevision.HasValue &&
                manifest.GetAccountStorageRevision(freshSession.SteamID) != revisionToCheck)
            {
                return new PendingAuthenticatorRecoveryResult(
                    PendingAuthenticatorRecoveryStatus.SaveFailed,
                    StorageResult.Failure(StorageFailureKind.Validation, "The account data changed while the session was being refreshed."));
            }

            SessionData previousSession = pendingAccount.Session;
            pendingAccount.Session = freshSession;
            AuthenticatorActivationVerificationResult verification;
            try
            {
                verification = await verifyActivationAsync(pendingAccount, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                pendingAccount.Session = previousSession;
                throw;
            }

            AuthenticatorActivationStatus activationStatus = verification.Status;
            if (activationStatus == AuthenticatorActivationStatus.NotActive ||
                activationStatus == AuthenticatorActivationStatus.ActiveForDifferentAuthenticator)
            {
                pendingAccount.Session = previousSession;
                return new PendingAuthenticatorRecoveryResult(
                    PendingAuthenticatorRecoveryStatus.ConfirmedMismatch,
                    activationStatus: activationStatus);
            }

            if (activationStatus != AuthenticatorActivationStatus.ActiveForThisRecord)
            {
                pendingAccount.Session = previousSession;
                return new PendingAuthenticatorRecoveryResult(
                    PendingAuthenticatorRecoveryStatus.Inconclusive,
                    activationStatus: activationStatus);
            }

            if (!TryApplyVerifiedIdentifiers(pendingAccount, verification, out string previousTokenGid, out string previousDeviceId))
            {
                pendingAccount.Session = previousSession;
                return new PendingAuthenticatorRecoveryResult(
                    PendingAuthenticatorRecoveryStatus.Inconclusive,
                    activationStatus: AuthenticatorActivationStatus.Unverified);
            }

            pendingAccount.FullyEnrolled = true;
            bool encrypt = ShouldEncryptAccountWrite(manifest, passKey);
            StorageResult saveResult = manifest.SaveAccount(
                pendingAccount,
                encrypt,
                passKey,
                revisionToCheck,
                allowCreate: false);
            if (!saveResult.Succeeded)
            {
                pendingAccount.FullyEnrolled = false;
                RestoreVerifiedIdentifiers(pendingAccount, previousTokenGid, previousDeviceId);
                pendingAccount.Session = previousSession;
                return new PendingAuthenticatorRecoveryResult(
                    PendingAuthenticatorRecoveryStatus.SaveFailed,
                    saveResult,
                    activationStatus);
            }

            return new PendingAuthenticatorRecoveryResult(
                PendingAuthenticatorRecoveryStatus.Recovered,
                saveResult,
                activationStatus);
        }

        internal static Task<PendingAuthenticatorRecoveryResult> TryRecoverExistingPendingAuthenticatorAsync(
            Manifest manifest,
            SessionData freshSession,
            string passKey,
            Func<SteamGuardAccount, CancellationToken, Task<AuthenticatorActivationStatus>> verifyActivationAsync,
            CancellationToken cancellationToken)
        {
            if (verifyActivationAsync == null)
                return Task.FromResult(new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.NotApplicable));

            return TryRecoverExistingPendingAuthenticatorAsync(
                manifest,
                freshSession,
                passKey,
                async (candidate, ct) => new AuthenticatorActivationVerificationResult(await verifyActivationAsync(candidate, ct)),
                cancellationToken);
        }

        internal static async Task<PendingAuthenticatorRecoveryResult> TryRecoverExistingPendingAuthenticatorAsync(
            Manifest manifest,
            SessionData freshSession,
            string passKey,
            Func<SteamGuardAccount, CancellationToken, Task<AuthenticatorActivationVerificationResult>> verifyActivationAsync,
            CancellationToken cancellationToken)
        {
            if (manifest == null || freshSession == null || freshSession.SteamID == 0 || verifyActivationAsync == null)
                return new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.NotApplicable);

            (SteamGuardAccount Account, long StorageRevision)[] existingSnapshots;
            try
            {
                existingSnapshots = manifest.GetAllAccountsWithRevisions(passKey) ?? Array.Empty<(SteamGuardAccount, long)>();
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator recovery", ex, "Saved accounts could not be inspected while checking for a pending authenticator.");
                return new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.Inconclusive);
            }

            (SteamGuardAccount pendingAccount, long pendingStorageRevision) = Array.Find(
                existingSnapshots,
                candidate => candidate.Account != null &&
                    !candidate.Account.FullyEnrolled &&
                    candidate.Account.Session != null &&
                    candidate.Account.Session.SteamID == freshSession.SteamID);
            if (pendingAccount == null)
            {
                bool hasManifestEntry = manifest.Entries != null &&
                    manifest.Entries.Exists(entry => entry != null && entry.SteamID == freshSession.SteamID);
                bool hasLoadedAccountForSteamId = Array.Exists(
                    existingSnapshots,
                    candidate => candidate.Account?.Session != null && candidate.Account.Session.SteamID == freshSession.SteamID);
                if (hasManifestEntry && !hasLoadedAccountForSteamId)
                    return new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.Inconclusive);

                return new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.NotApplicable);
            }

            return await TryRecoverPendingAccountWithFreshSessionAsync(
                manifest,
                pendingAccount,
                freshSession,
                passKey,
                verifyActivationAsync,
                cancellationToken,
                expectedStorageRevision: pendingStorageRevision);
        }

        internal static bool ApplyRefreshedSession(
            SteamGuardAccount targetAccount,
            SessionData refreshedSession,
            Func<bool> saveSession)
        {
            if (targetAccount == null)
                throw new ArgumentNullException(nameof(targetAccount));
            if (refreshedSession == null)
                throw new ArgumentNullException(nameof(refreshedSession));
            if (saveSession == null)
                throw new ArgumentNullException(nameof(saveSession));

            SessionData previousSession = targetAccount.Session;
            // Preserve FullyEnrolled; only Steam authenticator finalization may promote
            // a pending authenticator record to enrolled.
            targetAccount.Session = refreshedSession;
            if (!saveSession())
            {
                // SaveAccount serializes and commits the candidate before the
                // in-memory account becomes authoritative. Restore the old
                // session whenever that persistence step does not succeed.
                targetAccount.Session = previousSession;
                return false;
            }

            return true;
        }

        private bool SaveRefreshedSession(Manifest man, string suppliedPassKey, long? expectedRevision, out string usedPassKey)
        {
            usedPassKey = null;
            if (!TryResolveManifestPassKey(man, suppliedPassKey, out string passKey))
            {
                return false;
            }

            bool encrypt = ShouldEncryptAccountWrite(man, passKey);
            StorageResult saveResult = man.SaveAccount(account, encrypt, passKey, expectedRevision, allowCreate: false);
            if (!saveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", saveResult.Exception, "The refreshed Steam session could not be saved.");
                AstroMessageBox.Show(saveResult.UserMessage ?? "The refreshed session could not be saved.", "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            ResolvedPassKey = encrypt ? passKey : null;
            usedPassKey = ResolvedPassKey;
            return true;
        }

        private void TrySaveVerifiedPasswordAfterCommit(Manifest man, ulong steamId, string password, string usedPassKey, bool showRefreshNotice)
        {
            string extraNotice = String.Empty;
            if (savePasswordRequested &&
                man != null &&
                man.Encrypted &&
                man.AllowSavedSteamPasswords &&
                steamId != 0 &&
                !String.IsNullOrEmpty(password) &&
                !String.IsNullOrEmpty(usedPassKey))
            {
                StorageResult credResult = man.SaveAccountPassword(steamId, password, usedPassKey);
                if (credResult.Succeeded)
                {
                    SavedPasswordUpdated = true;
                    SavedPasswordUpdatedSteamId = steamId;
                    if (credResult.ObsoleteCleanupPending)
                    {
                        extraNotice = "\n\nYour password was saved. The previous encrypted credential file could not be removed immediately and will be cleaned up automatically on the next save or restart.";
                    }
                }
                else
                {
                    DiagnosticErrorLogger.Log("Credential storage", credResult.Exception, "The verified Steam password could not be saved after account commit.");
                    extraNotice = "\n\nNote: The Steam password could not be saved for silent renewal. You can update it later in Account Options.";
                }
            }
            else if (showRefreshNotice &&
                man != null &&
                man.Encrypted &&
                man.AllowSavedSteamPasswords &&
                !String.IsNullOrEmpty(usedPassKey))
            {
                // Preserve rejection state unless user explicitly replaced or deleted the saved password.
                SavedCredentialRecord existing = man.GetSavedCredentialRecord(steamId, usedPassKey);
                if (existing != null && existing.PasswordRejected)
                {
                    extraNotice = "\n\nNote: Your previously saved Steam password is still marked as rejected. Update or remove it in Account Options to re-enable silent renewal.";
                }
            }

            if (showRefreshNotice)
            {
                AstroMessageBox.Show("Your session was refreshed." + extraNotice, "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ShowRecoveryCode(SteamGuardAccount recoveryAccount, string statusMessage, bool requireBackupBeforeContinue = false)
        {
            if (IsDisposed || Disposing || recoveryAccount == null)
                return;

            using (RecoveryCodeForm recoveryCodeForm = new RecoveryCodeForm(recoveryAccount, statusMessage, requireBackupBeforeContinue))
            {
                recoveryCodeForm.ShowDialog(this);
            }
        }

        private void LoginForm_Load(object sender, EventArgs e)
        {
            AstroTheme.ApplyTheme(this);

            // Form-specific: style the login button as primary
            AstroTheme.StylePrimaryButton(btnSteamLogin);

            // Style explanation label as variant text
            labelLoginExplanation.ForeColor = AstroTheme.OnSurfaceVariant;

            if (account != null && account.AccountName != null)
            {
                txtUsername.Text = account.AccountName;
            }
        }

        private async Task ExecuteAuthenticatorTransferAsync(
            SessionData sessionData,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            Manifest manifest = Manifest.GetManifest();
            if (PendingTransferMarker.HasMarker(sessionData.SteamID))
            {
                AstroMessageBox.Show(
                    "An unresolved authenticator transfer marker already exists for this account. Restart Astro SDA or dismiss the previous transfer notice once you have verified your Steam account before starting another transfer.",
                    "Transfer Blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                ResetLoginButton(cancellationToken);
                return;
            }

            DialogResult confirmTransfer = AstroMessageBox.Show(
                "Transferring your authenticator will immediately deactivate Steam Guard on your old device and activate it in Astro SDA.\n\nSteam applies a 2-day trade and market restriction after transferring.\n\nDo you want to request the SMS transfer code now?",
                "Confirm Authenticator Transfer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirmTransfer != DialogResult.Yes)
            {
                ResetLoginButton(cancellationToken);
                return;
            }

            if (!TryResolveManifestPassKey(manifest, suppliedPassKey, out string resolvedPassKey))
            {
                this.Close();
                return;
            }

            bool encrypt = ShouldEncryptAccountWrite(manifest, resolvedPassKey);
            if (encrypt && String.IsNullOrEmpty(resolvedPassKey))
            {
                AstroMessageBox.Show("An encryption passkey is required before starting an authenticator transfer on an encrypted manifest.", "Authenticator Transfer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ResetLoginButton(cancellationToken);
                return;
            }

            string previousCommittedTransferAttemptId = manifest.GetLastTransferAttemptId(sessionData.SteamID);
            IDisposable transferFence = null;
            try
            {
                if (beginTransferFenceAsync != null)
                    transferFence = await beginTransferFenceAsync(sessionData.SteamID, cancellationToken);

                AuthenticatorLinker linker = new AuthenticatorLinker(sessionData);
                LoginTransferInteraction transferInteraction = new LoginTransferInteraction(
                    this,
                    manifest,
                    sessionData.SteamID,
                    encrypt,
                    resolvedPassKey,
                    previousCommittedTransferAttemptId,
                    committedAccount => onAccountCommitted?.Invoke(committedAccount, resolvedPassKey));

                AuthenticatorTransferCoordinator coordinator = new AuthenticatorTransferCoordinator(
                    linker,
                    transferInteraction,
                    sessionData.SteamID,
                    username);

                TransferCoordinatorOutcome outcome = await coordinator.ExecuteAsync(cancellationToken);
                if ((IsDisposed || Disposing) && outcome.Kind != TransferCoordinatorResultKind.ReplacementReceivedSavePending)
                    return;

                switch (outcome.Kind)
                {
                    case TransferCoordinatorResultKind.Succeeded:
                        ResolvedPassKey = encrypt ? resolvedPassKey : null;
                        TrySaveVerifiedPasswordAfterCommit(manifest, sessionData.SteamID, password, ResolvedPassKey, showRefreshNotice: false);
                        ShowRecoveryCode(
                            outcome.LinkedAccount,
                            "Authenticator transferred successfully! Steam applies a 2-day trade and market hold. Keep this new recovery code safe.",
                            requireBackupBeforeContinue: true);
                        this.Close();
                        return;

                    case TransferCoordinatorResultKind.Canceled:
                        CompleteTransferNonClosingOutcome(cancellationToken);
                        return;

                    case TransferCoordinatorResultKind.ReplacementReceivedSavePending:
                        using (TransferSaveRecoveryForm recoveryForm = new TransferSaveRecoveryForm(
                            outcome.LinkedAccount,
                            outcome.ErrorMessage,
                            outcome.StagedRecoveryPath,
                            outcome.StagingFailed,
                            resolvedPassKey,
                            candidate =>
                            {
                                StorageResult retryResult = manifest.SaveAccount(
                                    candidate,
                                    encrypt,
                                    resolvedPassKey,
                                    transferAttemptId: outcome.TransferAttemptId,
                                    expectedPreviousTransferAttemptId: previousCommittedTransferAttemptId,
                                    enforceTransferAttemptBase: true);
                                if (retryResult.Succeeded)
                                {
                                    if (PendingTransferMarker.DeleteMarkerBestEffort(candidate.Session.SteamID, outcome.TransferAttemptId))
                                        PendingTransferMarker.DeleteAllStagedRecoveriesForSteamIdBestEffort(candidate.Session.SteamID);
                                    onAccountCommitted?.Invoke(candidate, resolvedPassKey);
                                }
                                return retryResult;
                            },
                            transferAttemptId: outcome.TransferAttemptId))
                        {
                            if (!IsDisposed && !Disposing && IsHandleCreated)
                                recoveryForm.ShowDialog(this);
                            else
                                recoveryForm.ShowDialog();
                            if (recoveryForm.LocalSaveSucceeded)
                            {
                                ResolvedPassKey = encrypt ? resolvedPassKey : null;
                                TrySaveVerifiedPasswordAfterCommit(manifest, sessionData.SteamID, password, ResolvedPassKey, showRefreshNotice: false);
                                if (!IsDisposed && !Disposing)
                                {
                                    ShowRecoveryCode(
                                        outcome.LinkedAccount,
                                        "Authenticator transferred and saved successfully! Keep this new recovery code safe.");
                                }
                            }
                        }
                        if (!IsDisposed && !Disposing)
                            this.Close();
                        return;

                    case TransferCoordinatorResultKind.OutcomeUnknown:
                    case TransferCoordinatorResultKind.IncompleteReplacement:
                        AstroMessageBox.Show(
                            (outcome.ErrorMessage ?? "The authenticator transfer outcome could not be confirmed.") +
                            "\n\nDo not automatically resend the SMS code. Check your Steam Mobile App or Steam Support recovery options before attempting another transfer.",
                            "Transfer Outcome Unknown",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        this.Close();
                        return;

                    case TransferCoordinatorResultKind.PhoneNumberRequired:
                    case TransferCoordinatorResultKind.RateLimited:
                    case TransferCoordinatorResultKind.BlockedByPendingTransfer:
                    case TransferCoordinatorResultKind.Failed:
                    default:
                        if (!ShouldCloseForDeferredExit())
                        {
                            AstroMessageBox.Show(
                                outcome.ErrorMessage ?? "The authenticator transfer could not be completed.",
                                "Authenticator Transfer",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                        }
                        CompleteTransferNonClosingOutcome(cancellationToken);
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                CompleteTransferNonClosingOutcome(cancellationToken);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator transfer", ex, "The authenticator transfer encountered an unexpected error.");
                if (!ShouldCloseForDeferredExit())
                {
                    AstroMessageBox.Show("An unexpected error occurred during authenticator transfer.", "Authenticator Transfer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                CompleteTransferNonClosingOutcome(cancellationToken);
            }
            finally
            {
                transferFence?.Dispose();
            }
        }

        private sealed class LoginTransferInteraction : IAuthenticatorTransferInteraction
        {
            private readonly LoginForm owner;
            private readonly Manifest manifest;
            private readonly ulong steamId;
            private readonly bool encrypt;
            private readonly string passKey;
            private readonly string previousCommittedTransferAttemptId;
            private readonly Action<SteamGuardAccount> onCommitted;

            public LoginTransferInteraction(
                LoginForm owner,
                Manifest manifest,
                ulong steamId,
                bool encrypt,
                string passKey,
                string previousCommittedTransferAttemptId,
                Action<SteamGuardAccount> onCommitted)
            {
                this.owner = owner;
                this.manifest = manifest;
                this.steamId = steamId;
                this.encrypt = encrypt;
                this.passKey = passKey;
                this.previousCommittedTransferAttemptId = previousCommittedTransferAttemptId;
                this.onCommitted = onCommitted;
            }

            public bool HasUnresolvedPendingTransfer(ulong steamId) => PendingTransferMarker.HasMarker(steamId);

            public bool WritePreDispatchMarker(ulong steamId, string accountName, string deviceId, string transferAttemptId) =>
                PendingTransferMarker.TryWriteMarker(steamId, accountName, deviceId, transferAttemptId);

            public void ClearPreDispatchMarker(ulong steamId, string expectedTransferAttemptId) =>
                PendingTransferMarker.DeleteMarkerBestEffort(steamId, expectedTransferAttemptId);

            public IDisposable EnterIrreversibleContinueSection(out CancellationToken continueCancellationToken)
            {
                CancellationTokenSource boundedCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                continueCancellationToken = boundedCts.Token;
                owner?.SetIrreversibleTransferInFlight(true);
                return new ContinueSectionGuard(owner, boundedCts);
            }

            private sealed class ContinueSectionGuard : IDisposable
            {
                private readonly LoginForm owner;
                private CancellationTokenSource cts;
                private int disposed;

                public ContinueSectionGuard(LoginForm owner, CancellationTokenSource cts)
                {
                    this.owner = owner;
                    this.cts = cts;
                }

                public void Dispose()
                {
                    if (Interlocked.Exchange(ref disposed, 1) == 0)
                    {
                        owner?.SetIrreversibleTransferInFlight(false);
                    }
                    CancellationTokenSource toDispose = Interlocked.Exchange(ref cts, null);
                    toDispose?.Dispose();
                }
            }

            public string RequestTransferSmsCode(bool previousCodeWasInvalid)
            {
                string prompt = previousCodeWasInvalid
                    ? "That SMS code was not accepted. Enter the current SMS transfer code sent by Steam:"
                    : "Enter the SMS verification code Steam sent to your phone to complete the authenticator transfer:";
                if (owner == null || owner.IsDisposed || !owner.IsHandleCreated)
                    return null;

                Func<string> showDialog = () =>
                {
                    using (InputForm smsForm = new InputForm(prompt))
                    {
                        smsForm.ShowInputDialog(owner);
                        return (smsForm.Canceled || !smsForm.UiReady || smsForm.LoadFailed) ? null : smsForm.txtBox.Text;
                    }
                };
                return owner.InvokeRequired ? (string)owner.Invoke(showDialog) : showDialog();
            }

            public string TryStageProtectedReplacement(SteamGuardAccount replacement, string transferAttemptId, out bool stagingFailed)
            {
                return PendingTransferMarker.TryStageProtectedReplacement(
                    replacement,
                    encrypt,
                    passKey,
                    out stagingFailed,
                    transferAttemptId,
                    previousCommittedTransferAttemptId);
            }

            public void CleanupStagedReplacement(string stagedRecoveryPath)
            {
                // Only purge staged recovery files once the pending marker is verified gone.
                if (PendingTransferMarker.HasMarker(steamId))
                    return;

                if (!String.IsNullOrEmpty(stagedRecoveryPath))
                    PendingTransferMarker.DeleteStagedRecoveryBestEffort(stagedRecoveryPath);
                PendingTransferMarker.DeleteAllStagedRecoveriesForSteamIdBestEffort(steamId);
            }

            public bool PersistReplacementAccount(SteamGuardAccount replacement, string transferAttemptId, out string failureMessage)
            {
                StorageResult result = manifest.SaveAccount(
                    replacement,
                    encrypt,
                    passKey,
                    transferAttemptId: transferAttemptId,
                    expectedPreviousTransferAttemptId: previousCommittedTransferAttemptId,
                    enforceTransferAttemptBase: true);
                if (result.Succeeded)
                {
                    failureMessage = null;
                    // Synchronously retire the old account generation BEFORE RecoveryCodeForm!
                    // Note: marker clearing and staged recovery cleanup run in the coordinator
                    // strictly in order (ClearPreDispatchMarker -> CleanupStagedReplacement).
                    onCommitted?.Invoke(replacement);
                    return true;
                }

                DiagnosticErrorLogger.Log("Authenticator transfer", result.Exception, "Failed to persist transferred replacement authenticator.");
                failureMessage = result.UserMessage ?? "The transferred authenticator could not be saved locally.";
                return false;
            }

            public void PersistEnrichedDeviceId(SteamGuardAccount replacement, string transferAttemptId)
            {
                StorageResult result = manifest.SaveAccount(
                    replacement,
                    encrypt,
                    passKey,
                    transferAttemptId: transferAttemptId,
                    expectedPreviousTransferAttemptId: transferAttemptId,
                    enforceTransferAttemptBase: true);
                if (result.Succeeded)
                    onCommitted?.Invoke(replacement);
            }
        }

        public enum LoginType
        {
            Initial,
            Refresh,
            Import,
            Transfer
        }
    }
}
