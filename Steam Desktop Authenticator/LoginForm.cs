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

        internal static string GetRefreshLoginExplanation(SteamGuardAccount targetAccount)
        {
            return targetAccount != null && !targetAccount.FullyEnrolled
                ? "Sign in to Steam to verify whether this pending authenticator is active."
                : "Your Steam credentials have expired. For trade and market confirmations to work properly, please login again.";
        }

        public LoginForm(
            LoginType loginReason = LoginType.Initial,
            SteamGuardAccount account = null,
            string passKey = null,
            long? expectedStorageRevision = null)
        {
            InitializeComponent();
            this.LoginReason = loginReason;
            this.account = account;
            this.suppliedPassKey = passKey;
            this.expectedStorageRevision = expectedStorageRevision;

            try
            {
                if (this.LoginReason != LoginType.Initial)
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
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
                
                if (this.LoginReason != LoginType.Initial)
                {
                    string jsAccountName = Newtonsoft.Json.JsonConvert.SerializeObject(account.AccountName ?? String.Empty);
                    _ = ExecuteScriptSafelyAsync($"setUsername({jsAccountName}, true)", "Login account UI");
                }
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
                    IsPersistentSession = false,
                    PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                    ClientOSType = EOSType.Android9,
                    Authenticator = new UserFormAuthenticator(this.account, this, cancellationToken),
                });
            }
            catch (AuthenticatorAlreadyLinkedException)
            {
                ResetLoginButton(cancellationToken);
                return;
            }
            catch (OperationCanceledException)
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
            AuthPollResult pollResponse;
            try
            {
                pollResponse = await authSession.PollingWaitForResultAsync(cancellationToken);
            }
            catch (AuthenticatorAlreadyLinkedException)
            {
                ResetLoginButton(cancellationToken);
                return;
            }
            catch (OperationCanceledException)
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
                            (candidate, ct) => candidate.VerifyAuthenticatorActivationAsync(ct),
                            cancellationToken,
                            expectedStorageRevision);
                    }
                    catch (OperationCanceledException)
                    {
                        ResetLoginButton(cancellationToken);
                        return;
                    }
                    if (IsDisposed || Disposing)
                        return;

                    switch (pendingRecovery.Status)
                    {
                        case PendingAuthenticatorRecoveryStatus.Recovered:
                            bool recoveredEncrypt = (man.Entries.Count > 0 && man.Encrypted) || resolvedKey != null;
                            ResolvedPassKey = recoveredEncrypt ? resolvedKey : null;
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

                if (!ApplyRefreshedSession(account, sessionData, () => SaveRefreshedSession(man, suppliedPassKey, expectedStorageRevision)))
                {
                    ResetLoginButton(cancellationToken);
                    return;
                }
                RefreshSucceeded = true;
                this.Close();
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
                            (candidate, ct) => candidate.VerifyAuthenticatorActivationAsync(ct),
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        ResetLoginButton(cancellationToken);
                        return;
                    }
                    if (IsDisposed || Disposing)
                        return;
                    if (recoveryResult.Status == PendingAuthenticatorRecoveryStatus.Recovered)
                    {
                        bool recoveredEncrypt = (manifest.Entries.Count > 0 && manifest.Encrypted) || passKey != null;
                        ResolvedPassKey = recoveredEncrypt ? passKey : null;
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
            bool encrypt = (manifest.Entries.Count > 0 && manifest.Encrypted) || passKey != null;
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
            ResolvedPassKey = encrypt ? passKey : null;

            ShowRecoveryCode(linker.LinkedAccount, "The mobile authenticator is not linked yet. Save this recovery code before continuing.", true);

            AuthenticatorLinker.FinalizeResult finalizeResponse = AuthenticatorLinker.FinalizeResult.GeneralFailure;
            bool previousFinalizationCodeWasInvalid = false;
            while (finalizeResponse != AuthenticatorLinker.FinalizeResult.Success &&
                finalizeResponse != AuthenticatorLinker.FinalizeResult.FinalizedStatusUnverified)
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
                        if (!manifest.RemoveAccount(linker.LinkedAccount))
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
                    finalizeResponse != AuthenticatorLinker.FinalizeResult.FinalizedStatusUnverified)
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
                        manifest.RemoveAccount(linker.LinkedAccount);
                        if (!manifest.Encrypted)
                            ResolvedPassKey = null;
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

            // Linked, finally. Do not report success until the finalized record is durable.
            StorageResult finalSaveResult = manifest.SaveAccount(linker.LinkedAccount, encrypt, passKey);
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
            ShowRecoveryCode(
                linker.LinkedAccount,
                finalizeResponse == AuthenticatorLinker.FinalizeResult.FinalizedStatusUnverified
                    ? "Steam accepted the authenticator finalization, but its status could not be verified. The authenticator record was saved; keep this recovery code safe."
                    : "Mobile authenticator successfully linked. Keep this recovery code safe.");
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
                        return smsCodeForm.Canceled ? null : smsCodeForm.txtBox.Text;
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

        internal static async Task<PendingAuthenticatorRecoveryResult> TryRecoverPendingAccountWithFreshSessionAsync(
            Manifest manifest,
            SteamGuardAccount pendingAccount,
            SessionData freshSession,
            string passKey,
            Func<SteamGuardAccount, CancellationToken, Task<AuthenticatorActivationStatus>> verifyActivationAsync,
            CancellationToken cancellationToken,
            long? expectedStorageRevision = null)
        {
            if (manifest == null || pendingAccount == null || pendingAccount.FullyEnrolled ||
                freshSession == null || freshSession.SteamID == 0 || verifyActivationAsync == null)
            {
                return new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.NotApplicable);
            }

            long revisionToCheck = expectedStorageRevision ?? manifest.GetAccountStorageRevision(freshSession.SteamID);
            SessionData previousSession = pendingAccount.Session;
            pendingAccount.Session = freshSession;
            AuthenticatorActivationStatus activationStatus;
            try
            {
                activationStatus = await verifyActivationAsync(pendingAccount, cancellationToken);
            }
            catch
            {
                pendingAccount.Session = previousSession;
                throw;
            }

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

            pendingAccount.FullyEnrolled = true;
            bool encrypt = (manifest.Entries.Count > 0 && manifest.Encrypted) || passKey != null;
            StorageResult saveResult = manifest.SaveAccount(
                pendingAccount,
                encrypt,
                passKey,
                revisionToCheck,
                allowCreate: false);
            if (!saveResult.Succeeded)
            {
                pendingAccount.FullyEnrolled = false;
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

        internal static async Task<PendingAuthenticatorRecoveryResult> TryRecoverExistingPendingAuthenticatorAsync(
            Manifest manifest,
            SessionData freshSession,
            string passKey,
            Func<SteamGuardAccount, CancellationToken, Task<AuthenticatorActivationStatus>> verifyActivationAsync,
            CancellationToken cancellationToken)
        {
            if (manifest == null || freshSession == null || freshSession.SteamID == 0 || verifyActivationAsync == null)
                return new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.NotApplicable);

            SteamGuardAccount[] existingAccounts;
            try
            {
                existingAccounts = manifest.GetAllAccounts(passKey) ?? Array.Empty<SteamGuardAccount>();
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator recovery", ex, "Saved accounts could not be inspected while checking for a pending authenticator.");
                return new PendingAuthenticatorRecoveryResult(PendingAuthenticatorRecoveryStatus.Inconclusive);
            }

            SteamGuardAccount pendingAccount = Array.Find(
                existingAccounts,
                candidate => candidate != null &&
                    !candidate.FullyEnrolled &&
                    candidate.Session != null &&
                    candidate.Session.SteamID == freshSession.SteamID);
            if (pendingAccount == null)
            {
                bool hasManifestEntry = manifest.Entries != null &&
                    manifest.Entries.Exists(entry => entry != null && entry.SteamID == freshSession.SteamID);
                bool hasLoadedAccountForSteamId = Array.Exists(
                    existingAccounts,
                    candidate => candidate?.Session != null && candidate.Session.SteamID == freshSession.SteamID);
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
                cancellationToken);
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

        private bool SaveRefreshedSession(Manifest man, string suppliedPassKey = null, long? expectedRevision = null)
        {
            if (!TryResolveManifestPassKey(man, suppliedPassKey, out string passKey))
            {
                return false;
            }

            bool encrypt = (man.Entries.Count > 0 && man.Encrypted) || passKey != null;
            StorageResult saveResult = man.SaveAccount(account, encrypt, passKey, expectedRevision, allowCreate: false);
            if (!saveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", saveResult.Exception, "The refreshed Steam session could not be saved.");
                AstroMessageBox.Show(saveResult.UserMessage ?? "The refreshed session could not be saved.", "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            ResolvedPassKey = encrypt ? passKey : null;
            AstroMessageBox.Show("Your session was refreshed.", "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
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

        public enum LoginType
        {
            Initial,
            Refresh,
            Import
        }
    }
}
