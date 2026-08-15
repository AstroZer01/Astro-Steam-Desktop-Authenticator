using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using SteamAuth;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;
using System.Drawing;
using System.Threading;

namespace Steam_Desktop_Authenticator
{
    public partial class LoginForm : Form
    {
        public SteamGuardAccount account;
        public LoginType LoginReason;
        public SessionData Session;

        public LoginForm(LoginType loginReason = LoginType.Initial, SteamGuardAccount account = null)
        {
            InitializeComponent();
            this.LoginReason = loginReason;
            this.account = account;

            try
            {
                if (this.LoginReason != LoginType.Initial)
                {
                    txtUsername.Text = account.AccountName;
                    txtUsername.Enabled = false;
                }

                if (this.LoginReason == LoginType.Refresh)
                {
                    labelLoginExplanation.Text = "Your Steam credentials have expired. For trade and market confirmations to work properly, please login again.";
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
        private const int SteamConnectionTimeoutMilliseconds = 20000;
        private CancellationTokenSource loginCancellationSource;
        private SteamClient activeSteamClient;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            loginCancellationSource?.Cancel();
            activeSteamClient?.Disconnect();
            base.OnFormClosing(e);
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
                lblLoading.Text = "Login UI could not be loaded. Restore the complete release folder and try again.";
                return;
            }

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            webView.NavigationCompleted += (sender, args) =>
            {
                if (!args.IsSuccess)
                {
                    lblLoading.Text = "Login UI could not be loaded. Restore the complete release folder and try again.";
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
                string jsExp = labelLoginExplanation.Text.Replace("'", "\\'");
                webView.CoreWebView2.ExecuteScriptAsync($"setExplanation('{jsExp}')");
                
                if (this.LoginReason != LoginType.Initial)
                {
                    webView.CoreWebView2.ExecuteScriptAsync($"setUsername('{account.AccountName}', true)");
                }
            };

            string htmlPath = System.IO.Path.Combine(ApplicationPaths.UiDirectory, "login.html");
            webView.Source = new Uri(htmlPath);
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(message)) return;

            JObject payload = JObject.Parse(message);
            string action = (string)payload["action"];

            if (action == "login")
            {
                txtUsername.Text = (string)payload["user"];
                txtPassword.Text = (string)payload["pass"];
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
            if (IsDisposed || loginCancellationSource == null || loginCancellationSource.Token != attemptCancellationToken)
                return;
            btnSteamLogin.Enabled = true;
            btnSteamLogin.Text = "Login";
            if (webView != null && webView.CoreWebView2 != null)
                _ = webView.CoreWebView2.ExecuteScriptAsync("setButtonState('LOGIN', false)");
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
            if (webView != null && webView.CoreWebView2 != null)
                _ = webView.CoreWebView2.ExecuteScriptAsync("setButtonState('LOGGING IN...', true)");

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
            SteamClient steamClient = new SteamClient();
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
                pollResponse = await authSession.PollingWaitForResultAsync();
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

            // If we're only logging in for a session refresh then save it and exit
            if (LoginReason == LoginType.Refresh)
            {
                Manifest man = Manifest.GetManifest();
                account.FullyEnrolled = true;
                account.Session = sessionData;
                HandleManifest(man, true);
                this.Close();
                return;
            }

            // Begin linking mobile authenticator
            AuthenticatorLinker linker = new AuthenticatorLinker(sessionData);
            linker.FinalizationProgress += UpdateFinalizationProgress;

            AuthenticatorEnrollmentCoordinator coordinator = new AuthenticatorEnrollmentCoordinator(
                linker,
                new LoginPhoneEnrollmentInteraction(account, this));
            AuthenticatorEnrollmentOutcome enrollmentOutcome = await coordinator.StartAsync(cancellationToken);
            switch (enrollmentOutcome.Result)
            {
                case AuthenticatorEnrollmentResult.AwaitingFinalization:
                    break;

                case AuthenticatorEnrollmentResult.AuthenticatorPresent:
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

            Manifest manifest = Manifest.GetManifest();
            string passKey = null;
            if (manifest.Entries.Count == 0)
            {
                passKey = manifest.PromptSetupPassKey("Please enter an encryption passkey. Leave blank or hit cancel to not encrypt (VERY INSECURE).");
            }
            else if (manifest.Entries.Count > 0 && manifest.Encrypted)
            {
                bool passKeyValid = false;
                while (!passKeyValid)
                {
                    using (InputForm passKeyForm = new InputForm("Please enter your current encryption passkey."))
                    {
                        passKeyForm.ShowInputDialog(this);
                        if (!passKeyForm.Canceled)
                        {
                            passKey = passKeyForm.txtBox.Text;
                            passKeyValid = manifest.VerifyPasskey(passKey);
                            if (!passKeyValid)
                            {
                                AstroMessageBox.Show("That passkey is invalid. Please enter the same passkey you used for your other accounts.");
                            }
                        }
                        else
                        {
                            this.Close();
                            return;
                        }
                    }
                }
            }

            //Save the file immediately; losing this would be bad.
            StorageResult initialSaveResult = manifest.SaveAccount(linker.LinkedAccount, passKey != null, passKey);
            if (!initialSaveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", initialSaveResult.Exception, "The initial authenticator record could not be saved.");
                AstroMessageBox.Show(initialSaveResult.UserMessage ?? "Unable to save the mobile authenticator file. The setup was stopped before finalization.");
                this.Close();
                return;
            }

            ShowRecoveryCode(linker.LinkedAccount, "The mobile authenticator is not linked yet. Save this recovery code before continuing.", true);

            AuthenticatorLinker.FinalizeResult finalizeResponse = AuthenticatorLinker.FinalizeResult.GeneralFailure;
            bool previousFinalizationCodeWasInvalid = false;
            while (finalizeResponse != AuthenticatorLinker.FinalizeResult.Success)
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
                    if (confirmationCodeForm.Canceled)
                    {
                        if (!manifest.RemoveAccount(linker.LinkedAccount))
                        {
                            const string removalError = "The unfinished authenticator record could not be removed safely. It may still be present in the app; restart the app before making further account changes.";
                            DiagnosticErrorLogger.Log("Authenticator storage", new InvalidOperationException(removalError), "Removing the unfinished authenticator record after setup cancellation failed.");
                            AstroMessageBox.Show(removalError, "Steam Guard Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            ResetLoginButton(cancellationToken);
                            return;
                        }
                        this.Close();
                        return;
                    }

                    confirmationCode = confirmationCodeForm.txtBox.Text;
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

                if (finalizeResponse != AuthenticatorLinker.FinalizeResult.Success)
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
            StorageResult finalSaveResult = manifest.SaveAccount(linker.LinkedAccount, passKey != null, passKey);
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
            ShowRecoveryCode(linker.LinkedAccount, "Mobile authenticator successfully linked. Keep this recovery code safe.");
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
            if (webView?.CoreWebView2 != null)
                _ = webView.CoreWebView2.ExecuteScriptAsync("setExplanation(" + Newtonsoft.Json.JsonConvert.SerializeObject(message) + ");");
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
                using (PhoneInputForm phoneInputForm = new PhoneInputForm(account))
                {
                    phoneInputForm.ShowDialog(owner);
                    return phoneInputForm.Canceled
                        ? null
                        : new PhoneEnrollmentDetails(phoneInputForm.PhoneNumber, phoneInputForm.CountryCode, phoneInputForm.ContinueWithoutPhone);
                }
            }

            public bool ConfirmPhoneRequired()
            {
                DialogResult result = AstroMessageBox.ShowWithCustomButtons(
                    "Steam requires a verified phone number to set up a mobile authenticator for this account. Continue to add and verify a phone number, or cancel setup.",
                    "Phone Number Required",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information,
                    "Continue",
                    "Cancel");
                return result == DialogResult.OK;
            }

            public bool ConfirmEmail(string confirmationEmailAddress)
            {
                string destination = string.IsNullOrWhiteSpace(confirmationEmailAddress)
                    ? "your Steam account email address"
                    : confirmationEmailAddress;
                DialogResult result = AstroMessageBox.Show(
                    "Steam sent a confirmation link to " + destination + ". Click that link, then select OK to send the SMS verification code.",
                    "Confirm Phone Number",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information);
                return result == DialogResult.OK;
            }

            public string RequestSmsCode(bool previousCodeWasInvalid)
            {
                string message = previousCodeWasInvalid
                    ? "That SMS code was not accepted. Please enter the new code sent to your phone."
                    : "Please enter the SMS code sent to your phone to verify the phone number.";
                    using (InputForm smsCodeForm = new InputForm(message))
                    {
                        smsCodeForm.ShowInputDialog(owner);
                    return smsCodeForm.Canceled ? null : smsCodeForm.txtBox.Text;
                }
            }
        }

        private void HandleManifest(Manifest man, bool IsRefreshing = false)
        {
            string passKey = null;
            if (man.Entries.Count == 0)
            {
                passKey = man.PromptSetupPassKey("Please enter an encryption passkey. Leave blank or hit cancel to not encrypt (VERY INSECURE).");
            }
            else if (man.Entries.Count > 0 && man.Encrypted)
            {
                bool passKeyValid = false;
                while (!passKeyValid)
                {
                    using (InputForm passKeyForm = new InputForm("Please enter your current encryption passkey."))
                    {
                        passKeyForm.ShowInputDialog(this);
                        if (!passKeyForm.Canceled)
                        {
                            passKey = passKeyForm.txtBox.Text;
                            passKeyValid = man.VerifyPasskey(passKey);
                            if (!passKeyValid)
                            {
                                AstroMessageBox.Show("That passkey is invalid. Please enter the same passkey you used for your other accounts.", "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                        else
                        {
                            this.Close();
                            return;
                        }
                    }
                }
            }

            StorageResult saveResult = man.SaveAccount(account, passKey != null, passKey);
            if (!saveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", saveResult.Exception, IsRefreshing
                    ? "The refreshed Steam session could not be saved."
                    : "The finalized authenticator record could not be saved.");
                AstroMessageBox.Show(saveResult.UserMessage ?? (IsRefreshing
                    ? "The refreshed session could not be saved."
                    : "The finalized authenticator record could not be saved."), "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (IsRefreshing)
            {
                AstroMessageBox.Show("Your session was refreshed.", "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                ShowRecoveryCode(account, "Mobile authenticator successfully linked. Keep this recovery code safe.");
            }
            this.Close();
        }

        private void ShowRecoveryCode(SteamGuardAccount recoveryAccount, string statusMessage, bool requireBackupBeforeContinue = false)
        {
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
