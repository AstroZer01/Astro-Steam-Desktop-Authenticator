using System;
using System.Diagnostics;
using System.Windows.Forms;
using SteamAuth;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Newtonsoft.Json;
using System.Threading;
using System.Drawing;
using System.Linq;

using ZXing.QrCode;
using System.Runtime.InteropServices;
using ZXing.Common;
using ZXing;
using ZXing.Windows.Compatibility;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace Steam_Desktop_Authenticator
{
    public partial class MainForm : Form
    {
        private SteamGuardAccount currentAccount = null;
        private SteamGuardAccount[] allAccounts;
        private List<string> updatedSessions = new List<string>();
        private Manifest manifest;
        private static SemaphoreSlim confirmationsSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim loginActionsSemaphore = new SemaphoreSlim(1, 1);
        private readonly System.Windows.Forms.Timer loginActionsTimer = new System.Windows.Forms.Timer() { Interval = 2000 };
        private readonly HashSet<string> notifiedLoginRequests = new HashSet<string>();
        private readonly HashSet<string> notifiedUnavailableLoginAccounts = new HashSet<string>();
        private readonly Dictionary<string, DateTime> automatedLoginActionNotifications = new Dictionary<string, DateTime>();
        private readonly HashSet<string> completedAutomatedLoginActions = new HashSet<string>();
        private readonly Dictionary<string, PendingLoginRequest> pendingLoginRequests = new Dictionary<string, PendingLoginRequest>();
        private readonly List<RecentLoginAttempt> recentLoginAttempts = new List<RecentLoginAttempt>();
        private readonly HashSet<string> recordedRecentLoginAttempts = new HashSet<string>();
        private readonly HashSet<string> notifiedTradeConfirmations = new HashSet<string>();
        private readonly Dictionary<ulong, int> pendingTradeConfirmationCounts = new Dictionary<ulong, int>();
        private readonly SemaphoreSlim tradeLoadSemaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<ulong, SemaphoreSlim> steamAccountOperationLocks = new ConcurrentDictionary<ulong, SemaphoreSlim>();
        private readonly Dictionary<string, LoadedTradeConfirmation> loadedTradeConfirmations = new Dictionary<string, LoadedTradeConfirmation>();
        private readonly HashSet<ulong> loadedTradeConfirmationAccounts = new HashSet<ulong>();
        private readonly Dictionary<string, string> unavailableLoginAccounts = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> recentlyResolvedLoginRequests = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> recentlyResolvedTradeConfirmations = new Dictionary<string, DateTime>();
        private readonly List<LoginNotificationPopup> activeLoginNotificationPopups = new List<LoginNotificationPopup>();
        private LoginApprovalService loginApprovalService;
        private Action trayNotificationClickAction;
        private string tradeAccountSelection = "all";
        private readonly object tradeRateLimitLock = new object();
        private DateTime tradeRateLimitedUntilUtc = DateTime.MinValue;
        private readonly object loginRateLimitLock = new object();
        private DateTime loginRateLimitedUntilUtc = DateTime.MinValue;
        private readonly Dictionary<ulong, LoginMonitorSchedule> loginMonitorSchedules = new Dictionary<ulong, LoginMonitorSchedule>();
        private int loginMonitoringAccountIndex;
        private long loginMonitorRequestCount;
        private static readonly TimeSpan LoginMonitorSuccessInterval = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan LoginMonitorInitialFailureBackoff = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LoginMonitorMaximumFailureBackoff = TimeSpan.FromMinutes(5);
        private int tradeMonitoringAccountIndex;
        private string activeWebTab = "authenticator";
        private long tradeViewRevision;
        private long loginViewRevision;
        private static readonly TimeSpan RecentlyResolvedRequestRetention = TimeSpan.FromSeconds(30);

        private long steamTime = 0;
        private long currentSteamChunk = 0;
        private string passKey = null;
        private bool startSilent = false;
        private bool backgroundServicesEligible;
        private bool backgroundServicesStarted;

        const int VK_RCONTROL = 0xA3;
        const int VK_ESCAPE = 0x1B;
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern short GetAsyncKeyState(int vKey);

        // Forms
        private ToolStripMenuItem trayLoginActions;
        private ToolStripMenuItem trayAccountHeading;
        private QrScanOverlayForm qrScanOverlay;
        private bool qrScanInProgress;

        private sealed class RecentLoginAttempt
        {
            public PendingLoginRequest Request { get; set; }
            public string Outcome { get; set; }
            public DateTime OccurredAtUtc { get; set; }
        }

        private sealed class LoadedTradeConfirmation
        {
            public SteamGuardAccount Account { get; set; }
            public Confirmation Confirmation { get; set; }
        }

        private sealed class TradeRateLimitedException : Exception
        {
            public TradeRateLimitedException(string message) : base(message)
            {
            }
        }

        private async Task<T> RunSteamAccountOperationAsync<T>(SteamGuardAccount account, Func<Task<T>> operation)
        {
            ulong steamId = account?.Session?.SteamID ?? 0;
            SemaphoreSlim accountLock = steamAccountOperationLocks.GetOrAdd(steamId, _ => new SemaphoreSlim(1, 1));
            await accountLock.WaitAsync();
            try
            {
                return await operation();
            }
            finally
            {
                accountLock.Release();
            }
        }

        private Task<Confirmation[]> FetchTradeConfirmationsForPageAsync(SteamGuardAccount account)
        {
            return FetchTradeConfirmationsAsync(account, TimeSpan.FromSeconds(1), 2);
        }

        private Task<Confirmation[]> FetchTradeConfirmationsForMonitorAsync(SteamGuardAccount account)
        {
            int seconds = Math.Min(GetTradeConfirmationMonitorIntervalSeconds(), 15);
            return FetchTradeConfirmationsAsync(account, TimeSpan.FromSeconds(seconds), 3);
        }

        private int GetTradeConfirmationMonitorIntervalSeconds()
        {
            if (manifest?.TradeConfirmationCustomIntervalEnabled != true)
                return 15;

            return Math.Clamp(manifest.TradeConfirmationCheckInterval, 3, 3600);
        }

        private bool ShouldAutoConfirmTrade(Confirmation confirmation)
        {
            return confirmation != null &&
                ((confirmation.ConfType == Confirmation.EMobileConfirmationType.MarketListing && manifest?.AutoConfirmMarketTransactions == true) ||
                 (confirmation.ConfType == Confirmation.EMobileConfirmationType.Trade && manifest?.AutoConfirmTrades == true));
        }

        private async Task<Confirmation[]> FetchTradeConfirmationsAsync(SteamGuardAccount account, TimeSpan retryDelay, int retryCount)
        {
            if (TryGetTradeRateLimitMessage(out string rateLimitMessage))
                throw new TradeRateLimitedException(rateLimitMessage);

            Exception lastError = null;
            bool refreshAccessTokenBeforeRetry = false;

            // The mobile-confirmation signature is time-sensitive.  Make sure the
            // asynchronous time alignment has completed before creating the first
            // request, rather than relying on the one-second UI timer to win a race.
            await TimeAligner.GetSteamTimeAsync();

            for (int attempt = 0; attempt <= retryCount; attempt++)
            {
                try
                {
                    return await RunSteamAccountOperationAsync(account, async () =>
                    {
                        if (refreshAccessTokenBeforeRetry || account.Session.IsAccessTokenExpired())
                        {
                            await account.Session.RefreshAccessToken();
                            if (!PersistLoginSession(account))
                                throw new InvalidOperationException("Steam refreshed the session, but Astro SDA could not save it securely.");
                        }
                        return await account.FetchConfirmationsAsync();
                    });
                }
                catch (Exception ex) when (IsRateLimitedResponse(ex))
                {
                    throw ApplyTradeRateLimit(ex);
                }
                catch (Exception ex) when (attempt < retryCount)
                {
                    lastError = ex;
                    refreshAccessTokenBeforeRetry = IsTradeAuthenticationFailure(ex);
                    await Task.Delay(retryDelay);
                }
            }

            throw lastError ?? new InvalidOperationException("Steam could not load confirmations.");
        }

        private bool TryGetTradeRateLimitMessage(out string message)
        {
            lock (tradeRateLimitLock)
            {
                if (DateTime.UtcNow >= tradeRateLimitedUntilUtc)
                {
                    message = null;
                    return false;
                }

                TimeSpan remaining = tradeRateLimitedUntilUtc - DateTime.UtcNow;
                message = "Steam is temporarily limiting confirmation checks. Retrying automatically in about " +
                    Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) + " seconds.";
                return true;
            }
        }

        private TradeRateLimitedException ApplyTradeRateLimit(Exception exception)
        {
            TimeSpan delay = TimeSpan.FromSeconds(60);
            string retryAfter = null;
            if (exception is SteamWebRequestException steamException)
            {
                retryAfter = steamException.Headers?["Retry-After"];
            }
            else if (exception is WebException webException && webException.Response is HttpWebResponse response)
            {
                retryAfter = response.Headers?["Retry-After"];
            }

            if (Int32.TryParse(retryAfter, out int retryAfterSeconds) && retryAfterSeconds > 0)
                delay = TimeSpan.FromSeconds(retryAfterSeconds);
            else if (DateTimeOffset.TryParse(retryAfter, out DateTimeOffset retryAfterUtc))
                delay = retryAfterUtc.UtcDateTime - DateTime.UtcNow;

            if (delay < TimeSpan.FromSeconds(15))
                delay = TimeSpan.FromSeconds(15);
            if (delay > TimeSpan.FromMinutes(5))
                delay = TimeSpan.FromMinutes(5);

            lock (tradeRateLimitLock)
            {
                DateTime proposedUntil = DateTime.UtcNow.Add(delay);
                if (proposedUntil > tradeRateLimitedUntilUtc)
                    tradeRateLimitedUntilUtc = proposedUntil;
            }

            TryGetTradeRateLimitMessage(out string message);
            return new TradeRateLimitedException(message);
        }

        private static bool IsRateLimitedResponse(Exception exception)
        {
            if (exception is SteamWebRequestException steamException)
                return steamException.StatusCode == HttpStatusCode.TooManyRequests;
            return exception is WebException webException &&
                webException.Response is HttpWebResponse response &&
                response.StatusCode == HttpStatusCode.TooManyRequests;
        }

        private bool TryGetLoginRateLimitMessage(out string message)
        {
            lock (loginRateLimitLock)
            {
                if (DateTime.UtcNow >= loginRateLimitedUntilUtc)
                {
                    message = null;
                    return false;
                }

                TimeSpan remaining = loginRateLimitedUntilUtc - DateTime.UtcNow;
                message = "Steam is temporarily limiting login checks. Retrying automatically in about " +
                    Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) + " seconds.";
                return true;
            }
        }

        private void ApplyLoginRateLimit()
        {
            lock (loginRateLimitLock)
            {
                DateTime proposedUntil = DateTime.UtcNow.AddSeconds(60);
                if (proposedUntil > loginRateLimitedUntilUtc)
                    loginRateLimitedUntilUtc = proposedUntil;
            }
        }

        private static bool IsTradeAuthenticationFailure(Exception exception)
        {
            if (exception is SteamGuardAccount.WGTokenInvalidException)
                return true;

            string message = exception?.GetBaseException().Message ?? String.Empty;
            return message.IndexOf("Needs Authentication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("403", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public MainForm()
        {
            InitializeComponent();
            timerSteamGuard.Enabled = false;
            timerTradesPopup.Enabled = false;
            loginActionsTimer.Tick += loginActionsTimer_Tick;
        }

        public void SetEncryptionKey(string key)
        {
            passKey = key;
        }

        public void StartSilent(bool silent)
        {
            startSilent = silent;
        }

        // Form event handlers

        private void MainForm_Shown(object sender, EventArgs e)
        {
            this.labelVersion.Text = String.Format("v{0}", Application.ProductVersion);
            try
            {
                this.manifest = Manifest.GetManifest();
                DiagnosticErrorLogger.Configure(this.manifest.DiagnosticErrorLoggingEnabled);
            }
            catch (ManifestParseException)
            {
                AstroMessageBox.Show("Unable to read your settings. Try restating Astro Steam Desktop Assistant.", "Astro Steam Desktop Assistant", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }

            // Make sure we don't show that welcome dialog again
            this.manifest.FirstRun = false;
            this.manifest.Save();

            if (manifest.Encrypted)
            {
                if (passKey == null)
                {
                    passKey = manifest.PromptForPassKey();
                    if (passKey == null)
                    {
                        Application.Exit();
                    }
                }

                btnManageEncryption.Text = "Remove Encryption";
            }
            else
            {
                btnManageEncryption.Text = "Setup Encryption";
            }

            btnManageEncryption.Enabled = manifest.Entries.Count > 0;

            loadAccountsList();
            loginApprovalService = new LoginApprovalService(PersistLoginSession);

            if (backgroundServicesEligible)
                StartBackgroundServicesAfterUiReady();

            if (startSilent)
            {
                this.WindowState = FormWindowState.Minimized;
            }
        }

        // Custom progress bar that replaces the standard one
        private AstroProgressBar astroProgressBar;

        private void MainForm_Load(object sender, EventArgs e)
        {
            trayIcon.Icon = this.Icon;

            // Apply the Astro dark theme to all controls
            AstroTheme.ApplyTheme(this);

            // Style the tray context menu
            AstroTheme.StyleContextMenuStrip(menuStripTray);
            ConfigureTrayContextMenu();
            trayAccountList.BackColor = AstroTheme.SurfaceContainer;
            trayAccountList.ForeColor = AstroTheme.OnSurface;
            trayAccountList.FlatStyle = FlatStyle.Flat;

            // Handle left click on tray icon to restore
            trayIcon.MouseClick += (s, ev) =>
            {
                if (ev.Button == MouseButtons.Left)
                {
                    trayRestore_Click(s, ev);
                }
            };
            trayIcon.BalloonTipClicked += (s, ev) => (trayNotificationClickAction ?? OpenLoginActionsFromTray)();

            // Form-specific overrides for special controls
            // Login token textbox should use monospace font with cyan text
            txtLoginToken.Font = AstroTheme.FontLoginToken;
            txtLoginToken.ForeColor = AstroTheme.Primary;
            txtLoginToken.BackColor = AstroTheme.SurfaceContainerLowest;

            // Style specific buttons that need custom treatment
            AstroTheme.StylePrimaryButton(btnSteamLogin);
            AstroTheme.StylePrimaryButton(btnManageEncryption);
            AstroTheme.StyleSurfaceButton(btnTradeConfirmations);
            AstroTheme.StyleSurfaceButton(btnLoginViaQr);
            AstroTheme.StyleSurfaceButton(btnCopy);

            // Replace the standard ProgressBar with AstroProgressBar
            astroProgressBar = new AstroProgressBar();
            astroProgressBar.Name = "astroProgressBar";
            astroProgressBar.Location = pbTimeout.Location;
            astroProgressBar.Size = pbTimeout.Size;
            astroProgressBar.Anchor = pbTimeout.Anchor;
            astroProgressBar.Minimum = pbTimeout.Minimum;
            astroProgressBar.Maximum = pbTimeout.Maximum;
            astroProgressBar.Value = pbTimeout.Value;

            // Add the custom progress bar and remove the old one
            pbTimeout.Parent.Controls.Add(astroProgressBar);
            pbTimeout.Visible = false;

            // Label overrides
            lblStatus.ForeColor = AstroTheme.OnSurfaceVariant;

            // Restructure UI into Modern Tabs (Phase 1 & 2)
            SetupModernUI();
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                // Only hide to system tray when the user has that option enabled.
                // Otherwise keep the window in the taskbar as a normal minimized app.
                if (manifest != null && manifest.MinimizeToTray)
                {
                    this.Hide();
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.RestoreExistingInstanceMessage)
            {
                try
                {
                    BeginInvoke((MethodInvoker)RestoreWindowFromActivation);
                }
                catch (InvalidOperationException)
                {
                    // The form is shutting down and cannot accept an activation request.
                }
                return;
            }

            base.WndProc(ref m);
        }

        private void RestoreWindowFromActivation()
        {
            if (IsDisposed)
                return;

            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && manifest != null && manifest.MinimizeToTray)
            {
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                loginActionsTimer.Stop();
                CloseLoginNotificationPopups();
                Application.Exit();
            }
        }


        // UI Button handlers

        private void btnSteamLogin_Click(object sender, EventArgs e)
        {
            using (LoginForm loginForm = new LoginForm())
            {
                loginForm.ShowDialog(this);
            }
            this.loadAccountsList();
        }

        static async Task WaitForLeftAltKeyPress()
        {
            while (true)
            {
                if ((GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0)
                    break;

                await Task.Delay(100);
            }
        }

        private void SetQrScanWaitingState(bool isWaiting, int remainingSeconds = 0)
        {
            if (isWaiting)
            {
                if (qrScanOverlay == null || qrScanOverlay.IsDisposed)
                    qrScanOverlay = new QrScanOverlayForm();

                qrScanOverlay.FollowCursor();
                if (!qrScanOverlay.Visible)
                    qrScanOverlay.Show(this);
            }
            else if (qrScanOverlay?.Visible == true)
            {
                qrScanOverlay.Hide();
            }

            if (webView?.CoreWebView2 != null)
            {
                string waitingValue = isWaiting ? "true" : "false";
                _ = webView.CoreWebView2.ExecuteScriptAsync($"setQrScanWaiting({waitingValue}, {remainingSeconds})");
            }
        }

        private async void btnLoginViaQr_Click(object sender, EventArgs e)
        {
            if (qrScanInProgress)
                return;

            if (currentAccount == null)
            {
                SetQrScanWaitingState(false);
                return;
            }

            qrScanInProgress = true;
            try
            {
                this.btnLoginViaQr.Enabled = false;
                string originalText = this.btnLoginViaQr.Text;
                SetQrScanWaitingState(true, 30);

                bool keyPressed = false;
                bool cancelled = false;
                for (int i = 300; i > 0; i--)
                {
                    if ((GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0)
                    {
                        keyPressed = true;
                        break;
                    }

                    if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                    {
                        cancelled = true;
                        break;
                    }

                    if (i % 10 == 0)
                    {
                        this.btnLoginViaQr.Text = $"Press Right CTRL ({i / 10}s)";
                        SetQrScanWaitingState(true, i / 10);
                    }

                    await Task.Delay(100);
                }

                this.btnLoginViaQr.Text = originalText;
                this.btnLoginViaQr.Enabled = true;
                SetQrScanWaitingState(false);

                if (!keyPressed)
                {
                    string cancellationReason = cancelled ? "QR Code scan cancelled." : "QR Code scan cancelled (timeout).";
                    AstroMessageBox.Show(cancellationReason, "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                GetCursorPos(out Point cursorPos);
                int scanWidth = 500;
                int scanHeight = 500;

                using (Bitmap bitmap = new Bitmap(scanWidth, scanHeight))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(cursorPos.X - scanWidth / 2, cursorPos.Y - scanHeight / 2, 0, 0, bitmap.Size);
                    }

                    var reader = new BarcodeReader();
                    var result = reader.Decode(bitmap);

                    if (result == null)
                    {
                        AstroMessageBox.Show("No QR code detected. Make sure your cursor is exactly over the QR code.", "Scan Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                
                    string idOfQR = null;
                    if (!string.IsNullOrEmpty(result.Text))
                    {
                        string[] parts = result.Text.Split('/');
                        if (parts.Length > 5)
                        {
                            idOfQR = parts[5];
                        }
                    }

                    if (string.IsNullOrEmpty(idOfQR))
                    {
                        AstroMessageBox.Show("Can't get ID of QR code. Steam might have changed their QR format.", "Wrong QR code.", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    try
                    {
                        string response = await currentAccount.SignInViaQR(idOfQR);
                        if (response != "1")
                        {
                            DiagnosticErrorLogger.Log("QR login approval", new InvalidOperationException("Steam rejected a QR login approval with EResult " + response + "."), "The QR login approval was not accepted.");
                            AstroMessageBox.Show(GetQrLoginFailureMessage(response), "QR Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                            AstroMessageBox.Show("Successfully logged in via QR code!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("QR login approval", ex, "The QR login approval request failed.");
                        AstroMessageBox.Show("Steam could not complete the QR login approval. Refresh the Steam login page and scan a new QR code, then try again.", "QR Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            finally
            {
                qrScanInProgress = false;
            }
        }

        private void btnTradeConfirmations_Click(object sender, EventArgs e)
        {
            // Now handled by WebView2
        }

        private void btnManageEncryption_Click(object sender, EventArgs e)
        {
            if (manifest.Encrypted)
            {
                string curPassKey;
                using (InputForm currentPassKeyForm = new InputForm("Enter current passkey to remove encryption", true))
                {
                    currentPassKeyForm.ShowInputDialog(this);
                    if (currentPassKeyForm.Canceled)
                        return;

                    curPassKey = currentPassKeyForm.txtBox.Text;
                }

                StorageResult encryptionResult = manifest.ChangeEncryptionKey(curPassKey, null);
                if (!encryptionResult.Succeeded)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", encryptionResult.Exception, "Removing encryption from authenticator files failed.");
                    AstroMessageBox.Show(encryptionResult.UserMessage ?? "Unable to remove passkey. Incorrect passkey?");
                }
                else
                {
                    AstroMessageBox.Show("Encryption successfully removed.");
                    this.loadAccountsList();
                    btnManageEncryption.Text = "Setup Encryption";
                }
            }
            else
            {
                passKey = manifest.PromptSetupPassKey();
                this.loadAccountsList();
                if (manifest.Encrypted) btnManageEncryption.Text = "Remove Encryption";
            }
        }

        private void labelUpdate_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (newVersion == null || currentVersion == null)
            {
                checkForUpdates();
            }
            else
            {
                compareVersions();
            }
        }

        private void btnCopy_Click(object sender, EventArgs e)
        {
            CopyLoginToken();
        }


        // Tool strip menu handlers

        private void menuQuit_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void menuRemoveAccountFromManifest_Click(object sender, EventArgs e)
        {
            if (manifest.Encrypted)
            {
                AstroMessageBox.Show("You cannot remove accounts from the manifest file while it is encrypted.", "Remove from manifest", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                DialogResult res = AstroMessageBox.Show("This will remove the selected account from the manifest file.\nUse this to move a maFile to another computer.\nThis will NOT delete your maFile.", "Remove from manifest", MessageBoxButtons.OKCancel);
                if (res == DialogResult.OK)
                {
                    if (manifest.RemoveAccount(currentAccount, passKey, false))
                    {
                        AstroMessageBox.Show("Account removed from manifest.\nYou can now move its maFile to another computer and import it using the File menu.", "Remove from manifest");
                        loadAccountsList();
                    }
                    else
                    {
                        AstroMessageBox.Show("This app could not remove the account from the manifest. No files were deleted.", "Remove from manifest", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void menuLoginAgain_Click(object sender, EventArgs e)
        {
            this.PromptRefreshLogin(currentAccount);
        }

        private void menuImportAccount_Click(object sender, EventArgs e)
        {
            using (ImportAccountForm currentImportMaFileForm = new ImportAccountForm(this.passKey))
            {
                currentImportMaFileForm.ShowDialog(this);
            }
            loadAccountsList();
        }

        private void menuSettings_Click(object sender, EventArgs e)
        {
            // Now handled by WebView2
        }

        private async void menuDeactivateAuthenticator_Click(object sender, EventArgs e)
        {
            if (currentAccount == null) return;

            // Check for a valid refresh token first
            if (currentAccount.Session.IsRefreshTokenExpired())
            {
                AstroMessageBox.Show("Your session has expired. Use the login again button under the selected account menu.", "Deactivate Authenticator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Check for a valid access token, refresh it if needed
            if (currentAccount.Session.IsAccessTokenExpired())
            {
                try
                {
                    await RunSteamAccountOperationAsync(currentAccount, async () =>
                    {
                        await currentAccount.Session.RefreshAccessToken();
                        return true;
                    });
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Steam Guard deactivation", ex, "Steam Guard could not be removed from the selected account.");
                    AstroMessageBox.Show("Steam Guard could not be removed. Check the account's login status and try again.", "Deactivate Authenticator Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            DialogResult res = AstroMessageBox.Show("Would you like to remove Steam Guard completely?\nYes - Remove Steam Guard completely.\nNo - Switch back to Email authentication.", "Deactivate Authenticator: " + currentAccount.AccountName, MessageBoxButtons.YesNoCancel);
            int scheme = 0;
            if (res == DialogResult.Yes)
            {
                scheme = 2;
            }
            else if (res == DialogResult.No)
            {
                scheme = 1;
            }
            else if (res == DialogResult.Cancel)
            {
                scheme = 0;
            }

            if (scheme != 0)
            {
                string confCode;
                try
                {
                    confCode = await currentAccount.GenerateSteamGuardCodeAsync();
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Steam Guard deactivation", ex, "The confirmation code could not be generated.");
                    AstroMessageBox.Show("The confirmation code could not be generated. Check your connection and try again.", "Deactivate Authenticator Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                string enteredCode;
                using (InputForm confirmationDialog = new InputForm(String.Format("Removing Steam Guard from {0}. Enter this confirmation code: {1}", currentAccount.AccountName, confCode)))
                {
                    confirmationDialog.ShowInputDialog(this);
                    if (confirmationDialog.Canceled)
                        return;

                    enteredCode = confirmationDialog.txtBox.Text.ToUpperInvariant();
                }

                if (enteredCode != confCode)
                {
                    AstroMessageBox.Show("Confirmation codes do not match. Steam Guard not removed.");
                    return;
                }

                bool success = await RunSteamAccountOperationAsync(currentAccount, () => currentAccount.DeactivateAuthenticator(scheme));
                if (success)
                {
                    AstroMessageBox.Show(String.Format("Steam Guard {0}. maFile will be deleted after hitting okay. If you need to make a backup, now's the time.", (scheme == 2 ? "removed completely" : "switched to emails")));
                    this.manifest.RemoveAccount(currentAccount);
                    this.loadAccountsList();
                }
                else
                {
                    AstroMessageBox.Show(String.IsNullOrWhiteSpace(currentAccount.LastAuthenticatorOperationError)
                        ? "Steam Guard failed to deactivate."
                        : currentAccount.LastAuthenticatorOperationError);
                }
            }
            else
            {
                AstroMessageBox.Show("Steam Guard was not removed. No action was taken.");
            }
        }

        private static string GetQrLoginFailureMessage(string result)
        {
            if (!Int32.TryParse(result, out int steamResult))
                return "Steam returned an invalid QR login response. Refresh the Steam login page and scan a new QR code.";

            switch (steamResult)
            {
                case 84:
                case 87:
                    return "Steam is rate limiting QR login approvals. Wait a while, then scan a new QR code and try again.";
                case 15:
                    return "Steam denied this QR login approval. Refresh the Steam login page and scan a new QR code.";
                case 20:
                    return "Steam's QR approval service is temporarily unavailable. Wait a while, then try again.";
                case 27:
                case 29:
                    return "That QR login request has expired or is no longer available. Refresh the Steam login page and scan a new QR code.";
                default:
                    return "Steam did not accept this QR login approval. Refresh the Steam login page and scan a new QR code.";
            }
        }

        private async Task RemoveManagedAccountAsync(ulong? steamId, string accountName, string removalMode)
        {
            bool removeSteamGuard = removalMode == "unlink";
            SteamGuardAccount account = null;
            if (allAccounts != null)
            {
                if (steamId.HasValue)
                {
                    account = allAccounts.FirstOrDefault(candidate => candidate.Session != null && candidate.Session.SteamID == steamId.Value);
                }
                else if (!removeSteamGuard && !String.IsNullOrWhiteSpace(accountName))
                {
                    SteamGuardAccount[] localMatches = allAccounts
                        .Where(candidate => candidate.Session == null && String.Equals(candidate.AccountName, accountName, StringComparison.Ordinal))
                        .Take(2)
                        .ToArray();
                    if (localMatches.Length == 1)
                        account = localMatches[0];
                }
            }
            if (account == null)
            {
                AstroMessageBox.Show("That account is no longer managed by this app.", "Remove Account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (removeSteamGuard && account.Session == null)
            {
                AstroMessageBox.Show("Steam Guard cannot be removed because this local account does not have a saved Steam session. You can still remove it from this app.", "Remove Steam Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string confirmationText = removeSteamGuard
                ? "This will permanently remove Steam Guard from " + account.AccountName + ", then delete this account's local maFile and manifest entry. This decision is final. Continue?"
                : "This will permanently delete only " + account.AccountName + "'s local maFile and manifest entry. Steam Guard will remain enabled on the Steam account. This decision is final. Continue?";
            DialogResult confirmation = AstroMessageBox.Show(
                confirmationText,
                removeSteamGuard ? "Remove Steam Guard" : "Remove Account from App",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirmation != DialogResult.Yes)
                return;

            bool steamGuardWasRemoved = false;
            bool steamGuardRemovalStatusUnknown = false;
            if (removeSteamGuard)
            {
                if (account.Session.IsRefreshTokenExpired())
                {
                    AstroMessageBox.Show("Your session has expired. Log in again before removing Steam Guard.", "Remove Steam Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (account.Session.IsAccessTokenExpired())
                {
                    try
                    {
                        await RunSteamAccountOperationAsync(account, async () =>
                        {
                            await account.Session.RefreshAccessToken();
                            return true;
                        });
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Managed account removal", ex, "The Steam session could not be refreshed before Steam Guard removal.");
                        AstroMessageBox.Show("Steam could not refresh this account session. Check your connection or sign in again before removing Steam Guard.", "Remove Steam Guard Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                string confirmationCode = await account.GenerateSteamGuardCodeAsync();
                string enteredConfirmationCode;
                using (InputForm confirmationDialog = new InputForm(String.Format("Removing Steam Guard from {0}. Enter this confirmation code: {1}", account.AccountName, confirmationCode)))
                {
                    confirmationDialog.ShowInputDialog(this);
                    if (confirmationDialog.Canceled)
                        return;

                    enteredConfirmationCode = confirmationDialog.txtBox.Text;
                }

                if (!String.Equals(enteredConfirmationCode, confirmationCode, StringComparison.OrdinalIgnoreCase))
                {
                    AstroMessageBox.Show("Confirmation codes do not match. Steam Guard was not removed.", "Remove Steam Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                bool deactivated = await RunSteamAccountOperationAsync(account, () => account.DeactivateAuthenticator(2));
                if (!deactivated)
                {
                    string failureMessage = String.IsNullOrWhiteSpace(account.LastAuthenticatorOperationError)
                        ? "Steam Guard removal could not be verified."
                        : account.LastAuthenticatorOperationError;
                    DialogResult removeLocalData = AstroMessageBox.Show(
                        failureMessage + "\n\nSteam Guard may already have been removed. Remove only this account's local data now, or cancel and verify the Steam Guard status first.",
                        "Remove Steam Guard",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (removeLocalData != DialogResult.Yes)
                        return;

                    steamGuardRemovalStatusUnknown = true;
                }
                else
                {
                    steamGuardWasRemoved = true;
                }
            }

            if (!manifest.RemoveAccount(account, passKey))
            {
                AstroMessageBox.Show(
                    steamGuardWasRemoved
                        ? "Steam Guard was removed, but this app could not remove the local account data. Use Remove user from app to retry only the local cleanup."
                        : steamGuardRemovalStatusUnknown
                        ? "The app could not verify the Steam Guard status and could not remove this account's local data. Verify Steam Guard on Steam, then use Remove user from app to retry only the local cleanup."
                        : "This app could not remove the local account data. Retry the removal to finish the cleanup.",
                    steamGuardWasRemoved ? "Steam Guard Removed" : "Remove Account Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            loadAccountsList();
            AstroMessageBox.Show(
                steamGuardWasRemoved
                    ? "Steam Guard was removed and this account's local data was deleted."
                    : steamGuardRemovalStatusUnknown
                    ? "This account's local data was deleted. The app could not verify whether Steam Guard was removed; check the account on Steam."
                    : "This account's local data was deleted. Steam Guard remains enabled on Steam.",
                steamGuardWasRemoved ? "Steam Guard Removed" : "Account Removed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private async Task RemoveManagedAccountFromWebAsync(ulong? steamId, string accountName, string removalMode)
        {
            try
            {
                await RemoveManagedAccountAsync(steamId, accountName, removalMode);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Managed account removal", ex, "The account removal command failed.");
                AstroMessageBox.Show("This app could not complete the account removal. No other accounts were changed.", "Remove Account", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    try
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync("hideSpinner('remove-account');");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Managed account removal", ex, "The account-removal loading indicator could not be cleared.");
                    }
                }
            }
        }

        // Tray menu handlers
        private void trayIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            trayRestore_Click(sender, EventArgs.Empty);
        }

        private void trayRestore_Click(object sender, EventArgs e)
        {
            RestoreWindowFromActivation();
        }

        private void trayQuit_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void trayTradeConfirmations_Click(object sender, EventArgs e)
        {
            OpenTradeConfirmationsFromNotification();
        }

        private void ConfigureTrayContextMenu()
        {
            trayRestore.Text = "Open Astro SDA";
            trayRestore.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            trayTradeConfirmations.Text = "Trade confirmations";
            trayCopySteamGuard.Text = "Copy Steam Guard code";
            trayQuit.Text = "Exit Astro SDA";

            trayLoginActions = new ToolStripMenuItem("Login Actions");
            trayLoginActions.Click += (sender, args) => OpenLoginActionsFromTray();
            trayAccountHeading = new ToolStripMenuItem("ACTIVE ACCOUNT")
            {
                Enabled = false,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            trayAccountList.AutoSize = false;
            trayAccountList.Size = new Size(230, 24);

            menuStripTray.ShowImageMargin = false;
            menuStripTray.Padding = new Padding(5, 5, 5, 5);
            menuStripTray.Items.Clear();
            menuStripTray.Items.AddRange(new ToolStripItem[]
            {
                trayRestore,
                trayLoginActions,
                trayTradeConfirmations,
                new ToolStripSeparator(),
                trayAccountHeading,
                trayAccountList,
                trayCopySteamGuard,
                new ToolStripSeparator(),
                trayQuit
            });
            menuStripTray.Opening += trayMenuStripTray_Opening;
        }

        private void trayMenuStripTray_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool hasAccounts = allAccounts != null && allAccounts.Length > 0;
            trayLoginActions.Enabled = hasAccounts;
            trayTradeConfirmations.Enabled = hasAccounts;
            trayAccountHeading.Enabled = hasAccounts;
            trayAccountList.Enabled = hasAccounts;
            trayCopySteamGuard.Enabled = hasAccounts && !String.IsNullOrWhiteSpace(txtLoginToken.Text);
        }

        private void trayCopySteamGuard_Click(object sender, EventArgs e)
        {
            if (txtLoginToken.Text != "")
            {
                Clipboard.SetText(txtLoginToken.Text);
            }
        }

        private void trayAccountList_SelectedIndexChanged(object sender, EventArgs e)
        {
            listAccounts.SelectedIndex = trayAccountList.SelectedIndex;
        }


        // Misc UI handlers
        private void listAccounts_SelectedValueChanged(object sender, EventArgs e)
        {
            for (int i = 0; i < allAccounts.Length; i++)
            {
                // Check if index is out of bounds first
                if (i < 0 || listAccounts.SelectedIndex < 0)
                    continue;

                SteamGuardAccount account = allAccounts[i];
                if (account.AccountName == (string)listAccounts.Items[listAccounts.SelectedIndex])
                {
                    trayAccountList.Text = account.AccountName;
                    currentAccount = account;
                    loadAccountInfo();
                    break;
                }
            }
        }

        private void txtAccSearch_TextChanged(object sender, EventArgs e)
        {
            List<string> names = new List<string>(getAllNames());
            names = names.FindAll(new Predicate<string>(IsFilter));

            listAccounts.Items.Clear();
            listAccounts.Items.AddRange(names.ToArray());

            trayAccountList.Items.Clear();
            trayAccountList.Items.AddRange(names.ToArray());
        }


        // Timers

        private async void timerSteamGuard_Tick(object sender, EventArgs e)
        {
            lblStatus.Text = "Aligning time with Steam...";
            steamTime = await TimeAligner.GetSteamTimeAsync();
            lblStatus.Text = "";

            currentSteamChunk = steamTime / 30L;
            int secondsUntilChange = (int)(steamTime - (currentSteamChunk * 30L));

            loadAccountInfo();
            if (currentAccount != null)
            {
                int val = 30 - secondsUntilChange;
                if (pbTimeout != null) pbTimeout.Value = val;
                if (astroProgressBar != null) astroProgressBar.Value = val;

                if (webView != null && webView.CoreWebView2 != null)
                {
                    _ = webView.CoreWebView2.ExecuteScriptAsync($"updateProgressBar({val})");
                }
            }
        }

        private async void timerTradesPopup_Tick(object sender, EventArgs e)
        {
            if (manifest == null) return;
            if (TryGetTradeRateLimitMessage(out _)) return;
            if (!confirmationsSemaphore.Wait(0))
            {
                return; //Only one thread may access this critical section at once. Mutex is a bad choice here because it'll cause a pileup of threads.
            }

            SteamGuardAccount[] accountsToMonitor = (allAccounts ?? Array.Empty<SteamGuardAccount>())
                .Where(account => account?.Session != null)
                .ToArray();
            if (accountsToMonitor.Length == 0)
            {
                confirmationsSemaphore.Release();
                return;
            }

            tradeMonitoringAccountIndex = ((tradeMonitoringAccountIndex % accountsToMonitor.Length) + accountsToMonitor.Length) % accountsToMonitor.Length;
            SteamGuardAccount account = accountsToMonitor[tradeMonitoringAccountIndex];
            bool advanceQueue = true;

            try
            {
                lblStatus.Text = "Checking confirmations...";
                if (account.Session.IsRefreshTokenExpired())
                {
                    lblStatus.Text = "";
                    AstroMessageBox.Show("Your session for account " + account.AccountName + " has expired. You will be prompted to login again.", "Trade Confirmations", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    PromptRefreshLogin(account);
                    InvalidateTradeConfirmationCache(account);
                    UpdateTradePendingCount(pendingTradeConfirmationCounts.Values.Sum());
                    return;
                }

                Confirmation[] confirmations = await FetchTradeConfirmationsForMonitorAsync(account) ?? Array.Empty<Confirmation>();
                lblStatus.Text = "";
                var pendingConfirmations = new List<Confirmation>();
                var autoAcceptConfirmations = new List<Confirmation>();
                foreach (Confirmation confirmation in confirmations)
                {
                    if (ShouldAutoConfirmTrade(confirmation))
                    {
                        autoAcceptConfirmations.Add(confirmation);
                    }
                    else
                    {
                        pendingConfirmations.Add(confirmation);
                    }
                }

                ReconcileTradeConfirmationNotifications(account, confirmations);
                foreach (var pending in pendingConfirmations)
                {
                    string confirmationKey = account.Session.SteamID.ToString() + ":" + pending.ID.ToString();
                    if (notifiedTradeConfirmations.Add(confirmationKey))
                        NotifyTradeConfirmation(account, pending);
                }
                pendingTradeConfirmationCounts[account.Session.SteamID] = pendingConfirmations.Count;
                UpdateTradePendingCount(pendingTradeConfirmationCounts.Values.Sum());
                CacheTradeConfirmations(account, pendingConfirmations);
                if (IsWebTabActive("trades"))
                    await PublishCachedTradesAsync();

                if (autoAcceptConfirmations.Count > 0)
                {
                    await RunSteamAccountOperationAsync(account, () => account.AcceptMultipleConfirmations(autoAcceptConfirmations.ToArray()));
                }
            }
            catch (TradeRateLimitedException ex)
            {
                lblStatus.Text = "";
                advanceQueue = false;
                DiagnosticErrorLogger.Log("Trade confirmation monitor", ex, "Steam rate limited the queued confirmation monitor.");
            }
            catch (Exception ex)
            {
                lblStatus.Text = "";
                Debug.WriteLine("Trade confirmation monitor failed: " + ex.Message);
                DiagnosticErrorLogger.Log("Trade confirmation monitor", ex, "The background confirmation scan did not complete.");
            }
            finally
            {
                // The monitor already keeps notifications and the navigation badge current.
                // Do not immediately re-fetch the same accounts merely to redraw the page:
                // that doubled traffic and caused Steam to return HTTP 429 responses.
                if (advanceQueue)
                    tradeMonitoringAccountIndex = (tradeMonitoringAccountIndex + 1) % accountsToMonitor.Length;
                confirmationsSemaphore.Release();
            }
        }

        private void ConfigureLoginActionsMonitor()
        {
            if (manifest == null)
                return;

            loginActionsTimer.Enabled = manifest.LoginActionMonitoringEnabled;
            if (manifest.LoginActionMonitoringEnabled)
                _ = MonitorLoginActionsSafelyAsync();
        }

        private async void loginActionsTimer_Tick(object sender, EventArgs e)
        {
            await MonitorLoginActionsSafelyAsync();
        }

        private async Task MonitorLoginActionsSafelyAsync()
        {
            try
            {
                await MonitorLoginActionsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Login action monitor failed: " + ex.Message);
                DiagnosticErrorLogger.Log("Login action monitor", ex, "The background login-request scan did not complete.");
            }
        }

        private async Task MonitorLoginActionsAsync()
        {
            if (manifest == null || loginApprovalService == null || !manifest.LoginActionMonitoringEnabled || allAccounts == null)
                return;
            if (TryGetLoginRateLimitMessage(out _))
                return;
            if (!await loginActionsSemaphore.WaitAsync(0))
                return;

            try
            {
                string currentDeviceIp = null;
                bool currentDeviceIpChecked = false;
                SteamGuardAccount nextAccount = GetNextLoginAccountToMonitor();
                if (nextAccount == null)
                    return;

                foreach (SteamGuardAccount account in new[] { nextAccount })
                {
                    Dictionary<ulong, PendingLoginRequest> knownRequests = pendingLoginRequests.Values
                        .Where(request => request.SteamId == account.Session.SteamID)
                        .GroupBy(request => request.ClientId)
                        .ToDictionary(group => group.Key, group => group.First());
                    Interlocked.Increment(ref loginMonitorRequestCount);
                    LoginApprovalFetchResult result = await RunSteamAccountOperationAsync(account,
                        () => loginApprovalService.FetchPendingRequestsAsync(account, knownRequests));
                    if (result.ErrorKind == LoginApprovalErrorKind.SessionExpired)
                    {
                        ScheduleLoginAccountScan(account, TimeSpan.FromMinutes(5), true);
                        unavailableLoginAccounts[account.AccountName] = result.ErrorMessage;
                        if (notifiedUnavailableLoginAccounts.Add(account.AccountName))
                        {
                            NotifyLoginAction("Login monitoring needs attention", account.AccountName + " needs you to sign in again before login requests can be monitored.", ToolTipIcon.Warning);
                        }
                        continue;
                    }

                    if (result.ErrorKind == LoginApprovalErrorKind.RateLimited)
                    {
                        ApplyLoginRateLimit();
                        ScheduleLoginAccountScan(account, LoginMonitorMaximumFailureBackoff, true);
                        DiagnosticErrorLogger.Log("Login action monitor", new InvalidOperationException(result.ErrorMessage), "Steam rate limited the login-request monitor.");
                        return;
                    }

                    if (result.ErrorKind != LoginApprovalErrorKind.None)
                    {
                        ScheduleLoginAccountFailure(account);
                        continue;
                    }

                    notifiedUnavailableLoginAccounts.Remove(account.AccountName);
                    unavailableLoginAccounts.Remove(account.AccountName);
                    string accountRequestPrefix = account.Session.SteamID + ":";
                    var fetchedRequestKeys = new HashSet<string>(result.Requests
                        .Select(request => BuildLoginRequestKey(request.SteamId, request.ClientId)));
                    foreach (string resolvedKey in recentlyResolvedLoginRequests.Keys
                        .Where(key => key.StartsWith(accountRequestPrefix, StringComparison.Ordinal) && !fetchedRequestKeys.Contains(key))
                        .ToList())
                    {
                        recentlyResolvedLoginRequests.Remove(resolvedKey);
                    }
                    foreach (PendingLoginRequest request in result.Requests)
                    {
                        string requestKey = BuildLoginRequestKey(request.SteamId, request.ClientId);
                        if (IsRecentlyResolved(recentlyResolvedLoginRequests, requestKey))
                            continue;
                        pendingLoginRequests[requestKey] = request;

                        if (manifest.LoginActionMode == LoginActionModes.Manual)
                        {
                            if (notifiedLoginRequests.Add(requestKey))
                            {
                                string device = String.IsNullOrWhiteSpace(request.DeviceName) ? request.Platform : request.DeviceName;
                                NotifyLoginAction("Steam login approval needed", account.AccountName + ": " + device, ToolTipIcon.Info);
                            }
                            continue;
                        }

                        bool allowWhitelistedIp = false;
                        if (manifest.LoginActionMode == LoginActionModes.Deny && manifest.LoginActionAutoAllowIpEnabled)
                        {
                            bool allowAdditionalIp = !String.IsNullOrWhiteSpace(manifest.LoginActionAutoAllowIp) &&
                                AreSameIpv4Address(request.IPAddress, manifest.LoginActionAutoAllowIp);
                            bool allowCurrentDeviceIp = false;
                            if (manifest.LoginActionAutoAllowCurrentDeviceIp)
                            {
                                if (!currentDeviceIpChecked)
                                {
                                    currentDeviceIp = await GetCurrentPublicIpv4Async();
                                    currentDeviceIpChecked = true;
                                }
                                allowCurrentDeviceIp = AreSameIpv4Address(request.IPAddress, currentDeviceIp);
                            }
                            allowWhitelistedIp = allowAdditionalIp || allowCurrentDeviceIp;
                        }
                        LoginApprovalDecision decision = manifest.LoginActionMode == LoginActionModes.ApprovePersistent || allowWhitelistedIp
                            ? LoginApprovalDecision.ApprovePersistent
                            : LoginApprovalDecision.Deny;
                        string actionKey = requestKey + "|" + manifest.LoginActionMode + "|" + decision;
                        if (completedAutomatedLoginActions.Contains(actionKey))
                            continue;

                        LoginApprovalActionResult actionResult = await RunSteamAccountOperationAsync(account,
                            () => loginApprovalService.RespondAsync(account, request, decision));
                        if (actionResult.ErrorKind == LoginApprovalErrorKind.RateLimited)
                        {
                            ApplyLoginRateLimit();
                            ScheduleLoginAccountScan(account, LoginMonitorMaximumFailureBackoff, true);
                            DiagnosticErrorLogger.Log("Login action monitor", new InvalidOperationException(actionResult.ErrorMessage), "Steam rate limited an automatic login action.");
                            return;
                        }
                        if (actionResult.Succeeded || actionResult.ErrorKind == LoginApprovalErrorKind.ExpiredOrDuplicate)
                        {
                            MarkRecentlyResolved(recentlyResolvedLoginRequests, requestKey);
                            pendingLoginRequests.Remove(requestKey);
                            completedAutomatedLoginActions.Add(actionKey);
                            RecordRecentLoginAttempt(request, actionResult.Succeeded
                                ? (allowWhitelistedIp ? "Approved automatically (whitelisted IP)" : (decision == LoginApprovalDecision.ApprovePersistent ? "Approved automatically" : "Denied automatically"))
                                : "Expired or already handled");
                            NotifyAutomatedLoginActionOnce(actionKey,
                                actionResult.Succeeded
                                    ? (allowWhitelistedIp ? "Automatically allowed whitelisted Steam login" : (decision == LoginApprovalDecision.ApprovePersistent ? "Automatically approved Steam login" : "Automatically denied Steam login"))
                                    : "Steam login request was already handled",
                                account.AccountName + ": " + (request.DeviceName ?? request.Platform),
                                actionResult.Succeeded ? ToolTipIcon.Info : ToolTipIcon.Warning);
                        }
                        else
                        {
                            NotifyAutomatedLoginActionOnce(actionKey + "|failed",
                                "Automatic Steam login action failed",
                                account.AccountName + ": " + actionResult.ErrorMessage,
                                ToolTipIcon.Warning);
                        }
                    }

                    ReconcilePendingLoginRequestsForAccount(account, result.Requests);
                    ScheduleLoginAccountScan(account, LoginMonitorSuccessInterval, true);
                }
            }
            finally
            {
                loginActionsSemaphore.Release();
                if (IsWebTabActive("login-actions"))
                    await PublishCachedLoginActionsAsync();
            }
        }

        private void NotifyAutomatedLoginActionOnce(string key, string title, string message, ToolTipIcon icon)
        {
            DateTime now = DateTime.UtcNow;
            foreach (string expiredKey in automatedLoginActionNotifications
                .Where(pair => now - pair.Value >= TimeSpan.FromMinutes(5))
                .Select(pair => pair.Key)
                .ToList())
            {
                automatedLoginActionNotifications.Remove(expiredKey);
            }
            if (automatedLoginActionNotifications.TryGetValue(key, out DateTime previous) && now - previous < TimeSpan.FromMinutes(5))
                return;

            automatedLoginActionNotifications[key] = now;
            NotifyLoginAction(title, message, icon);
        }

        private void NotifyLoginAction(string title, string message, ToolTipIcon icon)
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)(() => NotifyLoginAction(title, message, icon)));
                return;
            }

            trayNotificationClickAction = OpenLoginActionsFromTray;
            trayIcon.Visible = true;
            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = message;
            trayIcon.BalloonTipIcon = icon;
            trayIcon.ShowBalloonTip(10000);

            ShowDesktopLoginNotification(title, message, icon);
        }

        private void ShowDesktopLoginNotification(string title, string message, ToolTipIcon icon)
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)(() => ShowDesktopLoginNotification(title, message, icon)));
                return;
            }

            var popup = new LoginNotificationPopup(title, message, icon);
            popup.NotificationClicked += (sender, args) => OpenLoginActionsFromTray();
            popup.FormClosed += (sender, args) => activeLoginNotificationPopups.Remove(popup);
            activeLoginNotificationPopups.Add(popup);
            popup.ShowAtBottomRight((activeLoginNotificationPopups.Count - 1) * 126);
        }

        private void NotifyTradeConfirmation(SteamGuardAccount account, Confirmation confirmation)
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)(() => NotifyTradeConfirmation(account, confirmation)));
                return;
            }

            string title = confirmation.ConfType == Confirmation.EMobileConfirmationType.MarketListing
                ? "Market confirmation needed"
                : "Trade confirmation needed";
            string detail = String.IsNullOrWhiteSpace(confirmation.Headline) ? "New Steam confirmation" : confirmation.Headline;
            string message = account.AccountName + " · " + detail;
            trayNotificationClickAction = OpenTradeConfirmationsFromNotification;
            trayIcon.Visible = true;
            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = message;
            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.ShowBalloonTip(10000);

            var popup = new LoginNotificationPopup(title, message, ToolTipIcon.Info, "Click to open Trade Confirmations");
            popup.NotificationClicked += (sender, args) => OpenTradeConfirmationsFromNotification();
            popup.FormClosed += (sender, args) => activeLoginNotificationPopups.Remove(popup);
            activeLoginNotificationPopups.Add(popup);
            popup.ShowAtBottomRight((activeLoginNotificationPopups.Count - 1) * 126);
        }

        private void UpdateTradePendingCount(int count)
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)(() => UpdateTradePendingCount(count)));
                return;
            }

            if (webView?.CoreWebView2 != null)
                _ = webView.CoreWebView2.ExecuteScriptAsync("setTradePendingCount(" + Math.Max(0, count).ToString() + ");");
        }

        private void CloseLoginNotificationPopups()
        {
            foreach (LoginNotificationPopup popup in activeLoginNotificationPopups.ToList())
            {
                if (!popup.IsDisposed)
                    popup.Close();
            }
        }

        private bool PersistLoginSession(SteamGuardAccount account)
        {
            if (manifest == null)
                return false;

            StorageResult saveResult = manifest.SaveAccount(account, manifest.Encrypted, passKey);
            if (!saveResult.Succeeded)
                DiagnosticErrorLogger.Log("Authenticator storage", saveResult.Exception, "The updated Steam login session could not be saved.");
            return saveResult.Succeeded;
        }

        private SteamGuardAccount GetNextLoginAccountToMonitor()
        {
            SteamGuardAccount[] accountsToMonitor = (allAccounts ?? Array.Empty<SteamGuardAccount>())
                .Where(account => account?.Session != null)
                .ToArray();
            if (accountsToMonitor.Length == 0)
                return null;

            DateTime now = DateTime.UtcNow;
            for (int attempt = 0; attempt < accountsToMonitor.Length; attempt++)
            {
                loginMonitoringAccountIndex = ((loginMonitoringAccountIndex % accountsToMonitor.Length) + accountsToMonitor.Length) % accountsToMonitor.Length;
                SteamGuardAccount candidate = accountsToMonitor[loginMonitoringAccountIndex];
                loginMonitoringAccountIndex = (loginMonitoringAccountIndex + 1) % accountsToMonitor.Length;
                if (!loginMonitorSchedules.TryGetValue(candidate.Session.SteamID, out LoginMonitorSchedule schedule) || schedule.NextScanUtc <= now)
                    return candidate;
            }

            return null;
        }

        private void ScheduleLoginAccountScan(SteamGuardAccount account, TimeSpan interval, bool resetFailureCount)
        {
            if (account?.Session == null)
                return;

            if (!loginMonitorSchedules.TryGetValue(account.Session.SteamID, out LoginMonitorSchedule schedule))
            {
                schedule = new LoginMonitorSchedule();
                loginMonitorSchedules[account.Session.SteamID] = schedule;
            }
            if (resetFailureCount)
                schedule.ConsecutiveFailures = 0;

            // Stable per-account jitter prevents accounts from re-forming a request burst.
            int jitterMilliseconds = (int)(account.Session.SteamID % 3001) - 1500;
            schedule.NextScanUtc = DateTime.UtcNow.Add(interval).AddMilliseconds(jitterMilliseconds);
            Debug.WriteLine("Login monitor scheduled next scan; account=" + account.Session.SteamID + "; next=" + schedule.NextScanUtc.ToString("O") + "; requests=" + Interlocked.Read(ref loginMonitorRequestCount));
        }

        private void ScheduleLoginAccountFailure(SteamGuardAccount account)
        {
            if (account?.Session == null)
                return;

            if (!loginMonitorSchedules.TryGetValue(account.Session.SteamID, out LoginMonitorSchedule schedule))
            {
                schedule = new LoginMonitorSchedule();
                loginMonitorSchedules[account.Session.SteamID] = schedule;
            }
            schedule.ConsecutiveFailures = Math.Min(schedule.ConsecutiveFailures + 1, 10);
            double multiplier = Math.Pow(2, schedule.ConsecutiveFailures - 1);
            TimeSpan backoff = TimeSpan.FromSeconds(Math.Min(
                LoginMonitorInitialFailureBackoff.TotalSeconds * multiplier,
                LoginMonitorMaximumFailureBackoff.TotalSeconds));
            ScheduleLoginAccountScan(account, backoff, false);
        }

        private sealed class LoginMonitorSchedule
        {
            public DateTime NextScanUtc { get; set; }
            public int ConsecutiveFailures { get; set; }
        }

        private static string BuildLoginRequestKey(ulong steamId, ulong clientId)
        {
            return steamId.ToString() + ":" + clientId.ToString();
        }

        private bool IsWebTabActive(string tabName)
        {
            return String.Equals(activeWebTab, tabName, StringComparison.Ordinal);
        }

        private static bool IsRecentlyResolved(Dictionary<string, DateTime> resolvedRequests, string key)
        {
            DateTime now = DateTime.UtcNow;
            foreach (string expiredKey in resolvedRequests
                .Where(entry => now - entry.Value >= RecentlyResolvedRequestRetention)
                .Select(entry => entry.Key)
                .ToList())
            {
                resolvedRequests.Remove(expiredKey);
            }
            return resolvedRequests.ContainsKey(key);
        }

        private static void MarkRecentlyResolved(Dictionary<string, DateTime> resolvedRequests, string key)
        {
            resolvedRequests[key] = DateTime.UtcNow;
        }

        private void RecordRecentLoginAttempt(PendingLoginRequest request, string outcome)
        {
            if (request == null)
                return;

            string key = BuildLoginRequestKey(request.SteamId, request.ClientId);
            if (!recordedRecentLoginAttempts.Add(key))
                return;

            recentLoginAttempts.Insert(0, new RecentLoginAttempt
            {
                Request = request,
                Outcome = outcome,
                OccurredAtUtc = DateTime.UtcNow
            });
            if (recentLoginAttempts.Count > 3)
            {
                foreach (RecentLoginAttempt removedAttempt in recentLoginAttempts.Skip(3))
                    recordedRecentLoginAttempts.Remove(BuildLoginRequestKey(removedAttempt.Request.SteamId, removedAttempt.Request.ClientId));
                recentLoginAttempts.RemoveRange(3, recentLoginAttempts.Count - 3);
            }
        }

        private void ReconcileTradeConfirmationNotifications(SteamGuardAccount account, IEnumerable<Confirmation> confirmations)
        {
            if (account?.Session == null)
                return;

            var fetchedConfirmationKeys = new HashSet<string>((confirmations ?? Enumerable.Empty<Confirmation>())
                .Select(confirmation => BuildTradeConfirmationKey(account, confirmation)));
            string accountConfirmationPrefix = account.Session.SteamID.ToString() + ":";
            notifiedTradeConfirmations.RemoveWhere(key => key.StartsWith(accountConfirmationPrefix, StringComparison.Ordinal) &&
                !fetchedConfirmationKeys.Contains(key));
        }

        private void CacheTradeConfirmations(SteamGuardAccount account, IEnumerable<Confirmation> confirmations)
        {
            if (account?.Session == null)
                return;

            string accountPrefix = account.Session.SteamID + ":";
            var confirmationList = (confirmations ?? Enumerable.Empty<Confirmation>()).ToList();
            var fetchedKeys = new HashSet<string>(confirmationList.Select(confirmation => BuildTradeConfirmationKey(account, confirmation)));
            foreach (string resolvedKey in recentlyResolvedTradeConfirmations.Keys
                .Where(key => key.StartsWith(accountPrefix, StringComparison.Ordinal) && !fetchedKeys.Contains(key))
                .ToList())
            {
                recentlyResolvedTradeConfirmations.Remove(resolvedKey);
            }
            foreach (string key in loadedTradeConfirmations.Keys
                .Where(key => key.StartsWith(accountPrefix, StringComparison.Ordinal))
                .ToList())
            {
                loadedTradeConfirmations.Remove(key);
            }

            foreach (Confirmation confirmation in confirmationList)
            {
                string confirmationKey = BuildTradeConfirmationKey(account, confirmation);
                if (IsRecentlyResolved(recentlyResolvedTradeConfirmations, confirmationKey))
                    continue;

                loadedTradeConfirmations[confirmationKey] =
                    new LoadedTradeConfirmation { Account = account, Confirmation = confirmation };
            }
            loadedTradeConfirmationAccounts.Add(account.Session.SteamID);
        }

        private void InvalidateTradeConfirmationCache(SteamGuardAccount account)
        {
            if (account?.Session == null)
                return;

            ulong steamId = account.Session.SteamID;
            pendingTradeConfirmationCounts.Remove(steamId);
            loadedTradeConfirmationAccounts.Remove(steamId);
            foreach (string confirmationKey in loadedTradeConfirmations
                .Where(entry => entry.Value.Account?.Session?.SteamID == steamId)
                .Select(entry => entry.Key)
                .ToArray())
            {
                loadedTradeConfirmations.Remove(confirmationKey);
            }
        }

        private bool IsTradeCacheCompleteForSelection()
        {
            if (allAccounts == null || allAccounts.Length == 0)
                return true;

            IEnumerable<SteamGuardAccount> accounts = tradeAccountSelection == "all"
                ? allAccounts
                : allAccounts.Where(account => account.AccountName == tradeAccountSelection);
            return accounts
                .Where(account => account?.Session != null)
                .All(account => loadedTradeConfirmationAccounts.Contains(account.Session.SteamID));
        }

        private async Task PublishCachedTradesAsync(string errorMessage = null, string selection = null)
        {
            if (webView == null || webView.CoreWebView2 == null)
                return;

            string selectionToPublish = String.IsNullOrWhiteSpace(selection) ? tradeAccountSelection : selection;
            IEnumerable<LoadedTradeConfirmation> entries = loadedTradeConfirmations.Values;
            if (selectionToPublish != "all")
            {
                entries = entries.Where(entry => String.Equals(entry.Account.AccountName, selectionToPublish, StringComparison.Ordinal));
            }

            var settings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            string jsonStr = JsonConvert.SerializeObject(entries.Select(entry => new
            {
                Id = BuildTradeConfirmationKey(entry.Account, entry.Confirmation),
                AccountName = entry.Account.AccountName,
                Headline = entry.Confirmation.Headline,
                Creator = entry.Confirmation.Creator.ToString(),
                Icon = entry.Confirmation.Icon,
                Summary = entry.Confirmation.Summary,
                Type = entry.Confirmation.ConfType.ToString()
            }), settings);
            string jsError = String.IsNullOrWhiteSpace(errorMessage) ? "null" : JsonConvert.SerializeObject(errorMessage);
            long revision = Interlocked.Increment(ref tradeViewRevision);
            string jsSelection = JsonConvert.SerializeObject(selectionToPublish);
            await webView.CoreWebView2.ExecuteScriptAsync($"loadConfirmations({jsonStr}, {jsError}, {revision}, {jsSelection})");
        }

        private async Task LoadCachedTradesAsync(string selectedAccountName)
        {
            if (!String.IsNullOrWhiteSpace(selectedAccountName))
                tradeAccountSelection = selectedAccountName;

            await PublishCachedTradesAsync();
            if (!IsTradeCacheCompleteForSelection())
                _ = LoadTradesAsync();
        }

        private void PruneLoginRequestBookkeeping(string requestKey)
        {
            notifiedLoginRequests.Remove(requestKey);
            recordedRecentLoginAttempts.Remove(requestKey);
            completedAutomatedLoginActions.RemoveWhere(key => key.StartsWith(requestKey + "|", StringComparison.Ordinal));
        }

        private void ReconcilePendingLoginRequestsForAccount(SteamGuardAccount account, IEnumerable<PendingLoginRequest> requests)
        {
            if (account?.Session == null)
                return;

            var fetchedRequestKeys = new HashSet<string>(requests.Select(request => BuildLoginRequestKey(request.SteamId, request.ClientId)));
            string accountRequestPrefix = account.Session.SteamID.ToString() + ":";
            foreach (string staleKey in pendingLoginRequests.Keys
                .Where(key => key.StartsWith(accountRequestPrefix, StringComparison.Ordinal) && !fetchedRequestKeys.Contains(key))
                .ToList())
            {
                RecordRecentLoginAttempt(pendingLoginRequests[staleKey], "Expired or handled elsewhere");
                pendingLoginRequests.Remove(staleKey);
                PruneLoginRequestBookkeeping(staleKey);
            }

            foreach (string staleRequestKey in completedAutomatedLoginActions
                .Where(actionKey => actionKey.StartsWith(accountRequestPrefix, StringComparison.Ordinal) &&
                    !fetchedRequestKeys.Contains(actionKey.Split('|')[0]))
                .Select(actionKey => actionKey.Split('|')[0])
                .Distinct()
                .ToList())
            {
                PruneLoginRequestBookkeeping(staleRequestKey);
            }
        }

        private static bool IsValidIpv4Address(string value)
        {
            return IPAddress.TryParse(value, out IPAddress address) && address.AddressFamily == AddressFamily.InterNetwork;
        }

        private static bool AreSameIpv4Address(string first, string second)
        {
            return IsValidIpv4Address(first) && IsValidIpv4Address(second) &&
                IPAddress.Parse(first).Equals(IPAddress.Parse(second));
        }

        private static async Task<string> GetCurrentPublicIpv4Async()
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    string candidate = (await client.GetStringAsync("https://api.ipify.org")).Trim();
                    return IsValidIpv4Address(candidate) ? candidate : null;
                }
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
        }

        private void OpenLoginActionsFromTray()
        {
            trayRestore_Click(this, EventArgs.Empty);
            if (webView != null && webView.CoreWebView2 != null)
                _ = webView.CoreWebView2.ExecuteScriptAsync("switchTab('login-actions');");
        }

        private void OpenTradeConfirmationsFromNotification()
        {
            trayRestore_Click(this, EventArgs.Empty);
            if (webView != null && webView.CoreWebView2 != null)
                _ = webView.CoreWebView2.ExecuteScriptAsync("switchTab('trades');");
        }

        private async Task FetchLoginActionsForManualRefreshAsync()
        {
            if (manifest == null || loginApprovalService == null || allAccounts == null)
                return;
            if (TryGetLoginRateLimitMessage(out _))
                return;
            if (!await loginActionsSemaphore.WaitAsync(0))
                return;

            try
            {
                foreach (SteamGuardAccount account in allAccounts)
                {
                    if (account?.Session == null)
                        continue;

                    LoginApprovalFetchResult result = await RunSteamAccountOperationAsync(account,
                        () => loginApprovalService.FetchPendingRequestsAsync(account));
                    if (result.ErrorKind == LoginApprovalErrorKind.SessionExpired)
                    {
                        unavailableLoginAccounts[account.AccountName] = result.ErrorMessage;
                        continue;
                    }

                    if (result.ErrorKind == LoginApprovalErrorKind.RateLimited)
                    {
                        ApplyLoginRateLimit();
                        DiagnosticErrorLogger.Log("Login action refresh", new InvalidOperationException(result.ErrorMessage), "Steam rate limited the manual login-request refresh.");
                        return;
                    }

                    if (result.ErrorKind != LoginApprovalErrorKind.None)
                        continue;

                    notifiedUnavailableLoginAccounts.Remove(account.AccountName);
                    unavailableLoginAccounts.Remove(account.AccountName);
                    string accountRequestPrefix = account.Session.SteamID + ":";
                    var fetchedRequestKeys = new HashSet<string>(result.Requests
                        .Select(request => BuildLoginRequestKey(request.SteamId, request.ClientId)));
                    foreach (string resolvedKey in recentlyResolvedLoginRequests.Keys
                        .Where(key => key.StartsWith(accountRequestPrefix, StringComparison.Ordinal) && !fetchedRequestKeys.Contains(key))
                        .ToList())
                    {
                        recentlyResolvedLoginRequests.Remove(resolvedKey);
                    }
                    foreach (PendingLoginRequest request in result.Requests)
                    {
                        string requestKey = BuildLoginRequestKey(request.SteamId, request.ClientId);
                        if (!IsRecentlyResolved(recentlyResolvedLoginRequests, requestKey))
                            pendingLoginRequests[requestKey] = request;
                    }
                    ReconcilePendingLoginRequestsForAccount(account, result.Requests);
                }
            }
            finally
            {
                loginActionsSemaphore.Release();
            }
        }

        private async Task PublishCachedLoginActionsAsync()
        {
            if (webView == null || webView.CoreWebView2 == null)
                return;

            var jsonSettings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            string json = JsonConvert.SerializeObject(new
            {
                revision = Interlocked.Increment(ref loginViewRevision),
                requests = pendingLoginRequests.Values.Select(request => new
                {
                    accountName = request.AccountName,
                    steamId = request.SteamId.ToString(),
                    clientId = request.ClientId.ToString(),
                    version = request.Version,
                    ipAddress = request.IPAddress,
                    geolocation = request.Geolocation,
                    city = request.City,
                    state = request.State,
                    country = request.Country,
                    platform = request.Platform,
                    deviceName = request.DeviceName,
                    requestedPersistence = request.RequestedPersistence,
                    securityHistory = request.SecurityHistory,
                    locationMismatch = request.LocationMismatch,
                    highUsageLogin = request.HighUsageLogin
                }),
                unavailableAccounts = unavailableLoginAccounts.Select(account => new { accountName = account.Key, reason = account.Value }),
                actionMode = manifest?.LoginActionMode ?? LoginActionModes.Manual,
                autoAllowIpEnabled = manifest?.LoginActionAutoAllowIpEnabled ?? false,
                autoAllowCurrentDeviceIp = manifest?.LoginActionAutoAllowCurrentDeviceIp ?? false,
                autoAllowAdditionalIp = manifest?.LoginActionAutoAllowIp ?? String.Empty,
                recentAttempts = recentLoginAttempts.Select(attempt => new
                {
                    accountName = attempt.Request.AccountName,
                    clientId = attempt.Request.ClientId.ToString(),
                    ipAddress = attempt.Request.IPAddress,
                    geolocation = attempt.Request.Geolocation,
                    city = attempt.Request.City,
                    state = attempt.Request.State,
                    country = attempt.Request.Country,
                    platform = attempt.Request.Platform,
                    deviceName = attempt.Request.DeviceName,
                    requestedPersistence = attempt.Request.RequestedPersistence,
                    securityHistory = attempt.Request.SecurityHistory,
                    locationMismatch = attempt.Request.LocationMismatch,
                    highUsageLogin = attempt.Request.HighUsageLogin,
                    outcome = attempt.Outcome,
                    occurredAtUtc = attempt.OccurredAtUtc.ToString("O")
                })
            }, jsonSettings);
            await webView.CoreWebView2.ExecuteScriptAsync("loadLoginActions(" + json + ");");
        }

        private async Task RefreshLoginActionsAsync()
        {
            try
            {
                if (manifest?.LoginActionMonitoringEnabled == true)
                    await MonitorLoginActionsSafelyAsync();
                else
                    await FetchLoginActionsForManualRefreshAsync();
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Login action refresh", ex, "The manual login-request refresh did not complete.");
            }
            finally
            {
                await PublishCachedLoginActionsAsync();
            }
        }

        private async Task RespondToLoginActionAsync(string accountName, ulong clientId, string action)
        {
            if (manifest.LoginActionMode != LoginActionModes.Manual)
            {
                AstroMessageBox.Show("Manual actions are disabled while an automatic login action is enabled.", "Login Actions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await PublishCachedLoginActionsAsync();
                return;
            }

            SteamGuardAccount account = allAccounts?.FirstOrDefault(item => item.AccountName == accountName);
            if (account?.Session == null || !pendingLoginRequests.TryGetValue(BuildLoginRequestKey(account.Session.SteamID, clientId), out PendingLoginRequest request))
            {
                AstroMessageBox.Show("This login request is no longer available. Refresh the list and try again.", "Login Actions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await PublishCachedLoginActionsAsync();
                return;
            }

            LoginApprovalDecision decision = action == "approve" ? LoginApprovalDecision.ApprovePersistent : LoginApprovalDecision.Deny;
            string location = String.Join(", ", new[] { request.City, request.State, request.Country }.Where(value => !String.IsNullOrWhiteSpace(value)));
            if (String.IsNullOrWhiteSpace(location)) location = "Unknown location";
            string device = String.IsNullOrWhiteSpace(request.DeviceName) ? request.Platform : request.DeviceName;
            string prompt = (decision == LoginApprovalDecision.ApprovePersistent ? "Approve" : "Deny") + " this Steam login request?\n\n" +
                "Account: " + request.AccountName + "\n" +
                "Device: " + device + "\n" +
                "Location: " + location;
            if (AstroMessageBox.Show(prompt, "Confirm Login Action", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                await webView.CoreWebView2.ExecuteScriptAsync("hideSpinner();");
                return;
            }

            await loginActionsSemaphore.WaitAsync();
            try
            {
                LoginApprovalActionResult result = await RunSteamAccountOperationAsync(account,
                    () => loginApprovalService.RespondAsync(account, request, decision));
                if (!result.Succeeded && result.ErrorKind != LoginApprovalErrorKind.ExpiredOrDuplicate)
                {
                    AstroMessageBox.Show(result.ErrorMessage, "Login Actions", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    string requestKey = BuildLoginRequestKey(request.SteamId, request.ClientId);
                    pendingLoginRequests.Remove(requestKey);
                    notifiedLoginRequests.Remove(requestKey);
                    MarkRecentlyResolved(recentlyResolvedLoginRequests, requestKey);
                    if (result.Succeeded)
                    {
                        RecordRecentLoginAttempt(request, decision == LoginApprovalDecision.ApprovePersistent ? "Approved manually" : "Denied manually");
                        AstroMessageBox.Show(decision == LoginApprovalDecision.ApprovePersistent ? "Steam login approved." : "Steam login denied.", "Login Actions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        RecordRecentLoginAttempt(request, "Expired or already handled");
                        AstroMessageBox.Show("This login request is no longer pending.", "Login Actions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            finally
            {
                loginActionsSemaphore.Release();
            }

            await PublishCachedLoginActionsAsync();
            _ = RefreshLoginActionsAsync();
        }

        // Other methods

        private void CopyLoginToken()
        {
            string text = txtLoginToken.Text;
            if (String.IsNullOrEmpty(text))
                return;
            Clipboard.SetText(text);
        }

        /// <summary>
        /// Display a login form to the user to refresh their OAuth Token
        /// </summary>
        /// <param name="account">The account to refresh</param>
        private void PromptRefreshLogin(SteamGuardAccount account)
        {
            if (account == null)
            {
                AstroMessageBox.Show("Please select an account first.", "Login Again", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (LoginForm loginForm = new LoginForm(LoginForm.LoginType.Refresh, account))
            {
                loginForm.ShowDialog(this);
            }
        }

        /// <summary>
        /// Load UI with the current account info, this is run every second
        /// </summary>
        private void loadAccountInfo()
        {
            if (currentAccount != null && steamTime != 0)
            {
                string token = currentAccount.GenerateSteamGuardCodeForTime(steamTime);
                txtLoginToken.Text = token;
                groupAccount.Text = "Account: " + currentAccount.AccountName;

                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.ExecuteScriptAsync($"updateToken('{token}')");
                    webView.CoreWebView2.ExecuteScriptAsync($"updateCurrentAccount('{currentAccount.AccountName}')");
                }
            }
        }

        /// <summary>
        /// Decrypts files and populates list UI with accounts
        /// </summary>
        private void loadAccountsList()
        {
            currentAccount = null;

            listAccounts.Items.Clear();
            listAccounts.SelectedIndex = -1;

            trayAccountList.Items.Clear();
            trayAccountList.SelectedIndex = -1;

            allAccounts = manifest.GetAllAccounts(passKey);
            var activeSteamIds = new HashSet<ulong>(allAccounts.Where(account => account.Session != null).Select(account => account.Session.SteamID));
            var activeAccountNames = new HashSet<string>(allAccounts.Select(account => account.AccountName), StringComparer.Ordinal);
            foreach (ulong steamId in pendingTradeConfirmationCounts.Keys.Where(steamId => !activeSteamIds.Contains(steamId)).ToArray())
                pendingTradeConfirmationCounts.Remove(steamId);
            loadedTradeConfirmationAccounts.RemoveWhere(steamId => !activeSteamIds.Contains(steamId));
            foreach (string accountName in unavailableLoginAccounts.Keys.Where(accountName => !activeAccountNames.Contains(accountName)).ToArray())
                unavailableLoginAccounts.Remove(accountName);
            foreach (string confirmationKey in loadedTradeConfirmations
                .Where(entry => entry.Value.Account?.Session == null || !activeSteamIds.Contains(entry.Value.Account.Session.SteamID))
                .Select(entry => entry.Key)
                .ToArray())
            {
                loadedTradeConfirmations.Remove(confirmationKey);
            }
            UpdateTradePendingCount(pendingTradeConfirmationCounts.Values.Sum());

            if (allAccounts.Length > 0)
            {
                for (int i = 0; i < allAccounts.Length; i++)
                {
                    SteamGuardAccount account = allAccounts[i];
                    listAccounts.Items.Add(account.AccountName);
                    trayAccountList.Items.Add(account.AccountName);
                }

                listAccounts.SelectedIndex = 0;
                trayAccountList.SelectedIndex = 0;

                listAccounts.Sorted = true;
                trayAccountList.Sorted = true;
            }
            bool hasAccounts = allAccounts.Length > 0;
            menuDeactivateAuthenticator.Enabled = btnTradeConfirmations.Enabled = btnLoginViaQr.Enabled = btnCopy.Enabled = hasAccounts;

            if (hasAccounts)
            {
                AstroTheme.StyleSurfaceButton(btnLoginViaQr);
                AstroTheme.StyleSurfaceButton(btnCopy);
            }
            else
            {
                AstroTheme.StyleDisabledGlassButton(btnLoginViaQr);
                AstroTheme.StyleDisabledGlassButton(btnCopy);
            }

            if (webView != null && webView.CoreWebView2 != null)
            {
                var accounts = allAccounts.Select(a => new
                {
                    name = a.AccountName,
                    steamId = a.Session == null ? null : a.Session.SteamID.ToString()
                }).ToArray();
                string jsonAccounts = JsonConvert.SerializeObject(accounts);
                webView.CoreWebView2.ExecuteScriptAsync($"updateAccountList({jsonAccounts})");
                webView.CoreWebView2.ExecuteScriptAsync($"updateEncryptionState({manifest.Encrypted.ToString().ToLower()}, {hasAccounts.ToString().ToLower()})");
            }
        }

        private void listAccounts_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control)
            {
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    int to = listAccounts.SelectedIndex - (e.KeyCode == Keys.Up ? 1 : -1);
                    manifest.MoveEntry(listAccounts.SelectedIndex, to);
                    loadAccountsList();
                }
                return;
            }

            if (!IsKeyAChar(e.KeyCode) && !IsKeyADigit(e.KeyCode))
            {
                return;
            }

            txtAccSearch.Focus();
            txtAccSearch.Text = e.KeyCode.ToString();
            txtAccSearch.SelectionStart = 1;
        }

        private static bool IsKeyAChar(Keys key)
        {
            return key >= Keys.A && key <= Keys.Z;
        }

        private static bool IsKeyADigit(Keys key)
        {
            return (key >= Keys.D0 && key <= Keys.D9) || (key >= Keys.NumPad0 && key <= Keys.NumPad9);
        }

        private bool IsFilter(string f)
        {
            if (txtAccSearch.Text.StartsWith("~"))
            {
                try
                {
                    return Regex.IsMatch(f, txtAccSearch.Text);
                }
                catch (Exception)
                {
                    return true;
                }

            }
            else
            {
                return f.Contains(txtAccSearch.Text.ToLower());
            }
        }

        private string[] getAllNames()
        {
            string[] itemArray = new string[allAccounts.Length];
            for (int i = 0; i < itemArray.Length; i++)
            {
                itemArray[i] = allAccounts[i].AccountName;
            }
            return itemArray;
        }

        private void loadSettings()
        {
            ConfigureTradeConfirmationMonitor();
        }

        private void ConfigureTradeConfirmationMonitor()
        {
            if (manifest == null)
                return;

            int intervalSeconds = GetTradeConfirmationMonitorIntervalSeconds();
            timerTradesPopup.Interval = intervalSeconds * 1000;
            timerTradesPopup.Enabled = true;
        }

        private void StartBackgroundServicesAfterUiReady()
        {
            backgroundServicesEligible = true;
            if (manifest == null || backgroundServicesStarted)
                return;

            backgroundServicesStarted = true;
            timerSteamGuard.Enabled = true;
            timerSteamGuard_Tick(this, EventArgs.Empty);
            loadSettings();
            ConfigureLoginActionsMonitor();
            checkForUpdates();
        }

        // Logic for version checking
        // Logic for version checking
        private Version newVersion = null;
        private Version currentVersion = null;
        private static readonly HttpClient updateClient = new HttpClient();
        private string updateUrl = null;
        private bool startupUpdateCheck = true;
        private bool isCheckingForUpdates = false;

        private async void checkForUpdates()
        {
            if (isCheckingForUpdates) return;
            
            if (startupUpdateCheck && !Manifest.GetManifest().CheckForUpdates)
            {
                startupUpdateCheck = false;
                return;
            }

            isCheckingForUpdates = true;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/AstroZer01/Astro-Steam-Desktop-Authenticator/releases/latest");
                request.Headers.Add("User-Agent", "Astro Steam Desktop Assistant");
                request.Headers.Add("Accept", "application/json");
                
                var response = await updateClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                
                string result = await response.Content.ReadAsStringAsync();
                dynamic resultObject = JsonConvert.DeserializeObject(result);
                newVersion = new Version(resultObject.tag_name.Value);
                currentVersion = new Version(Application.ProductVersion);
                updateUrl = resultObject.assets.First.browser_download_url.Value;
                compareVersions();
            }
            catch (Exception)
            {
                if (!startupUpdateCheck)
                {
                    AstroMessageBox.Show("Failed to check for updates.");
                }
            }
            finally
            {
                isCheckingForUpdates = false;
                startupUpdateCheck = false; // Set when it's done checking on startup
            }
        }

        private void compareVersions()
        {
            if (newVersion > currentVersion)
            {
                labelUpdate.Text = "Download new version"; // Show the user a new version is available if they press no
                
                string checkboxText = startupUpdateCheck ? "Don't check for updates on launch" : null;
                bool isChecked = false;
                
                DialogResult updateDialog;
                if (checkboxText != null)
                {
                    updateDialog = AstroMessageBox.Show(String.Format("A new version is available! Would you like to download it now?\nYou will update from version {0} to {1}", Application.ProductVersion, newVersion.ToString()), "New Version", MessageBoxButtons.YesNo, MessageBoxIcon.None, checkboxText, out isChecked);
                }
                else
                {
                    updateDialog = AstroMessageBox.Show(String.Format("A new version is available! Would you like to download it now?\nYou will update from version {0} to {1}", Application.ProductVersion, newVersion.ToString()), "New Version", MessageBoxButtons.YesNo);
                }

                if (startupUpdateCheck && isChecked)
                {
                    Manifest.GetManifest().CheckForUpdates = false;
                    Manifest.GetManifest().Save();
                }

                if (updateDialog == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(updateUrl) { UseShellExecute = true });
                }
            }
            else
            {
                if (!startupUpdateCheck)
                {
                    AstroMessageBox.Show(String.Format("You are using the latest version: {0}", Application.ProductVersion));
                }
            }

            newVersion = null; // Check the api again next time they check for updates
        }


        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.C && e.Modifiers == Keys.Control)
            {
                CopyLoginToken();
            }
        }

        private void panelButtons_SizeChanged(object sender, EventArgs e)
        {
            int totButtons = panelButtons.Controls.OfType<Button>().Count();

            Point curPos = new Point(0, 0);
            foreach (Button but in panelButtons.Controls.OfType<Button>())
            {
                but.Width = panelButtons.Width / totButtons;
                but.Location = curPos;
                curPos = new Point(curPos.X + but.Width, 0);
            }
        }

        // --- Astro Modern UI Restructuring (WebView2) ---
        private WebView2 webView;

        private async void SetupModernUI()
        {
            this.Size = new Size(450, 750);
            this.MinimumSize = new Size(450, 750);
            this.MaximumSize = new Size(450, 750);
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.BackColor = Color.FromArgb(11, 19, 38); // Dark background
            this.Text = "Astro SDA";

            // Create loading screen
            Panel loadingPanel = new Panel();
            loadingPanel.Dock = DockStyle.Fill;
            loadingPanel.BackColor = Color.FromArgb(11, 19, 38);
            
            Label lblLoading = new Label();
            lblLoading.Text = "Loading Astro UI...";
            lblLoading.ForeColor = Color.FromArgb(186, 201, 204);
            lblLoading.AutoSize = false;
            lblLoading.Size = new Size(400, 30);
            lblLoading.TextAlign = ContentAlignment.MiddleCenter;
            lblLoading.Font = new Font("Segoe UI", 10F, FontStyle.Regular);
            loadingPanel.Controls.Add(lblLoading);

            int spinnerAngle = 0;
            loadingPanel.Paint += (s, e) => {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int size = 44;
                int x = (loadingPanel.Width - size) / 2;
                int y = (loadingPanel.Height - size) / 2 - 20;
                
                using (Pen bgPen = new Pen(Color.FromArgb(40, 255, 255, 255), 4))
                {
                    e.Graphics.DrawEllipse(bgPen, x, y, size, size);
                }
                using (Pen fgPen = new Pen(Color.FromArgb(0, 229, 255), 4)) // Primary color
                {
                    fgPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    fgPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    e.Graphics.DrawArc(fgPen, x, y, size, size, spinnerAngle, 100);
                }
                spinnerAngle = (spinnerAngle + 12) % 360;
            };

            System.Windows.Forms.Timer loadingTimer = new System.Windows.Forms.Timer();
            loadingTimer.Interval = 30;
            loadingTimer.Tick += (s, e) => {
                if (lblStatus.Text != "") lblLoading.Text = lblStatus.Text;
                else lblLoading.Text = "Loading Astro UI...";
                
                lblLoading.Location = new Point((loadingPanel.Width - lblLoading.Width) / 2, loadingPanel.Height / 2 + 15);
                loadingPanel.Invalidate();
            };
            loadingTimer.Start();
            
            this.Controls.Add(loadingPanel);
            loadingPanel.BringToFront();

            // Initialize WebView2 but keep it hidden until loaded
            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            webView.Visible = false;
            this.Controls.Add(webView);
            webView.BringToFront();

            // Hide old UI immediately during loading
            foreach (Control c in this.Controls)
            {
                if (c != webView && c != loadingPanel)
                {
                    c.Visible = false;
                }
            }

            // Wait for WebView2 runtime to be initialized
            try
            {
                await webView.EnsureCoreWebView2Async(await WebViewEnvironmentProvider.GetAsync());
            }
            catch (Exception ex)
            {
                loadingTimer.Stop();
                loadingTimer.Dispose();
                lblLoading.Text = "Astro UI could not be initialized. Restore the complete release folder and try again.";
                DiagnosticErrorLogger.Log("Astro UI", ex, "The dashboard could not be initialized.");
                StartBackgroundServicesAfterUiReady();
                return;
            }

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            // Wire up message receiving from JS
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            // When navigation is done, we swap out the old UI for the new one
            webView.NavigationCompleted += (sender, args) =>
            {
                if (!args.IsSuccess)
                {
                    loadingTimer.Stop();
                    loadingTimer.Dispose();
                    lblLoading.Text = "Astro UI could not be loaded. Restore the complete release folder and try again.";
                    DiagnosticErrorLogger.Log("Astro UI", new InvalidOperationException("WebView2 navigation failed: " + args.WebErrorStatus), "The dashboard could not be loaded.");
                    StartBackgroundServicesAfterUiReady();
                    return;
                }

                loadingTimer.Stop();
                loadingTimer.Dispose();
                loadingPanel.Visible = false;
                webView.Visible = true;

                // Push initial data to JS now that it's ready
                loadAccountsList();
                loadAccountInfo();
                
                // Set app version
                webView.ExecuteScriptAsync($"setAppVersion('{Application.ProductVersion}');");
                
                // Set autostart checkbox
                bool isAutoStart = WindowsStartup.IsEnabled();
                webView.ExecuteScriptAsync($"setAutoStart({isAutoStart.ToString().ToLower()});");

                StartBackgroundServicesAfterUiReady();
            };

            // Load local html file
            string htmlPath = System.IO.Path.Combine(ApplicationPaths.UiDirectory, "index.html");
            webView.Source = new Uri(htmlPath);
        }

        private void SendSettingsToWebView()
        {
            if (webView == null || webView.CoreWebView2 == null || manifest == null)
                return;

            var settings = new JObject();
            settings["tradeConfirmationCustomIntervalEnabled"] = manifest.TradeConfirmationCustomIntervalEnabled;
            settings["tradeConfirmationCheckInterval"] = manifest.TradeConfirmationCheckInterval;
            settings["autoConfirmMarket"] = manifest.AutoConfirmMarketTransactions;
            settings["autoConfirmTrades"] = manifest.AutoConfirmTrades;
            settings["minimizeToTray"] = manifest.MinimizeToTray;
            settings["diagnosticErrorLoggingEnabled"] = manifest.DiagnosticErrorLoggingEnabled;
            settings["loginActionMonitoringEnabled"] = manifest.LoginActionMonitoringEnabled;
            settings["loginActionMode"] = manifest.LoginActionMode;
            settings["loginActionAutoAllowIpEnabled"] = manifest.LoginActionAutoAllowIpEnabled;
            settings["loginActionAutoAllowCurrentDeviceIp"] = manifest.LoginActionAutoAllowCurrentDeviceIp;
            settings["loginActionAutoAllowIp"] = manifest.LoginActionAutoAllowIp;

            webView.CoreWebView2.ExecuteScriptAsync($"loadSettings({settings.ToString(Newtonsoft.Json.Formatting.None)})");
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(message)) return;

            JObject payload = JObject.Parse(message);
            string action = (string)payload["action"];

            if (action == "copy_token")
            {
                CopyLoginToken();
            }
            else if (action == "setup_account")
            {
                this.BeginInvoke((MethodInvoker)delegate { btnSteamLogin_Click(this, EventArgs.Empty); });
            }
            else if (action == "setup_encryption")
            {
                this.BeginInvoke((MethodInvoker)delegate { btnManageEncryption_Click(this, EventArgs.Empty); });
            }
            else if (action == "import_account")
            {
                this.BeginInvoke((MethodInvoker)delegate { 
                    using (ImportAccountForm importForm = new ImportAccountForm(this.passKey))
                    {
                        importForm.ShowDialog(this);
                    }
                    this.loadAccountsList();
                });
            }

            else if (action == "login_qr")
            {
                this.BeginInvoke((MethodInvoker)delegate { btnLoginViaQr_Click(this, EventArgs.Empty); });
            }
            else if (action == "switch_account")
            {
                string accName = (string)payload["accountName"];
                string steamIdText = (string)payload["steamId"];
                ulong steamId;
                SteamGuardAccount selectedAccount = null;
                if (allAccounts != null && ulong.TryParse(steamIdText, out steamId))
                {
                    selectedAccount = allAccounts.FirstOrDefault(account => account.Session != null && account.Session.SteamID == steamId);
                }

                if (allAccounts != null && selectedAccount == null)
                    selectedAccount = allAccounts.FirstOrDefault(account => account.AccountName == accName);

                if (selectedAccount != null)
                {
                    currentAccount = selectedAccount;
                    loadAccountInfo();
                }
            }
            else if (action == "remove_account")
            {
                string steamIdText = (string)payload["steamId"];
                string accountName = (string)payload["accountName"];
                string removalMode = (string)payload["removalMode"];
                ulong steamId;
                bool hasSteamId = ulong.TryParse(steamIdText, out steamId);
                bool validMode = removalMode == "unlink" || removalMode == "remove";
                bool validLocalRemoval = removalMode == "remove" && !String.IsNullOrWhiteSpace(accountName);
                if (validMode && (hasSteamId || validLocalRemoval))
                {
                    ulong? selectedSteamId = hasSteamId ? steamId : (ulong?)null;
                    this.BeginInvoke((MethodInvoker)delegate { _ = RemoveManagedAccountFromWebAsync(selectedSteamId, accountName, removalMode); });
                }
                else
                {
                    AstroMessageBox.Show("The requested account removal is invalid. Refresh the account list and try again.", "Remove Account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    if (webView != null && webView.CoreWebView2 != null)
                        _ = webView.CoreWebView2.ExecuteScriptAsync("hideSpinner('remove-account');");
                }
            }
            else if (action == "load_settings")
            {
                SendSettingsToWebView();
            }
            else if (action == "save_settings")
            {
                string newLoginActionMode = (string)payload["loginActionMode"] ?? LoginActionModes.Manual;
                if (newLoginActionMode != LoginActionModes.Manual &&
                    newLoginActionMode != LoginActionModes.ApprovePersistent &&
                    newLoginActionMode != LoginActionModes.Deny)
                {
                    newLoginActionMode = LoginActionModes.Manual;
                }

                bool newLoginActionAutoAllowIpEnabled = (bool?)payload["loginActionAutoAllowIpEnabled"] ?? false;
                bool newLoginActionAutoAllowCurrentDeviceIp = (bool?)payload["loginActionAutoAllowCurrentDeviceIp"] ?? false;
                string newLoginActionAutoAllowIp = ((string)payload["loginActionAutoAllowIp"] ?? String.Empty).Trim();
                if (newLoginActionMode != LoginActionModes.Deny)
                {
                    newLoginActionAutoAllowIpEnabled = false;
                    newLoginActionAutoAllowCurrentDeviceIp = false;
                }
                else if (newLoginActionAutoAllowIpEnabled && !String.IsNullOrWhiteSpace(newLoginActionAutoAllowIp) && !IsValidIpv4Address(newLoginActionAutoAllowIp))
                {
                    AstroMessageBox.Show("Enter a valid additional public IPv4 address, or leave it blank to use only the current-device option.", "Login Actions", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    SendSettingsToWebView();
                    return;
                }
                else if (!newLoginActionAutoAllowIpEnabled)
                {
                    newLoginActionAutoAllowCurrentDeviceIp = false;
                }

                bool automaticDenyExceptionChanged = newLoginActionMode == LoginActionModes.Deny &&
                    (newLoginActionAutoAllowIpEnabled != manifest.LoginActionAutoAllowIpEnabled ||
                     newLoginActionAutoAllowCurrentDeviceIp != manifest.LoginActionAutoAllowCurrentDeviceIp ||
                     !String.Equals(newLoginActionAutoAllowIp, manifest.LoginActionAutoAllowIp, StringComparison.Ordinal));
                if ((newLoginActionMode != manifest.LoginActionMode && newLoginActionMode != LoginActionModes.Manual) || automaticDenyExceptionChanged)
                {
                    string actionDescription = newLoginActionMode == LoginActionModes.ApprovePersistent
                        ? "automatically approve every pending login request with a persistent sign-in"
                        : "automatically deny every pending login request";
                    if (newLoginActionMode == LoginActionModes.Deny && newLoginActionAutoAllowIpEnabled)
                    {
                        var allowedSources = new List<string>();
                        if (newLoginActionAutoAllowCurrentDeviceIp)
                            allowedSources.Add("this device's current public IP address");
                        if (!String.IsNullOrWhiteSpace(newLoginActionAutoAllowIp))
                            allowedSources.Add(newLoginActionAutoAllowIp);
                        if (allowedSources.Count > 0)
                            actionDescription += ", except requests from " + String.Join(" or ", allowedSources) + ", which will be approved with a persistent sign-in";
                    }
                    DialogResult confirmation = AstroMessageBox.Show(
                        "This setting will " + actionDescription + " for every managed account, including requests that are already pending. Login monitoring will remain enabled while this rule is active. Continue?",
                        "Enable Automatic Login Action",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (confirmation != DialogResult.Yes)
                    {
                        SendSettingsToWebView();
                        return;
                    }
                }

                manifest.TradeConfirmationCustomIntervalEnabled = (bool?)payload["tradeConfirmationCustomIntervalEnabled"] ?? false;
                manifest.TradeConfirmationCheckInterval = Math.Clamp((int?)payload["tradeConfirmationCheckInterval"] ?? 15, 3, 3600);
                manifest.AutoConfirmMarketTransactions = (bool?)payload["autoConfirmMarket"] ?? false;
                manifest.AutoConfirmTrades = (bool?)payload["autoConfirmTrades"] ?? false;
                manifest.MinimizeToTray = (bool?)payload["minimizeToTray"] ?? false;
                manifest.DiagnosticErrorLoggingEnabled = (bool?)payload["diagnosticErrorLoggingEnabled"] ?? false;
                // Automatic rules require the dedicated monitor; manual mode can be monitored or disabled independently.
                manifest.LoginActionMonitoringEnabled = ((bool?)payload["loginActionMonitoringEnabled"] ?? false) || newLoginActionMode != LoginActionModes.Manual;
                manifest.LoginActionMode = newLoginActionMode;
                manifest.LoginActionAutoAllowIpEnabled = newLoginActionAutoAllowIpEnabled;
                manifest.LoginActionAutoAllowCurrentDeviceIp = newLoginActionAutoAllowCurrentDeviceIp;
                manifest.LoginActionAutoAllowIp = newLoginActionAutoAllowIp;
                manifest.Save();
                DiagnosticErrorLogger.Configure(manifest.DiagnosticErrorLoggingEnabled);
                ConfigureTradeConfirmationMonitor();
                ConfigureLoginActionsMonitor();
                SendSettingsToWebView();
                webView.CoreWebView2.ExecuteScriptAsync("settingsSaved()");
            }
            else if (action == "toggle_autostart")
            {
                bool enable = (bool?)payload["enabled"] ?? false;
                WindowsStartup.SetEnabled(enable);
            }
            else if (action == "active_tab_changed")
            {
                string tabName = (string)payload["tabName"];
                if (tabName == "authenticator" || tabName == "trades" || tabName == "login-actions" || tabName == "settings")
                    activeWebTab = tabName;
            }
            else if (action == "load_trades_cache")
            {
                _ = LoadCachedTradesAsync((string)payload["accountName"]);
            }
            else if (action == "refresh_trades")
            {
                _ = LoadTradesAsync((string)payload["accountName"]);
            }
            else if (action == "load_login_actions")
            {
                _ = PublishCachedLoginActionsAsync();
            }
            else if (action == "refresh_login_actions")
            {
                _ = RefreshLoginActionsAsync();
            }
            else if (action == "respond_login_action")
            {
                string accountName = (string)payload["accountName"];
                string clientIdText = (string)payload["clientId"];
                string decision = (string)payload["decision"];
                if (!String.IsNullOrWhiteSpace(accountName) && ulong.TryParse(clientIdText, out ulong clientId) &&
                    (decision == "approve" || decision == "deny"))
                {
                    _ = RespondToLoginActionAsync(accountName, clientId, decision);
                }
                else
                {
                    webView.CoreWebView2.ExecuteScriptAsync("hideSpinner();");
                }
            }
            else if (action == "refresh_login_account")
            {
                string accountName = (string)payload["accountName"];
                SteamGuardAccount account = allAccounts?.FirstOrDefault(item => item.AccountName == accountName);
                if (account != null)
                {
                    PromptRefreshLogin(account);
                    loadAccountsList();
                }
                webView.CoreWebView2.ExecuteScriptAsync("hideSpinner();");
            }
            else if (action == "accept_trade" || action == "reject_trade")
            {
                string confirmationKey = (string)payload["id"];
                if (!String.IsNullOrWhiteSpace(confirmationKey))
                    _ = RespondToTradeConfirmationAsync(confirmationKey, action == "accept_trade");
                else
                    webView.CoreWebView2.ExecuteScriptAsync("hideSpinner()");
            }
        }

        private static string BuildTradeConfirmationKey(SteamGuardAccount account, Confirmation confirmation)
        {
            return account.Session.SteamID.ToString() + ":" + confirmation.ID.ToString();
        }

        private async Task RespondToTradeConfirmationAsync(string confirmationKey, bool accept)
        {
            if (!loadedTradeConfirmations.TryGetValue(confirmationKey, out LoadedTradeConfirmation entry))
            {
                await webView.CoreWebView2.ExecuteScriptAsync("hideSpinner()");
                return;
            }

            bool actionSucceeded = false;
            try
            {
                await confirmationsSemaphore.WaitAsync();
                try
                {
                    bool steamAcceptedAction = await RunSteamAccountOperationAsync(entry.Account, () => accept
                        ? entry.Account.AcceptConfirmation(entry.Confirmation)
                        : entry.Account.DenyConfirmation(entry.Confirmation));
                    if (!steamAcceptedAction)
                        throw new InvalidOperationException("Steam did not accept that confirmation action. The confirmation remains pending.");
                    actionSucceeded = true;
                }
                finally
                {
                    confirmationsSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Trade confirmation action", ex, accept ? "Accept request failed." : "Deny request failed.");
                AstroMessageBox.Show(ex.Message, "Trade Confirmations", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (actionSucceeded)
                {
                    MarkRecentlyResolved(recentlyResolvedTradeConfirmations, confirmationKey);
                    loadedTradeConfirmations.Remove(confirmationKey);
                    await PublishCachedTradesAsync();
                }
                _ = LoadTradesAsync();
            }
        }

        private async Task LoadTradesAsync(string selectedAccountName = null)
        {
            if (!String.IsNullOrWhiteSpace(selectedAccountName))
                tradeAccountSelection = selectedAccountName;
            string selectionToLoad = tradeAccountSelection;

            if (allAccounts == null || allAccounts.Length == 0)
            {
                await PublishCachedTradesAsync();
                return;
            }

            if (!await tradeLoadSemaphore.WaitAsync(0))
                return;
            bool confirmationsSemaphoreAcquired = false;
            try
            {
                await confirmationsSemaphore.WaitAsync();
                confirmationsSemaphoreAcquired = true;

                SteamGuardAccount[] accountsToLoad = selectionToLoad == "all"
                    ? allAccounts.Where(account => account?.Session != null).ToArray()
                    : allAccounts.Where(account => account?.Session != null && account.AccountName == selectionToLoad).ToArray();
                if (accountsToLoad.Length == 0 && currentAccount?.Session != null)
                    accountsToLoad = new[] { currentAccount };

                var unavailableAccounts = new List<string>();
                string rateLimitMessage = null;
                foreach (SteamGuardAccount account in accountsToLoad)
                {
                    try
                    {
                        if (account.Session.IsRefreshTokenExpired())
                        {
                            unavailableAccounts.Add(account.AccountName + " needs you to sign in again.");
                            InvalidateTradeConfirmationCache(account);
                            continue;
                        }
                        Confirmation[] accountConfirmations = await FetchTradeConfirmationsForPageAsync(account);
                        if (accountConfirmations == null)
                            continue;
                        CacheTradeConfirmations(account, accountConfirmations.Where(confirmation => !ShouldAutoConfirmTrade(confirmation)));
                    }
                    catch (TradeRateLimitedException ex)
                    {
                        rateLimitMessage = ex.Message;
                        DiagnosticErrorLogger.Log("Trade confirmation fetch", ex, "Steam rate limited confirmation requests.");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Trade confirmation fetch", ex, "A confirmation list request failed after all retries.");
                        unavailableAccounts.Add(account.AccountName + " could not be loaded.");
                    }
                }

                string errorMessage = !String.IsNullOrWhiteSpace(rateLimitMessage)
                    ? rateLimitMessage
                    : unavailableAccounts.Count == 0
                        ? null
                        : "Some account confirmations could not be loaded: " + String.Join(" ", unavailableAccounts);
                UpdateTradePendingCount(pendingTradeConfirmationCounts.Values.Sum());
                await PublishCachedTradesAsync(errorMessage, selectionToLoad);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Trade confirmation load failed: " + ex.Message);
                DiagnosticErrorLogger.Log("Trade confirmation page", ex, "The confirmation page could not be updated.");
                await PublishCachedTradesAsync("Steam confirmations could not be loaded. Please try refreshing.", selectionToLoad);
            }
            finally
            {
                if (confirmationsSemaphoreAcquired)
                    confirmationsSemaphore.Release();
                tradeLoadSemaphore.Release();

                if (!String.Equals(selectionToLoad, tradeAccountSelection, StringComparison.Ordinal))
                    _ = LoadTradesAsync();
            }
        }
    }
}
