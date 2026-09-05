using System;
using System.Diagnostics;
using System.Windows.Forms;
using SteamAuth;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
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
        private readonly SteamAccountOperationCoordinator steamAccountOperationCoordinator = new SteamAccountOperationCoordinator();
        private readonly object sessionStateLock = new object();
        private readonly Dictionary<string, LoadedTradeConfirmation> loadedTradeConfirmations = new Dictionary<string, LoadedTradeConfirmation>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> tradeActionsInProgress = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private readonly HashSet<ulong> loadedTradeConfirmationAccounts = new HashSet<ulong>();
        private readonly Dictionary<string, string> unavailableLoginAccounts = new Dictionary<string, string>();
        private readonly HashSet<ulong> sessionRenewalRequired = new HashSet<ulong>();
        private readonly HashSet<ulong> startupDeferredSessionRenewals = new HashSet<ulong>();
        private readonly Dictionary<string, DateTime> recentlyResolvedLoginRequests = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> recentlyResolvedTradeConfirmations = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> completedTradeActions = new Dictionary<string, DateTime>();
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
        private static readonly TimeSpan RecentlyResolvedTradeConfirmationsRetention = TimeSpan.FromMinutes(2);

        private long steamTime = 0;
        private long currentSteamChunk = 0;
        private string passKey = null;
        private bool startSilent = false;
        private bool backgroundServicesEligible;
        private bool backgroundServicesStarted;
        private bool startupAccountMaintenanceCompleted;
        private bool startupAccountMaintenanceFinished;
        private bool settingsDirty;
        private bool explicitExitRequested;
        private bool allowExitAfterSettingsSave;
        private bool exitAfterSettingsSaveRequested;
        private bool settingsSaveInProgress;
        private CancellationTokenSource proxyTestCancellationSource;
        private readonly CancellationTokenSource lifetimeCancellationSource = new CancellationTokenSource();

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
        private System.Windows.Forms.Timer loadingTimer;
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

        private sealed class SessionUnavailableException : InvalidOperationException
        {
            public SessionUnavailableException(string accountName)
                : base("The session for " + (String.IsNullOrWhiteSpace(accountName) ? "this account" : accountName) + " is unavailable. Renew it before using Steam account services.")
            {
            }
        }

        private Task<T> RunSteamAccountOperationAsync<T>(SteamGuardAccount account, Func<Task<T>> operation, CancellationToken cancellationToken = default, Action<Exception> onFailure = null)
        {
            ulong steamId = account?.Session?.SteamID ?? 0;
            return steamAccountOperationCoordinator.RunAsync(steamId, operation, onFailure, cancellationToken);
        }

        private Task<T> RunAvailableSteamAccountOperationAsync<T>(SteamGuardAccount account, Func<Task<T>> operation, CancellationToken cancellationToken = default, Action<T> onResult = null)
        {
            SessionData sessionAtOperationStart = null;
            return RunSteamAccountOperationAsync(account, async () =>
            {
                sessionAtOperationStart = account?.Session;
                // Check after acquiring the per-account lock. An operation that is
                // already on the wire may finish, but work queued behind it is
                // discarded as soon as another call invalidates the session.
                if (!IsSessionAvailableForAccountOperations(account))
                    throw new SessionUnavailableException(account?.AccountName);
                T result = await operation();
                onResult?.Invoke(result);
                return result;
            }, cancellationToken, exception =>
            {
                if (!(exception is SessionUnavailableException) && IsDefinitiveSessionFailure(exception))
                    MarkSessionRenewalRequired(account, exception.Message, sessionAtOperationStart);
            });
        }

        private async Task RefreshAndPersistAccessTokenAsync(SteamGuardAccount account, CancellationToken cancellationToken)
        {
            SessionData session = account?.Session ?? throw new SessionUnavailableException(account?.AccountName);
            string previousAccessToken = session.AccessToken;
            string previousRefreshToken = session.RefreshToken;
            try
            {
                await session.RefreshAccessToken(false, cancellationToken);
                if (!PersistLoginSession(account))
                    throw new IOException("Steam refreshed the session, but Astro SDA could not save it securely.");
            }
            catch
            {
                // Never leave a non-durable token active only in memory.
                session.AccessToken = previousAccessToken;
                session.RefreshToken = previousRefreshToken;
                throw;
            }
        }

        private Task<Confirmation[]> FetchTradeConfirmationsForPageAsync(SteamGuardAccount account, CancellationToken cancellationToken = default)
        {
            return FetchTradeConfirmationsAsync(account, TimeSpan.FromSeconds(1), 2, cancellationToken);
        }

        private Task<Confirmation[]> FetchTradeConfirmationsForMonitorAsync(SteamGuardAccount account, CancellationToken cancellationToken = default)
        {
            int seconds = Math.Min(GetTradeConfirmationMonitorIntervalSeconds(), 15);
            return FetchTradeConfirmationsAsync(account, TimeSpan.FromSeconds(seconds), 3, cancellationToken);
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

        private async Task<Confirmation[]> FetchTradeConfirmationsAsync(SteamGuardAccount account, TimeSpan retryDelay, int retryCount, CancellationToken cancellationToken = default)
        {
            if (TryGetTradeRateLimitMessage(out string rateLimitMessage))
                throw new TradeRateLimitedException(rateLimitMessage);

            Exception lastError = null;
            bool refreshAccessTokenBeforeRetry = false;

            // The mobile-confirmation signature is time-sensitive.  Make sure the
            // asynchronous time alignment has completed before creating the first
            // request, rather than relying on the one-second UI timer to win a race.
            await TimeAligner.GetSteamTimeAsync(cancellationToken);

            for (int attempt = 0; attempt <= retryCount; attempt++)
            {
                try
                {
                    return await RunAvailableSteamAccountOperationAsync(account, async () =>
                    {
                        if (refreshAccessTokenBeforeRetry || account.Session.IsAccessTokenExpired())
                        {
                            await RefreshAndPersistAccessTokenAsync(account, cancellationToken);
                            // A successful forced refresh has done its job. If the
                            // confirmation request fails transiently, retry that
                            // request without refreshing the token again.
                            refreshAccessTokenBeforeRetry = false;
                        }
                        return await account.FetchConfirmationsAsync(cancellationToken);
                    }, cancellationToken);
                }
                catch (Exception ex) when (IsRateLimitedResponse(ex))
                {
                    throw ApplyTradeRateLimit(ex);
                }
                catch (SessionUnavailableException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < retryCount)
                {
                    lastError = ex;
                    refreshAccessTokenBeforeRetry = refreshAccessTokenBeforeRetry || IsTradeAuthenticationFailure(ex);
                    await Task.Delay(retryDelay, cancellationToken);
                }
                catch (Exception)
                {
                    throw;
                }
            }

            Exception finalError = lastError ?? new InvalidOperationException("Steam could not load confirmations.");
            throw finalError;
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

            if (Int32.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out int retryAfterSeconds) && retryAfterSeconds > 0)
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
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamSessionException sessionException && sessionException.Kind == SteamSessionFailureKind.RateLimited)
                    return true;
                if (current is SteamWebRequestException steamException && steamException.StatusCode == HttpStatusCode.TooManyRequests)
                    return true;
                if (current is WebException webException && webException.Response is HttpWebResponse response && response.StatusCode == HttpStatusCode.TooManyRequests)
                    return true;
            }
            return false;
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
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamGuardAccount.WGTokenInvalidException || current is SteamGuardAccount.WGTokenExpiredException)
                    return true;
                if (current is SteamSessionException sessionException)
                    return sessionException.Kind == SteamSessionFailureKind.InvalidSession;
                if (current is SteamWebRequestException steamWebException &&
                    steamWebException.StatusCode == HttpStatusCode.Unauthorized)
                    return true;
                if (current is WebException webException && webException.Response is HttpWebResponse httpResponse &&
                    httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                    return true;

            }
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                string message = current.Message ?? String.Empty;
                if (message.IndexOf("Needs Authentication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
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

        private async void MainForm_Shown(object sender, EventArgs e)
        {
            this.labelVersion.Text = String.Format("v{0}", Application.ProductVersion);
            try
            {
                this.manifest = Manifest.GetManifest();
                DiagnosticErrorLogger.Configure(this.manifest.DiagnosticErrorLoggingEnabled);
            }
            catch (ManifestParseException)
            {
                AstroMessageBox.Show("Unable to read your settings. Try restarting Astro Steam Desktop Assistant.", "Astro Steam Desktop Assistant", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
                return;
            }

            // Make sure we don't show that welcome dialog again
            this.manifest.FirstRun = false;
            StorageResult startupSaveResult = this.manifest.SaveWithResult();
            if (!startupSaveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Application startup", startupSaveResult.Exception, "The startup manifest could not be saved.");
                AstroMessageBox.Show(
                    startupSaveResult.UserMessage ?? "Unable to save application settings. Check that the data folder is writable.",
                    "Astro Steam Desktop Assistant",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                this.Close();
                return;
            }

            if (manifest.Encrypted)
            {
                if (passKey == null)
                {
                    passKey = manifest.PromptForPassKey();
                    if (passKey == null)
                    {
                        Close();
                        return;
                    }
                }

                btnManageEncryption.Text = "Remove Encryption";
            }
            else
            {
                btnManageEncryption.Text = "Setup Encryption";
            }

            StorageResult filenameNormalizationResult = manifest.NormalizeAccountFilenames();
            if (!filenameNormalizationResult.Succeeded)
                DiagnosticErrorLogger.Log("Application startup", filenameNormalizationResult.Exception, "Some non-canonical authenticator filenames were kept for later recovery.");

            btnManageEncryption.Enabled = manifest.Entries.Count > 0;

            loadAccountsList();
            loginApprovalService = new LoginApprovalService(PersistLoginSession);

            try
            {
                await RunStartupAccountMaintenanceAsync();
            }
            catch (Exception ex)
            {
                startupAccountMaintenanceFinished = true;
                DiagnosticErrorLogger.Log("Application startup", ex, "Startup account maintenance did not complete.");
            }
            if (IsDisposed || Disposing)
                return;

            if (backgroundServicesEligible)
                StartBackgroundServicesAfterUiReady();

            if (startSilent)
            {
                this.WindowState = FormWindowState.Minimized;
            }
        }

        private async Task RunStartupAccountMaintenanceAsync()
        {
            if (startupAccountMaintenanceCompleted || manifest == null)
                return;
            startupAccountMaintenanceCompleted = true;

            // This is the only automatic maFile directory scan. Runtime account
            // activity never re-runs it and never watches the directory.
            IReadOnlyList<Manifest.UnmanagedMaFileCandidate> candidates;
            try
            {
                candidates = manifest.FindUnmanagedMaFiles();
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator startup import", ex, "The startup maFile scan could not be completed.");
                candidates = Array.Empty<Manifest.UnmanagedMaFileCandidate>();
            }

            if (candidates.Count > 0 && StartupMaFilePromptForm.Show(this, candidates.Select(candidate => candidate.FileName).ToList()))
            {
                StorageResult importResult = manifest.ImportUnmanagedMaFiles(candidates, passKey);
                if (!importResult.Succeeded)
                {
                    DiagnosticErrorLogger.Log("Authenticator startup import", importResult.Exception, "The selected maFiles were not imported.");
                    AstroMessageBox.Show(
                        importResult.UserMessage ?? "The selected authenticator files could not be imported. The original files were left untouched.",
                        "Import authenticator files",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                else if (candidates.Count == 1)
                {
                    AstroMessageBox.Show("The authenticator file was imported.", "Import authenticator files", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    AstroMessageBox.Show(candidates.Count + " authenticator files were imported.", "Import authenticator files", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }

            loadAccountsList();
            await ValidateStartupSessionsAsync();
            loadAccountsList();
            await PromptForUnavailableStartupSessionsAsync();
            loadAccountsList();
            startupAccountMaintenanceFinished = true;
        }

        private async Task ValidateStartupSessionsAsync()
        {
            SteamGuardAccount[] accounts = allAccounts ?? Array.Empty<SteamGuardAccount>();
            foreach (SteamGuardAccount account in accounts)
            {
                if (account?.Session == null)
                    continue;

                SessionData sessionAtValidationStart = account.Session;
                if (sessionAtValidationStart.IsRefreshTokenExpired())
                {
                    MarkSessionRenewalRequired(account, "The refresh token is expired or malformed.", sessionAtValidationStart);
                    continue;
                }

                try
                {
                    await RunSteamAccountOperationAsync(account, async () =>
                    {
                        await RefreshAndPersistAccessTokenAsync(account, lifetimeCancellationSource.Token);
                        return true;
                    }, lifetimeCancellationSource.Token, exception =>
                    {
                        if (IsDefinitiveSessionFailure(exception))
                            MarkSessionRenewalRequired(account, exception.Message, sessionAtValidationStart);
                    });
                }
                catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (IsTransientSessionFailure(ex))
                    {
                        DiagnosticErrorLogger.Log("Authenticator startup recovery", ex, "Startup session validation stopped because Steam or the network was unavailable.");
                        return;
                    }
                    else if (!IsDefinitiveSessionFailure(ex))
                    {
                        DiagnosticErrorLogger.Log("Authenticator startup recovery", ex, "A startup session refresh failed without proving that the saved Steam session was revoked.");
                    }
                }
            }
        }

        private async Task PromptForUnavailableStartupSessionsAsync()
        {
            SteamGuardAccount[] accounts = allAccounts ?? Array.Empty<SteamGuardAccount>();
            foreach (SteamGuardAccount startupAccount in accounts)
            {
                if (startupAccount?.Session == null)
                    continue;

                ulong steamId = startupAccount.Session.SteamID;
                if (startupDeferredSessionRenewals.Contains(steamId) || !IsSessionRenewalRequired(startupAccount))
                    continue;

                DialogResult choice = AstroMessageBox.ShowWithCustomButtons(
                    "The session for " + startupAccount.AccountName + " was lost. Renew the session to continue using this account, remove it from this app (Steam Guard will remain enabled), or do this later.",
                    "Session renewal required",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning,
                    "Renew session",
                    "Remove from app",
                    "Later");

                if (choice == DialogResult.Yes)
                {
                    bool renewed = await PromptRefreshLoginAsync(startupAccount);
                    loadAccountsList(steamId);
                    if (!renewed)
                        startupDeferredSessionRenewals.Add(steamId);
                    continue;
                }

                if (choice == DialogResult.No)
                {
                    await RemoveManagedAccountAsync(steamId, startupAccount.AccountName, "remove");
                    loadAccountsList();
                    continue;
                }

                startupDeferredSessionRenewals.Add(steamId);
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
                TryBeginInvoke(RestoreWindowFromActivation);
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
            if (e.CloseReason == CloseReason.UserClosing && !explicitExitRequested && manifest != null && manifest.MinimizeToTray)
            {
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                if (!allowExitAfterSettingsSave && settingsDirty &&
                    e.CloseReason != CloseReason.WindowsShutDown &&
                    e.CloseReason != CloseReason.TaskManagerClosing)
                {
                    DialogResult result = AstroMessageBox.ShowWithCustomButtons(
                        "You have unsaved settings changes. Save them before exiting?",
                        "Unsaved Settings",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning,
                        "Save Changes",
                        "Leave Without Saving",
                        "Cancel",
                        true);
                    if (result == DialogResult.Cancel)
                    {
                        e.Cancel = true;
                        explicitExitRequested = false;
                        exitAfterSettingsSaveRequested = false;
                        return;
                    }
                    if (result == DialogResult.Yes)
                    {
                        e.Cancel = true;
                        explicitExitRequested = false;
                        exitAfterSettingsSaveRequested = true;
                        RestoreWindowFromActivation();
                        if (settingsSaveInProgress)
                        {
                            AstroMessageBox.Show(
                                "Settings are already being saved. The app will exit after that save succeeds.",
                                "Saving Settings",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                        else if (GetCoreWebView2IfAvailable() != null)
                        {
                            _ = ExecuteScriptSafelyAsync("saveSettings('exit');", "Settings save on exit");
                        }
                        else
                        {
                            AstroMessageBox.Show(
                                "Settings cannot be saved until the Settings page is available. The app will stay open.",
                                "Saving Settings",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                        return;
                    }
                    settingsDirty = false;
                }

                lifetimeCancellationSource.Cancel();
                CancelProxyOperation();
                loginActionsTimer.Stop();
                CloseLoginNotificationPopups();
                // Allow the original FormClosing event to complete. Calling Application.Exit()
                // here re-enters shutdown and leaves the first close request unfulfilled.
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

            string waitingValue = isWaiting ? "true" : "false";
            _ = ExecuteScriptSafelyAsync($"setQrScanWaiting({waitingValue}, {remainingSeconds})", "QR login UI");
        }

        private async void btnLoginViaQr_Click(object sender, EventArgs e)
        {
            if (qrScanInProgress)
                return;

            SteamGuardAccount accountAtScanStart = currentAccount;
            if (!IsSessionAvailableForAccountOperations(accountAtScanStart))
            {
                SetQrScanWaitingState(false);
                return;
            }

            ulong steamIdAtScanStart = accountAtScanStart.Session.SteamID;

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

                    await Task.Delay(100, lifetimeCancellationSource.Token);
                }

                this.btnLoginViaQr.Text = originalText;
                this.btnLoginViaQr.Enabled = IsSessionAvailableForAccountOperations(currentAccount);
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
                
                    if (!TryGetQrClientId(result.Text, out ulong clientId))
                    {
                        AstroMessageBox.Show("Can't get ID of QR code. Steam might have changed their QR format.", "Wrong QR code.", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    if (currentAccount?.Session?.SteamID != steamIdAtScanStart)
                    {
                        AstroMessageBox.Show("The selected account changed while scanning. Start the QR scan again for the intended account.", "QR Login", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    if (!IsSessionAvailableForAccountOperations(accountAtScanStart))
                    {
                        AstroMessageBox.Show("This account session is no longer available. Renew it before approving a QR login.", "QR Login", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    try
                    {
                        await RunAvailableSteamAccountOperationAsync(accountAtScanStart, async () =>
                        {
                            if (accountAtScanStart.Session.IsAccessTokenExpired())
                                await RefreshAndPersistAccessTokenAsync(accountAtScanStart, lifetimeCancellationSource.Token);
                            return await accountAtScanStart.SignInViaQR(clientId.ToString(), lifetimeCancellationSource.Token);
                        }, lifetimeCancellationSource.Token);
                        AstroMessageBox.Show("Successfully logged in via QR code!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("QR login approval", ex, "The QR login approval request failed.");
                        if (IsDefinitiveSessionFailure(ex))
                        {
                            AstroMessageBox.Show("Steam rejected this account session. Renew it before approving another QR login.", "QR Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else if (ex is SteamSessionException sessionException && sessionException.Result != 0)
                        {
                            AstroMessageBox.Show(GetQrLoginFailureMessage(sessionException.Result.ToString(CultureInfo.InvariantCulture)), "QR Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            AstroMessageBox.Show("Steam could not complete the QR login approval. Refresh the Steam login page and scan a new QR code, then try again.", "QR Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
            {
                // The form is closing; the cancellation is expected.
            }
            finally
            {
                qrScanInProgress = false;
                btnLoginViaQr.Enabled = IsSessionAvailableForAccountOperations(currentAccount);
                SetQrScanWaitingState(false);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            lifetimeCancellationSource.Cancel();

            if (qrScanOverlay != null)
            {
                qrScanOverlay.Dispose();
                qrScanOverlay = null;
            }

            if (webView != null)
            {
                webView.Dispose();
                webView = null;
            }

            if (loadingTimer != null)
            {
                loadingTimer.Stop();
                loadingTimer.Dispose();
                loadingTimer = null;
            }

            lifetimeCancellationSource.Dispose();
            base.OnFormClosed(e);
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
                    passKey = null;
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
            explicitExitRequested = true;
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

        private async void menuLoginAgain_Click(object sender, EventArgs e)
        {
            ulong? steamId = currentAccount?.Session?.SteamID;
            await PromptRefreshLoginAsync(currentAccount);
            loadAccountsList(steamId);
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
            SteamGuardAccount accountAtStart = currentAccount;
            if (!IsSessionAvailableForAccountOperations(accountAtStart))
            {
                AstroMessageBox.Show("Renew this account's session before removing Steam Guard.", "Deactivate Authenticator", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Check for a valid refresh token first
            if (accountAtStart.Session.IsRefreshTokenExpired())
            {
                AstroMessageBox.Show("Your session has expired. Use the login again button under the selected account menu.", "Deactivate Authenticator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Check for a valid access token, refresh it if needed
            if (accountAtStart.Session.IsAccessTokenExpired())
            {
                try
                {
                    await RunAvailableSteamAccountOperationAsync(accountAtStart, async () =>
                    {
                        await RefreshAndPersistAccessTokenAsync(accountAtStart, lifetimeCancellationSource.Token);
                        return true;
                    }, lifetimeCancellationSource.Token);
                }
                catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Steam Guard deactivation", ex, "Steam Guard could not be removed from the selected account.");
                    AstroMessageBox.Show("Steam Guard could not be removed. Check the account's login status and try again.", "Deactivate Authenticator Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            DialogResult res = AstroMessageBox.Show("Would you like to remove Steam Guard completely?\nYes - Remove Steam Guard completely.\nNo - Switch back to Email authentication.", "Deactivate Authenticator: " + accountAtStart.AccountName, MessageBoxButtons.YesNoCancel);
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
                    confCode = await accountAtStart.GenerateSteamGuardCodeAsync(lifetimeCancellationSource.Token);
                }
                catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Steam Guard deactivation", ex, "The confirmation code could not be generated.");
                    AstroMessageBox.Show("The confirmation code could not be generated. Check your connection and try again.", "Deactivate Authenticator Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                string enteredCode;
                using (InputForm confirmationDialog = new InputForm(String.Format("Removing Steam Guard from {0}. Enter this confirmation code: {1}", accountAtStart.AccountName, confCode)))
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

                bool success;
                try
                {
                    success = await RunAvailableSteamAccountOperationAsync(accountAtStart, async () =>
                    {
                        if (accountAtStart.Session.IsAccessTokenExpired())
                            await RefreshAndPersistAccessTokenAsync(accountAtStart, lifetimeCancellationSource.Token);
                        return await accountAtStart.DeactivateAuthenticator(scheme, lifetimeCancellationSource.Token);
                    }, lifetimeCancellationSource.Token);
                }
                catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Steam Guard deactivation", ex, "Steam Guard could not be removed from the selected account.");
                    AstroMessageBox.Show(ex.Message, "Deactivate Authenticator Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (success)
                {
                    AstroMessageBox.Show(String.Format("Steam Guard {0}. maFile will be deleted after hitting okay. If you need to make a backup, now's the time.", (scheme == 2 ? "removed completely" : "switched to emails")));
                    this.manifest.RemoveAccount(accountAtStart);
                    this.loadAccountsList();
                }
                else
                {
                    AstroMessageBox.Show(String.IsNullOrWhiteSpace(accountAtStart.LastAuthenticatorOperationError)
                        ? "Steam Guard failed to deactivate."
                        : accountAtStart.LastAuthenticatorOperationError);
                }
            }
            else
            {
                AstroMessageBox.Show("Steam Guard was not removed. No action was taken.");
            }
        }

        private static string GetQrLoginFailureMessage(string result)
        {
            if (!Int32.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out int steamResult))
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
            if (removeSteamGuard && !IsSessionAvailableForAccountOperations(account))
            {
                AstroMessageBox.Show("Renew this account's session before removing Steam Guard. You can still remove only its local data.", "Remove Steam Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                        await RunAvailableSteamAccountOperationAsync(account, async () =>
                        {
                            await RefreshAndPersistAccessTokenAsync(account, lifetimeCancellationSource.Token);
                            return true;
                        }, lifetimeCancellationSource.Token);
                    }
                    catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Managed account removal", ex, "The Steam session could not be refreshed before Steam Guard removal.");
                        AstroMessageBox.Show("Steam could not refresh this account session. Check your connection or sign in again before removing Steam Guard.", "Remove Steam Guard Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                string confirmationCode;
                try
                {
                    confirmationCode = await account.GenerateSteamGuardCodeAsync(lifetimeCancellationSource.Token);
                }
                catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                {
                    return;
                }
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

                bool deactivated;
                try
                {
                    deactivated = await RunAvailableSteamAccountOperationAsync(account, async () =>
                    {
                        if (account.Session.IsAccessTokenExpired())
                            await RefreshAndPersistAccessTokenAsync(account, lifetimeCancellationSource.Token);
                        return await account.DeactivateAuthenticator(2, lifetimeCancellationSource.Token);
                    }, lifetimeCancellationSource.Token);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Managed account removal", ex, "Steam Guard removal was rejected by Steam.");
                    AstroMessageBox.Show(ex.Message, "Remove Steam Guard Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
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
            catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
            {
                // The form is closing; cancellation is expected.
            }
            catch (SessionUnavailableException)
            {
                // Another operation invalidated this account while it was queued.
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Managed account removal", ex, "The account removal command failed.");
                AstroMessageBox.Show("This app could not complete the account removal. No other accounts were changed.", "Remove Account", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                await ExecuteScriptSafelyAsync("hideSpinner('remove-account');", "Managed account removal");
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
            explicitExitRequested = true;
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
                    btnLoginViaQr.Enabled = IsSessionAvailableForAccountOperations(account);
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
            try
            {
                lblStatus.Text = "Aligning time with Steam...";
                steamTime = await TimeAligner.GetSteamTimeAsync(lifetimeCancellationSource.Token);
                lblStatus.Text = "";
            }
            catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lblStatus.Text = "";
                DiagnosticErrorLogger.Log("Authenticator token", ex, "The Steam time synchronization request failed.");
                return;
            }

            currentSteamChunk = steamTime / 30L;
            int secondsUntilChange = (int)(steamTime - (currentSteamChunk * 30L));

            loadAccountInfo();
            if (currentAccount != null)
            {
                int val = 30 - secondsUntilChange;
                if (pbTimeout != null) pbTimeout.Value = val;
                if (astroProgressBar != null) astroProgressBar.Value = val;

                _ = ExecuteScriptSafelyAsync($"updateProgressBar({val})", "Authenticator progress UI");
            }
        }

        private async void timerTradesPopup_Tick(object sender, EventArgs e)
        {
            if (manifest == null) return;
            if (TryGetTradeRateLimitMessage(out _)) return;
            PersistExpiredRefreshTokenStates();
            SteamGuardAccount[] accountsToMonitor = (allAccounts ?? Array.Empty<SteamGuardAccount>())
                .Where(IsSessionAvailableForAccountOperations)
                .ToArray();
            if (accountsToMonitor.Length == 0)
                return;

            if (!confirmationsSemaphore.Wait(0))
            {
                return; //Only one thread may access this critical section at once. Mutex is a bad choice here because it'll cause a pileup of threads.
            }

            tradeMonitoringAccountIndex = ((tradeMonitoringAccountIndex % accountsToMonitor.Length) + accountsToMonitor.Length) % accountsToMonitor.Length;
            SteamGuardAccount account = accountsToMonitor[tradeMonitoringAccountIndex];
            bool advanceQueue = true;

            try
            {
                lblStatus.Text = "Checking confirmations...";
                if (!IsSessionAvailableForAccountOperations(account))
                {
                    lblStatus.Text = "";
                    InvalidateTradeConfirmationCache(account);
                    UpdateTradePendingCount(pendingTradeConfirmationCounts.Values.Sum());
                    return;
                }

                Confirmation[] confirmations = await FetchTradeConfirmationsForMonitorAsync(account, lifetimeCancellationSource.Token) ?? Array.Empty<Confirmation>();
                lblStatus.Text = "";
                if (!IsSessionAvailableForAccountOperations(account))
                    return;
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
                    bool accepted = await RunAvailableSteamAccountOperationAsync(account, async () =>
                    {
                        if (account.Session.IsAccessTokenExpired())
                            await RefreshAndPersistAccessTokenAsync(account, lifetimeCancellationSource.Token);
                        return await account.AcceptMultipleConfirmations(autoAcceptConfirmations.ToArray(), lifetimeCancellationSource.Token);
                    }, lifetimeCancellationSource.Token);
                    if (!accepted)
                        throw new InvalidOperationException("Steam did not accept the automatic confirmation action. It will remain pending for a later scan.");
                }
            }
            catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
            {
                advanceQueue = false;
            }
            catch (TradeRateLimitedException ex)
            {
                lblStatus.Text = "";
                advanceQueue = false;
                DiagnosticErrorLogger.Log("Trade confirmation monitor", ex, "Steam rate limited the queued confirmation monitor.");
            }
            catch (SessionUnavailableException)
            {
                lblStatus.Text = "";
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
            catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
            {
                // The form is closing; cancellation is expected.
            }
            catch (SessionUnavailableException)
            {
                // Another operation invalidated this account while it was queued.
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
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            if (cancellationToken.IsCancellationRequested)
                return;
            if (TryGetLoginRateLimitMessage(out _))
                return;
            PersistExpiredRefreshTokenStates();
            if (!allAccounts.Any(IsSessionAvailableForAccountOperations))
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
                    LoginApprovalFetchResult result = await RunAvailableSteamAccountOperationAsync(account,
                        () => loginApprovalService.FetchPendingRequestsAsync(account, knownRequests, cancellationToken), cancellationToken,
                        fetchResult =>
                        {
                            if (fetchResult.ErrorKind == LoginApprovalErrorKind.SessionExpired || fetchResult.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                                MarkSessionRenewalRequired(account, fetchResult.ErrorMessage);
                        });
                    if (result.ErrorKind == LoginApprovalErrorKind.SessionExpired || result.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                    {
                        continue;
                    }

                    if (!IsSessionAvailableForAccountOperations(account))
                        continue;

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
                        if (!IsSessionAvailableForAccountOperations(account))
                            break;
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
                                    currentDeviceIp = await GetCurrentPublicIpv4Async(cancellationToken);
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

                        LoginApprovalActionResult actionResult = await RunAvailableSteamAccountOperationAsync(account,
                            () => loginApprovalService.RespondAsync(account, request, decision, cancellationToken), cancellationToken,
                            response =>
                            {
                                if (response.ErrorKind == LoginApprovalErrorKind.SessionExpired || response.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                                    MarkSessionRenewalRequired(account, response.ErrorMessage);
                            });
                        if (actionResult.ErrorKind == LoginApprovalErrorKind.RateLimited)
                        {
                            ApplyLoginRateLimit();
                            ScheduleLoginAccountScan(account, LoginMonitorMaximumFailureBackoff, true);
                            DiagnosticErrorLogger.Log("Login action monitor", new InvalidOperationException(actionResult.ErrorMessage), "Steam rate limited an automatic login action.");
                            return;
                        }
                        if (actionResult.ErrorKind == LoginApprovalErrorKind.SessionExpired || actionResult.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                        {
                            break;
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

                    if (!IsSessionAvailableForAccountOperations(account))
                        continue;
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
                TryBeginInvoke(() => NotifyLoginAction(title, message, icon));
                return;
            }
            if (IsDisposed || Disposing)
                return;

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
                TryBeginInvoke(() => ShowDesktopLoginNotification(title, message, icon));
                return;
            }
            if (IsDisposed || Disposing)
                return;

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
                TryBeginInvoke(() => NotifyTradeConfirmation(account, confirmation));
                return;
            }
            if (IsDisposed || Disposing || account == null || confirmation == null)
                return;

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
                TryBeginInvoke(() => UpdateTradePendingCount(count));
                return;
            }
            if (IsDisposed || Disposing)
                return;

            _ = ExecuteScriptSafelyAsync("setTradePendingCount(" + Math.Max(0, count).ToString() + ");", "Trade confirmation badge");
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
            else if (account?.Session != null && !account.Session.IsRefreshTokenExpired())
                ClearSessionRecoveryState(account);
            return saveResult.Succeeded;
        }

        private bool IsSessionRenewalRequired(SteamGuardAccount account)
        {
            if (account?.Session == null || account.Session.SteamID == 0)
                return true;

            ulong steamId = account.Session.SteamID;
            lock (sessionStateLock)
            {
                return sessionRenewalRequired.Contains(steamId) ||
                    manifest?.IsSessionMarkedForRenewal(steamId) == true ||
                    account.Session.IsRefreshTokenExpired();
            }
        }

        private bool IsSessionAvailableForAccountOperations(SteamGuardAccount account)
        {
            return account?.Session != null && !IsSessionRenewalRequired(account);
        }

        private SteamGuardAccount FindAccountBySelectionKey(string selectionKey)
        {
            if (String.IsNullOrWhiteSpace(selectionKey) || selectionKey == "all" || allAccounts == null)
                return null;
            if (ulong.TryParse(selectionKey, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) && steamId != 0)
                return allAccounts.FirstOrDefault(account => account.Session?.SteamID == steamId);
            return allAccounts.FirstOrDefault(account => String.Equals(account.AccountName, selectionKey, StringComparison.Ordinal));
        }

        private void ClearSessionRecoveryState(SteamGuardAccount account)
        {
            if (account?.Session == null || account.Session.SteamID == 0)
                return;

            lock (sessionStateLock)
            {
                ulong steamId = account.Session.SteamID;
                sessionRenewalRequired.Remove(steamId);
                startupDeferredSessionRenewals.Remove(steamId);
                loginMonitorSchedules.Remove(steamId);
                unavailableLoginAccounts.Remove(account.AccountName);
            }
        }

        private void MarkSessionRenewalRequired(SteamGuardAccount account, string reason, SessionData expectedSession = null)
        {
            if (account == null)
                return;

            lock (sessionStateLock)
            {
                SessionData currentSession = account.Session;
                if (currentSession == null || currentSession.SteamID == 0 ||
                    (expectedSession != null && !Object.ReferenceEquals(currentSession, expectedSession)))
                    return;

                ulong steamId = currentSession.SteamID;

                sessionRenewalRequired.Add(steamId);
                loginMonitorSchedules.Remove(steamId);
                loginMonitoringAccountIndex = 0;
                tradeMonitoringAccountIndex = 0;
                unavailableLoginAccounts.Remove(account.AccountName);
                notifiedUnavailableLoginAccounts.Remove(account.AccountName);
                foreach (string requestKey in pendingLoginRequests.Keys
                    .Where(key => key.StartsWith(steamId.ToString(CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal))
                    .ToArray())
                {
                    pendingLoginRequests.Remove(requestKey);
                    PruneLoginRequestBookkeeping(requestKey);
                }

                if (manifest != null && !manifest.IsSessionMarkedForRenewal(steamId))
                {
                    StorageResult stateResult = manifest.SetSessionNeedsRenewal(steamId, true);
                    if (!stateResult.Succeeded)
                    {
                        DiagnosticErrorLogger.Log("Session recovery", stateResult.Exception, "The account's unavailable session state could not be persisted.");
                    }
                }

                InvalidateTradeConfirmationCache(account);
                if (currentAccount?.Session?.SteamID == steamId)
                {
                    btnLoginViaQr.Enabled = false;
                    menuDeactivateAuthenticator.Enabled = false;
                }
                if (GetCoreWebView2IfAvailable() != null)
                {
                    TryBeginInvoke(() =>
                    {
                        // Resolve the current selection when the queued refresh runs,
                        // in case the user selected another account in the meantime.
                        loadAccountsList();
                        _ = PublishCachedLoginActionsAsync();
                    });
                }
            }
        }

        private void PersistExpiredRefreshTokenStates()
        {
            foreach (SteamGuardAccount account in allAccounts ?? Array.Empty<SteamGuardAccount>())
            {
                SessionData session = account?.Session;
                if (session != null && session.IsRefreshTokenExpired())
                    MarkSessionRenewalRequired(account, "The refresh token is expired or malformed.", session);
            }
        }

        private static bool IsTransientSessionFailure(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamSessionException sessionException &&
                    (sessionException.Kind == SteamSessionFailureKind.RateLimited || sessionException.Kind == SteamSessionFailureKind.Transient))
                    return true;
                if (current is SteamWebRequestException steamWebException &&
                    (steamWebException.StatusCode == HttpStatusCode.Unauthorized || steamWebException.StatusCode == HttpStatusCode.Forbidden))
                {
                    continue;
                }
                if (current is WebException webException && webException.Response is HttpWebResponse httpResponse &&
                    (httpResponse.StatusCode == HttpStatusCode.Unauthorized || httpResponse.StatusCode == HttpStatusCode.Forbidden))
                {
                    continue;
                }
                if (current is HttpRequestException || current is WebException || current is TimeoutException || current is TaskCanceledException)
                    return true;

                string message = current.Message ?? String.Empty;
                if (message.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("temporarily unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("could not be reached", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsDefinitiveSessionFailure(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SessionUnavailableException)
                    return true;
                // Structured endpoint classification takes precedence over error
                // text, which can describe a denied action rather than credentials.
                if (current is SteamSessionException sessionException)
                    return sessionException.Kind == SteamSessionFailureKind.InvalidSession;
                if (current is SteamWebRequestException steamWebException &&
                    steamWebException.StatusCode == HttpStatusCode.Unauthorized)
                    return true;
                if (current is WebException webException && webException.Response is HttpWebResponse httpResponse &&
                    httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                    return true;
            }

            if (IsTransientSessionFailure(exception))
                return false;

            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamGuardAccount.WGTokenInvalidException || current is SteamGuardAccount.WGTokenExpiredException)
                    return true;

                string message = current.Message ?? String.Empty;
                if (message.IndexOf("needs authentication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("authorization expired", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("refresh token is expired", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("refresh token is empty", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("invalid token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private SteamGuardAccount GetNextLoginAccountToMonitor()
        {
            SteamGuardAccount[] accountsToMonitor = (allAccounts ?? Array.Empty<SteamGuardAccount>())
                .Where(IsSessionAvailableForAccountOperations)
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
            if (!IsSessionAvailableForAccountOperations(account))
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
            if (!IsSessionAvailableForAccountOperations(account))
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
                : allAccounts.Where(account => account.Session?.SteamID.ToString(CultureInfo.InvariantCulture) == tradeAccountSelection || account.AccountName == tradeAccountSelection);
            return accounts
                .Where(IsSessionAvailableForAccountOperations)
                .All(account => loadedTradeConfirmationAccounts.Contains(account.Session.SteamID));
        }

        private async Task PublishCachedTradesAsync(string errorMessage = null, string selection = null)
        {
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView == null)
                return;

            string selectionToPublish = String.IsNullOrWhiteSpace(selection) ? tradeAccountSelection : selection;
            IEnumerable<LoadedTradeConfirmation> entries = loadedTradeConfirmations.Values
                .Where(entry => IsSessionAvailableForAccountOperations(entry.Account));
            if (selectionToPublish != "all")
            {
                entries = entries.Where(entry => entry.Account.Session?.SteamID.ToString(CultureInfo.InvariantCulture) == selectionToPublish ||
                    String.Equals(entry.Account.AccountName, selectionToPublish, StringComparison.Ordinal));
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
            try
            {
                await coreWebView.ExecuteScriptAsync($"loadConfirmations({jsonStr}, {jsError}, {revision}, {jsSelection})");
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Trade confirmation UI", ex, "The trade confirmation list could not be sent to the UI.");
            }
        }

        private async Task LoadCachedTradesAsync(string selectedAccountName)
        {
            if (!String.IsNullOrWhiteSpace(selectedAccountName))
                tradeAccountSelection = selectedAccountName;

            SteamGuardAccount selectedAccount = FindAccountBySelectionKey(tradeAccountSelection);
            if (selectedAccount != null && !IsSessionAvailableForAccountOperations(selectedAccount))
                tradeAccountSelection = "all";

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

        private static async Task<string> GetCurrentPublicIpv4Async(CancellationToken cancellationToken = default)
        {
            try
            {
                using (var client = ProxyService.CreateActiveHttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    string candidate = (await client.GetStringAsync("https://api.ipify.org", cancellationToken)).Trim();
                    return IsValidIpv4Address(candidate) ? candidate : null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private void OpenLoginActionsFromTray()
        {
            trayRestore_Click(this, EventArgs.Empty);
            _ = ExecuteScriptSafelyAsync("switchTab('login-actions');", "Login actions navigation");
        }

        private void OpenTradeConfirmationsFromNotification()
        {
            trayRestore_Click(this, EventArgs.Empty);
            _ = ExecuteScriptSafelyAsync("switchTab('trades');", "Trade confirmations navigation");
        }

        private async Task FetchLoginActionsForManualRefreshAsync()
        {
            if (manifest == null || loginApprovalService == null || allAccounts == null)
                return;
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            if (cancellationToken.IsCancellationRequested)
                return;
            if (TryGetLoginRateLimitMessage(out _))
                return;
            PersistExpiredRefreshTokenStates();
            if (!allAccounts.Any(IsSessionAvailableForAccountOperations))
                return;
            if (!await loginActionsSemaphore.WaitAsync(0))
                return;

            try
            {
                foreach (SteamGuardAccount account in allAccounts)
                {
                    if (!IsSessionAvailableForAccountOperations(account))
                        continue;

                    LoginApprovalFetchResult result;
                    try
                    {
                        result = await RunAvailableSteamAccountOperationAsync(account,
                            () => loginApprovalService.FetchPendingRequestsAsync(account, cancellationToken: cancellationToken), cancellationToken,
                            fetchResult =>
                            {
                                if (fetchResult.ErrorKind == LoginApprovalErrorKind.SessionExpired || fetchResult.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                                    MarkSessionRenewalRequired(account, fetchResult.ErrorMessage);
                            });
                    }
                    catch (SessionUnavailableException)
                    {
                        continue;
                    }
                    if (result.ErrorKind == LoginApprovalErrorKind.SessionExpired || result.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                    {
                        continue;
                    }

                    if (!IsSessionAvailableForAccountOperations(account))
                        continue;

                    if (result.ErrorKind == LoginApprovalErrorKind.RateLimited)
                    {
                        ApplyLoginRateLimit();
                        DiagnosticErrorLogger.Log("Login action refresh", new InvalidOperationException(result.ErrorMessage), "Steam rate limited the manual login-request refresh.");
                        return;
                    }

                    if (result.ErrorKind != LoginApprovalErrorKind.None)
                        continue;

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
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView == null)
                return;

            var jsonSettings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            string json = JsonConvert.SerializeObject(new
            {
                revision = Interlocked.Increment(ref loginViewRevision),
                requests = pendingLoginRequests.Values
                    .Where(request => allAccounts?.Any(account => account?.Session?.SteamID == request.SteamId && IsSessionAvailableForAccountOperations(account)) == true)
                    .Select(request => new
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
                unavailableAccounts = unavailableLoginAccounts.Select(account => new
                {
                    accountName = account.Key,
                    steamId = allAccounts?.FirstOrDefault(candidate => String.Equals(candidate.AccountName, account.Key, StringComparison.Ordinal))?.Session?.SteamID.ToString(CultureInfo.InvariantCulture) ?? String.Empty,
                    reason = account.Value
                }).Where(account => !String.IsNullOrWhiteSpace(account.steamId)),
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
            try
            {
                await coreWebView.ExecuteScriptAsync("loadLoginActions(" + json + ");");
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Login actions UI", ex, "The login-action list could not be returned to the UI.");
            }
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
            catch (SessionUnavailableException)
            {
                // The account was invalidated while waiting for its operation lock.
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

        private async Task RespondToLoginActionAsync(ulong steamId, ulong clientId, string action)
        {
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (manifest.LoginActionMode != LoginActionModes.Manual)
                {
                    AstroMessageBox.Show("Manual actions are disabled while an automatic login action is enabled.", "Login Actions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    await PublishCachedLoginActionsAsync();
                    return;
                }

                SteamGuardAccount account = allAccounts?.FirstOrDefault(item => item.Session?.SteamID == steamId && IsSessionAvailableForAccountOperations(item));
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
                    return;

                await loginActionsSemaphore.WaitAsync(cancellationToken);
                try
                {
                    LoginApprovalActionResult result = await RunAvailableSteamAccountOperationAsync(account,
                        () => loginApprovalService.RespondAsync(account, request, decision, cancellationToken), cancellationToken,
                        response =>
                        {
                            if (response.ErrorKind == LoginApprovalErrorKind.SessionExpired || response.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                                MarkSessionRenewalRequired(account, response.ErrorMessage);
                        });
                    if (!result.Succeeded && (result.ErrorKind == LoginApprovalErrorKind.SessionExpired || result.ErrorKind == LoginApprovalErrorKind.Unauthorized))
                    {
                        pendingLoginRequests.Remove(BuildLoginRequestKey(request.SteamId, request.ClientId));
                    }
                    else if (!result.Succeeded && result.ErrorKind != LoginApprovalErrorKind.ExpiredOrDuplicate)
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The form is closing; cancellation is expected.
            }
            catch (SessionUnavailableException)
            {
                AstroMessageBox.Show("This account session is no longer available. Renew it before handling login requests.", "Login Actions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await PublishCachedLoginActionsAsync();
            }
            finally
            {
                await HideWebSpinnerAsync("login-action");
            }
        }

        private static bool TryGetQrClientId(string text, out ulong clientId)
        {
            clientId = 0;
            if (String.IsNullOrWhiteSpace(text) ||
                !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri uri) ||
                !String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                uri.IsDefaultPort == false && uri.Port != 443)
            {
                return false;
            }

            string[] allowedHosts = { "steamcommunity.com", "s.team", "steampowered.com" };
            if (!allowedHosts.Any(host => String.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)))
                return false;

            string[] pathSegments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return pathSegments.Length > 0 &&
                UInt64.TryParse(pathSegments[pathSegments.Length - 1], NumberStyles.None, CultureInfo.InvariantCulture, out clientId) &&
                clientId != 0;
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
        private async Task<bool> PromptRefreshLoginAsync(SteamGuardAccount account)
        {
            if (account == null)
            {
                AstroMessageBox.Show("Please select an account first.", "Login Again", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            return await RunSteamAccountOperationAsync(account, () =>
            {
                using (LoginForm loginForm = new LoginForm(LoginForm.LoginType.Refresh, account, passKey))
                {
                    loginForm.ShowDialog(this);
                    bool succeeded = loginForm.RefreshSucceeded;
                    if (succeeded && account.Session != null)
                    {
                        ClearSessionRecoveryState(account);
                        InvalidateTradeConfirmationCache(account);
                    }
                    return Task.FromResult(succeeded);
                }
            });
        }

        /// <summary>
        /// Load UI with the current account info, this is run every second
        /// </summary>
        private void loadAccountInfo()
        {
            if (currentAccount != null)
            {
                string token = steamTime == 0 ? txtLoginToken.Text : currentAccount.GenerateSteamGuardCodeForTime(steamTime);
                if (steamTime != 0)
                    txtLoginToken.Text = token;
                groupAccount.Text = "Account: " + currentAccount.AccountName;

                string jsToken = JsonConvert.SerializeObject(token ?? String.Empty);
                string jsAccountName = JsonConvert.SerializeObject(currentAccount.AccountName ?? String.Empty);
                string jsSteamId = JsonConvert.SerializeObject(currentAccount.Session?.SteamID.ToString(CultureInfo.InvariantCulture) ?? String.Empty);
                string jsExpired = IsSessionRenewalRequired(currentAccount).ToString().ToLowerInvariant();
                _ = ExecuteScriptSafelyAsync($"updateToken({jsToken})", "Authenticator token UI");
                _ = ExecuteScriptSafelyAsync($"updateCurrentAccount({jsAccountName}, {jsExpired}, {jsSteamId})", "Current account UI");
            }
        }

        /// <summary>
        /// Decrypts files and populates list UI with accounts
        /// </summary>
        private void loadAccountsList(ulong? preferredSteamId = null)
        {
            ulong? previousSteamId = preferredSteamId ?? currentAccount?.Session?.SteamID;
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
            foreach (ulong steamId in loginMonitorSchedules.Keys.Where(steamId => !activeSteamIds.Contains(steamId)).ToArray())
                loginMonitorSchedules.Remove(steamId);
            loadedTradeConfirmationAccounts.RemoveWhere(steamId => !activeSteamIds.Contains(steamId));
            lock (sessionStateLock)
            {
                sessionRenewalRequired.RemoveWhere(steamId => !activeSteamIds.Contains(steamId));
            }
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

                listAccounts.Sorted = true;
                trayAccountList.Sorted = true;

                string preferredName = previousSteamId.HasValue
                    ? allAccounts.FirstOrDefault(account => account.Session?.SteamID == previousSteamId.Value)?.AccountName
                    : null;
                int selectedIndex = !String.IsNullOrWhiteSpace(preferredName)
                    ? listAccounts.Items.IndexOf(preferredName)
                    : -1;
                if (selectedIndex < 0)
                    selectedIndex = 0;
                listAccounts.SelectedIndex = selectedIndex;
                trayAccountList.SelectedIndex = Math.Min(selectedIndex, trayAccountList.Items.Count - 1);
            }
            bool hasAccounts = allAccounts.Length > 0;
            menuDeactivateAuthenticator.Enabled = btnTradeConfirmations.Enabled = btnCopy.Enabled = hasAccounts;
            menuDeactivateAuthenticator.Enabled = hasAccounts && IsSessionAvailableForAccountOperations(currentAccount);
            btnLoginViaQr.Enabled = hasAccounts && IsSessionAvailableForAccountOperations(currentAccount);

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

            if (GetCoreWebView2IfAvailable() != null)
            {
                var accounts = allAccounts.Select(a => new
                {
                    name = a.AccountName,
                    steamId = a.Session == null ? null : a.Session.SteamID.ToString(CultureInfo.InvariantCulture),
                    sessionExpired = IsSessionRenewalRequired(a),
                    needsRenewal = IsSessionRenewalRequired(a)
                }).ToArray();
                string jsonAccounts = JsonConvert.SerializeObject(accounts);
                _ = ExecuteScriptSafelyAsync($"updateAccountList({jsonAccounts})", "Account list UI");
                _ = ExecuteScriptSafelyAsync($"updateEncryptionState({manifest.Encrypted.ToString().ToLowerInvariant()}, {hasAccounts.ToString().ToLowerInvariant()})", "Encryption state UI");
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
            if (manifest == null || backgroundServicesStarted || !startupAccountMaintenanceFinished)
                return;

            backgroundServicesStarted = true;
            timerSteamGuard.Enabled = true;
            timerSteamGuard_Tick(this, EventArgs.Empty);
            loadSettings();
            ConfigureLoginActionsMonitor();
            checkForUpdates();
        }

        // Logic for version checking
        private Version newVersion = null;
        private Version currentVersion = null;
        private static readonly HttpClient updateClient = new HttpClient();
        private const int MaximumUpdateResponseBytes = 1024 * 1024;
        private static readonly TimeSpan UpdateRequestTimeout = TimeSpan.FromSeconds(20);
        private string updateUrl = null;
        private bool startupUpdateCheck = true;
        private bool isCheckingForUpdates = false;

        private async void checkForUpdates()
        {
            if (isCheckingForUpdates) return;
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            if (cancellationToken.IsCancellationRequested)
                return;
            
            if (startupUpdateCheck && !Manifest.GetManifest().CheckForUpdates)
            {
                startupUpdateCheck = false;
                return;
            }

            isCheckingForUpdates = true;

            try
            {
                using (CancellationTokenSource updateTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/AstroZer01/Astro-Steam-Desktop-Authenticator/releases/latest"))
                {
                    updateTimeoutSource.CancelAfter(UpdateRequestTimeout);
                    CancellationToken requestCancellationToken = updateTimeoutSource.Token;
                    request.Headers.Add("User-Agent", "Astro Steam Desktop Assistant");
                    request.Headers.Add("Accept", "application/json");
                
                    using (HttpResponseMessage response = await updateClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content == null || response.Content.Headers.ContentLength > MaximumUpdateResponseBytes)
                            throw new InvalidDataException("The update service returned an oversized response.");
                
                        string responseBody = await ReadResponseBodyWithLimitAsync(response.Content, requestCancellationToken);

                        JObject resultObject;
                        using (StringReader stringReader = new StringReader(responseBody))
                        using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                        {
                            resultObject = JObject.Load(jsonReader);
                        }

                        string tagName = resultObject.Value<string>("tag_name")?.Trim();
                        if (String.IsNullOrWhiteSpace(tagName) || tagName.Length > 64)
                            throw new InvalidDataException("The update service returned an invalid version.");
                        if (tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                            tagName = tagName.Substring(1);
                        if (!Version.TryParse(tagName, out Version parsedVersion) || parsedVersion < new Version(0, 0))
                            throw new InvalidDataException("The update service returned an invalid version.");

                        JArray assets = resultObject["assets"] as JArray;
                        string downloadUrl = assets?.OfType<JObject>()
                            .Select(asset => asset.Value<string>("browser_download_url"))
                            .FirstOrDefault(IsTrustedUpdateUrl);
                        if (String.IsNullOrWhiteSpace(downloadUrl))
                            throw new InvalidDataException("The update service returned no trusted download.");

                        newVersion = parsedVersion;
                        currentVersion = new Version(Application.ProductVersion);
                        updateUrl = downloadUrl;
                        compareVersions();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The form is closing; cancellation is expected.
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
                    try
                    {
                        using (Process process = Process.Start(new ProcessStartInfo(updateUrl) { UseShellExecute = true })
                            ?? throw new InvalidOperationException("Windows did not create the update process."))
                        {
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Application update", ex, "The trusted update page could not be opened.");
                        AstroMessageBox.Show("The update page could not be opened. Visit the project release page to download the update.", "Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
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
        private string trustedUiDocumentPath;

        private CoreWebView2 GetCoreWebView2IfAvailable()
        {
            if (IsDisposed || Disposing || webView == null)
                return null;

            try
            {
                return webView.CoreWebView2;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                return null;
            }
        }

        private bool TryBeginInvoke(MethodInvoker action)
        {
            if (action == null || IsDisposed || Disposing || !IsHandleCreated)
                return false;

            try
            {
                BeginInvoke(action);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private async Task ExecuteScriptSafelyAsync(string script, string operation)
        {
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView == null)
                return;

            try
            {
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

            loadingTimer = new System.Windows.Forms.Timer();
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
                if (!IsDisposed && !Disposing)
                    lblLoading.Text = "Astro UI could not be initialized. Restore the complete release folder and try again.";
                DiagnosticErrorLogger.Log("Astro UI", ex, "The dashboard could not be initialized.");
                return;
            }

            if (IsDisposed || lifetimeCancellationSource.IsCancellationRequested || webView?.CoreWebView2 == null)
                return;

            string htmlPath = Path.GetFullPath(Path.Combine(ApplicationPaths.UiDirectory, "index.html"));
            trustedUiDocumentPath = htmlPath;

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            webView.NavigationStarting += (sender, args) =>
            {
                if (WebViewSecurityPolicy.IsTrustedLocalDocument(args.Uri, htmlPath))
                    return;

                args.Cancel = true;
                DiagnosticErrorLogger.Log(
                    "Astro UI",
                    new InvalidOperationException("An untrusted WebView2 navigation was blocked."),
                    "The dashboard attempted to navigate away from its packaged local document.");
            };

            // Wire up message receiving from JS
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            // When navigation is done, we swap out the old UI for the new one
            webView.NavigationCompleted += (sender, args) =>
            {
                if (IsDisposed || lifetimeCancellationSource.IsCancellationRequested || webView?.CoreWebView2 == null)
                    return;
                if (!args.IsSuccess)
                {
                    loadingTimer.Stop();
                    loadingTimer.Dispose();
                    if (!IsDisposed && !Disposing)
                        lblLoading.Text = "Astro UI could not be loaded. Restore the complete release folder and try again.";
                    DiagnosticErrorLogger.Log("Astro UI", new InvalidOperationException("WebView2 navigation failed: " + args.WebErrorStatus), "The dashboard could not be loaded.");
                    return;
                }
                if (!WebViewSecurityPolicy.IsTrustedLocalDocument(webView.CoreWebView2.Source, htmlPath))
                {
                    loadingTimer.Stop();
                    loadingTimer.Dispose();
                    if (!IsDisposed && !Disposing)
                        lblLoading.Text = "Astro UI could not be loaded. Restore the complete release folder and try again.";
                    DiagnosticErrorLogger.Log(
                        "Astro UI",
                        new InvalidOperationException("WebView2 completed navigation to an untrusted document."),
                        "The dashboard refused to expose account data to an untrusted document.");
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
                string jsVersion = JsonConvert.SerializeObject(Application.ProductVersion);
                _ = ExecuteScriptSafelyAsync($"setAppVersion({jsVersion});", "Application version UI");
                
                // Set autostart checkbox
                bool isAutoStart = WindowsStartup.IsEnabled();
                _ = ExecuteScriptSafelyAsync($"setAutoStart({isAutoStart.ToString().ToLowerInvariant()});", "Startup setting UI");

                StartBackgroundServicesAfterUiReady();
            };

            if (IsDisposed || lifetimeCancellationSource.IsCancellationRequested || webView == null)
                return;
            webView.Source = new Uri(trustedUiDocumentPath);
        }

        private void SendSettingsToWebView()
        {
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView == null || manifest == null)
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
            settings["proxyEnabled"] = manifest.ProxyEnabled;
            settings["proxyScheme"] = manifest.ProxyScheme;
            settings["proxyHost"] = manifest.ProxyHost;
            settings["proxyPort"] = manifest.ProxyPort;
            settings["proxyUsername"] = manifest.ProxyUsername;
            settings["proxyHasPassword"] = !String.IsNullOrEmpty(manifest.ProxyPassword);

            _ = ExecuteScriptSafelyAsync($"loadSettings({settings.ToString(Newtonsoft.Json.Formatting.None)})", "Settings UI");
        }

        private async Task PublishProxyTestResultAsync(ProxyTestResult result)
        {
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView == null || result == null)
                return;
            JObject payload = new JObject
            {
                ["succeeded"] = result.Succeeded,
                ["message"] = result.Message ?? String.Empty,
                ["exitIp"] = result.ExitIp
            };
            try
            {
                await coreWebView.ExecuteScriptAsync($"proxyTestCompleted({payload.ToString(Newtonsoft.Json.Formatting.None)});");
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Proxy settings UI", ex, "The proxy test result could not be returned to the UI.");
            }
        }

        private async Task PublishSettingsSaveFailureAsync(string message)
        {
            exitAfterSettingsSaveRequested = false;
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView == null)
                return;
            string jsMessage = JsonConvert.SerializeObject(message ?? "Settings were not saved.");
            try
            {
                await coreWebView.ExecuteScriptAsync($"settingsSaveFailed({jsMessage});");
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Settings UI", ex, "The settings-save failure could not be returned to the UI.");
            }
        }

        private CancellationTokenSource BeginProxyOperation()
        {
            CancellationTokenSource replacement = new CancellationTokenSource();
            CancellationTokenSource previous = Interlocked.Exchange(ref proxyTestCancellationSource, replacement);
            try
            {
                previous?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation completed between the swap and cancellation request.
            }
            return replacement;
        }

        private void CancelProxyOperation()
        {
            try
            {
                Volatile.Read(ref proxyTestCancellationSource)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation completed before it could be canceled.
            }
        }

        private void CompleteProxyOperation(CancellationTokenSource source)
        {
            if (source == null)
                return;

            Interlocked.CompareExchange(ref proxyTestCancellationSource, null, source);
            source.Dispose();
        }

        private async Task TestProxyFromPayloadAsync(JObject payload)
        {
            CancellationTokenSource operationSource = BeginProxyOperation();
            CancellationToken token = operationSource.Token;

            try
            {
                if (!ProxyConfiguration.TryFromPayload(payload, manifest, true, out ProxyConfiguration configuration, out string error))
                {
                    await PublishProxyTestResultAsync(new ProxyTestResult { Succeeded = false, Message = error });
                    return;
                }

                ProxyTestResult result = await ProxyService.TestAsync(configuration, token);
                if (!token.IsCancellationRequested)
                    await PublishProxyTestResultAsync(result);
            }
            catch (OperationCanceledException)
            {
                // Form shutdown or a newer test superseded this request.
            }
            finally
            {
                CompleteProxyOperation(operationSource);
            }
        }

        private async Task SaveSettingsAsync(JObject payload)
        {
            if (settingsSaveInProgress || manifest == null)
                return;

            settingsSaveInProgress = true;
            string saveContext = (string)payload["saveContext"] ?? String.Empty;
            CancellationTokenSource proxySaveSource = null;
            try
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
                    await PublishSettingsSaveFailureAsync("Enter a valid additional public IPv4 address, or leave it blank to use only the current-device option.");
                    return;
                }
                else if (!newLoginActionAutoAllowIpEnabled)
                {
                    newLoginActionAutoAllowCurrentDeviceIp = false;
                }

                if (!ProxyConfiguration.TryFromPayload(payload, manifest, false, out ProxyConfiguration proxyConfiguration, out string proxyError))
                {
                    await PublishSettingsSaveFailureAsync(proxyError);
                    return;
                }

                if (proxyConfiguration.Enabled)
                {
                    proxySaveSource = BeginProxyOperation();
                    ProxyTestResult proxyResult;
                    try
                    {
                        proxyResult = await ProxyService.TestAsync(proxyConfiguration, proxySaveSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        await PublishSettingsSaveFailureAsync("The proxy test was canceled. Settings were not saved.");
                        return;
                    }
                    if (!proxyResult.Succeeded)
                    {
                        await PublishSettingsSaveFailureAsync(proxyResult.Message + " Settings were not saved.");
                        return;
                    }
                    proxySaveSource.Token.ThrowIfCancellationRequested();
                }
                else
                {
                    CancelProxyOperation();
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
                        await PublishSettingsSaveFailureAsync("Settings were not saved.");
                        return;
                    }
                }

                bool tradeConfirmationCustomIntervalEnabled = (bool?)payload["tradeConfirmationCustomIntervalEnabled"] ?? false;
                int tradeConfirmationCheckInterval = Math.Clamp((int?)payload["tradeConfirmationCheckInterval"] ?? 15, 3, 3600);
                bool autoConfirmMarket = (bool?)payload["autoConfirmMarket"] ?? false;
                bool autoConfirmTrades = (bool?)payload["autoConfirmTrades"] ?? false;
                bool minimizeToTray = (bool?)payload["minimizeToTray"] ?? false;
                bool diagnosticLogging = (bool?)payload["diagnosticErrorLoggingEnabled"] ?? false;
                bool loginMonitoring = ((bool?)payload["loginActionMonitoringEnabled"] ?? false) || newLoginActionMode != LoginActionModes.Manual;

                if (proxyConfiguration.Enabled)
                    proxySaveSource.Token.ThrowIfCancellationRequested();

                StorageResult saveResult = manifest.SaveSettingsWithResult(staged =>
                {
                    staged.TradeConfirmationCustomIntervalEnabled = tradeConfirmationCustomIntervalEnabled;
                    staged.TradeConfirmationCheckInterval = tradeConfirmationCheckInterval;
                    staged.AutoConfirmMarketTransactions = autoConfirmMarket;
                    staged.AutoConfirmTrades = autoConfirmTrades;
                    staged.MinimizeToTray = minimizeToTray;
                    staged.DiagnosticErrorLoggingEnabled = diagnosticLogging;
                    staged.LoginActionMonitoringEnabled = loginMonitoring;
                    staged.LoginActionMode = newLoginActionMode;
                    staged.LoginActionAutoAllowIpEnabled = newLoginActionAutoAllowIpEnabled;
                    staged.LoginActionAutoAllowCurrentDeviceIp = newLoginActionAutoAllowCurrentDeviceIp;
                    staged.LoginActionAutoAllowIp = newLoginActionAutoAllowIp;
                    staged.ProxyEnabled = proxyConfiguration.Enabled;
                    staged.ProxyScheme = proxyConfiguration.Scheme;
                    staged.ProxyHost = proxyConfiguration.Host;
                    staged.ProxyPort = proxyConfiguration.Port;
                    staged.ProxyUsername = proxyConfiguration.Username;
                    staged.ProxyPassword = proxyConfiguration.Password;
                });
                if (!saveResult.Succeeded)
                {
                    DiagnosticErrorLogger.Log("Settings storage", saveResult.Exception, "Application settings could not be saved atomically.");
                    await PublishSettingsSaveFailureAsync(saveResult.UserMessage ?? "The application settings could not be saved.");
                    return;
                }

                ProxyService.Apply(proxyConfiguration);
                DiagnosticErrorLogger.Configure(manifest.DiagnosticErrorLoggingEnabled);
                ConfigureTradeConfirmationMonitor();
                ConfigureLoginActionsMonitor();
                settingsDirty = false;
                SendSettingsToWebView();
                string jsContext = JsonConvert.SerializeObject(saveContext);
                await ExecuteScriptSafelyAsync($"settingsSaved({jsContext});", "Settings UI");

                if (String.Equals(saveContext, "exit", StringComparison.Ordinal) || exitAfterSettingsSaveRequested)
                {
                    exitAfterSettingsSaveRequested = false;
                    allowExitAfterSettingsSave = true;
                    explicitExitRequested = true;
                    TryBeginInvoke(Close);
                }
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && !Disposing)
                    await PublishSettingsSaveFailureAsync("The proxy test was canceled. Settings were not saved.");
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Settings save", ex, "The settings save pipeline failed.");
                await PublishSettingsSaveFailureAsync("The settings could not be saved. Check the entered values and try again.");
            }
            finally
            {
                CompleteProxyOperation(proxySaveSource);
                settingsSaveInProgress = false;
            }
        }

        private static bool IsTrustedUpdateUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) &&
                String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.Port == 443 &&
                uri.AbsolutePath.StartsWith(
                    "/AstroZer01/Astro-Steam-Desktop-Authenticator/releases/download/",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> ReadResponseBodyWithLimitAsync(HttpContent content, CancellationToken cancellationToken)
        {
            if (content == null)
                throw new InvalidDataException("The update service returned an empty response.");

            using (Stream responseStream = await content.ReadAsStreamAsync())
            using (MemoryStream responseBody = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                int totalBytes = 0;
                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    if (bytesRead > MaximumUpdateResponseBytes - totalBytes)
                        throw new InvalidDataException("The update service returned an oversized response.");
                    responseBody.Write(buffer, 0, bytesRead);
                    totalBytes += bytesRead;
                }

                return new UTF8Encoding(false, true).GetString(responseBody.ToArray());
            }
        }

        private static bool TryGetWebMessageString(JObject payload, string propertyName, int maximumLength, out string value, bool required = false)
        {
            value = null;
            JToken token = payload?[propertyName];
            if (token == null || token.Type == JTokenType.Null)
                return !required;
            if (token.Type != JTokenType.String)
                return false;

            value = token.Value<string>();
            return value != null && value.Length <= maximumLength && (!required || !String.IsNullOrWhiteSpace(value));
        }

        private static bool HasPayloadType(JObject payload, string propertyName, JTokenType expectedType)
        {
            JToken token = payload?[propertyName];
            return token == null || token.Type == JTokenType.Null || token.Type == expectedType;
        }

        private static bool IsSettingsPayloadValid(JObject payload)
        {
            string[] booleanProperties =
            {
                "proxyEnabled", "loginActionAutoAllowIpEnabled", "loginActionAutoAllowCurrentDeviceIp",
                "tradeConfirmationCustomIntervalEnabled", "autoConfirmMarket", "autoConfirmTrades",
                "minimizeToTray", "diagnosticErrorLoggingEnabled", "loginActionMonitoringEnabled"
            };
            foreach (string property in booleanProperties)
            {
                if (!HasPayloadType(payload, property, JTokenType.Boolean))
                    return false;
            }

            if (!HasPayloadType(payload, "tradeConfirmationCheckInterval", JTokenType.Integer) ||
                !HasPayloadType(payload, "proxyPort", JTokenType.Integer))
                return false;

            return TryGetWebMessageString(payload, "saveContext", 32, out _) &&
                TryGetWebMessageString(payload, "loginActionMode", 32, out _) &&
                TryGetWebMessageString(payload, "loginActionAutoAllowIp", 64, out _) &&
                TryGetWebMessageString(payload, "proxyScheme", 16, out _) &&
                TryGetWebMessageString(payload, "proxyHost", 256, out _) &&
                TryGetWebMessageString(payload, "proxyUsername", 256, out _) &&
                TryGetWebMessageString(payload, "proxyPasswordAction", 16, out _) &&
                TryGetWebMessageString(payload, "proxyPassword", 4096, out _);
        }

        private async void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson;
            if (!WebViewSecurityPolicy.IsTrustedLocalDocument(e.Source, trustedUiDocumentPath) ||
                String.IsNullOrEmpty(message) || message.Length > 64 * 1024) return;

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

            if (!TryGetWebMessageString(payload, "action", 64, out string action, true))
                return;

            if (action == "copy_token")
            {
                CopyLoginToken();
            }
            else if (action == "setup_account")
            {
                TryBeginInvoke(() => btnSteamLogin_Click(this, EventArgs.Empty));
            }
            else if (action == "setup_encryption")
            {
                TryBeginInvoke(() => btnManageEncryption_Click(this, EventArgs.Empty));
            }
            else if (action == "import_account")
            {
                TryBeginInvoke(() =>
                {
                    using (ImportAccountForm importForm = new ImportAccountForm(this.passKey))
                    {
                        importForm.ShowDialog(this);
                    }
                    this.loadAccountsList();
                });
            }

            else if (action == "login_qr")
            {
                TryBeginInvoke(() => btnLoginViaQr_Click(this, EventArgs.Empty));
            }
            else if (action == "switch_account")
            {
                if (!TryGetWebMessageString(payload, "accountName", 256, out string accName) ||
                    !TryGetWebMessageString(payload, "steamId", 32, out string steamIdText))
                    return;
                ulong steamId;
                SteamGuardAccount selectedAccount = null;
                if (allAccounts != null && ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out steamId))
                {
                    selectedAccount = allAccounts.FirstOrDefault(account => account.Session != null && account.Session.SteamID == steamId);
                }

                if (allAccounts != null && selectedAccount == null && String.IsNullOrWhiteSpace(steamIdText))
                    selectedAccount = allAccounts.FirstOrDefault(account => account.AccountName == accName);

                if (selectedAccount != null)
                {
                    currentAccount = selectedAccount;
                    loadAccountInfo();
                }
            }
            else if (action == "remove_account")
            {
                if (!TryGetWebMessageString(payload, "steamId", 32, out string steamIdText) ||
                    !TryGetWebMessageString(payload, "accountName", 256, out string accountName) ||
                    !TryGetWebMessageString(payload, "removalMode", 16, out string removalMode, true))
                    return;
                ulong steamId;
                bool hasSteamId = ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out steamId);
                bool validMode = removalMode == "unlink" || removalMode == "remove";
                bool validLocalRemoval = removalMode == "remove" && !String.IsNullOrWhiteSpace(accountName);
                if (validMode && (hasSteamId || validLocalRemoval))
                {
                    ulong? selectedSteamId = hasSteamId ? steamId : (ulong?)null;
                    TryBeginInvoke(() => _ = RemoveManagedAccountFromWebAsync(selectedSteamId, accountName, removalMode));
                }
                else
                {
                    AstroMessageBox.Show("The requested account removal is invalid. Refresh the account list and try again.", "Remove Account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _ = ExecuteScriptSafelyAsync("hideSpinner('remove-account');", "Managed account removal");
                }
            }
            else if (action == "load_settings")
            {
                SendSettingsToWebView();
            }
            else if (action == "save_settings")
            {
                if (!IsSettingsPayloadValid(payload))
                {
                    await PublishSettingsSaveFailureAsync("The settings message was invalid. No changes were saved.");
                    return;
                }
                await SaveSettingsAsync(payload);
            }
            else if (action == "test_proxy")
            {
                if (!IsSettingsPayloadValid(payload))
                {
                    await PublishProxyTestResultAsync(new ProxyTestResult { Succeeded = false, Message = "The proxy settings message was invalid." });
                    return;
                }
                await TestProxyFromPayloadAsync(payload);
            }
            else if (action == "settings_dirty_changed")
            {
                if (!HasPayloadType(payload, "dirty", JTokenType.Boolean))
                    return;
                settingsDirty = (bool?)payload["dirty"] ?? false;
            }
            else if (action == "discard_settings")
            {
                settingsDirty = false;
                SendSettingsToWebView();
            }
            else if (action == "toggle_autostart")
            {
                if (!HasPayloadType(payload, "enabled", JTokenType.Boolean))
                    return;
                bool enable = (bool?)payload["enabled"] ?? false;
                WindowsStartup.SetEnabled(enable);
            }
            else if (action == "active_tab_changed")
            {
                if (!TryGetWebMessageString(payload, "tabName", 32, out string tabName, true))
                    return;
                if (tabName == "authenticator" || tabName == "trades" || tabName == "login-actions" || tabName == "settings")
                    activeWebTab = tabName;
            }
            else if (action == "load_trades_cache")
            {
                if (TryGetWebMessageString(payload, "steamId", 32, out string steamIdText))
                    _ = LoadCachedTradesAsync(steamIdText);
            }
            else if (action == "refresh_trades")
            {
                if (TryGetWebMessageString(payload, "steamId", 32, out string steamIdText))
                    _ = LoadTradesAsync(steamIdText);
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
                if (!TryGetWebMessageString(payload, "steamId", 32, out string steamIdText, true) ||
                    !TryGetWebMessageString(payload, "clientId", 32, out string clientIdText, true) ||
                    !TryGetWebMessageString(payload, "decision", 16, out string decision, true))
                {
                    await HideWebSpinnerAsync("login-action");
                }
                else if (ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) && steamId != 0 &&
                    ulong.TryParse(clientIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong clientId) && clientId != 0 &&
                    (decision == "approve" || decision == "deny"))
                {
                    _ = RespondToLoginActionAsync(steamId, clientId, decision);
                }
                else
                {
                    await HideWebSpinnerAsync("login-action");
                }
            }
            else if (action == "refresh_login_account")
            {
                if (!TryGetWebMessageString(payload, "steamId", 32, out string steamIdText, true) ||
                    !ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) ||
                    steamId == 0)
                    return;
                SteamGuardAccount account = allAccounts?.FirstOrDefault(item => item.Session?.SteamID == steamId);
                if (account != null)
                {
                    await PromptRefreshLoginAsync(account);
                    loadAccountsList(steamId);
                }
            }
            else if (action == "accept_trade" || action == "reject_trade")
            {
                if (!TryGetWebMessageString(payload, "id", 64, out string confirmationKey, true))
                {
                    await CompleteWebTradeActionAsync(null, false);
                }
                else if (TryParseTradeConfirmationKey(confirmationKey, out _))
                    _ = RespondToTradeConfirmationAsync(confirmationKey, action == "accept_trade");
                else
                    await CompleteWebTradeActionAsync(confirmationKey, false);
            }
        }

        private static string BuildTradeConfirmationKey(SteamGuardAccount account, Confirmation confirmation)
        {
            return account.Session.SteamID.ToString() + ":" + confirmation.ID.ToString();
        }

        private async Task RespondToTradeConfirmationAsync(string confirmationKey, bool accept)
        {
            if (!TryParseTradeConfirmationKey(confirmationKey, out _))
                return;
            if (IsTradeActionRecentlyCompleted(confirmationKey))
            {
                await CompleteWebTradeActionAsync(confirmationKey, true);
                return;
            }

            TaskCompletionSource<bool> tradeActionCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!tradeActionsInProgress.TryAdd(confirmationKey, tradeActionCompletion))
            {
                if (tradeActionsInProgress.TryGetValue(confirmationKey, out TaskCompletionSource<bool> existingCompletion))
                {
                    bool existingResult = false;
                    try
                    {
                        existingResult = await existingCompletion.Task;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Trade confirmation action", ex, "A duplicate trade action could not observe the original result.");
                    }
                    await CompleteWebTradeActionAsync(confirmationKey, existingResult);
                }
                else
                {
                    await CompleteWebTradeActionAsync(confirmationKey, IsTradeActionRecentlyCompleted(confirmationKey));
                }
                return;
            }

            bool actionSucceeded = false;
            try
            {
                if (!loadedTradeConfirmations.TryGetValue(confirmationKey, out LoadedTradeConfirmation entry))
                    throw new InvalidOperationException("This confirmation is no longer available. Refresh the list and try again.");

                await confirmationsSemaphore.WaitAsync(lifetimeCancellationSource.Token);
                try
                {
                    bool steamAcceptedAction = await RunAvailableSteamAccountOperationAsync(entry.Account, async () =>
                    {
                        if (entry.Account.Session.IsAccessTokenExpired())
                            await RefreshAndPersistAccessTokenAsync(entry.Account, lifetimeCancellationSource.Token);
                        return accept
                            ? await entry.Account.AcceptConfirmation(entry.Confirmation, lifetimeCancellationSource.Token)
                            : await entry.Account.DenyConfirmation(entry.Confirmation, lifetimeCancellationSource.Token);
                    }, lifetimeCancellationSource.Token);
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
                if (!lifetimeCancellationSource.IsCancellationRequested)
                    AstroMessageBox.Show(ex.Message, "Trade Confirmations", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try
                {
                    if (actionSucceeded)
                    {
                        MarkRecentlyResolved(recentlyResolvedTradeConfirmations, confirmationKey);
                        completedTradeActions[confirmationKey] = DateTime.UtcNow;
                        loadedTradeConfirmations.Remove(confirmationKey);
                        try
                        {
                            await PublishCachedTradesAsync();
                        }
                        catch (Exception ex)
                        {
                            DiagnosticErrorLogger.Log("Trade confirmation action", ex, "The confirmation was resolved, but the trade list could not be refreshed.");
                        }
                    }
                }
                finally
                {
                    tradeActionCompletion.TrySetResult(actionSucceeded);
                    tradeActionsInProgress.TryRemove(confirmationKey, out _);
                await CompleteWebTradeActionAsync(confirmationKey, actionSucceeded);
                    _ = LoadTradesAsync();
                }
            }
        }

        private async Task CompleteWebTradeActionAsync(string confirmationKey, bool succeeded)
        {
            try
            {
                CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
                if (coreWebView == null)
                    return;

                string jsKey = JsonConvert.SerializeObject(confirmationKey ?? String.Empty);
                string jsSucceeded = succeeded.ToString().ToLowerInvariant();
                await coreWebView.ExecuteScriptAsync($"tradeActionCompleted({jsKey}, {jsSucceeded});");
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Trade confirmation action", ex, "The trade action result could not be returned to the UI.");
            }
        }

        private bool IsTradeActionRecentlyCompleted(string confirmationKey)
        {
            DateTime now = DateTime.UtcNow;
            foreach (string expiredKey in completedTradeActions
                .Where(entry => now - entry.Value >= RecentlyResolvedTradeConfirmationsRetention)
                .Select(entry => entry.Key)
                .ToList())
            {
                completedTradeActions.Remove(expiredKey);
            }

            return completedTradeActions.ContainsKey(confirmationKey);
        }

        private static bool TryParseTradeConfirmationKey(string confirmationKey, out ulong steamId)
        {
            steamId = 0;
            if (String.IsNullOrWhiteSpace(confirmationKey) || confirmationKey.Length > 64)
                return false;

            string[] parts = confirmationKey.Split(new[] { ':' }, StringSplitOptions.None);
            return parts.Length == 2 &&
                UInt64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out steamId) && steamId != 0 &&
                UInt64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong confirmationId) && confirmationId != 0;
        }

        private async Task HideWebSpinnerAsync(string operation)
        {
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView != null)
            {
                try
                {
                    await coreWebView.ExecuteScriptAsync("hideSpinner(" + JsonConvert.SerializeObject(operation) + ");");
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Web UI", ex, "The operation spinner could not be updated.");
                }
            }
        }

        private async Task LoadTradesAsync(string selectedAccountName = null)
        {
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            PersistExpiredRefreshTokenStates();
            if (!String.IsNullOrWhiteSpace(selectedAccountName))
                tradeAccountSelection = selectedAccountName;
            SteamGuardAccount selectedAccount = FindAccountBySelectionKey(tradeAccountSelection);
            if (selectedAccount != null && !IsSessionAvailableForAccountOperations(selectedAccount))
                tradeAccountSelection = "all";
            string selectionToLoad = tradeAccountSelection;

            if (allAccounts == null || allAccounts.Length == 0)
            {
                await PublishCachedTradesAsync();
                return;
            }

            SteamGuardAccount[] accountsToLoad = selectionToLoad == "all"
                ? allAccounts.Where(IsSessionAvailableForAccountOperations).ToArray()
                : allAccounts.Where(account => IsSessionAvailableForAccountOperations(account) &&
                    (account.Session.SteamID.ToString(CultureInfo.InvariantCulture) == selectionToLoad || account.AccountName == selectionToLoad)).ToArray();
            if (accountsToLoad.Length == 0 && IsSessionAvailableForAccountOperations(currentAccount))
                accountsToLoad = new[] { currentAccount };
            if (accountsToLoad.Length == 0)
            {
                await PublishCachedTradesAsync();
                return;
            }

            if (cancellationToken.IsCancellationRequested || !await tradeLoadSemaphore.WaitAsync(0))
                return;
            bool confirmationsSemaphoreAcquired = false;
            try
            {
                await confirmationsSemaphore.WaitAsync(cancellationToken);
                confirmationsSemaphoreAcquired = true;

                var unavailableAccounts = new List<string>();
                string rateLimitMessage = null;
                foreach (SteamGuardAccount account in accountsToLoad)
                {
                    try
                    {
                        Confirmation[] accountConfirmations = await FetchTradeConfirmationsForPageAsync(account, cancellationToken);
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
                        if (IsDefinitiveSessionFailure(ex))
                            InvalidateTradeConfirmationCache(account);
                        else
                        {
                            unavailableAccounts.Add(account.AccountName + " could not be loaded.");
                        }
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
            catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
            {
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

                if (!lifetimeCancellationSource.IsCancellationRequested &&
                    !String.Equals(selectionToLoad, tradeAccountSelection, StringComparison.Ordinal))
                    _ = LoadTradesAsync();
            }
        }
    }
}
