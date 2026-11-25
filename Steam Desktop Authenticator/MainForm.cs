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
        private readonly object tradeLoadLock = new object();
        private bool tradeLoadActive;
        private bool tradeLoadScanStarted;
        private long tradeLoadRunnerSequence;
        private long tradeLoadOwnerId;
        private TradeLoadScope activeTradeLoadScope;
        private TradeLoadScope queuedTradeLoadScope;
        private TaskCompletionSource<bool> activeTradeLoadCompletion;
        private TaskCompletionSource<bool> queuedTradeLoadCompletion;
        private readonly SteamAccountOperationCoordinator steamAccountOperationCoordinator = new SteamAccountOperationCoordinator();
        private readonly object accountGenerationLock = new object();
        private readonly object sessionStateLock = new object();
        private readonly Dictionary<string, LoadedTradeConfirmation> loadedTradeConfirmations = new Dictionary<string, LoadedTradeConfirmation>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> tradeActionsInProgress = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private readonly HashSet<ulong> loadedTradeConfirmationAccounts = new HashSet<ulong>();
        private readonly Dictionary<string, string> unavailableLoginAccounts = new Dictionary<string, string>();
        private readonly HashSet<ulong> sessionRenewalRequired = new HashSet<ulong>();
        private readonly HashSet<ulong> startupDeferredSessionRenewals = new HashSet<ulong>();
        private readonly Dictionary<ulong, DateTime> sessionRenewalPersistBackoffUntilUtc = new Dictionary<ulong, DateTime>();
        private static readonly TimeSpan SessionRenewalPersistRetryBackoff = TimeSpan.FromSeconds(30);
        private readonly Dictionary<string, DateTime> recentlyResolvedLoginRequests = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> recentlyResolvedTradeConfirmations = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> completedTradeActions = new Dictionary<string, DateTime>();
        private readonly List<LoginNotificationPopup> activeLoginNotificationPopups = new List<LoginNotificationPopup>();
        private LoginApprovalService loginApprovalService;
        private Action trayNotificationClickAction;
        private string tradeAccountSelection = "all";
        private readonly TradeConfirmationScheduler tradeConfirmationScheduler = new TradeConfirmationScheduler();
        private readonly SilentReloginService silentReloginService = new SilentReloginService();
        private readonly ConcurrentDictionary<ulong, Task> activeSilentReloginTasks = new ConcurrentDictionary<ulong, Task>();
        private readonly ConcurrentDictionary<ulong, byte> transferFencedSteamIds = new ConcurrentDictionary<ulong, byte>();
        private readonly ConcurrentDictionary<ulong, byte> unresolvedTransferMarkerSteamIds = new ConcurrentDictionary<ulong, byte>();
        private readonly object loginRateLimitLock = new object();
        private DateTime loginRateLimitedUntilUtc = DateTime.MinValue;
        private readonly object manualLoginRefreshLock = new object();
        private bool manualLoginRefreshActive;
        private bool manualLoginRefreshScanStarted;
        private bool manualLoginRefreshPublishing;
        private long manualLoginRefreshRunnerSequence;
        private long manualLoginRefreshOwnerId;
        private ManualLoginRefreshScope activeManualLoginRefreshScope;
        private ManualLoginRefreshScope queuedManualLoginRefreshScope;
        private TaskCompletionSource<bool> activeManualLoginRefreshCompletion;
        private TaskCompletionSource<bool> queuedManualLoginRefreshCompletion;
        private readonly Dictionary<ulong, LoginMonitorSchedule> loginMonitorSchedules = new Dictionary<ulong, LoginMonitorSchedule>();
        private int loginMonitoringAccountIndex;
        private long loginMonitorRequestCount;
        private static readonly TimeSpan LoginMonitorSuccessInterval = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan LoginMonitorInitialFailureBackoff = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LoginMonitorMaximumFailureBackoff = TimeSpan.FromMinutes(5);
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
        private bool startupUpdateCheckStarted;
        private bool settingsDirty;
        private bool explicitExitRequested;
        private bool allowExitAfterSettingsSave;
        private bool exitAfterSettingsSaveRequested;
        private bool settingsSaveInProgress;
        private int activeIrreversibleTransferCount;
        private bool exitAfterTransferRequested;
        private bool IsIrreversibleTransferInFlight => Volatile.Read(ref activeIrreversibleTransferCount) > 0;
        private readonly UpdatePreferenceRevisionTracker updatePreferenceRevision;
        private readonly Func<ProxyConfiguration, CancellationToken, Task<ProxyTestResult>> proxyTestAsync;
        private readonly Action<ProxyConfiguration> applyProxy;
        private readonly HttpClient updateClient;
        private readonly Func<string, DialogResult> confirmAutomaticLoginAction;
        private readonly Func<Version, string, (DialogResult Result, bool DisableStartupChecks)> promptForUpdate;
        private readonly Func<bool, string, string, Task> publishUpdateCheckStateAsync;
        private readonly Action<string, string, ToolTipIcon> notifyLoginAction;
        private readonly Func<Task> publishLoginActionsAsync;
        private readonly Func<CancellationToken, Task<string>> getCurrentPublicIpv4Async;
        private readonly Func<SteamGuardAccount, CancellationToken, Task<Confirmation[]>> fetchTradeConfirmationsForPageAsync;
        private readonly Func<SteamGuardAccount, Confirmation, bool, CancellationToken, Task<bool>> respondTradeConfirmationAsync;
        private readonly Action<int> updateTradePendingCount;
        private readonly Func<string, string, Task> publishTradesAsync;
        private readonly Action<string, MessageBoxIcon> showTradeConfirmationMessage;
        private readonly Func<string, string> showAddAccountLoginDialog;
        private readonly Func<SteamGuardAccount, string, long?, (bool Succeeded, string ResolvedPassKey)> showRefreshLoginDialog;
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
        private ToolStripMenuItem trayActiveAccountMenu;
        private QrScanOverlayForm qrScanOverlay;
        private System.Windows.Forms.Timer loadingTimer;
        private bool qrScanInProgress;
        private volatile bool qrScanCancelRequested;
        private readonly SteamAvatarService steamAvatarService = new SteamAvatarService();

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

        internal sealed class TradeRateLimitedException : Exception
        {
            public TradeRateLimitedException(string message) : base(message)
            {
            }
        }

        private sealed class TradeLoadScope
        {
            public bool IncludeSelection { get; private set; }
            public HashSet<ulong> AccountSteamIds { get; } = new HashSet<ulong>();

            public void AddFullSelection()
            {
                IncludeSelection = true;
            }

            public void AddAccount(ulong steamId)
            {
                if (steamId != 0)
                    AccountSteamIds.Add(steamId);
            }
        }

        private class SessionUnavailableException : InvalidOperationException
        {
            public SessionUnavailableException(string accountName)
                : base("The session for " + (String.IsNullOrWhiteSpace(accountName) ? "this account" : accountName) + " is unavailable. Renew it before using Steam account services.")
            {
            }
        }

        private sealed class StaleAccountGenerationException : SessionUnavailableException
        {
            public StaleAccountGenerationException(string accountName)
                : base(accountName)
            {
            }
        }

        private Task<T> RunSteamAccountOperationAsync<T>(SteamGuardAccount account, Func<Task<T>> operation, CancellationToken cancellationToken = default, Action<Exception> onFailure = null)
        {
            ulong steamId = account?.Session?.SteamID ?? 0;
            return steamAccountOperationCoordinator.RunAsync(steamId, operation, onFailure, cancellationToken);
        }

        private Task<T> RunAvailableSteamAccountOperationAsync<T>(SteamGuardAccount account, Func<Task<T>> operation, CancellationToken cancellationToken = default, Action<T> onResult = null, Func<Exception, bool> shouldDeferFailureMarking = null)
        {
            SessionData sessionAtOperationStart = null;
            return RunSteamAccountOperationAsync(account, async () =>
            {
                lock (accountGenerationLock)
                {
                    sessionAtOperationStart = account?.Session;
                    if (!IsCurrentAccountGeneration(account, allAccounts))
                        throw new StaleAccountGenerationException(account?.AccountName);
                    // Check after acquiring the per-account lock. An operation that is
                    // already on the wire may finish, but work queued behind it is
                    // discarded as soon as another call invalidates the session.
                    if (!IsSessionAvailableForAccountOperations(account))
                        throw new SessionUnavailableException(account?.AccountName);
                }
                T result = await operation();
                onResult?.Invoke(result);
                return result;
            }, cancellationToken, exception =>
            {
                if (!(exception is SessionUnavailableException) && IsDefinitiveSessionFailure(exception) &&
                    !(shouldDeferFailureMarking?.Invoke(exception) ?? false))
                    MarkSessionRenewalRequired(account, exception.Message, sessionAtOperationStart);
            });
        }

        private bool IsAccountBusyWithRecoveryOrTransfer(ulong steamId)
        {
            return steamId != 0 &&
                (silentReloginService.IsRecoveryInProgress(steamId) ||
                 transferFencedSteamIds.ContainsKey(steamId) ||
                 unresolvedTransferMarkerSteamIds.ContainsKey(steamId));
        }

        private async Task<(bool Acquired, T Result)> TryRunAvailableSteamAccountOperationNowAsync<T>(
            SteamGuardAccount account,
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default,
            Action<T> onResult = null,
            Func<Exception, bool> shouldDeferFailureMarking = null)
        {
            ulong steamId = account?.Session?.SteamID ?? 0;
            if (IsAccountBusyWithRecoveryOrTransfer(steamId))
                return (false, default);

            SessionData sessionAtOperationStart = null;
            return await steamAccountOperationCoordinator.TryRunNowAsync(steamId, async () =>
            {
                lock (accountGenerationLock)
                {
                    sessionAtOperationStart = account?.Session;
                    if (!IsCurrentAccountGeneration(account, allAccounts))
                        throw new StaleAccountGenerationException(account?.AccountName);
                    if (!IsSessionAvailableForAccountOperations(account))
                        throw new SessionUnavailableException(account?.AccountName);
                }
                T result = await operation();
                onResult?.Invoke(result);
                return result;
            }, exception =>
            {
                if (!(exception is SessionUnavailableException) && IsDefinitiveSessionFailure(exception) &&
                    !(shouldDeferFailureMarking?.Invoke(exception) ?? false))
                {
                    MarkSessionRenewalRequired(account, exception.Message, sessionAtOperationStart);
                    DispatchBackgroundSilentRelogin(account);
                }
            }, cancellationToken);
        }

        internal void DispatchEligibleSilentReloginsForExpiredAccounts(IEnumerable<SteamGuardAccount> accounts)
        {
            if (manifest == null || !manifest.Encrypted || !manifest.AllowSavedSteamPasswords || String.IsNullOrEmpty(passKey) || accounts == null)
                return;

            foreach (SteamGuardAccount account in accounts)
            {
                ulong steamId = account?.Session?.SteamID ?? 0;
                if (steamId == 0 || IsPendingEnrollment(account))
                    continue;
                if (IsSessionAvailableForAccountOperations(account))
                    continue;

                if (silentReloginService.CanAttemptRecovery(manifest, steamId, passKey))
                    DispatchBackgroundSilentRelogin(account);
            }
        }

        private void DispatchBackgroundSilentRelogin(SteamGuardAccount account)
        {
            ulong steamId = account?.Session?.SteamID ?? 0;
            if (manifest == null || steamId == 0 || IsPendingEnrollment(account) || String.IsNullOrEmpty(passKey))
                return;
            if (transferFencedSteamIds.ContainsKey(steamId) || unresolvedTransferMarkerSteamIds.ContainsKey(steamId))
                return;
            if (!manifest.Encrypted || !manifest.AllowSavedSteamPasswords)
                return;
            if (lifetimeCancellationSource.IsCancellationRequested || IsDisposed || Disposing)
                return;
            if (!silentReloginService.CanAttemptRecovery(manifest, steamId, passKey))
                return;

            TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!activeSilentReloginTasks.TryAdd(steamId, gate.Task))
                return;

            // Dekker re-check after registration so BeginTransferFenceAsync cannot miss this task.
            if (transferFencedSteamIds.ContainsKey(steamId) || unresolvedTransferMarkerSteamIds.ContainsKey(steamId))
            {
                activeSilentReloginTasks.TryRemove(steamId, out _);
                gate.TrySetResult(true);
                return;
            }

            SessionData sessionAtStart = account.Session;
            string generationAtStart = manifest.GetSavedCredentialStatus(steamId, passKey).CredentialGenerationId;

            PublishAccountHealthMetadata(steamId);
            _ = Task.Run(async () =>
            {
                try
                {
                    SilentReloginResult result = await silentReloginService.TryRecoverAccountSessionAsync(
                        manifest,
                        account,
                        passKey,
                        lifetimeCancellationSource.Token);

                    if (lifetimeCancellationSource.IsCancellationRequested || IsDisposed || Disposing)
                        return;

                    if (result.Kind == SilentReloginOutcomeKind.Succeeded)
                    {
                        lock (sessionStateLock)
                        {
                            sessionRenewalRequired.Remove(steamId);
                            startupDeferredSessionRenewals.Remove(steamId);
                            sessionRenewalPersistBackoffUntilUtc.Remove(steamId);
                        }
                        tradeConfirmationScheduler.OnSettingsChanged(steamId, TimeSpan.FromSeconds(3));
                        TryBeginInvoke(() =>
                        {
                            if (IsDisposed || Disposing)
                                return;
                            loadAccountsList(currentAccount?.Session?.SteamID);
                        });
                    }
                    else if (result.Kind == SilentReloginOutcomeKind.InvalidPassword)
                    {
                        TryBeginInvoke(() =>
                        {
                            if (IsDisposed || Disposing)
                                return;
                            if (!CanUseAccountGeneration(account, sessionAtStart, allAccounts))
                                return;

                            MarkSessionRenewalRequired(account, "Steam rejected the saved password for automatic session renewal.", sessionAtStart);
                            PublishAccountHealthMetadata(steamId);

                            string currentGeneration = manifest?.GetSavedCredentialStatus(steamId, passKey).CredentialGenerationId;
                            if (!String.IsNullOrEmpty(generationAtStart) &&
                                !String.Equals(currentGeneration, generationAtStart, StringComparison.Ordinal))
                            {
                                return;
                            }

                            if (result.RejectionPersistFailed)
                            {
                                NotifyLoginAction(
                                    "Saved password rejected",
                                    account.AccountName + ": Automatic renewal paused in memory, but the rejection flag could not be saved to disk.",
                                    ToolTipIcon.Warning);
                            }
                            else
                            {
                                NotifyLoginAction(
                                    "Saved password rejected",
                                    account.AccountName + ": Steam rejected the saved password. Update it in Account Options.",
                                    ToolTipIcon.Warning);
                            }
                        });
                    }
                    else if (result.Kind == SilentReloginOutcomeKind.GuardRejected ||
                        result.Kind == SilentReloginOutcomeKind.InteractiveRequired ||
                        result.Kind == SilentReloginOutcomeKind.AccountMismatch)
                    {
                        TryBeginInvoke(() =>
                        {
                            if (!IsDisposed && !Disposing && CanUseAccountGeneration(account, sessionAtStart, allAccounts))
                                MarkSessionRenewalRequired(account, "Interactive Steam login is required to renew this session.", sessionAtStart);
                        });
                    }
                }
                catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Silent re-login", ex, "Background silent re-login did not complete.");
                }
                finally
                {
                    activeSilentReloginTasks.TryRemove(steamId, out _);
                    gate.TrySetResult(true);
                    TryBeginInvoke(() =>
                    {
                        if (!IsDisposed && !Disposing)
                            PublishAccountHealthMetadata(steamId);
                    });
                }
            }, CancellationToken.None);
        }

        private Task RefreshAndPersistAccessTokenAsync(
            SteamGuardAccount account,
            CancellationToken cancellationToken,
            long? expectedStorageRevision = null)
        {
            return RefreshAndPersistAccessTokenAsync(
                account,
                (session, ct) => session.RefreshAccessToken(false, ct),
                cancellationToken,
                expectedStorageRevision);
        }

        internal async Task RefreshAndPersistAccessTokenAsync(
            SteamGuardAccount account,
            Func<SessionData, CancellationToken, Task> refreshSessionAsync,
            CancellationToken cancellationToken,
            long? expectedStorageRevision = null)
        {
            if (refreshSessionAsync == null)
                throw new ArgumentNullException(nameof(refreshSessionAsync));
            SessionData session = account?.Session ?? throw new SessionUnavailableException(account?.AccountName);
            long accountStorageRevisionAtStart = expectedStorageRevision ?? (manifest?.GetAccountStorageRevision(session.SteamID) ?? 0);
            if (expectedStorageRevision.HasValue && manifest != null &&
                manifest.GetAccountStorageRevision(session.SteamID) != expectedStorageRevision.Value)
            {
                throw new StaleAccountGenerationException(account.AccountName);
            }

            string previousAccessToken = session.AccessToken;
            string previousRefreshToken = session.RefreshToken;
            try
            {
                await refreshSessionAsync(session, cancellationToken);
                if (!PersistLoginSession(account, session, accountStorageRevisionAtStart))
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

        private async Task<bool> RunTradeConfirmationActionWithRefreshRetryAsync(
            SteamGuardAccount account,
            Func<Task<bool>> action,
            CancellationToken cancellationToken)
        {
            if (account?.Session == null)
                throw new SessionUnavailableException(account?.AccountName);

            if (account.Session.IsAccessTokenExpired())
                await RefreshAndPersistAccessTokenAsync(account, cancellationToken);

            try
            {
                await TimeAligner.GetSteamTimeAsync(cancellationToken);
                await WaitForTradeConfirmationDispatchSlotAsync(cancellationToken);
                try
                {
                    return await action();
                }
                finally
                {
                    tradeConfirmationScheduler.RecordGlobalDispatch();
                }
            }
            catch (Exception exception) when (IsTradeConfirmationTokenFailure(exception))
            {
                // Steam can reject an access token before its local JWT expiry.
                // Refresh once and retry the same confirmation action before the
                // coordinator classifies the session as requiring reauthentication.
                await RefreshAndPersistAccessTokenAsync(account, cancellationToken);
                await TimeAligner.GetSteamTimeAsync(cancellationToken);
                await WaitForTradeConfirmationDispatchSlotAsync(cancellationToken);
                try
                {
                    return await action();
                }
                finally
                {
                    tradeConfirmationScheduler.RecordGlobalDispatch();
                }
            }
        }

        private async Task<T> RunSteamActionWithRefreshRetryAsync<T>(
            SteamGuardAccount account,
            Func<Task<T>> action,
            CancellationToken cancellationToken)
        {
            if (account?.Session == null)
                throw new SessionUnavailableException(account?.AccountName);

            if (account.Session.IsAccessTokenExpired())
                await RefreshAndPersistAccessTokenAsync(account, cancellationToken);

            try
            {
                return await action();
            }
            catch (Exception exception) when (IsRecoverableAccessTokenFailure(exception))
            {
                // A transport-level 401 can invalidate an access token before its
                // local JWT expiry. Refresh once and retry before marking the
                // account session as requiring full reauthentication.
                await RefreshAndPersistAccessTokenAsync(account, cancellationToken);
                return await action();
            }
        }

        private Task<Confirmation[]> FetchTradeConfirmationsForPageAsync(SteamGuardAccount account, CancellationToken cancellationToken = default, bool nonBlocking = false)
        {
            return FetchTradeConfirmationsAsync(account, TimeSpan.FromSeconds(1), 2, cancellationToken, nonBlocking);
        }

        private Task<Confirmation[]> FetchTradeConfirmationsForMonitorAsync(SteamGuardAccount account, CancellationToken cancellationToken = default, bool nonBlocking = false)
        {
            int seconds = Math.Min(GetTradeConfirmationMonitorIntervalSeconds(), 15);
            return FetchTradeConfirmationsAsync(account, TimeSpan.FromSeconds(seconds), 3, cancellationToken, nonBlocking);
        }

        private int GetTradeConfirmationMonitorIntervalSeconds()
        {
            if (manifest?.TradeConfirmationCustomIntervalEnabled != true)
                return 15;

            return Math.Clamp(manifest.TradeConfirmationCheckInterval, 3, 3600);
        }

        private int CountEligibleInheritedConfirmationAccounts()
        {
            if (manifest == null || allAccounts == null)
                return 1;

            int count = 0;
            foreach (SteamGuardAccount account in allAccounts)
            {
                if (!IsSessionAvailableForAccountOperations(account))
                    continue;

                if (!manifest.GetAccountConfirmationSettings(account.Session.SteamID).AutoConfirmOverrideEnabled)
                    count++;
            }

            return Math.Max(1, count);
        }

        private ResolvedAccountConfirmationPolicy ResolveAccountConfirmationPolicyForMonitor(ulong steamId, int? eligibleInheritedCount = null)
        {
            if (manifest == null)
                return new ResolvedAccountConfirmationPolicy(false, 15, false, false);

            int inheritedCount = eligibleInheritedCount ?? CountEligibleInheritedConfirmationAccounts();
            return manifest.ResolveConfirmationPolicy(steamId, inheritedCount);
        }

        private bool ShouldAutoConfirmTrade(Confirmation confirmation)
        {
            return ShouldAutoConfirmTradeForAccount(confirmation, null);
        }

        private bool ShouldAutoConfirmTradeForAccount(Confirmation confirmation, ulong? steamId)
        {
            if (confirmation == null || manifest == null)
                return false;

            if (confirmation.ConfType != Confirmation.EMobileConfirmationType.Trade &&
                confirmation.ConfType != Confirmation.EMobileConfirmationType.MarketListing)
            {
                return false;
            }

            ResolvedAccountConfirmationPolicy policy = steamId.HasValue && steamId.Value != 0
                ? manifest.ResolveConfirmationPolicy(steamId.Value)
                : new ResolvedAccountConfirmationPolicy(
                    false,
                    GetTradeConfirmationMonitorIntervalSeconds(),
                    manifest.AutoConfirmTrades,
                    manifest.AutoConfirmMarketTransactions);

            return (confirmation.ConfType == Confirmation.EMobileConfirmationType.MarketListing && policy.AutoConfirmMarketTransactions) ||
                (confirmation.ConfType == Confirmation.EMobileConfirmationType.Trade && policy.AutoConfirmTrades);
        }

        private bool AreAllAutoConfirmAuthorized(ulong steamId, IReadOnlyList<Confirmation> confirmations)
        {
            if (manifest == null || steamId == 0 || confirmations == null || confirmations.Count == 0)
                return false;

            for (int i = 0; i < confirmations.Count; i++)
            {
                if (!ShouldAutoConfirmTradeForAccount(confirmations[i], steamId))
                    return false;
            }

            return true;
        }

        private async Task WaitForTradeConfirmationDispatchSlotAsync(CancellationToken cancellationToken)
        {
            if (TryGetTradeRateLimitMessage(out string rateLimitMessage))
                throw new TradeRateLimitedException(rateLimitMessage);

            TimeSpan spacingWait = tradeConfirmationScheduler.GetRemainingGlobalDispatchSpacing();
            if (spacingWait > TimeSpan.Zero)
                await Task.Delay(spacingWait, cancellationToken);

            if (TryGetTradeRateLimitMessage(out string postWaitRateLimitMessage))
                throw new TradeRateLimitedException(postWaitRateLimitMessage);

            tradeConfirmationScheduler.RecordGlobalDispatch();
        }

        private async Task<Confirmation[]> FetchTradeConfirmationsAsync(SteamGuardAccount account, TimeSpan retryDelay, int retryCount, CancellationToken cancellationToken = default, bool nonBlocking = false)
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
                if (TryGetTradeRateLimitMessage(out string retryRateLimitMessage))
                    throw new TradeRateLimitedException(retryRateLimitMessage);
                try
                {
                    Func<Task<Confirmation[]>> fetchAction = async () =>
                    {
                        if (refreshAccessTokenBeforeRetry || account.Session.IsAccessTokenExpired())
                        {
                            await RefreshAndPersistAccessTokenAsync(account, cancellationToken);
                            refreshAccessTokenBeforeRetry = false;
                        }
                        await TimeAligner.GetSteamTimeAsync(cancellationToken);
                        await WaitForTradeConfirmationDispatchSlotAsync(cancellationToken);
                        try
                        {
                            return await account.FetchConfirmationsAsync(cancellationToken);
                        }
                        finally
                        {
                            tradeConfirmationScheduler.RecordGlobalDispatch();
                        }
                    };

                    if (nonBlocking)
                    {
                        (bool acquired, Confirmation[] fetched) = await TryRunAvailableSteamAccountOperationNowAsync(
                            account,
                            fetchAction,
                            cancellationToken,
                            null,
                            exception => ShouldDeferTradeConfirmationFailure(exception, attempt < retryCount));
                        if (!acquired)
                            return null;
                        return fetched ?? Array.Empty<Confirmation>();
                    }

                    return await RunAvailableSteamAccountOperationAsync(
                        account,
                        fetchAction,
                        cancellationToken,
                        null,
                        exception => ShouldDeferTradeConfirmationFailure(exception, attempt < retryCount));
                }
                catch (TradeRateLimitedException)
                {
                    throw;
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
            if (!tradeConfirmationScheduler.TryGetRateLimitRemaining(out TimeSpan remaining))
            {
                message = null;
                return false;
            }

            message = "Steam is temporarily limiting confirmation checks. Retrying automatically in about " +
                Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) + " seconds.";
            return true;
        }

        internal TradeRateLimitedException ApplyTradeRateLimit(Exception exception)
        {
            TimeSpan delay = TradeConfirmationScheduler.DefaultRateLimitDelay;
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

            tradeConfirmationScheduler.ApplyRateLimit(delay);
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

        private static bool IsRecoverableAccessTokenFailure(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamWebRequestException steamWebException &&
                    steamWebException.StatusCode == HttpStatusCode.Unauthorized)
                    return true;
                if (current is HttpRequestException httpRequestException &&
                    httpRequestException.StatusCode == HttpStatusCode.Unauthorized)
                    return true;
                if (current is WebException webException && webException.Response is HttpWebResponse httpResponse &&
                    httpResponse.StatusCode == HttpStatusCode.Unauthorized)
                    return true;
            }

            return false;
        }

        private static bool IsTradeConfirmationTokenFailure(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamGuardAccount.WGTokenInvalidException || current is SteamGuardAccount.WGTokenExpiredException)
                    return true;
            }

            return false;
        }

        private static bool ShouldDeferTradeConfirmationFailure(Exception exception, bool hasRetry)
        {
            return hasRetry && IsTradeConfirmationTokenFailure(exception);
        }

        public MainForm() : this(null, null, null, null, null)
        {
        }

        internal MainForm(
            Manifest initialManifest,
            UpdatePreferenceRevisionTracker updatePreferenceRevision,
            Func<ProxyConfiguration, CancellationToken, Task<ProxyTestResult>> proxyTestAsync,
            Action<ProxyConfiguration> applyProxy,
            HttpClient updateClient = null,
            Func<string, DialogResult> confirmAutomaticLoginAction = null,
            Func<Version, string, (DialogResult Result, bool DisableStartupChecks)> promptForUpdate = null,
            Func<bool, string, string, Task> publishUpdateCheckStateAsync = null,
            LoginApprovalService loginApprovalService = null,
            Action<string, string, ToolTipIcon> notifyLoginAction = null,
            SteamGuardAccount[] initialAccounts = null,
            Func<Task> publishLoginActionsAsync = null,
            Func<CancellationToken, Task<string>> getCurrentPublicIpv4Async = null,
            Func<SteamGuardAccount, CancellationToken, Task<Confirmation[]>> fetchTradeConfirmationsForPageAsync = null,
            Func<SteamGuardAccount, Confirmation, bool, CancellationToken, Task<bool>> respondTradeConfirmationAsync = null,
            Action<int> updateTradePendingCount = null,
            Func<string, string, Task> publishTradesAsync = null,
            Action<string, MessageBoxIcon> showTradeConfirmationMessage = null,
            Func<string, string> showAddAccountLoginDialog = null,
            Func<SteamGuardAccount, string, (bool Succeeded, string ResolvedPassKey)> showRefreshLoginDialog = null,
            Func<SteamGuardAccount, string, long?, (bool Succeeded, string ResolvedPassKey)> showRefreshLoginDialogWithRevision = null)
        {
            InitializeComponent();
            timerSteamGuard.Enabled = false;
            timerTradesPopup.Enabled = false;
            loginActionsTimer.Tick += loginActionsTimer_Tick;
            manifest = initialManifest;
            allAccounts = initialAccounts;
            this.updatePreferenceRevision = updatePreferenceRevision ?? new UpdatePreferenceRevisionTracker();
            this.proxyTestAsync = proxyTestAsync ?? ProxyService.TestAsync;
            this.applyProxy = applyProxy ?? ProxyService.Apply;
            this.updateClient = updateClient ?? defaultUpdateClient;
            this.confirmAutomaticLoginAction = confirmAutomaticLoginAction ?? ShowAutomaticLoginActionConfirmation;
            this.promptForUpdate = promptForUpdate ?? ShowUpdateAvailablePrompt;
            this.publishUpdateCheckStateAsync = publishUpdateCheckStateAsync;
            this.loginApprovalService = loginApprovalService;
            this.notifyLoginAction = notifyLoginAction ?? ShowLoginActionNotification;
            this.publishLoginActionsAsync = publishLoginActionsAsync ?? PublishCachedLoginActionsToWebViewAsync;
            this.getCurrentPublicIpv4Async = getCurrentPublicIpv4Async ?? GetCurrentPublicIpv4Async;
            this.fetchTradeConfirmationsForPageAsync = fetchTradeConfirmationsForPageAsync ?? FetchTradeConfirmationsForPageNonBlockingAsync;
            this.respondTradeConfirmationAsync = respondTradeConfirmationAsync ?? RespondTradeConfirmationWithRefreshRetryAsync;
            this.updateTradePendingCount = updateTradePendingCount ?? UpdateTradePendingCountToWebView;
            this.publishTradesAsync = publishTradesAsync ?? PublishCachedTradesToWebViewAsync;
            this.showTradeConfirmationMessage = showTradeConfirmationMessage ?? ShowTradeConfirmationDialogMessage;
            this.showAddAccountLoginDialog = showAddAccountLoginDialog ?? ShowAddAccountLoginDialog;
            this.showRefreshLoginDialog = showRefreshLoginDialogWithRevision ??
                (showRefreshLoginDialog != null
                    ? (targetAccount, currentPassKey, _) => showRefreshLoginDialog(targetAccount, currentPassKey)
                    : (Func<SteamGuardAccount, string, long?, (bool Succeeded, string ResolvedPassKey)>)ShowRefreshLoginDialog);
        }

        private string ShowAddAccountLoginDialog(string currentPassKey)
        {
            using (LoginForm loginForm = new LoginForm(LoginForm.LoginType.Initial, null, currentPassKey))
            {
                loginForm.ShowDialog(this);
                if (loginForm.SavedPasswordUpdated && loginForm.SavedPasswordUpdatedSteamId.HasValue)
                    silentReloginService.ClearAccountRuntimeState(loginForm.SavedPasswordUpdatedSteamId.Value);
                return loginForm.ResolvedPassKey;
            }
        }

        private (bool Succeeded, string ResolvedPassKey) ShowRefreshLoginDialog(
            SteamGuardAccount account,
            string currentPassKey,
            long? expectedStorageRevision)
        {
            using (LoginForm loginForm = new LoginForm(LoginForm.LoginType.Refresh, account, currentPassKey, expectedStorageRevision))
            {
                loginForm.ShowDialog(this);
                if (loginForm.RefreshSucceeded && loginForm.SavedPasswordUpdated && loginForm.SavedPasswordUpdatedSteamId.HasValue)
                    silentReloginService.ClearAccountRuntimeState(loginForm.SavedPasswordUpdatedSteamId.Value);
                return (loginForm.RefreshSucceeded, loginForm.ResolvedPassKey);
            }
        }

        private static void ShowTradeConfirmationDialogMessage(string message, MessageBoxIcon icon)
        {
            AstroMessageBox.Show(message, "Trade Confirmations", MessageBoxButtons.OK, icon);
        }

        private Task<Confirmation[]> FetchTradeConfirmationsForPageNonBlockingAsync(
            SteamGuardAccount account,
            CancellationToken cancellationToken)
        {
            return FetchTradeConfirmationsForPageAsync(account, cancellationToken, nonBlocking: true);
        }

        private Task<bool> RespondTradeConfirmationWithRefreshRetryAsync(
            SteamGuardAccount account,
            Confirmation confirmation,
            bool accept,
            CancellationToken cancellationToken)
        {
            return RunAvailableSteamAccountOperationAsync(account, () =>
                RunTradeConfirmationActionWithRefreshRetryAsync(
                    account,
                    () => accept
                        ? account.AcceptConfirmation(confirmation, cancellationToken)
                        : account.DenyConfirmation(confirmation, cancellationToken),
                    cancellationToken),
                cancellationToken);
        }

        private static (DialogResult Result, bool DisableStartupChecks) ShowUpdateAvailablePrompt(Version latestVersion, string checkboxText)
        {
            string message = String.Format(
                "A new version is available! Would you like to download it now?\nYou will update from version {0} to {1}",
                Application.ProductVersion,
                latestVersion.ToString());
            if (checkboxText != null)
            {
                DialogResult result = AstroMessageBox.Show(message, "New Version", MessageBoxButtons.YesNo, MessageBoxIcon.None, checkboxText, out bool isChecked);
                return (result, isChecked);
            }

            return (AstroMessageBox.Show(message, "New Version", MessageBoxButtons.YesNo), false);
        }

        private static DialogResult ShowAutomaticLoginActionConfirmation(string message)
        {
            return AstroMessageBox.Show(
                message,
                "Enable Automatic Login Action",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
        }

        internal static bool ShouldPromptStartupPassKey(Manifest currentManifest, string currentPassKey)
        {
            return currentManifest != null &&
                currentManifest.Encrypted &&
                currentManifest.Entries != null &&
                currentManifest.Entries.Count > 0 &&
                currentPassKey == null;
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

            if (ShouldPromptStartupPassKey(manifest, passKey))
            {
                passKey = manifest.PromptForPassKey();
                if (passKey == null)
                {
                    Close();
                    return;
                }
            }

            if (manifest.Encrypted && manifest.Entries.Count > 0 && !String.IsNullOrEmpty(passKey))
            {
                btnManageEncryption.Text = "Remove Encryption";
                StorageResult credPruneResult = manifest.PruneOrphanedSavedCredentials(passKey);
                if (!credPruneResult.Succeeded)
                    DiagnosticErrorLogger.Log("Credential storage", credPruneResult.Exception, "Orphaned saved credentials could not be pruned on startup.");
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
            loginApprovalService = loginApprovalService ?? new LoginApprovalService(
                account => manifest?.GetAccountStorageRevision(account?.Session?.SteamID ?? 0) ?? 0,
                (account, session, expectedRevision) => PersistLoginSession(account, session, expectedRevision));

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

            StartStartupUpdateCheck();

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
            ReconcilePendingTransfersOnStartup();
            if (manifest.CredentialsStoreMissingWarning)
            {
                DiagnosticErrorLogger.Log("Credential storage", new FileNotFoundException("The referenced encrypted credential store file was not found."), "Saved Steam passwords are unavailable because the credential store file is missing.");
                AstroMessageBox.Show(
                    "Your manifest references a saved-passwords file that is missing from the maFiles folder. Saved passwords for silent renewal are currently unavailable; you can re-save passwords in Account Options or disable saved passwords in Settings.",
                    "Saved Passwords Unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            await ValidateStartupSessionsAsync();
            loadAccountsList();
            await PromptForUnavailableStartupSessionsAsync();
            loadAccountsList();
            startupAccountMaintenanceFinished = true;
        }

        internal static Task<bool> TryRecoverPendingAccountActivationAsync(
            SteamGuardAccount pendingAccount,
            Func<CancellationToken, Task<AuthenticatorActivationStatus>> verifyActivationAsync,
            Func<SteamGuardAccount, bool> persistActivatedAccount,
            CancellationToken cancellationToken)
        {
            if (verifyActivationAsync == null || persistActivatedAccount == null)
                return Task.FromResult(false);

            return TryRecoverPendingAccountActivationAsync(
                pendingAccount,
                async ct => new AuthenticatorActivationVerificationResult(await verifyActivationAsync(ct)),
                (activated, verification) =>
                {
                    if (!LoginForm.TryApplyVerifiedIdentifiers(activated, verification, out string previousTokenGid, out string previousDeviceId))
                        return false;
                    activated.FullyEnrolled = true;
                    try
                    {
                        if (!persistActivatedAccount(activated))
                        {
                            activated.FullyEnrolled = false;
                            LoginForm.RestoreVerifiedIdentifiers(activated, previousTokenGid, previousDeviceId);
                            return false;
                        }
                        return true;
                    }
                    catch
                    {
                        activated.FullyEnrolled = false;
                        LoginForm.RestoreVerifiedIdentifiers(activated, previousTokenGid, previousDeviceId);
                        throw;
                    }
                },
                cancellationToken);
        }

        internal static async Task<bool> TryRecoverPendingAccountActivationAsync(
            SteamGuardAccount pendingAccount,
            Func<CancellationToken, Task<AuthenticatorActivationVerificationResult>> verifyActivationAsync,
            Func<SteamGuardAccount, AuthenticatorActivationVerificationResult, bool> persistActivatedAccount,
            CancellationToken cancellationToken)
        {
            if (pendingAccount == null || pendingAccount.FullyEnrolled ||
                verifyActivationAsync == null || persistActivatedAccount == null)
                return false;

            AuthenticatorActivationVerificationResult verification = await verifyActivationAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (verification.Status != AuthenticatorActivationStatus.ActiveForThisRecord)
                return false;

            // Keep original identifiers unmutated before persistence so revision/reload matching
            // compares against the original in-memory secrets, then applies backfill atomically.
            if (String.IsNullOrWhiteSpace(pendingAccount.DeviceID) &&
                String.IsNullOrWhiteSpace(verification.RemoteDeviceIdentifier))
            {
                return false;
            }

            try
            {
                return persistActivatedAccount(pendingAccount, verification);
            }
            catch (StaleAccountGenerationException)
            {
                return false;
            }
        }

        internal static Task<bool> RecoverPendingAccountOnStartupAsync(
            SteamGuardAccount pendingAccount,
            long initialStorageRevision,
            Func<bool> isAccessTokenExpired,
            Func<bool> isRefreshTokenExpired,
            Func<long, CancellationToken, Task<long>> refreshAccessTokenAsync,
            Func<long> getCurrentStorageRevision,
            Func<CancellationToken, Task<AuthenticatorActivationStatus>> verifyActivationAsync,
            Func<SteamGuardAccount, long, bool> persistActivatedAccount,
            CancellationToken cancellationToken)
        {
            if (verifyActivationAsync == null || persistActivatedAccount == null)
                return Task.FromResult(false);

            return RecoverPendingAccountOnStartupAsync(
                pendingAccount,
                initialStorageRevision,
                isAccessTokenExpired,
                isRefreshTokenExpired,
                refreshAccessTokenAsync,
                getCurrentStorageRevision,
                async ct => new AuthenticatorActivationVerificationResult(await verifyActivationAsync(ct)),
                (activated, verification, expectedRevision) =>
                {
                    if (!LoginForm.TryApplyVerifiedIdentifiers(activated, verification, out string previousTokenGid, out string previousDeviceId))
                        return false;
                    activated.FullyEnrolled = true;
                    try
                    {
                        if (!persistActivatedAccount(activated, expectedRevision))
                        {
                            activated.FullyEnrolled = false;
                            LoginForm.RestoreVerifiedIdentifiers(activated, previousTokenGid, previousDeviceId);
                            return false;
                        }
                        return true;
                    }
                    catch
                    {
                        activated.FullyEnrolled = false;
                        LoginForm.RestoreVerifiedIdentifiers(activated, previousTokenGid, previousDeviceId);
                        throw;
                    }
                },
                cancellationToken);
        }

        internal static async Task<bool> RecoverPendingAccountOnStartupAsync(
            SteamGuardAccount pendingAccount,
            long initialStorageRevision,
            Func<bool> isAccessTokenExpired,
            Func<bool> isRefreshTokenExpired,
            Func<long, CancellationToken, Task<long>> refreshAccessTokenAsync,
            Func<long> getCurrentStorageRevision,
            Func<CancellationToken, Task<AuthenticatorActivationVerificationResult>> verifyActivationAsync,
            Func<SteamGuardAccount, AuthenticatorActivationVerificationResult, long, bool> persistActivatedAccount,
            CancellationToken cancellationToken)
        {
            if (pendingAccount == null || pendingAccount.FullyEnrolled ||
                isAccessTokenExpired == null || isRefreshTokenExpired == null ||
                refreshAccessTokenAsync == null || getCurrentStorageRevision == null ||
                verifyActivationAsync == null || persistActivatedAccount == null)
            {
                return false;
            }

            if (isAccessTokenExpired() && isRefreshTokenExpired())
                return false;

            long boundStorageRevision = initialStorageRevision;
            if (getCurrentStorageRevision() != boundStorageRevision)
                return false;

            bool refreshedAccessToken = false;
            if (isAccessTokenExpired())
            {
                boundStorageRevision = await refreshAccessTokenAsync(boundStorageRevision, cancellationToken);
                refreshedAccessToken = true;
            }

            return await TryRecoverPendingAccountActivationAsync(
                pendingAccount,
                async ct =>
                {
                    AuthenticatorActivationVerificationResult verification = await verifyActivationAsync(ct);
                    if (verification.Status == AuthenticatorActivationStatus.SessionInvalid &&
                        !refreshedAccessToken &&
                        !isRefreshTokenExpired())
                    {
                        boundStorageRevision = await refreshAccessTokenAsync(boundStorageRevision, ct);
                        refreshedAccessToken = true;
                        verification = await verifyActivationAsync(ct);
                    }
                    return verification;
                },
                (activated, verification) => persistActivatedAccount(activated, verification, boundStorageRevision),
                cancellationToken);
        }

        private Task ValidateStartupSessionsAsync()
        {
            return ValidateStartupSessionsAsync(
                (account, ct) => account.VerifyAuthenticatorActivationDetailsAsync(ct),
                (account, expectedRevision, ct) => RefreshAndPersistAccessTokenAsync(account, ct, expectedRevision));
        }

        internal Task ValidateStartupSessionsAsync(
            Func<SteamGuardAccount, CancellationToken, Task<AuthenticatorActivationStatus>> verifyPendingActivationAsync,
            Func<SteamGuardAccount, long, CancellationToken, Task> refreshEnrolledAccountAsync)
        {
            if (verifyPendingActivationAsync == null)
                throw new ArgumentNullException(nameof(verifyPendingActivationAsync));

            return ValidateStartupSessionsAsync(
                async (account, ct) => new AuthenticatorActivationVerificationResult(await verifyPendingActivationAsync(account, ct)),
                refreshEnrolledAccountAsync);
        }

        internal async Task ValidateStartupSessionsAsync(
            Func<SteamGuardAccount, CancellationToken, Task<AuthenticatorActivationVerificationResult>> verifyPendingActivationAsync,
            Func<SteamGuardAccount, long, CancellationToken, Task> refreshEnrolledAccountAsync)
        {
            if (verifyPendingActivationAsync == null)
                throw new ArgumentNullException(nameof(verifyPendingActivationAsync));
            if (refreshEnrolledAccountAsync == null)
                throw new ArgumentNullException(nameof(refreshEnrolledAccountAsync));

            (SteamGuardAccount Account, long StorageRevision)[] startupSnapshot;
            lock (accountGenerationLock)
            {
                SteamGuardAccount[] accounts = allAccounts ?? Array.Empty<SteamGuardAccount>();
                startupSnapshot = accounts.Select(candidate => (
                    Account: candidate,
                    StorageRevision: (manifest != null && candidate?.Session != null && candidate.Session.SteamID != 0)
                        ? manifest.GetAccountStorageRevision(candidate.Session.SteamID)
                        : 0L)).ToArray();
            }

            foreach ((SteamGuardAccount account, long snapshotStorageRevision) in startupSnapshot)
            {
                if (account?.Session == null)
                    continue;

                ulong steamId = account.Session.SteamID;
                if (IsAccountBusyWithRecoveryOrTransfer(steamId))
                    continue;

                if (IsPendingEnrollment(account))
                {
                    SessionData pendingSessionAtStart = account.Session;
                    if (pendingSessionAtStart.IsAccessTokenExpired() && pendingSessionAtStart.IsRefreshTokenExpired())
                        continue;

                    try
                    {
                        await RunSteamAccountOperationAsync(account, () => RecoverPendingAccountOnStartupAsync(
                            account,
                            snapshotStorageRevision,
                            () => pendingSessionAtStart.IsAccessTokenExpired(),
                            () => pendingSessionAtStart.IsRefreshTokenExpired(),
                            (expectedRevision, ct) => RefreshAndPersistPendingAccessTokenAsync(account, expectedRevision, ct),
                            () => manifest?.GetAccountStorageRevision(pendingSessionAtStart.SteamID) ?? 0,
                            ct => verifyPendingActivationAsync(account, ct),
                            (activated, verification, expectedRevision) => PersistRecoveredPendingActivation(activated, pendingSessionAtStart, expectedRevision, verification),
                            lifetimeCancellationSource.Token), lifetimeCancellationSource.Token);
                    }
                    catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (StaleAccountGenerationException)
                    {
                        // The pending account was removed or replaced before startup recovery finished.
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Authenticator startup recovery", ex, "A pending authenticator activation check could not be completed during startup.");
                    }
                    continue;
                }

                SessionData sessionAtValidationStart = account.Session;
                if (manifest != null && sessionAtValidationStart.SteamID != 0 &&
                    manifest.GetAccountStorageRevision(sessionAtValidationStart.SteamID) != snapshotStorageRevision)
                {
                    continue;
                }
                if (sessionAtValidationStart.IsRefreshTokenExpired() || IsSessionRenewalRequired(account))
                {
                    if (await TryRecoverStartupSessionWithSilentReloginAsync(account))
                        continue;

                    MarkSessionRenewalRequired(account, "The refresh token is expired or malformed.", sessionAtValidationStart);
                    continue;
                }

                try
                {
                    await RunSteamAccountOperationAsync(account, async () =>
                    {
                        await refreshEnrolledAccountAsync(account, snapshotStorageRevision, lifetimeCancellationSource.Token);
                        return true;
                    }, lifetimeCancellationSource.Token, null);
                }
                catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                {
                    return;
                }
                catch (StaleAccountGenerationException)
                {
                    // The enrolled account was removed or replaced while startup awaited an earlier entry.
                }
                catch (Exception ex)
                {
                    if (IsDefinitiveSessionFailure(ex))
                    {
                        if (await TryRecoverStartupSessionWithSilentReloginAsync(account))
                            continue;

                        MarkSessionRenewalRequired(account, ex.Message, sessionAtValidationStart);
                    }
                    else if (IsTransientSessionFailure(ex))
                    {
                        DiagnosticErrorLogger.Log("Authenticator startup recovery", ex, "Startup session validation stopped because Steam or the network was unavailable.");
                        return;
                    }
                    else
                    {
                        DiagnosticErrorLogger.Log("Authenticator startup recovery", ex, "A startup session refresh failed without proving that the saved Steam session was revoked.");
                    }
                }
            }
        }

        private async Task<bool> TryRecoverStartupSessionWithSilentReloginAsync(SteamGuardAccount account)
        {
            ulong steamId = account?.Session?.SteamID ?? 0;
            if (manifest == null || steamId == 0 || IsPendingEnrollment(account) || IsAccountBusyWithRecoveryOrTransfer(steamId) ||
                !silentReloginService.CanAttemptRecovery(manifest, steamId, passKey))
                return false;

            TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!activeSilentReloginTasks.TryAdd(steamId, gate.Task))
                return false;

            if (transferFencedSteamIds.ContainsKey(steamId) || unresolvedTransferMarkerSteamIds.ContainsKey(steamId))
            {
                activeSilentReloginTasks.TryRemove(steamId, out _);
                gate.TrySetResult(true);
                return false;
            }

            try
            {
                SilentReloginResult reloginResult = await silentReloginService.TryRecoverAccountSessionAsync(
                    manifest,
                    account,
                    passKey,
                    lifetimeCancellationSource.Token);

                if (reloginResult.Kind == SilentReloginOutcomeKind.Succeeded)
                {
                    lock (sessionStateLock)
                    {
                        sessionRenewalRequired.Remove(steamId);
                        startupDeferredSessionRenewals.Remove(steamId);
                        sessionRenewalPersistBackoffUntilUtc.Remove(steamId);
                    }
                    return true;
                }

                if (reloginResult.Kind == SilentReloginOutcomeKind.InvalidPassword && reloginResult.RejectionPersistFailed)
                {
                    AstroMessageBox.Show(
                        "Steam rejected the saved password for " + account.AccountName + ". Automatic renewal has been paused for this session, but the rejection could not be written to disk.",
                        "Saved Password Rejected",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                return false;
            }
            finally
            {
                activeSilentReloginTasks.TryRemove(steamId, out _);
                gate.TrySetResult(true);
            }
        }

        private async Task PromptForUnavailableStartupSessionsAsync()
        {
            SteamGuardAccount[] accounts = allAccounts ?? Array.Empty<SteamGuardAccount>();
            foreach (SteamGuardAccount startupAccount in accounts)
            {
                if (startupAccount?.Session == null || IsPendingEnrollment(startupAccount))
                    continue;

                ulong steamId = startupAccount.Session.SteamID;
                if (IsAccountBusyWithRecoveryOrTransfer(steamId) ||
                    startupDeferredSessionRenewals.Contains(steamId) ||
                    !IsSessionRenewalRequired(startupAccount))
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
                TryBeginInvoke(() => RestoreWindowFromActivation(true));
                return;
            }

            base.WndProc(ref m);
        }

        private void RestoreWindowFromActivation(bool checkForUpdates = false)
        {
            if (IsDisposed)
                return;

            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();

            if (checkForUpdates)
                StartUpdateCheckFromActivation();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsIrreversibleTransferInFlight &&
                e.CloseReason != CloseReason.WindowsShutDown &&
                e.CloseReason != CloseReason.TaskManagerClosing)
            {
                e.Cancel = true;
                if (e.CloseReason == CloseReason.ApplicationExitCall || explicitExitRequested)
                    exitAfterTransferRequested = true;
                return;
            }

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
            string resolvedPassKey = showAddAccountLoginDialog(passKey);
            AdoptResolvedPassKey(resolvedPassKey);
            this.loadAccountsList();
        }

        internal void AdoptResolvedPassKey(string resolvedPassKey)
        {
            if (manifest == null || !manifest.Encrypted || manifest.Entries == null || manifest.Entries.Count == 0 ||
                String.IsNullOrEmpty(resolvedPassKey))
            {
                return;
            }

            bool wasLockedOrUnencrypted = String.IsNullOrEmpty(passKey);
            passKey = resolvedPassKey;
            btnManageEncryption.Text = "Remove Encryption";
            btnManageEncryption.Enabled = true;
            if (wasLockedOrUnencrypted && GetCoreWebView2IfAvailable() != null)
            {
                SendSettingsToWebView();
            }
        }

        private sealed class TransferFenceHandle : IDisposable
        {
            private readonly ConcurrentDictionary<ulong, byte> fencedIds;
            private readonly ConcurrentDictionary<ulong, byte> unresolvedMarkers;
            private readonly ulong steamId;
            private IDisposable exclusiveLock;

            public TransferFenceHandle(
                ConcurrentDictionary<ulong, byte> fencedIds,
                ConcurrentDictionary<ulong, byte> unresolvedMarkers,
                ulong steamId,
                IDisposable exclusiveLock)
            {
                this.fencedIds = fencedIds;
                this.unresolvedMarkers = unresolvedMarkers;
                this.steamId = steamId;
                this.exclusiveLock = exclusiveLock;
            }

            public void Dispose()
            {
                IDisposable lockToRelease = Interlocked.Exchange(ref exclusiveLock, null);
                try
                {
                    // Atomically hand off fence coverage to unresolvedTransferMarkerSteamIds BEFORE
                    // releasing exclusiveLock or transferFencedSteamIds so no background timer or
                    // silent re-login can slip through after an OutcomeUnknown / SavePending transfer.
                    if (PendingTransferMarker.HasMarker(steamId))
                        unresolvedMarkers[steamId] = 0;
                    else
                        unresolvedMarkers.TryRemove(steamId, out _);
                }
                finally
                {
                    try
                    {
                        lockToRelease?.Dispose();
                    }
                    finally
                    {
                        fencedIds.TryRemove(steamId, out _);
                    }
                }
            }
        }

        private async Task<IDisposable> BeginTransferFenceAsync(ulong steamId, CancellationToken cancellationToken)
        {
            if (steamId == 0)
                return null;

            transferFencedSteamIds[steamId] = 0;
            try
            {
                if (activeSilentReloginTasks.TryGetValue(steamId, out Task activeReloginTask) && activeReloginTask != null)
                {
                    await activeReloginTask.WaitAsync(cancellationToken);
                }

                IDisposable exclusiveLock = await steamAccountOperationCoordinator.AcquireExclusiveLockAsync(steamId, cancellationToken);
                return new TransferFenceHandle(transferFencedSteamIds, unresolvedTransferMarkerSteamIds, steamId, exclusiveLock);
            }
            catch
            {
                transferFencedSteamIds.TryRemove(steamId, out _);
                throw;
            }
        }

        internal void RefreshUnresolvedTransferMarkers()
        {
            unresolvedTransferMarkerSteamIds.Clear();
            foreach (PendingTransferMarkerRecord marker in PendingTransferMarker.EnumerateMarkers())
            {
                if (marker != null && marker.SteamId != 0)
                    unresolvedTransferMarkerSteamIds[marker.SteamId] = 0;
            }
        }

        private void OnTransferredAccountCommitted(SteamGuardAccount committedAccount, string usedPassKey)
        {
            if (committedAccount?.Session == null || committedAccount.Session.SteamID == 0)
                return;

            ulong steamId = committedAccount.Session.SteamID;
            if (manifest != null && manifest.Encrypted && !String.IsNullOrEmpty(usedPassKey) &&
                (String.IsNullOrEmpty(this.passKey) || !manifest.VerifyPasskey(this.passKey)))
            {
                AdoptResolvedPassKey(usedPassKey);
            }

            lock (accountGenerationLock)
            {
                // Synchronously retire any previous SteamGuardAccount generation for this SteamID
                // BEFORE RecoveryCodeForm is shown so concurrent refreshes cannot overwrite it.
                List<SteamGuardAccount> updated = (allAccounts ?? Array.Empty<SteamGuardAccount>())
                    .Where(existing => existing?.Session?.SteamID != steamId)
                    .ToList();
                updated.Add(committedAccount);
                allAccounts = updated.ToArray();
            }

            ClearSessionRecoveryState(committedAccount);
            silentReloginService.ClearTransientCooldown(steamId);
            InvalidateTradeConfirmationCache(committedAccount);
            lock (sessionStateLock)
            {
                foreach (string requestKey in pendingLoginRequests.Keys
                    .Where(key => key.StartsWith(steamId.ToString(CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal))
                    .ToArray())
                {
                    pendingLoginRequests.Remove(requestKey);
                    PruneLoginRequestBookkeeping(requestKey);
                }
            }

            if (!IsDisposed && !Disposing)
            {
                if (InvokeRequired)
                    Invoke((MethodInvoker)(() => loadAccountsList(steamId)));
                else
                    loadAccountsList(steamId);
            }
        }

        private void StartAuthenticatorTransferFlow()
        {
            using (LoginForm loginForm = new LoginForm(
                LoginForm.LoginType.Transfer,
                account: null,
                passKey: this.passKey,
                beginTransferFenceAsync: BeginTransferFenceAsync,
                onAccountCommitted: OnTransferredAccountCommitted,
                onIrreversibleTransferStateChanged: inFlight =>
                {
                    if (inFlight)
                        Interlocked.Increment(ref activeIrreversibleTransferCount);
                    else
                        Interlocked.Decrement(ref activeIrreversibleTransferCount);
                },
                isDeferredExitRequested: () => exitAfterTransferRequested))
            {
                loginForm.ShowDialog(this);
                AdoptResolvedPassKey(loginForm.ResolvedPassKey);
                if (loginForm.SavedPasswordUpdated && loginForm.SavedPasswordUpdatedSteamId.HasValue)
                {
                    silentReloginService.ClearAccountRuntimeState(loginForm.SavedPasswordUpdatedSteamId.Value);
                }
            }
            RefreshUnresolvedTransferMarkers();
            this.loadAccountsList();
            if (exitAfterTransferRequested && !IsIrreversibleTransferInFlight)
            {
                exitAfterTransferRequested = false;
                explicitExitRequested = true;
                Application.Exit();
            }
        }

        private void ReconcilePendingTransfersOnStartup()
        {
            if (manifest == null)
                return;

            // Fence unresolved markers immediately BEFORE showing any dialogs so an error or early exit
            // can never leave background automation running against an unresolved transfer.
            RefreshUnresolvedTransferMarkers();
            try
            {
                List<StagedTransferRecoveryRecord> stagedRecoveries = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(allAccounts, passKey, manifest);
                foreach (StagedTransferRecoveryRecord staged in stagedRecoveries)
                {
                    if (String.IsNullOrEmpty(staged?.FilePath) || !File.Exists(staged.FilePath))
                        continue;

                    StagedTransferRecoveryRecord resolvedStaged = staged;
                    string recoveryPassKey = passKey;

                    if (resolvedStaged.RequiresPasskey)
                    {
                        while (true)
                        {
                            using (InputForm passForm = new InputForm(
                                "A passkey-protected staged authenticator transfer recovery was found for SteamID " +
                                resolvedStaged.SteamId.ToString(CultureInfo.InvariantCulture) +
                                ". Enter the passkey used during that transfer to unlock and recover it:",
                                true))
                            {
                                passForm.ShowInputDialog(this);
                                if (passForm.Canceled || !passForm.UiReady || passForm.LoadFailed)
                                {
                                    resolvedStaged = null;
                                    break;
                                }

                                string candidatePassKey = passForm.txtBox.Text;
                                StagedTransferRecoveryRecord decrypted = PendingTransferMarker.TryLoadStagedRecoveryWithPasskey(resolvedStaged.FilePath, candidatePassKey);
                                if (decrypted?.Account != null)
                                {
                                    resolvedStaged = decrypted;
                                    recoveryPassKey = candidatePassKey;
                                    break;
                                }

                                AstroMessageBox.Show(
                                    "That passkey could not unlock the staged transfer recovery file. Try again or cancel to keep it staged.",
                                    "Staged Transfer Recovery",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Error);
                            }
                        }

                        if (resolvedStaged?.Account == null)
                            continue;

                        SteamGuardAccount existingCommitted = FindLoadedAccountBySteamId(allAccounts, resolvedStaged.SteamId);
                        if (existingCommitted != null && PendingTransferMarker.IsSameAuthenticatorReplacement(existingCommitted, resolvedStaged.Account))
                        {
                            if (PendingTransferMarker.DeleteMarkerBestEffort(resolvedStaged.SteamId, resolvedStaged.TransferAttemptId))
                                PendingTransferMarker.DeleteStagedRecoveryBestEffort(resolvedStaged.FilePath);
                            continue;
                        }
                    }

                    if (resolvedStaged.Account == null)
                        continue;

                    bool hasEncryptedEntries = manifest.Encrypted && manifest.Entries.Count > 0;
                    bool saveEncrypted = hasEncryptedEntries || !String.IsNullOrEmpty(recoveryPassKey);
                    string effectivePassKey = hasEncryptedEntries ? passKey : (saveEncrypted ? recoveryPassKey : null);
                    long expectedRevisionAtRecovery = manifest.GetAccountStorageRevision(resolvedStaged.SteamId);

                    using (TransferSaveRecoveryForm recoveryForm = new TransferSaveRecoveryForm(
                        resolvedStaged.Account,
                        "A transferred authenticator for " + (resolvedStaged.Account.AccountName ?? resolvedStaged.SteamId.ToString(CultureInfo.InvariantCulture)) +
                        " was staged before a previous save completed or the app closed.",
                        resolvedStaged.FilePath,
                        stagingFailed: false,
                        manifestPassKey: effectivePassKey ?? recoveryPassKey,
                        retryLocalSave: candidate =>
                        {
                            StorageResult retryResult = manifest.SaveAccount(
                                candidate,
                                saveEncrypted,
                                effectivePassKey,
                                expectedStorageRevision: manifest.Entries.Any(entry => entry.SteamID == resolvedStaged.SteamId)
                                    ? expectedRevisionAtRecovery
                                    : (long?)null,
                                transferAttemptId: resolvedStaged.TransferAttemptId,
                                expectedPreviousTransferAttemptId: resolvedStaged.PreviousCommittedTransferAttemptId,
                                enforceTransferAttemptBase: true);
                            if (retryResult.Succeeded)
                            {
                                if (PendingTransferMarker.DeleteMarkerBestEffort(resolvedStaged.SteamId, resolvedStaged.TransferAttemptId))
                                    PendingTransferMarker.DeleteAllStagedRecoveriesForSteamIdBestEffort(resolvedStaged.SteamId);
                                OnTransferredAccountCommitted(candidate, effectivePassKey);
                            }
                            return retryResult;
                        },
                        transferAttemptId: resolvedStaged.TransferAttemptId))
                    {
                        recoveryForm.ShowDialog(this);
                        if (recoveryForm.LocalSaveSucceeded)
                        {
                            loadAccountsList(resolvedStaged.SteamId);
                        }
                    }
                }

                List<PendingTransferMarkerRecord> unresolvedMarkers = PendingTransferMarker.EnumerateMarkers();
                foreach (PendingTransferMarkerRecord marker in unresolvedMarkers)
                {
                    string accountDisplay = String.IsNullOrWhiteSpace(marker.AccountName)
                        ? "SteamID " + marker.SteamId.ToString(CultureInfo.InvariantCulture)
                        : marker.AccountName + " (" + marker.SteamId.ToString(CultureInfo.InvariantCulture) + ")";
                    string whenText = marker.DispatchedAtUtc == DateTime.MinValue
                        ? "an earlier session"
                        : marker.DispatchedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

                    DialogResult decision = AstroMessageBox.ShowWithCustomButtons(
                        "An authenticator transfer for " + accountDisplay + " was interrupted after sending the SMS code (" + whenText + ").\n\n" +
                        "Steam may have already replaced your previous authenticator. Automation and account operations remain paused for this account until resolved. Verify your Steam Mobile App or Steam Support recovery status first.\n\n" +
                        "Dismiss this warning only after you have verified the account.",
                        "Unresolved Authenticator Transfer",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning,
                        "Dismiss Notice",
                        "Keep Notice");

                    if (decision == DialogResult.OK)
                    {
                        PendingTransferMarker.DeleteMarkerBestEffort(marker.SteamId);
                    }
                }
            }
            finally
            {
                RefreshUnresolvedTransferMarkers();
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
            qrScanCancelRequested = false;
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

                    if (qrScanCancelRequested || (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
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
                    AstroMessageBox.Show(cancellationReason, "QR Login Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                        AstroMessageBox.Show("Can't get ID of QR code. Steam might have changed their QR format.", "Invalid QR Code", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                        await RunAvailableSteamAccountOperationAsync(accountAtScanStart, () =>
                            RunSteamActionWithRefreshRetryAsync(
                                accountAtScanStart,
                                () => accountAtScanStart.SignInViaQR(clientId.ToString(), lifetimeCancellationSource.Token),
                                lifetimeCancellationSource.Token),
                            lifetimeCancellationSource.Token);
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
                if (manifest.AllowSavedSteamPasswords || !String.IsNullOrWhiteSpace(manifest.CredentialsStoreFilename))
                {
                    DialogResult warningResult = AstroMessageBox.Show(
                        "Removing encryption will also disable silent session renewal and permanently delete any saved Steam passwords. Do you want to continue?",
                        "Remove Encryption",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (warningResult != DialogResult.Yes)
                        return;
                }

                string curPassKey;
                using (InputForm currentPassKeyForm = new InputForm("Enter current passkey to remove encryption", true))
                {
                    currentPassKeyForm.ShowInputDialog(this);
                    if (currentPassKeyForm.Canceled || !currentPassKeyForm.UiReady || currentPassKeyForm.LoadFailed)
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
                    string removedNotice = encryptionResult.ObsoleteCleanupPending
                        ? "Encryption successfully removed. An old encrypted credential file could not be deleted immediately and will be removed automatically on the next save or restart."
                        : "Encryption successfully removed.";
                    AstroMessageBox.Show(removedNotice);
                    passKey = null;
                    this.loadAccountsList();
                    SendSettingsToWebView();
                    btnManageEncryption.Text = "Setup Encryption";
                }
            }
            else
            {
                passKey = manifest.PromptSetupPassKey();
                this.loadAccountsList();
                SendSettingsToWebView();
                if (manifest.Encrypted) btnManageEncryption.Text = "Remove Encryption";
            }
        }

        private void labelUpdate_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            _ = CheckForUpdatesAsync(false);
        }

        private void btnCopy_Click(object sender, EventArgs e)
        {
            CopyLoginToken();
        }


        // Tool strip menu handlers

        private void menuQuit_Click(object sender, EventArgs e)
        {
            if (IsIrreversibleTransferInFlight)
            {
                exitAfterTransferRequested = true;
                return;
            }
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
            if (IsPendingEnrollment(accountAtStart))
            {
                AstroMessageBox.Show("This authenticator is still pending Steam activation and cannot be unlinked from Steam yet.", "Deactivate Authenticator", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
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
                    if (confirmationDialog.Canceled || !confirmationDialog.UiReady || confirmationDialog.LoadFailed)
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
                    success = await RunAvailableSteamAccountOperationAsync(accountAtStart, () =>
                        RunSteamActionWithRefreshRetryAsync(
                            accountAtStart,
                            () => accountAtStart.DeactivateAuthenticator(scheme, lifetimeCancellationSource.Token),
                            lifetimeCancellationSource.Token),
                        lifetimeCancellationSource.Token);
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
                    if (!this.manifest.RemoveAccount(accountAtStart, this.passKey))
                    {
                        AstroMessageBox.Show("Steam Guard was removed on Steam, but the local authenticator record could not be deleted safely. Restart Astro SDA and remove the account manually.", "Deactivate Authenticator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
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

            bool isPendingAccount = IsPendingEnrollment(account);
            if (removeSteamGuard && isPendingAccount)
            {
                AstroMessageBox.Show("This authenticator is still pending Steam activation and cannot be unlinked from Steam yet. You can still remove only its local data.", "Remove Steam Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                : isPendingAccount
                    ? "This will permanently delete " + account.AccountName + "'s pending local maFile, saved recovery code record, and manifest entry. Make sure you have backed up your recovery code before continuing. Continue?"
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
                    if (confirmationDialog.Canceled || !confirmationDialog.UiReady || confirmationDialog.LoadFailed)
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
                    deactivated = await RunAvailableSteamAccountOperationAsync(account, () =>
                        RunSteamActionWithRefreshRetryAsync(
                            account,
                            () => account.DeactivateAuthenticator(2, lifetimeCancellationSource.Token),
                            lifetimeCancellationSource.Token),
                        lifetimeCancellationSource.Token);
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
                    : isPendingAccount
                    ? "This pending account's local data was deleted."
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
            if (IsIrreversibleTransferInFlight)
            {
                exitAfterTransferRequested = true;
                return;
            }
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
            trayTradeConfirmations.Text = "Confirmations";
            trayCopySteamGuard.Text = "Copy Steam Guard Code";
            trayQuit.Text = "Quit";
            trayQuit.ForeColor = AstroTheme.Error;

            trayLoginActions = new ToolStripMenuItem("Login Actions");
            trayLoginActions.Click += (sender, args) => OpenLoginActionsFromTray();

            trayActiveAccountMenu = new ToolStripMenuItem("Active Account");

            menuStripTray.ShowImageMargin = false;
            menuStripTray.Padding = new Padding(4, 5, 4, 5);
            menuStripTray.Items.Clear();
            menuStripTray.Items.AddRange(new ToolStripItem[]
            {
                trayRestore,
                new ToolStripSeparator(),
                trayActiveAccountMenu,
                trayCopySteamGuard,
                trayTradeConfirmations,
                trayLoginActions,
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
            trayActiveAccountMenu.Enabled = hasAccounts;
            bool hasToken = hasAccounts && !IsPendingEnrollment(currentAccount) && !String.IsNullOrWhiteSpace(txtLoginToken.Text);
            trayCopySteamGuard.Enabled = hasToken;

            string activeName = currentAccount?.AccountName;
            trayActiveAccountMenu.Text = String.IsNullOrWhiteSpace(activeName)
                ? "Active Account"
                : "Active Account: " + activeName;

            trayActiveAccountMenu.DropDownItems.Clear();
            if (hasAccounts)
            {
                foreach (SteamGuardAccount account in allAccounts.OrderBy(a => a.AccountName, StringComparer.OrdinalIgnoreCase))
                {
                    SteamGuardAccount captured = account;
                    bool isSelected = currentAccount != null &&
                        ((captured.Session?.SteamID ?? 0) != 0
                            ? captured.Session.SteamID == (currentAccount.Session?.SteamID ?? 0)
                            : String.Equals(captured.AccountName, currentAccount.AccountName, StringComparison.Ordinal));
                    var item = new ToolStripMenuItem(isSelected ? "●  " + captured.AccountName : "    " + captured.AccountName)
                    {
                        Font = isSelected ? new Font("Segoe UI", 9F, FontStyle.Bold) : new Font("Segoe UI", 9F, FontStyle.Regular)
                    };
                    item.Click += (s, args) =>
                    {
                        currentAccount = captured;
                        int idx = listAccounts.Items.IndexOf(captured.AccountName);
                        if (idx >= 0)
                            listAccounts.SelectedIndex = idx;
                        else
                        {
                            btnLoginViaQr.Enabled = IsSessionAvailableForAccountOperations(captured);
                            menuDeactivateAuthenticator.Enabled = IsSessionAvailableForAccountOperations(captured);
                            loadAccountInfo();
                        }
                    };
                    trayActiveAccountMenu.DropDownItems.Add(item);
                }
            }

            trayCopySteamGuard.Text = hasToken
                ? "Copy Steam Guard Code   (" + txtLoginToken.Text.Trim() + ")"
                : "Copy Steam Guard Code";

            int totalConfirmations = pendingTradeConfirmationCounts.Values.Sum();
            trayTradeConfirmations.Text = totalConfirmations > 0
                ? "Confirmations (" + totalConfirmations.ToString(CultureInfo.InvariantCulture) + ")"
                : "Confirmations";

            int totalLogins = pendingLoginRequests.Count;
            trayLoginActions.Text = totalLogins > 0
                ? "Login Actions (" + totalLogins.ToString(CultureInfo.InvariantCulture) + ")"
                : "Login Actions";
        }

        private void trayCopySteamGuard_Click(object sender, EventArgs e)
        {
            CopyLoginToken();
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
                    menuDeactivateAuthenticator.Enabled = IsSessionAvailableForAccountOperations(account);
                    btnCopy.Enabled = !IsPendingEnrollment(account);
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
            // Evaluate silent re-login eligibility BEFORE IsSessionAvailableForAccountOperations filters out expired accounts!
            DispatchEligibleSilentReloginsForExpiredAccounts(allAccounts);
            SteamGuardAccount[] accountsToMonitor = (allAccounts ?? Array.Empty<SteamGuardAccount>())
                .Where(account => IsSessionAvailableForAccountOperations(account) &&
                    !IsAccountBusyWithRecoveryOrTransfer(account.Session.SteamID))
                .ToArray();
            if (accountsToMonitor.Length == 0)
                return;

            int globalIntervalSeconds = GetTradeConfirmationMonitorIntervalSeconds();
            ulong[] eligibleSteamIds = accountsToMonitor.Select(item => item.Session.SteamID).ToArray();
            int eligibleInheritedCount = accountsToMonitor.Count(item =>
                !manifest.GetAccountConfirmationSettings(item.Session.SteamID).AutoConfirmOverrideEnabled);
            eligibleInheritedCount = Math.Max(1, eligibleInheritedCount);

            if (!tradeConfirmationScheduler.TrySelectNextDueAccount(
                eligibleSteamIds,
                steamId => manifest.GetAccountConfirmationSettings(steamId).AutoConfirmOverrideEnabled,
                globalIntervalSeconds,
                out ulong dueSteamId))
            {
                return;
            }

            SteamGuardAccount account = accountsToMonitor.FirstOrDefault(item => item.Session.SteamID == dueSteamId);
            if (account == null)
                return;

            if (!confirmationsSemaphore.Wait(0))
            {
                return; //Only one thread may access this critical section at once. Mutex is a bad choice here because it'll cause a pileup of threads.
            }

            ulong steamId = account.Session.SteamID;
            ResolvedAccountConfirmationPolicy policyAtStart = ResolveAccountConfirmationPolicyForMonitor(steamId, eligibleInheritedCount);
            bool scanCompletedSuccessfully = false;
            string failureSummary = null;
            bool rateLimited = false;

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

                Confirmation[] confirmations = await FetchTradeConfirmationsForMonitorAsync(account, lifetimeCancellationSource.Token, nonBlocking: true);
                lblStatus.Text = "";
                if (confirmations == null)
                {
                    // Account lock was busy (e.g. concurrent recovery); defer briefly without failing.
                    tradeConfirmationScheduler.DeferTemporarily(steamId, TimeSpan.FromSeconds(5));
                    return;
                }
                if (!IsSessionAvailableForAccountOperations(account))
                    return;

                var pendingConfirmations = new List<Confirmation>();
                var autoAcceptConfirmations = new List<Confirmation>();
                foreach (Confirmation confirmation in confirmations)
                {
                    if (ShouldAutoConfirmTradeForAccount(confirmation, steamId))
                    {
                        autoAcceptConfirmations.Add(confirmation);
                    }
                    else
                    {
                        pendingConfirmations.Add(confirmation);
                    }
                }

                bool autoAcceptFailedTransiently = false;
                if (autoAcceptConfirmations.Count > 0)
                {
                    bool accepted = false;
                    try
                    {
                        accepted = await RunAvailableSteamAccountOperationAsync(account, () =>
                            RunTradeConfirmationActionWithRefreshRetryAsync(
                                account,
                                () => account.AcceptMultipleConfirmations(
                                    autoAcceptConfirmations.ToArray(),
                                    () => AreAllAutoConfirmAuthorized(steamId, autoAcceptConfirmations),
                                    lifetimeCancellationSource.Token),
                                lifetimeCancellationSource.Token),
                            lifetimeCancellationSource.Token);
                    }
                    catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (TradeRateLimitedException)
                    {
                        throw;
                    }
                    catch (Exception autoEx) when (IsRateLimitedResponse(autoEx))
                    {
                        throw ApplyTradeRateLimit(autoEx);
                    }
                    catch (SessionUnavailableException)
                    {
                        throw;
                    }
                    catch (Exception autoEx)
                    {
                        autoAcceptFailedTransiently = true;
                        failureSummary = "Automatic confirmation could not be completed.";
                        DiagnosticErrorLogger.Log("Trade confirmation auto-accept", autoEx, "An automatic confirmation batch failed; items remain visible.");
                    }

                    if (!accepted)
                    {
                        pendingConfirmations.AddRange(autoAcceptConfirmations);
                        if (!autoAcceptFailedTransiently && AreAllAutoConfirmAuthorized(steamId, autoAcceptConfirmations))
                        {
                            autoAcceptFailedTransiently = true;
                            failureSummary = "Steam did not accept the automatic confirmation action.";
                        }
                    }
                }

                ReconcileTradeConfirmationNotifications(account, pendingConfirmations);
                foreach (var pending in pendingConfirmations)
                {
                    string confirmationKey = steamId.ToString(CultureInfo.InvariantCulture) + ":" + pending.ID.ToString(CultureInfo.InvariantCulture);
                    if (notifiedTradeConfirmations.Add(confirmationKey))
                        NotifyTradeConfirmation(account, pending);
                }
                CacheTradeConfirmations(account, pendingConfirmations);
                UpdateTradePendingCount(pendingTradeConfirmationCounts.Values.Sum());
                if (IsWebTabActive("trades"))
                    await PublishCachedTradesAsync();

                scanCompletedSuccessfully = !autoAcceptFailedTransiently;
            }
            catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
            {
            }
            catch (TradeRateLimitedException ex)
            {
                lblStatus.Text = "";
                rateLimited = true;
                DiagnosticErrorLogger.Log("Trade confirmation monitor", ex, "Steam rate limited the queued confirmation monitor.");
            }
            catch (SessionUnavailableException)
            {
                lblStatus.Text = "";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "";
                failureSummary = "Background confirmation check did not complete.";
                Debug.WriteLine("Trade confirmation monitor failed: " + ex.Message);
                DiagnosticErrorLogger.Log("Trade confirmation monitor", ex, "The background confirmation scan did not complete.");
            }
            finally
            {
                if (!lifetimeCancellationSource.IsCancellationRequested && !rateLimited)
                {
                    ResolvedAccountConfirmationPolicy latestPolicy = ResolveAccountConfirmationPolicyForMonitor(steamId);
                    if (scanCompletedSuccessfully)
                    {
                        tradeConfirmationScheduler.RecordSuccess(steamId, latestPolicy.Interval);
                    }
                    else if (!String.IsNullOrEmpty(failureSummary))
                    {
                        tradeConfirmationScheduler.RecordTransientFailure(steamId, latestPolicy.Interval, failureSummary);
                    }
                    PublishAccountHealthMetadata(steamId);
                }
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

        internal async Task MonitorLoginActionsAsync()
        {
            if (manifest == null || loginApprovalService == null || !manifest.LoginActionMonitoringEnabled || allAccounts == null)
                return;
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            if (cancellationToken.IsCancellationRequested)
                return;
            if (TryGetLoginRateLimitMessage(out _))
                return;
            PersistExpiredRefreshTokenStates();
            DispatchEligibleSilentReloginsForExpiredAccounts(allAccounts);
            if (!allAccounts.Any(account => IsSessionAvailableForAccountOperations(account) &&
                !IsAccountBusyWithRecoveryOrTransfer(account.Session.SteamID)))
                return;
            if (!await loginActionsSemaphore.WaitAsync(0))
                return;

            bool scannedAccount = false;
            try
            {
                SteamGuardAccount nextAccount = GetNextLoginAccountToMonitor();
                if (nextAccount == null || IsAccountBusyWithRecoveryOrTransfer(nextAccount.Session.SteamID))
                    return;

                scannedAccount = true;
                await ScanLoginAccountAsync(
                    nextAccount,
                    useKnownRequestsCache: true,
                    notifyManualRequests: true,
                    isBackgroundMonitor: true,
                    scanContext: new LoginScanContext(),
                    cancellationToken: cancellationToken);
            }
            finally
            {
                loginActionsSemaphore.Release();
                if (scannedAccount && IsWebTabActive("login-actions") && !IsManualLoginRefreshActive())
                    await PublishCachedLoginActionsAsync();
            }
        }

        private sealed class LoginScanContext
        {
            public string CurrentDeviceIp { get; set; }
            public bool CurrentDeviceIpChecked { get; set; }
        }

        private enum LoginAccountScanOutcome
        {
            Completed,
            RateLimited
        }

        private sealed class ManualLoginRefreshScope
        {
            public bool AllAccounts { get; private set; }
            public HashSet<string> SelectionKeys { get; } = new HashSet<string>(StringComparer.Ordinal);

            public void Add(string selectionKey)
            {
                string normalized = NormalizeLoginRefreshSelection(selectionKey);
                if (normalized == "all")
                {
                    AllAccounts = true;
                    SelectionKeys.Clear();
                }
                else if (!AllAccounts)
                {
                    SelectionKeys.Add(normalized);
                }
            }

            public bool Covers(string selectionKey)
            {
                if (AllAccounts)
                    return true;
                string normalized = NormalizeLoginRefreshSelection(selectionKey);
                return normalized != "all" && SelectionKeys.Contains(normalized);
            }
        }

        internal static string NormalizeLoginRefreshSelection(string selectionKey)
        {
            if (String.IsNullOrWhiteSpace(selectionKey) || String.Equals(selectionKey.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                return "all";
            return selectionKey.Trim();
        }

        private bool IsManualLoginRefreshActive()
        {
            lock (manualLoginRefreshLock)
            {
                return manualLoginRefreshActive;
            }
        }

        private void NotifyManualLoginRequestIfNeeded(
            SteamGuardAccount account,
            PendingLoginRequest request,
            string requestKey,
            bool notifyManualRequests)
        {
            if (notifyManualRequests &&
                manifest?.LoginActionMonitoringEnabled == true &&
                manifest.LoginActionMode == LoginActionModes.Manual &&
                notifiedLoginRequests.Add(requestKey))
            {
                string device = String.IsNullOrWhiteSpace(request.DeviceName) ? request.Platform : request.DeviceName;
                NotifyLoginAction("Steam login approval needed", account.AccountName + ": " + device, ToolTipIcon.Info);
            }
        }

        private bool TryEvaluateAutomaticLoginDecision(
            PendingLoginRequest request,
            LoginScanContext scanContext,
            out string actionMode,
            out LoginApprovalDecision decision,
            out bool allowWhitelistedIp)
        {
            actionMode = manifest?.LoginActionMode ?? LoginActionModes.Manual;
            decision = LoginApprovalDecision.Deny;
            allowWhitelistedIp = false;
            if (manifest == null || !manifest.LoginActionMonitoringEnabled || actionMode == LoginActionModes.Manual)
                return false;

            if (actionMode == LoginActionModes.Deny && manifest.LoginActionAutoAllowIpEnabled)
            {
                bool allowAdditionalIp = !String.IsNullOrWhiteSpace(manifest.LoginActionAutoAllowIp) &&
                    AreSameIpv4Address(request.IPAddress, manifest.LoginActionAutoAllowIp);
                bool allowCurrentDeviceIp = false;
                if (!allowAdditionalIp && manifest.LoginActionAutoAllowCurrentDeviceIp && IsValidIpv4Address(request.IPAddress))
                {
                    if (!scanContext.CurrentDeviceIpChecked)
                        return false;
                    allowCurrentDeviceIp = AreSameIpv4Address(request.IPAddress, scanContext.CurrentDeviceIp);
                }
                allowWhitelistedIp = allowAdditionalIp || allowCurrentDeviceIp;
            }

            decision = actionMode == LoginActionModes.ApprovePersistent || allowWhitelistedIp
                ? LoginApprovalDecision.ApprovePersistent
                : LoginApprovalDecision.Deny;
            return true;
        }

        private async Task<LoginAccountScanOutcome> ScanLoginAccountAsync(
            SteamGuardAccount account,
            bool useKnownRequestsCache,
            bool notifyManualRequests,
            bool isBackgroundMonitor,
            LoginScanContext scanContext,
            CancellationToken cancellationToken)
        {
            if (!IsSessionAvailableForAccountOperations(account) ||
                IsAccountBusyWithRecoveryOrTransfer(account.Session.SteamID))
                return LoginAccountScanOutcome.Completed;

            Dictionary<ulong, PendingLoginRequest> knownRequests = useKnownRequestsCache
                ? pendingLoginRequests.Values
                    .Where(request => request.SteamId == account.Session.SteamID)
                    .GroupBy(request => request.ClientId)
                    .ToDictionary(group => group.Key, group => group.First())
                : null;
            if (isBackgroundMonitor)
                Interlocked.Increment(ref loginMonitorRequestCount);

            LoginApprovalFetchResult result;
            try
            {
                Action<LoginApprovalFetchResult> onFetchResult = fetchResult =>
                {
                    if (fetchResult.ErrorKind == LoginApprovalErrorKind.SessionExpired || fetchResult.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                    {
                        MarkSessionRenewalRequired(account, fetchResult.ErrorMessage);
                        DispatchBackgroundSilentRelogin(account);
                    }
                };

                if (isBackgroundMonitor)
                {
                    (bool acquired, LoginApprovalFetchResult fetched) = await TryRunAvailableSteamAccountOperationNowAsync(
                        account,
                        () => loginApprovalService.FetchPendingRequestsAsync(account, knownRequests, cancellationToken),
                        cancellationToken,
                        onFetchResult);
                    if (!acquired || fetched == null)
                    {
                        ScheduleLoginAccountScan(account, TimeSpan.FromSeconds(5), false);
                        return LoginAccountScanOutcome.Completed;
                    }
                    result = fetched;
                }
                else
                {
                    result = await RunAvailableSteamAccountOperationAsync(
                        account,
                        () => loginApprovalService.FetchPendingRequestsAsync(account, knownRequests, cancellationToken),
                        cancellationToken,
                        onFetchResult);
                }
            }
            catch (SessionUnavailableException)
            {
                return LoginAccountScanOutcome.Completed;
            }

            if (result.ErrorKind == LoginApprovalErrorKind.SessionExpired || result.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                return LoginAccountScanOutcome.Completed;

            if (!IsSessionAvailableForAccountOperations(account))
                return LoginAccountScanOutcome.Completed;

            if (result.ErrorKind == LoginApprovalErrorKind.RateLimited)
            {
                ApplyLoginRateLimit();
                ScheduleLoginAccountScan(account, LoginMonitorMaximumFailureBackoff, true);
                DiagnosticErrorLogger.Log(
                    isBackgroundMonitor ? "Login action monitor" : "Login action refresh",
                    new InvalidOperationException(result.ErrorMessage),
                    isBackgroundMonitor
                        ? "Steam rate limited the login-request monitor."
                        : "Steam rate limited the manual login-request refresh.");
                return LoginAccountScanOutcome.RateLimited;
            }

            if (result.ErrorKind != LoginApprovalErrorKind.None)
            {
                ScheduleLoginAccountFailure(account);
                return LoginAccountScanOutcome.Completed;
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

                if (!manifest.LoginActionMonitoringEnabled || manifest.LoginActionMode == LoginActionModes.Manual)
                {
                    NotifyManualLoginRequestIfNeeded(account, request, requestKey, notifyManualRequests);
                    continue;
                }

                if (manifest.LoginActionMode == LoginActionModes.Deny &&
                    manifest.LoginActionAutoAllowIpEnabled &&
                    manifest.LoginActionAutoAllowCurrentDeviceIp &&
                    !(!String.IsNullOrWhiteSpace(manifest.LoginActionAutoAllowIp) &&
                      AreSameIpv4Address(request.IPAddress, manifest.LoginActionAutoAllowIp)) &&
                    IsValidIpv4Address(request.IPAddress) &&
                    !scanContext.CurrentDeviceIpChecked)
                {
                    scanContext.CurrentDeviceIp = await getCurrentPublicIpv4Async(cancellationToken);
                    scanContext.CurrentDeviceIpChecked = true;
                }

                // Re-evaluate live rules and session availability after any async public-IP lookup.
                if (!IsSessionAvailableForAccountOperations(account))
                    break;
                if (!TryEvaluateAutomaticLoginDecision(
                    request,
                    scanContext,
                    out string currentActionMode,
                    out LoginApprovalDecision decision,
                    out bool allowWhitelistedIp))
                {
                    NotifyManualLoginRequestIfNeeded(account, request, requestKey, notifyManualRequests);
                    continue;
                }
                string actionKey = requestKey + "|" + currentActionMode + "|" + decision;
                if (completedAutomatedLoginActions.Contains(actionKey))
                    continue;

                bool IsAutomaticDecisionStillCurrent()
                {
                    return TryEvaluateAutomaticLoginDecision(
                        request,
                        scanContext,
                        out string liveActionMode,
                        out LoginApprovalDecision liveDecision,
                        out bool liveAllowWhitelistedIp) &&
                        String.Equals(liveActionMode, currentActionMode, StringComparison.Ordinal) &&
                        liveDecision == decision &&
                        liveAllowWhitelistedIp == allowWhitelistedIp;
                }

                LoginApprovalActionResult actionResult;
                try
                {
                    actionResult = await RunAvailableSteamAccountOperationAsync(account,
                        () =>
                        {
                            if (!IsAutomaticDecisionStillCurrent())
                                return Task.FromResult(new LoginApprovalActionResult { Succeeded = false, Skipped = true });

                            return loginApprovalService.RespondAsync(
                                account,
                                request,
                                decision,
                                cancellationToken,
                                shouldSubmitDecision: IsAutomaticDecisionStillCurrent);
                        },
                        cancellationToken,
                        response =>
                        {
                            if (response.ErrorKind == LoginApprovalErrorKind.SessionExpired || response.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                                MarkSessionRenewalRequired(account, response.ErrorMessage);
                        });
                }
                catch (SessionUnavailableException)
                {
                    break;
                }

                if (actionResult.Skipped)
                {
                    NotifyManualLoginRequestIfNeeded(account, request, requestKey, notifyManualRequests);
                    continue;
                }

                if (actionResult.ErrorKind == LoginApprovalErrorKind.RateLimited)
                {
                    ApplyLoginRateLimit();
                    ScheduleLoginAccountScan(account, LoginMonitorMaximumFailureBackoff, true);
                    DiagnosticErrorLogger.Log(
                        isBackgroundMonitor ? "Login action monitor" : "Login action refresh",
                        new InvalidOperationException(actionResult.ErrorMessage),
                        "Steam rate limited an automatic login action.");
                    NotifyManualLoginRequestIfNeeded(account, request, requestKey, notifyManualRequests: true);
                    return LoginAccountScanOutcome.RateLimited;
                }
                if (actionResult.ErrorKind == LoginApprovalErrorKind.SessionExpired || actionResult.ErrorKind == LoginApprovalErrorKind.Unauthorized)
                    break;
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
                    if (!IsAutomaticDecisionStillCurrent())
                    {
                        NotifyManualLoginRequestIfNeeded(account, request, requestKey, notifyManualRequests);
                        continue;
                    }

                    NotifyAutomatedLoginActionOnce(actionKey + "|failed",
                        "Automatic Steam login action failed",
                        account.AccountName + ": " + actionResult.ErrorMessage,
                        ToolTipIcon.Warning);
                }
            }

            if (!IsSessionAvailableForAccountOperations(account))
                return LoginAccountScanOutcome.Completed;
            ReconcilePendingLoginRequestsForAccount(account, result.Requests);
            ScheduleLoginAccountScan(account, LoginMonitorSuccessInterval, true);
            return LoginAccountScanOutcome.Completed;
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
            notifyLoginAction(title, message, icon);
        }

        private void ShowLoginActionNotification(string title, string message, ToolTipIcon icon)
        {
            if (InvokeRequired)
            {
                TryBeginInvoke(() => ShowLoginActionNotification(title, message, icon));
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

            string title = TradeConfirmationClassifier.GetNotificationTitle(confirmation.ConfType);
            string notificationTab = TradeConfirmationClassifier.GetNotificationTab(confirmation.ConfType);
            string detail = String.IsNullOrWhiteSpace(confirmation.Headline) ? "New Steam confirmation" : confirmation.Headline;
            string message = account.AccountName + " · " + detail;
            trayNotificationClickAction = () => OpenTradeConfirmationsFromNotification(notificationTab);
            trayIcon.Visible = true;
            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = message;
            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.ShowBalloonTip(10000);

            var popup = new LoginNotificationPopup(title, message, ToolTipIcon.Info, "Click to open Confirmations");
            popup.NotificationClicked += (sender, args) => OpenTradeConfirmationsFromNotification(notificationTab);
            popup.FormClosed += (sender, args) => activeLoginNotificationPopups.Remove(popup);
            activeLoginNotificationPopups.Add(popup);
            popup.ShowAtBottomRight((activeLoginNotificationPopups.Count - 1) * 126);
        }

        private void UpdateTradePendingCount(int count)
        {
            updateTradePendingCount(count);
        }

        private void UpdateTradePendingCountToWebView(int count)
        {
            if (InvokeRequired)
            {
                TryBeginInvoke(() => UpdateTradePendingCountToWebView(count));
                return;
            }
            if (IsDisposed || Disposing)
                return;

            _ = ExecuteScriptSafelyAsync("setTradePendingCount(" + Math.Max(0, count).ToString(CultureInfo.InvariantCulture) + ");", "Trade confirmation badge");

            var perAccount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<ulong, int> entry in pendingTradeConfirmationCounts)
            {
                if (entry.Key != 0 && entry.Value > 0)
                    perAccount[entry.Key.ToString(CultureInfo.InvariantCulture)] = entry.Value;
            }
            string perAccountJson = JsonConvert.SerializeObject(perAccount);
            _ = ExecuteScriptSafelyAsync($"if (typeof setAccountPendingConfirmations === 'function') setAccountPendingConfirmations({perAccountJson});", "Per-account confirmation counts");
        }

        private void CloseLoginNotificationPopups()
        {
            foreach (LoginNotificationPopup popup in activeLoginNotificationPopups.ToList())
            {
                if (!popup.IsDisposed)
                    popup.Close();
            }
        }

        private static bool HasSameAuthenticatorSecrets(SteamGuardAccount left, SteamGuardAccount right)
        {
            if (left == null || right == null)
                return false;

            return String.Equals(left.SharedSecret, right.SharedSecret, StringComparison.Ordinal) &&
                String.Equals(left.TokenGID, right.TokenGID, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(left.DeviceID, right.DeviceID, StringComparison.OrdinalIgnoreCase);
        }

        private SteamGuardAccount ResolvePendingAccountForUnchangedRevisionLocked(
            SteamGuardAccount originalAccount,
            SessionData expectedSession,
            long expectedStorageRevision)
        {
            ulong steamId = expectedSession.SteamID;
            if (manifest.GetAccountStorageRevision(steamId) != expectedStorageRevision)
                return null;

            if (CanPersistAccountGeneration(originalAccount, expectedSession, allAccounts))
                return originalAccount;

            // A UI-only WebView2 reload (NavigationCompleted -> loadAccountsList) replaces
            // in-memory SteamGuardAccount instances without changing the on-disk record.
            // Promote or update the current loaded instance only when the storage revision
            // is unchanged and the authenticator secrets still match.
            SteamGuardAccount reloadedMatch = FindLoadedAccountBySteamId(allAccounts, steamId);
            if (reloadedMatch != null &&
                !reloadedMatch.FullyEnrolled &&
                HasSameAuthenticatorSecrets(originalAccount, reloadedMatch))
            {
                return reloadedMatch;
            }

            return null;
        }

        internal async Task<long> RefreshAndPersistPendingAccessTokenAsync(
            SteamGuardAccount account,
            long expectedStorageRevision,
            CancellationToken cancellationToken)
        {
            return await RefreshAndPersistPendingAccessTokenAsync(
                account,
                expectedStorageRevision,
                (session, ct) => session.RefreshAccessToken(false, ct),
                cancellationToken);
        }

        internal async Task<long> RefreshAndPersistPendingAccessTokenAsync(
            SteamGuardAccount account,
            long expectedStorageRevision,
            Func<SessionData, CancellationToken, Task> refreshSessionAsync,
            CancellationToken cancellationToken)
        {
            if (refreshSessionAsync == null)
                throw new ArgumentNullException(nameof(refreshSessionAsync));
            SessionData session = account?.Session ?? throw new SessionUnavailableException(account?.AccountName);
            if (manifest != null && manifest.GetAccountStorageRevision(session.SteamID) != expectedStorageRevision)
                throw new StaleAccountGenerationException(account.AccountName);

            string previousAccessToken = session.AccessToken;
            string previousRefreshToken = session.RefreshToken;
            try
            {
                await refreshSessionAsync(session, cancellationToken);
                if (!PersistRefreshedPendingSession(account, session, expectedStorageRevision, out long updatedStorageRevision))
                    throw new IOException("Steam refreshed the session, but Astro SDA could not save it securely.");
                return updatedStorageRevision;
            }
            catch
            {
                // Never leave a non-durable token active only in memory.
                session.AccessToken = previousAccessToken;
                session.RefreshToken = previousRefreshToken;
                throw;
            }
        }

        internal bool PersistRefreshedPendingSession(
            SteamGuardAccount originalAccount,
            SessionData expectedSession,
            long expectedStorageRevision,
            out long updatedStorageRevision)
        {
            updatedStorageRevision = expectedStorageRevision;
            if (manifest == null || originalAccount == null || expectedSession == null || expectedSession.SteamID == 0)
                return false;

            StorageResult saveResult;
            SteamGuardAccount targetAccount;
            lock (accountGenerationLock)
            {
                targetAccount = ResolvePendingAccountForUnchangedRevisionLocked(
                    originalAccount,
                    expectedSession,
                    expectedStorageRevision);
                if (targetAccount == null || targetAccount.Session == null)
                    throw new StaleAccountGenerationException(originalAccount.AccountName);

                bool copiedToReloadedInstance = !Object.ReferenceEquals(targetAccount, originalAccount);
                string previousTargetAccessToken = targetAccount.Session.AccessToken;
                string previousTargetRefreshToken = targetAccount.Session.RefreshToken;
                if (copiedToReloadedInstance)
                {
                    targetAccount.Session.AccessToken = expectedSession.AccessToken;
                    targetAccount.Session.RefreshToken = expectedSession.RefreshToken;
                }

                saveResult = manifest.SaveAccount(
                    targetAccount,
                    manifest.Encrypted,
                    passKey,
                    expectedStorageRevision,
                    allowCreate: false);
                if (saveResult.Succeeded)
                {
                    updatedStorageRevision = saveResult.CommittedStorageRevision
                        ?? manifest.GetAccountStorageRevision(expectedSession.SteamID);
                }
                else if (copiedToReloadedInstance)
                {
                    targetAccount.Session.AccessToken = previousTargetAccessToken;
                    targetAccount.Session.RefreshToken = previousTargetRefreshToken;
                }
            }

            if (!saveResult.Succeeded)
                DiagnosticErrorLogger.Log("Authenticator storage", saveResult.Exception, "The refreshed pending Steam session could not be saved.");
            return saveResult.Succeeded;
        }

        internal bool PersistRecoveredPendingActivation(
            SteamGuardAccount originalAccount,
            SessionData expectedSession,
            long expectedStorageRevision,
            AuthenticatorActivationVerificationResult? verification = null)
        {
            if (manifest == null || originalAccount == null || expectedSession == null || expectedSession.SteamID == 0)
                return false;

            StorageResult saveResult;
            SteamGuardAccount targetAccount;
            lock (accountGenerationLock)
            {
                targetAccount = ResolvePendingAccountForUnchangedRevisionLocked(
                    originalAccount,
                    expectedSession,
                    expectedStorageRevision);
                if (targetAccount == null)
                    throw new StaleAccountGenerationException(originalAccount.AccountName);

                AuthenticatorActivationVerificationResult effectiveVerification = verification
                    ?? new AuthenticatorActivationVerificationResult(
                        AuthenticatorActivationStatus.ActiveForThisRecord,
                        originalAccount.TokenGID,
                        originalAccount.DeviceID);
                if (!LoginForm.TryApplyVerifiedIdentifiers(
                    targetAccount,
                    effectiveVerification,
                    out string previousTargetTokenGid,
                    out string previousTargetDeviceId))
                {
                    return false;
                }

                bool previousTargetEnrolled = targetAccount.FullyEnrolled;
                targetAccount.FullyEnrolled = true;
                saveResult = manifest.SaveAccount(
                    targetAccount,
                    manifest.Encrypted,
                    passKey,
                    expectedStorageRevision,
                    allowCreate: false);
                if (!saveResult.Succeeded)
                {
                    targetAccount.FullyEnrolled = previousTargetEnrolled;
                    LoginForm.RestoreVerifiedIdentifiers(targetAccount, previousTargetTokenGid, previousTargetDeviceId);
                }
                else if (!Object.ReferenceEquals(targetAccount, originalAccount))
                {
                    originalAccount.TokenGID = targetAccount.TokenGID;
                    originalAccount.DeviceID = targetAccount.DeviceID;
                    originalAccount.FullyEnrolled = true;
                }
            }

            if (!saveResult.Succeeded)
                DiagnosticErrorLogger.Log("Authenticator storage", saveResult.Exception, "The verified authenticator activation could not be saved.");
            else if (targetAccount.Session != null && !targetAccount.Session.IsRefreshTokenExpired())
                ClearSessionRecoveryState(targetAccount);
            return saveResult.Succeeded;
        }

        internal SteamGuardAccount ResolveQueuedAccountForOperation(
            SteamGuardAccount queuedAccount,
            SessionData expectedSessionAtQueue,
            long? expectedStorageRevisionAtQueue)
        {
            if (queuedAccount == null)
                return null;

            lock (accountGenerationLock)
            {
                if (expectedSessionAtQueue == null || expectedSessionAtQueue.SteamID == 0)
                    return IsCurrentAccountGeneration(queuedAccount, allAccounts) ? queuedAccount : null;

                ulong steamId = expectedSessionAtQueue.SteamID;
                if (expectedStorageRevisionAtQueue.HasValue && manifest != null &&
                    manifest.GetAccountStorageRevision(steamId) != expectedStorageRevisionAtQueue.Value)
                {
                    return null;
                }

                if (CanUseAccountGeneration(queuedAccount, expectedSessionAtQueue, allAccounts))
                    return queuedAccount;

                if (expectedStorageRevisionAtQueue.HasValue && manifest != null)
                {
                    SteamGuardAccount reloadedMatch = FindLoadedAccountBySteamId(allAccounts, steamId);
                    if (reloadedMatch != null &&
                        reloadedMatch.FullyEnrolled == queuedAccount.FullyEnrolled &&
                        HasSameAuthenticatorSecrets(queuedAccount, reloadedMatch))
                    {
                        return reloadedMatch;
                    }
                }

                return null;
            }
        }

        internal bool PersistLoginSession(SteamGuardAccount account, SessionData expectedSession = null, long? expectedStorageRevision = null)
        {
            if (manifest == null)
                return false;

            StorageResult saveResult;
            lock (accountGenerationLock)
            {
                if (!CanPersistAccountGeneration(account, expectedSession, allAccounts))
                    throw new StaleAccountGenerationException(account?.AccountName);

                saveResult = manifest.SaveAccount(
                    account,
                    manifest.Encrypted,
                    passKey,
                    expectedStorageRevision,
                    allowCreate: false);
            }

            if (!saveResult.Succeeded)
                DiagnosticErrorLogger.Log("Authenticator storage", saveResult.Exception, "The updated Steam login session could not be saved.");
            else if (account?.Session != null && !account.Session.IsRefreshTokenExpired())
                ClearSessionRecoveryState(account);
            return saveResult.Succeeded;
        }

        internal bool IsSessionRenewalRequired(SteamGuardAccount account)
        {
            if (IsPendingEnrollment(account))
                return false;
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

        internal static bool IsPendingEnrollment(SteamGuardAccount account)
        {
            return account != null && !account.FullyEnrolled;
        }

        internal static string GetDisplayLoginToken(SteamGuardAccount account, long currentSteamTime, string existingToken = null)
        {
            if (account == null || IsPendingEnrollment(account))
                return String.Empty;

            return currentSteamTime == 0
                ? (existingToken ?? String.Empty)
                : account.GenerateSteamGuardCodeForTime(currentSteamTime);
        }

        internal static string GetAccountDisplayTitle(SteamGuardAccount account)
        {
            string accountName = account?.AccountName ?? String.Empty;
            return IsPendingEnrollment(account)
                ? "Account: " + accountName + " (pending activation)"
                : "Account: " + accountName;
        }

        internal bool IsTransferFenceOrMarkerActive(ulong steamId)
        {
            return steamId != 0 &&
                (transferFencedSteamIds.ContainsKey(steamId) ||
                 unresolvedTransferMarkerSteamIds.ContainsKey(steamId) ||
                 PendingTransferMarker.HasMarker(steamId));
        }

        private bool IsSessionAvailableForAccountOperations(SteamGuardAccount account)
        {
            if (account?.Session == null || !account.FullyEnrolled)
                return false;

            ulong steamId = account.Session.SteamID;
            if (steamId != 0 &&
                (transferFencedSteamIds.ContainsKey(steamId) ||
                 unresolvedTransferMarkerSteamIds.ContainsKey(steamId)))
                return false;

            return !IsSessionRenewalRequired(account);
        }

        private static bool IsCurrentAccountGeneration(SteamGuardAccount account, SteamGuardAccount[] loadedAccounts)
        {
            return account != null && loadedAccounts != null &&
                loadedAccounts.Any(candidate => Object.ReferenceEquals(candidate, account));
        }

        private static bool CanPersistAccountGeneration(
            SteamGuardAccount account,
            SessionData expectedSession,
            SteamGuardAccount[] loadedAccounts)
        {
            return CanUseAccountGeneration(account, expectedSession, loadedAccounts);
        }

        private static bool CanUseAccountGeneration(
            SteamGuardAccount account,
            SessionData expectedSession,
            SteamGuardAccount[] loadedAccounts)
        {
            return IsCurrentAccountGeneration(account, loadedAccounts) &&
                (expectedSession == null || Object.ReferenceEquals(account.Session, expectedSession));
        }

        private static SteamGuardAccount FindLoadedAccountBySteamId(SteamGuardAccount[] loadedAccounts, ulong steamId)
        {
            if (loadedAccounts == null || steamId == 0)
                return null;

            return loadedAccounts.FirstOrDefault(account => account?.Session?.SteamID == steamId);
        }

        private SteamGuardAccount FindAccountBySelectionKey(string selectionKey)
        {
            return FindAccountBySelectionKey(allAccounts, selectionKey);
        }

        private static SteamGuardAccount FindAccountBySelectionKey(SteamGuardAccount[] accounts, string selectionKey)
        {
            if (String.IsNullOrWhiteSpace(selectionKey) || selectionKey == "all" || accounts == null)
                return null;
            if (ulong.TryParse(selectionKey, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) && steamId != 0)
                return accounts.FirstOrDefault(account => account?.Session?.SteamID == steamId);
            return accounts.FirstOrDefault(account => String.Equals(account?.AccountName, selectionKey, StringComparison.Ordinal));
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
                sessionRenewalPersistBackoffUntilUtc.Remove(steamId);
                loginMonitorSchedules.Remove(steamId);
                unavailableLoginAccounts.Remove(account.AccountName);
            }
        }

        private void MarkSessionRenewalRequired(SteamGuardAccount account, string reason, SessionData expectedSession = null)
        {
            if (account == null || IsPendingEnrollment(account))
                return;

            // A failed operation may complete after the account list has been
            // reloaded. Do not let that stale completion mark the replacement
            // account generation as unavailable.
            lock (accountGenerationLock)
            {
                if (!CanUseAccountGeneration(account, expectedSession, allAccounts))
                    return;

                lock (sessionStateLock)
                {
                    SessionData currentSession = account.Session;
                    if (currentSession == null || currentSession.SteamID == 0 ||
                        (expectedSession != null && !Object.ReferenceEquals(currentSession, expectedSession)))
                        return;

                    ulong steamId = currentSession.SteamID;
                    bool newlyMarkedInMemory = sessionRenewalRequired.Add(steamId);
                    bool needsManifestPersist = manifest != null && !manifest.IsSessionMarkedForRenewal(steamId);
                    bool persistRetryAllowed = !sessionRenewalPersistBackoffUntilUtc.TryGetValue(steamId, out DateTime retryAfterUtc) ||
                        DateTime.UtcNow >= retryAfterUtc;

                    if (!newlyMarkedInMemory && (!needsManifestPersist || !persistRetryAllowed))
                        return;

                    bool persistedNow = false;
                    if (needsManifestPersist && (newlyMarkedInMemory || persistRetryAllowed))
                    {
                        StorageResult stateResult = manifest.SetSessionNeedsRenewal(steamId, true);
                        if (stateResult.Succeeded)
                        {
                            sessionRenewalPersistBackoffUntilUtc.Remove(steamId);
                            persistedNow = true;
                        }
                        else
                        {
                            sessionRenewalPersistBackoffUntilUtc[steamId] = DateTime.UtcNow.Add(SessionRenewalPersistRetryBackoff);
                            DiagnosticErrorLogger.Log("Session recovery", stateResult.Exception, "The account's unavailable session state could not be persisted.");
                        }
                    }

                    if (!newlyMarkedInMemory && !persistedNow)
                        return;

                    loginMonitorSchedules.Remove(steamId);
                    loginMonitoringAccountIndex = 0;
                    unavailableLoginAccounts.Remove(account.AccountName);
                    notifiedUnavailableLoginAccounts.Remove(account.AccountName);
                    foreach (string requestKey in pendingLoginRequests.Keys
                        .Where(key => key.StartsWith(steamId.ToString(CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal))
                        .ToArray())
                    {
                        pendingLoginRequests.Remove(requestKey);
                        PruneLoginRequestBookkeeping(requestKey);
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
        }

        private void PersistExpiredRefreshTokenStates()
        {
            DateTime nowUtc = DateTime.UtcNow;
            foreach (SteamGuardAccount account in allAccounts ?? Array.Empty<SteamGuardAccount>())
            {
                if (IsPendingEnrollment(account))
                    continue;
                SessionData session = account?.Session;
                if (session == null || session.SteamID == 0 || !session.IsRefreshTokenExpired())
                    continue;

                ulong steamId = session.SteamID;
                bool alreadyHandled;
                lock (sessionStateLock)
                {
                    bool inMemory = sessionRenewalRequired.Contains(steamId);
                    bool persisted = manifest == null || manifest.IsSessionMarkedForRenewal(steamId);
                    bool backedOff = sessionRenewalPersistBackoffUntilUtc.TryGetValue(steamId, out DateTime retryAfterUtc) && nowUtc < retryAfterUtc;
                    alreadyHandled = inMemory && (persisted || backedOff);
                }
                if (!alreadyHandled)
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
                if (current is StaleAccountGenerationException)
                    return false;
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
                .Where(account => IsSessionAvailableForAccountOperations(account) &&
                    !IsAccountBusyWithRecoveryOrTransfer(account.Session.SteamID))
                .ToArray();
            if (accountsToMonitor.Length == 0)
                return null;

            DateTime now = DateTime.UtcNow;
            lock (sessionStateLock)
            {
                for (int attempt = 0; attempt < accountsToMonitor.Length; attempt++)
                {
                    loginMonitoringAccountIndex = ((loginMonitoringAccountIndex % accountsToMonitor.Length) + accountsToMonitor.Length) % accountsToMonitor.Length;
                    SteamGuardAccount candidate = accountsToMonitor[loginMonitoringAccountIndex];
                    loginMonitoringAccountIndex = (loginMonitoringAccountIndex + 1) % accountsToMonitor.Length;
                    if (!loginMonitorSchedules.TryGetValue(candidate.Session.SteamID, out LoginMonitorSchedule schedule) || schedule.NextScanUtc <= now)
                        return candidate;
                }
            }

            return null;
        }

        private void ScheduleLoginAccountScan(SteamGuardAccount account, TimeSpan interval, bool resetFailureCount)
        {
            if (!IsSessionAvailableForAccountOperations(account))
                return;

            DateTime nextScanUtc;
            lock (sessionStateLock)
            {
                if (!loginMonitorSchedules.TryGetValue(account.Session.SteamID, out LoginMonitorSchedule schedule))
                {
                    schedule = new LoginMonitorSchedule();
                    loginMonitorSchedules[account.Session.SteamID] = schedule;
                }
                if (resetFailureCount)
                    schedule.ConsecutiveFailures = 0;

                // Stable per-account jitter prevents accounts from re-forming a request burst.
                int jitterMilliseconds = (int)(account.Session.SteamID % 3001) - 1500;
                nextScanUtc = DateTime.UtcNow.Add(interval).AddMilliseconds(jitterMilliseconds);
                schedule.NextScanUtc = nextScanUtc;
            }
            Debug.WriteLine("Login monitor scheduled next scan; account=" + account.Session.SteamID + "; next=" + nextScanUtc.ToString("O") + "; requests=" + Interlocked.Read(ref loginMonitorRequestCount));
        }

        private void ScheduleLoginAccountFailure(SteamGuardAccount account)
        {
            if (!IsSessionAvailableForAccountOperations(account))
                return;

            TimeSpan backoff;
            lock (sessionStateLock)
            {
                if (!loginMonitorSchedules.TryGetValue(account.Session.SteamID, out LoginMonitorSchedule schedule))
                {
                    schedule = new LoginMonitorSchedule();
                    loginMonitorSchedules[account.Session.SteamID] = schedule;
                }
                schedule.ConsecutiveFailures = Math.Min(schedule.ConsecutiveFailures + 1, 10);
                double multiplier = Math.Pow(2, schedule.ConsecutiveFailures - 1);
                backoff = TimeSpan.FromSeconds(Math.Min(
                    LoginMonitorInitialFailureBackoff.TotalSeconds * multiplier,
                    LoginMonitorMaximumFailureBackoff.TotalSeconds));
            }
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

            int cachedCount = 0;
            foreach (Confirmation confirmation in confirmationList)
            {
                string confirmationKey = BuildTradeConfirmationKey(account, confirmation);
                if (IsRecentlyResolved(recentlyResolvedTradeConfirmations, confirmationKey))
                    continue;

                if (!loadedTradeConfirmations.ContainsKey(confirmationKey))
                    cachedCount++;
                loadedTradeConfirmations[confirmationKey] =
                    new LoadedTradeConfirmation { Account = account, Confirmation = confirmation };
            }
            pendingTradeConfirmationCounts[account.Session.SteamID] = cachedCount;
            loadedTradeConfirmationAccounts.Add(account.Session.SteamID);
        }

        private void ReconcilePendingTradeCountForAccount(ulong steamId)
        {
            if (steamId == 0)
                return;

            SteamGuardAccount account = FindLoadedAccountBySteamId(allAccounts, steamId);
            if (!IsSessionAvailableForAccountOperations(account))
            {
                pendingTradeConfirmationCounts.Remove(steamId);
                return;
            }

            string accountPrefix = steamId.ToString(CultureInfo.InvariantCulture) + ":";
            pendingTradeConfirmationCounts[steamId] = loadedTradeConfirmations.Keys
                .Count(key => key.StartsWith(accountPrefix, StringComparison.Ordinal));
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

            string currentSelection = GetCurrentTradeSelection();
            IEnumerable<SteamGuardAccount> accounts = currentSelection == "all"
                ? allAccounts
                : allAccounts.Where(account => account.Session?.SteamID.ToString(CultureInfo.InvariantCulture) == currentSelection || account.AccountName == currentSelection);
            return accounts
                .Where(IsSessionAvailableForAccountOperations)
                .All(account => loadedTradeConfirmationAccounts.Contains(account.Session.SteamID));
        }

        private Task PublishCachedTradesAsync(string errorMessage = null, string selection = null)
        {
            return publishTradesAsync(errorMessage, selection);
        }

        private async Task PublishCachedTradesToWebViewAsync(string errorMessage = null, string selection = null)
        {
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView == null)
                return;

            string selectionToPublish = String.IsNullOrWhiteSpace(selection) ? GetCurrentTradeSelection() : selection;
            IEnumerable<LoadedTradeConfirmation> entries = loadedTradeConfirmations.Values
                .Where(entry => IsSessionAvailableForAccountOperations(entry.Account));
            if (selectionToPublish != "all")
            {
                entries = entries.Where(entry => entry.Account.Session?.SteamID.ToString(CultureInfo.InvariantCulture) == selectionToPublish ||
                    String.Equals(entry.Account.AccountName, selectionToPublish, StringComparison.Ordinal));
            }

            var settings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            string jsonStr = JsonConvert.SerializeObject(
                entries.Select(entry => TradeConfirmationClassifier.CreateWebPayload(
                    BuildTradeConfirmationKey(entry.Account, entry.Confirmation),
                    entry.Account.AccountName,
                    entry.Confirmation)),
                settings);
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

        private string GetCurrentTradeSelection()
        {
            lock (tradeLoadLock)
            {
                return tradeAccountSelection;
            }
        }

        private string GetNormalizedTradeSelection()
        {
            string candidateSelection = GetCurrentTradeSelection();
            SteamGuardAccount selectedAccount = FindAccountBySelectionKey(candidateSelection);
            if (selectedAccount != null && !IsSessionAvailableForAccountOperations(selectedAccount))
            {
                lock (tradeLoadLock)
                {
                    if (String.Equals(tradeAccountSelection, candidateSelection, StringComparison.Ordinal))
                        tradeAccountSelection = "all";
                    candidateSelection = tradeAccountSelection;
                }
            }
            return candidateSelection;
        }

        private async Task LoadCachedTradesAsync(string selectedAccountName)
        {
            if (!String.IsNullOrWhiteSpace(selectedAccountName))
            {
                lock (tradeLoadLock)
                {
                    tradeAccountSelection = selectedAccountName;
                }
            }

            GetNormalizedTradeSelection();
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

        private const int MaximumPublicIpResponseBytes = 256;
        private static readonly TimeSpan PublicIpLookupTimeout = TimeSpan.FromSeconds(5);

        internal static async Task<string> GetCurrentPublicIpv4Async(CancellationToken cancellationToken = default)
        {
            try
            {
                using (ProxyService.ActiveHttpClientLease clientLease = ProxyService.AcquireActiveHttpClient())
                {
                    return await GetCurrentPublicIpv4Async(clientLease.Client, PublicIpLookupTimeout, cancellationToken);
                }
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        internal static async Task<string> GetCurrentPublicIpv4Async(
            HttpClient client,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            try
            {
                using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://api.ipify.org"))
                {
                    timeoutSource.CancelAfter(timeout);
                    using (HttpResponseMessage response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutSource.Token))
                    {
                        if (!response.IsSuccessStatusCode || response.Content == null)
                            return null;
                        if (response.Content.Headers.ContentLength > MaximumPublicIpResponseBytes)
                            return null;

                        string candidate = (await ReadBoundedResponseBodyAsync(
                            response.Content,
                            MaximumPublicIpResponseBytes,
                            "The public IP lookup returned an oversized response.",
                            timeoutSource.Token)).Trim();
                        return IsValidIpv4Address(candidate) ? candidate : null;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
            catch (DecoderFallbackException)
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

        private void OpenTradeConfirmationsFromNotification(string confirmationTab = null)
        {
            trayRestore_Click(this, EventArgs.Empty);
            string script = TradeConfirmationClassifier.IsSelectableTab(confirmationTab)
                ? $"openTradeConfirmations({JsonConvert.SerializeObject(confirmationTab)});"
                : "switchTab('trades');";
            _ = ExecuteScriptSafelyAsync(script, "Trade confirmations navigation");
        }

        internal bool PersistTradeConfirmationSelectedTab(string tabName)
        {
            if (manifest == null || !TradeConfirmationClassifier.IsSelectableTab(tabName))
                return false;

            if (String.Equals(manifest.TradeConfirmationSelectedTab, tabName, StringComparison.Ordinal))
                return true;

            StorageResult saveResult = manifest.SaveSettingsWithResult(staged =>
            {
                staged.TradeConfirmationSelectedTab = tabName;
            });
            if (!saveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log(
                    "Trade confirmation tab",
                    saveResult.Exception,
                    saveResult.UserMessage ?? "The selected confirmation tab could not be saved.");
                return false;
            }

            return true;
        }

        private SteamGuardAccount[] GetLoginActionsRefreshTargets(ManualLoginRefreshScope scope)
        {
            SteamGuardAccount[] accountsSnapshot = allAccounts;
            if (accountsSnapshot == null || accountsSnapshot.Length == 0 || scope == null)
                return Array.Empty<SteamGuardAccount>();

            if (scope.AllAccounts)
                return accountsSnapshot
                    .Where(account => IsSessionAvailableForAccountOperations(account) &&
                        !IsAccountBusyWithRecoveryOrTransfer(account.Session.SteamID))
                    .ToArray();

            var targets = new List<SteamGuardAccount>();
            var seenSteamIds = new HashSet<ulong>();
            foreach (string selectionKey in scope.SelectionKeys)
            {
                SteamGuardAccount account = FindAccountBySelectionKey(accountsSnapshot, selectionKey);
                if (account != null &&
                    IsSessionAvailableForAccountOperations(account) &&
                    !IsAccountBusyWithRecoveryOrTransfer(account.Session.SteamID) &&
                    seenSteamIds.Add(account.Session.SteamID))
                    targets.Add(account);
            }
            return targets.ToArray();
        }

        private async Task FetchLoginActionsForManualRefreshCoreAsync(ManualLoginRefreshScope scope, CancellationToken cancellationToken)
        {
            if (manifest == null || loginApprovalService == null || allAccounts == null)
                return;
            if (cancellationToken.IsCancellationRequested)
                return;
            if (TryGetLoginRateLimitMessage(out _))
                return;
            PersistExpiredRefreshTokenStates();
            DispatchEligibleSilentReloginsForExpiredAccounts(allAccounts);

            SteamGuardAccount[] targetAccounts = GetLoginActionsRefreshTargets(scope);
            if (targetAccounts.Length == 0)
                return;

            var scanContext = new LoginScanContext();
            foreach (SteamGuardAccount account in targetAccounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetLoginRateLimitMessage(out _))
                    return;

                LoginAccountScanOutcome outcome = await ScanLoginAccountAsync(
                    account,
                    useKnownRequestsCache: false,
                    notifyManualRequests: false,
                    isBackgroundMonitor: false,
                    scanContext: scanContext,
                    cancellationToken: cancellationToken);
                if (outcome == LoginAccountScanOutcome.RateLimited)
                    return;
            }
        }

        internal string BuildCachedLoginActionsPayloadJson()
        {
            var jsonSettings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            return JsonConvert.SerializeObject(new
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
                    isNewLogin = request.IsNewLogin,
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
                    isNewLogin = attempt.Request.IsNewLogin,
                    locationMismatch = attempt.Request.LocationMismatch,
                    highUsageLogin = attempt.Request.HighUsageLogin,
                    outcome = attempt.Outcome,
                    occurredAtUtc = attempt.OccurredAtUtc.ToString("O")
                })
            }, jsonSettings);
        }

        private Task PublishCachedLoginActionsAsync()
        {
            return publishLoginActionsAsync();
        }

        private async Task PublishCachedLoginActionsToWebViewAsync()
        {
            CoreWebView2 coreWebView = GetCoreWebView2IfAvailable();
            if (coreWebView == null)
                return;

            string json = BuildCachedLoginActionsPayloadJson();
            try
            {
                await coreWebView.ExecuteScriptAsync("loadLoginActions(" + json + ");");
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Login actions UI", ex, "The login-action list could not be returned to the UI.");
            }
        }

        internal async Task RefreshLoginActionsAsync(string selectionKey = null)
        {
            Task waitTask = null;
            long runnerId = 0;
            lock (manualLoginRefreshLock)
            {
                if (!manualLoginRefreshActive)
                {
                    manualLoginRefreshActive = true;
                    manualLoginRefreshScanStarted = false;
                    manualLoginRefreshPublishing = false;
                    runnerId = ++manualLoginRefreshRunnerSequence;
                    manualLoginRefreshOwnerId = runnerId;
                    activeManualLoginRefreshScope = new ManualLoginRefreshScope();
                    activeManualLoginRefreshScope.Add(selectionKey);
                    activeManualLoginRefreshCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else if (!manualLoginRefreshScanStarted)
                {
                    activeManualLoginRefreshScope.Add(selectionKey);
                    waitTask = activeManualLoginRefreshCompletion.Task;
                }
                else if (!manualLoginRefreshPublishing && activeManualLoginRefreshScope.Covers(selectionKey))
                {
                    waitTask = activeManualLoginRefreshCompletion.Task;
                }
                else
                {
                    if (queuedManualLoginRefreshScope == null)
                    {
                        queuedManualLoginRefreshScope = new ManualLoginRefreshScope();
                        queuedManualLoginRefreshCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    queuedManualLoginRefreshScope.Add(selectionKey);
                    waitTask = queuedManualLoginRefreshCompletion.Task;
                }
            }

            if (runnerId == 0)
            {
                try
                {
                    await waitTask;
                }
                catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
                {
                    // The form is closing; cancellation is expected.
                }
                return;
            }

            try
            {
                await RunCoalescedManualLoginRefreshesAsync(runnerId);
            }
            finally
            {
                List<TaskCompletionSource<bool>> remainingCompletions = null;
                lock (manualLoginRefreshLock)
                {
                    if (manualLoginRefreshOwnerId == runnerId)
                    {
                        manualLoginRefreshActive = false;
                        manualLoginRefreshScanStarted = false;
                        manualLoginRefreshPublishing = false;
                        manualLoginRefreshOwnerId = 0;
                        activeManualLoginRefreshScope = null;
                        queuedManualLoginRefreshScope = null;
                        if (activeManualLoginRefreshCompletion != null)
                        {
                            remainingCompletions = remainingCompletions ?? new List<TaskCompletionSource<bool>>();
                            remainingCompletions.Add(activeManualLoginRefreshCompletion);
                            activeManualLoginRefreshCompletion = null;
                        }
                        if (queuedManualLoginRefreshCompletion != null)
                        {
                            remainingCompletions = remainingCompletions ?? new List<TaskCompletionSource<bool>>();
                            remainingCompletions.Add(queuedManualLoginRefreshCompletion);
                            queuedManualLoginRefreshCompletion = null;
                        }
                    }
                }
                if (remainingCompletions != null)
                {
                    foreach (TaskCompletionSource<bool> completion in remainingCompletions)
                        completion.TrySetResult(true);
                }
            }
        }

        private async Task RunCoalescedManualLoginRefreshesAsync(long runnerId)
        {
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            while (true)
            {
                TaskCompletionSource<bool> currentCompletion = null;
                try
                {
                    if (manifest != null && loginApprovalService != null && allAccounts != null && !cancellationToken.IsCancellationRequested)
                    {
                        await loginActionsSemaphore.WaitAsync(cancellationToken);
                        try
                        {
                            ManualLoginRefreshScope scopeToScan;
                            lock (manualLoginRefreshLock)
                            {
                                manualLoginRefreshScanStarted = true;
                                scopeToScan = activeManualLoginRefreshScope;
                                currentCompletion = activeManualLoginRefreshCompletion;
                            }

                            await FetchLoginActionsForManualRefreshCoreAsync(scopeToScan, cancellationToken);
                        }
                        finally
                        {
                            loginActionsSemaphore.Release();
                        }
                    }
                    else
                    {
                        lock (manualLoginRefreshLock)
                        {
                            manualLoginRefreshScanStarted = true;
                            currentCompletion = activeManualLoginRefreshCompletion;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SessionUnavailableException)
                {
                    // The account was invalidated while waiting for its operation lock.
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Login action refresh", ex, "The manual login-request refresh did not complete.");
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                bool shouldPublish = false;
                lock (manualLoginRefreshLock)
                {
                    if (currentCompletion == null)
                        currentCompletion = activeManualLoginRefreshCompletion;

                    if (queuedManualLoginRefreshScope == null)
                    {
                        manualLoginRefreshScanStarted = true;
                        manualLoginRefreshPublishing = true;
                        shouldPublish = true;
                    }
                }

                if (shouldPublish && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await PublishCachedLoginActionsAsync();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Login actions UI", ex, "The login-action list could not be returned to the UI.");
                    }
                }

                bool continueLoop = false;
                TaskCompletionSource<bool> canceledQueuedCompletion = null;
                lock (manualLoginRefreshLock)
                {
                    manualLoginRefreshPublishing = false;
                    if (!cancellationToken.IsCancellationRequested && queuedManualLoginRefreshScope != null)
                    {
                        activeManualLoginRefreshScope = queuedManualLoginRefreshScope;
                        activeManualLoginRefreshCompletion = queuedManualLoginRefreshCompletion;
                        queuedManualLoginRefreshScope = null;
                        queuedManualLoginRefreshCompletion = null;
                        manualLoginRefreshScanStarted = false;
                        continueLoop = true;
                    }
                    else if (manualLoginRefreshOwnerId == runnerId)
                    {
                        manualLoginRefreshActive = false;
                        manualLoginRefreshScanStarted = false;
                        manualLoginRefreshOwnerId = 0;
                        activeManualLoginRefreshScope = null;
                        activeManualLoginRefreshCompletion = null;
                        queuedManualLoginRefreshScope = null;
                        canceledQueuedCompletion = queuedManualLoginRefreshCompletion;
                        queuedManualLoginRefreshCompletion = null;
                    }
                }

                currentCompletion?.TrySetResult(true);
                canceledQueuedCompletion?.TrySetResult(true);

                if (!continueLoop)
                    break;
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
                _ = RefreshLoginActionsAsync(steamId.ToString(CultureInfo.InvariantCulture));
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
            if (IsPendingEnrollment(currentAccount))
                return;
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

            SessionData expectedSessionAtQueue = account.Session;
            ulong steamIdAtQueue = expectedSessionAtQueue?.SteamID ?? 0;
            long? expectedStorageRevisionAtQueue = (manifest != null && steamIdAtQueue != 0)
                ? manifest.GetAccountStorageRevision(steamIdAtQueue)
                : (long?)null;

            return await RunSteamAccountOperationAsync(account, () =>
            {
                SteamGuardAccount currentTarget = ResolveQueuedAccountForOperation(
                    account,
                    expectedSessionAtQueue,
                    expectedStorageRevisionAtQueue);
                if (currentTarget == null)
                    return Task.FromResult(false);

                ulong steamId = currentTarget.Session?.SteamID ?? 0;
                if (IsTransferFenceOrMarkerActive(steamId))
                {
                    AstroMessageBox.Show(
                        "This account has an active or unresolved authenticator transfer. Resolve or dismiss the transfer notice before renewing its session.",
                        "Login Again",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return Task.FromResult(false);
                }

                (bool succeeded, string resolvedPassKey) = showRefreshLoginDialog(currentTarget, passKey, expectedStorageRevisionAtQueue);
                if (succeeded)
                {
                    AdoptResolvedPassKey(resolvedPassKey);
                }
                if (succeeded && currentTarget.Session != null)
                {
                    ClearSessionRecoveryState(currentTarget);
                    InvalidateTradeConfirmationCache(currentTarget);
                }
                return Task.FromResult(succeeded);
            });
        }

        private async Task RefreshLoginAccountFromWebAsync(ulong steamId)
        {
            try
            {
                SteamGuardAccount account = allAccounts?.FirstOrDefault(item => item?.Session?.SteamID == steamId);
                if (account == null)
                    return;

                await PromptRefreshLoginAsync(account);
                loadAccountsList(steamId);
            }
            catch (OperationCanceledException) when (lifetimeCancellationSource.IsCancellationRequested)
            {
                // The form is closing; cancellation is expected.
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Session renewal", ex, "The account session renewal command failed.");
                AstroMessageBox.Show(
                    "The account session could not be renewed. The existing account data was kept. Try again or restart Astro SDA.",
                    "Session renewal",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Load UI with the current account info, this is run every second
        /// </summary>
        private void loadAccountInfo()
        {
            if (currentAccount != null)
            {
                bool pendingActivation = IsPendingEnrollment(currentAccount);
                string token = GetDisplayLoginToken(currentAccount, steamTime, txtLoginToken.Text);
                txtLoginToken.Text = token;
                groupAccount.Text = GetAccountDisplayTitle(currentAccount);
                btnCopy.Enabled = !pendingActivation;

                string jsToken = JsonConvert.SerializeObject(token ?? String.Empty);
                string jsAccountName = JsonConvert.SerializeObject(currentAccount.AccountName ?? String.Empty);
                string jsSteamId = JsonConvert.SerializeObject(currentAccount.Session?.SteamID.ToString(CultureInfo.InvariantCulture) ?? String.Empty);
                string jsExpired = IsSessionRenewalRequired(currentAccount).ToString().ToLowerInvariant();
                string jsPendingActivation = pendingActivation.ToString().ToLowerInvariant();
                _ = ExecuteScriptSafelyAsync($"updateToken({jsToken})", "Authenticator token UI");
                _ = ExecuteScriptSafelyAsync($"updateCurrentAccount({jsAccountName}, {jsExpired}, {jsSteamId}, {jsPendingActivation})", "Current account UI");
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

            if (manifest != null && !manifest.Encrypted && !String.IsNullOrEmpty(passKey))
            {
                passKey = null;
                btnManageEncryption.Text = "Setup Encryption";
            }

            lock (accountGenerationLock)
            {
                SteamGuardAccount[] loadedAccounts = manifest.GetAllAccounts(passKey);
                allAccounts = loadedAccounts;
                var activeSteamIds = new HashSet<ulong>(allAccounts.Where(account => account.Session != null).Select(account => account.Session.SteamID));
                var activeAccountNames = new HashSet<string>(allAccounts.Select(account => account.AccountName), StringComparer.Ordinal);
                RebindTradeConfirmationCache();
                foreach (ulong steamId in pendingTradeConfirmationCounts.Keys.Where(steamId => !activeSteamIds.Contains(steamId)).ToArray())
                    pendingTradeConfirmationCounts.Remove(steamId);
                loadedTradeConfirmationAccounts.RemoveWhere(steamId => !activeSteamIds.Contains(steamId));
                tradeConfirmationScheduler.PruneRemovedAccounts(activeSteamIds);
                lock (sessionStateLock)
                {
                    foreach (ulong steamId in loginMonitorSchedules.Keys.Where(steamId => !activeSteamIds.Contains(steamId)).ToArray())
                        loginMonitorSchedules.Remove(steamId);
                    sessionRenewalRequired.RemoveWhere(steamId => !activeSteamIds.Contains(steamId));
                    foreach (ulong steamId in sessionRenewalPersistBackoffUntilUtc.Keys.Where(id => !activeSteamIds.Contains(id)).ToArray())
                        sessionRenewalPersistBackoffUntilUtc.Remove(steamId);
                }
                foreach (string accountName in unavailableLoginAccounts.Keys.Where(accountName => !activeAccountNames.Contains(accountName)).ToArray())
                    unavailableLoginAccounts.Remove(accountName);
                UpdateTradePendingCount(pendingTradeConfirmationCounts.Values.Sum());
            }

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
            bool canCopyCurrentAccount = hasAccounts && !IsPendingEnrollment(currentAccount);
            btnTradeConfirmations.Enabled = hasAccounts;
            btnCopy.Enabled = canCopyCurrentAccount;
            menuDeactivateAuthenticator.Enabled = hasAccounts && IsSessionAvailableForAccountOperations(currentAccount);
            btnLoginViaQr.Enabled = hasAccounts && IsSessionAvailableForAccountOperations(currentAccount);

            if (canCopyCurrentAccount)
            {
                AstroTheme.StyleSurfaceButton(btnCopy);
            }
            else
            {
                AstroTheme.StyleDisabledGlassButton(btnCopy);
            }

            if (btnLoginViaQr.Enabled)
            {
                AstroTheme.StyleSurfaceButton(btnLoginViaQr);
            }
            else
            {
                AstroTheme.StyleDisabledGlassButton(btnLoginViaQr);
            }

            if (GetCoreWebView2IfAvailable() != null)
            {
                Dictionary<ulong, SavedCredentialStatus> credentialStatuses = manifest.GetSavedCredentialStatuses(passKey);
                var accounts = allAccounts.Select(a => BuildWebAccountPayload(a, credentialStatuses)).ToArray();
                string jsonAccounts = JsonConvert.SerializeObject(accounts);
                _ = ExecuteScriptSafelyAsync($"updateAccountList({jsonAccounts})", "Account list UI");
                _ = ExecuteScriptSafelyAsync($"updateEncryptionState({manifest.Encrypted.ToString().ToLowerInvariant()}, {hasAccounts.ToString().ToLowerInvariant()})", "Encryption state UI");
                PublishProxyStatusToWebView();
                DispatchAvatarFetchesForAccounts(allAccounts);
            }
        }

        private void PublishProxyStatusToWebView()
        {
            if (manifest == null || GetCoreWebView2IfAvailable() == null)
                return;

            string scheme = String.Equals(manifest.ProxyScheme, "https", StringComparison.OrdinalIgnoreCase) ? "HTTPS" : "HTTP";
            string jsScheme = JsonConvert.SerializeObject(scheme);
            _ = ExecuteScriptSafelyAsync(
                $"if (typeof updateProxyStatus === 'function') updateProxyStatus({manifest.ProxyEnabled.ToString().ToLowerInvariant()}, {jsScheme});",
                "Proxy status UI");
        }

        private void DispatchAvatarFetchesForAccounts(SteamGuardAccount[] accounts)
        {
            if (accounts == null || accounts.Length == 0 || lifetimeCancellationSource.IsCancellationRequested)
                return;

            ulong[] steamIds = accounts
                .Select(a => a?.Session?.SteamID ?? 0UL)
                .Where(id => id != 0 && !steamAvatarService.IsCacheFresh(id))
                .Distinct()
                .ToArray();

            if (steamIds.Length == 0)
                return;

            _ = Task.Run(async () =>
            {
                foreach (ulong steamId in steamIds)
                {
                    if (lifetimeCancellationSource.IsCancellationRequested || IsDisposed || Disposing)
                        return;

                    string dataUrl = await steamAvatarService.GetOrFetchAvatarDataUrlAsync(steamId, lifetimeCancellationSource.Token).ConfigureAwait(false);
                    if (String.IsNullOrEmpty(dataUrl) || lifetimeCancellationSource.IsCancellationRequested || IsDisposed || Disposing)
                        continue;

                    string jsSteamId = JsonConvert.SerializeObject(steamId.ToString(CultureInfo.InvariantCulture));
                    string jsDataUrl = JsonConvert.SerializeObject(dataUrl);
                    TryBeginInvoke(() =>
                    {
                        if (!IsDisposed && !Disposing)
                        {
                            _ = ExecuteScriptSafelyAsync(
                                $"if (typeof updateAccountAvatar === 'function') updateAccountAvatar({jsSteamId}, {jsDataUrl});",
                                "Account avatar UI");
                        }
                    });
                }
            }, CancellationToken.None);
        }

        private object BuildWebAccountPayload(SteamGuardAccount account, IReadOnlyDictionary<ulong, SavedCredentialStatus> credentialStatuses = null)
        {
            ulong steamId = account?.Session?.SteamID ?? 0;
            AccountConfirmationSettings confSettings = manifest != null && steamId != 0
                ? manifest.GetAccountConfirmationSettings(steamId)
                : new AccountConfirmationSettings();
            AccountScanHealthSnapshot health = steamId != 0
                ? tradeConfirmationScheduler.GetHealthSnapshot(steamId)
                : null;
            SavedCredentialStatus credStatus = SavedCredentialStatus.None;
            if (steamId != 0 && credentialStatuses != null)
            {
                if (credentialStatuses.TryGetValue(steamId, out SavedCredentialStatus found))
                    credStatus = found;
            }
            else if (steamId != 0 && manifest != null && manifest.Encrypted && manifest.AllowSavedSteamPasswords && !String.IsNullOrEmpty(passKey))
            {
                credStatus = manifest.GetSavedCredentialStatus(steamId, passKey);
            }

            bool passwordRejected = credStatus.PasswordRejected ||
                (steamId != 0 && silentReloginService.IsGenerationRejectedInMemory(steamId, credStatus.CredentialGenerationId));

            bool pendingActivation = IsPendingEnrollment(account);
            int pendingConfCount = steamId != 0 && pendingTradeConfirmationCounts.TryGetValue(steamId, out int countVal) ? countVal : 0;
            return new
            {
                name = account?.AccountName,
                steamId = steamId == 0 ? null : steamId.ToString(CultureInfo.InvariantCulture),
                avatarUrl = steamId == 0 ? null : steamAvatarService.TryGetCachedAvatarDataUrl(steamId),
                pendingConfirmations = pendingConfCount,
                sessionExpired = IsSessionRenewalRequired(account),
                needsRenewal = IsSessionRenewalRequired(account),
                pendingActivation = pendingActivation,
                autoConfirmOverrideEnabled = confSettings.AutoConfirmOverrideEnabled,
                autoConfirmTrades = confSettings.AutoConfirmTrades,
                autoConfirmMarket = confSettings.AutoConfirmMarketTransactions,
                tradeConfirmationCheckInterval = confSettings.TradeConfirmationCheckInterval,
                healthState = (health?.State ?? AccountScanHealthState.Idle).ToString().ToLowerInvariant(),
                lastScanError = health?.LastErrorSummary,
                hasSavedPassword = credStatus.HasSavedPassword,
                passwordRejected = passwordRejected,
                recoveryInProgress = !pendingActivation && steamId != 0 && (silentReloginService.IsRecoveryInProgress(steamId) || activeSilentReloginTasks.ContainsKey(steamId)),
                canSavePasswords = !pendingActivation && manifest != null && manifest.Encrypted && manifest.AllowSavedSteamPasswords
            };
        }

        private void PublishAccountHealthMetadata(ulong steamId)
        {
            if (steamId == 0 || GetCoreWebView2IfAvailable() == null || manifest == null)
                return;

            SteamGuardAccount targetAccount = FindLoadedAccountBySteamId(allAccounts, steamId);
            bool pendingActivation = IsPendingEnrollment(targetAccount);
            AccountConfirmationSettings confSettings = manifest.GetAccountConfirmationSettings(steamId);
            AccountScanHealthSnapshot health = tradeConfirmationScheduler.GetHealthSnapshot(steamId);
            SavedCredentialStatus credStatus = manifest.Encrypted && manifest.AllowSavedSteamPasswords && !String.IsNullOrEmpty(passKey)
                ? manifest.GetSavedCredentialStatus(steamId, passKey)
                : SavedCredentialStatus.None;
            bool passwordRejected = credStatus.PasswordRejected ||
                silentReloginService.IsGenerationRejectedInMemory(steamId, credStatus.CredentialGenerationId);

            var metadata = new
            {
                steamId = steamId.ToString(CultureInfo.InvariantCulture),
                pendingActivation = pendingActivation,
                autoConfirmOverrideEnabled = confSettings.AutoConfirmOverrideEnabled,
                autoConfirmTrades = confSettings.AutoConfirmTrades,
                autoConfirmMarket = confSettings.AutoConfirmMarketTransactions,
                tradeConfirmationCheckInterval = confSettings.TradeConfirmationCheckInterval,
                healthState = health.State.ToString().ToLowerInvariant(),
                lastScanError = health.LastErrorSummary,
                hasSavedPassword = credStatus.HasSavedPassword,
                passwordRejected = passwordRejected,
                recoveryInProgress = !pendingActivation && (silentReloginService.IsRecoveryInProgress(steamId) || activeSilentReloginTasks.ContainsKey(steamId)),
                canSavePasswords = !pendingActivation && manifest.Encrypted && manifest.AllowSavedSteamPasswords
            };
            string json = JsonConvert.SerializeObject(metadata);
            _ = ExecuteScriptSafelyAsync($"updateAccountMetadata({json});", "Account metadata UI");
        }

        private void RebindTradeConfirmationCache()
        {
            foreach (string confirmationKey in loadedTradeConfirmations.Keys.ToArray())
            {
                if (!TryParseTradeConfirmationKey(confirmationKey, out ulong steamId))
                {
                    loadedTradeConfirmations.Remove(confirmationKey);
                    continue;
                }

                SteamGuardAccount currentAccount = FindLoadedAccountBySteamId(allAccounts, steamId);
                if (currentAccount == null)
                {
                    loadedTradeConfirmations.Remove(confirmationKey);
                    continue;
                }

                loadedTradeConfirmations[confirmationKey].Account = currentAccount;
            }
        }

        private void listAccounts_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control)
            {
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;

                    if (manifest == null)
                        return;

                    int selectedIndex = listAccounts.SelectedIndex;
                    int delta = e.KeyCode == Keys.Up ? -1 : 1;
                    string[] visibleNames = listAccounts.Items.Cast<object>().Select(item => item?.ToString()).ToArray();
                    SteamGuardAccount[] accountsSnapshot;
                    lock (accountGenerationLock)
                    {
                        accountsSnapshot = allAccounts;
                    }

                    if (!TryResolveAccountForReorder(accountsSnapshot, visibleNames, selectedIndex, out ulong sourceSteamId))
                        return;

                    long revisionBefore = manifest.StorageRevision;
                    StorageResult moveResult = manifest.MoveEntryBySteamIdOffsetWithResult(sourceSteamId, delta);
                    if (!moveResult.Succeeded)
                    {
                        DiagnosticErrorLogger.Log("Account reordering", moveResult.Exception, "The updated account order could not be saved.");
                        if (moveResult.FailureKind == StorageFailureKind.Validation)
                        {
                            RefreshAccountListPreservingFilter(sourceSteamId);
                            return;
                        }

                        AstroMessageBox.Show(
                            moveResult.UserMessage ?? "The account order could not be saved.",
                            "Account Order",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }

                    if (manifest.StorageRevision != revisionBefore)
                        RefreshAccountListPreservingFilter(sourceSteamId);
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

        private void RefreshAccountListPreservingFilter(ulong? preferredSteamId)
        {
            loadAccountsList(preferredSteamId);
            if (!String.IsNullOrEmpty(txtAccSearch.Text))
            {
                txtAccSearch_TextChanged(this, EventArgs.Empty);
                string preferredName = preferredSteamId.HasValue
                    ? allAccounts.FirstOrDefault(account => account.Session?.SteamID == preferredSteamId.Value)?.AccountName
                    : null;
                int filteredIndex = !String.IsNullOrWhiteSpace(preferredName)
                    ? listAccounts.Items.IndexOf(preferredName)
                    : -1;
                if (filteredIndex >= 0)
                    listAccounts.SelectedIndex = filteredIndex;
                else if (listAccounts.Items.Count > 0)
                    listAccounts.SelectedIndex = 0;
            }
        }

        internal static bool TryResolveAccountForReorder(
            SteamGuardAccount[] accounts,
            IReadOnlyList<string> visibleAccountNames,
            int selectedIndex,
            out ulong steamId)
        {
            steamId = 0;

            if (accounts == null || visibleAccountNames == null ||
                selectedIndex < 0 || selectedIndex >= visibleAccountNames.Count)
            {
                return false;
            }

            return TryResolveUniqueSteamIdByAccountName(accounts, visibleAccountNames[selectedIndex], out steamId);
        }

        private static bool TryResolveUniqueSteamIdByAccountName(
            SteamGuardAccount[] accounts,
            string accountName,
            out ulong steamId)
        {
            steamId = 0;
            if (String.IsNullOrEmpty(accountName))
                return false;

            bool matched = false;
            ulong matchedSteamId = 0;
            foreach (SteamGuardAccount account in accounts)
            {
                if (account == null || !String.Equals(account.AccountName, accountName, StringComparison.Ordinal))
                    continue;

                if (matched || account.Session == null || account.Session.SteamID == 0)
                    return false;

                matched = true;
                matchedSteamId = account.Session.SteamID;
            }

            if (!matched)
                return false;

            steamId = matchedSteamId;
            return true;
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

            timerTradesPopup.Interval = 1000;
            timerTradesPopup.Enabled = true;
            tradeConfirmationScheduler.OnGlobalSettingsChanged(steamId => ResolveAccountConfirmationPolicyForMonitor(steamId).Interval);
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
        }

        private void StartStartupUpdateCheck()
        {
            if (manifest == null)
                return;

            TryStartStartupUpdateCheck(ref startupUpdateCheckStarted, () => _ = CheckForUpdatesAsync(true));
        }

        private void StartUpdateCheckFromActivation()
        {
            if (manifest == null)
                return;

            if (TryStartStartupUpdateCheck(ref startupUpdateCheckStarted, () => _ = CheckForUpdatesAsync(true)))
                return;

            _ = CheckForUpdatesAsync(true);
        }

        internal static bool TryStartStartupUpdateCheck(ref bool startupUpdateCheckStarted, Action startCheck)
        {
            if (startCheck == null)
                throw new ArgumentNullException(nameof(startCheck));
            if (startupUpdateCheckStarted)
                return false;

            startupUpdateCheckStarted = true;
            startCheck();
            return true;
        }

        private static async Task<StorageResult> ExecuteSettingsSaveWithUpdaterPreferenceAsync(
            UpdatePreferenceRevisionTracker updatePreferenceRevision,
            Manifest manifest,
            bool requestedValue,
            Func<Task<StorageResult>> prepareSettingsAsync,
            Action<Manifest> updateSettings)
        {
            if (updatePreferenceRevision == null)
                throw new ArgumentNullException(nameof(updatePreferenceRevision));
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (prepareSettingsAsync == null)
                throw new ArgumentNullException(nameof(prepareSettingsAsync));
            if (updateSettings == null)
                throw new ArgumentNullException(nameof(updateSettings));

            UpdatePreferenceRevisionTracker.SettingsSaveOperation settingsSave = updatePreferenceRevision.BeginSettingsSave();
            StorageResult preparationResult = await prepareSettingsAsync();
            if (!preparationResult.Succeeded)
                return preparationResult;

            return settingsSave.SaveSettingsWithResult(manifest, requestedValue, updateSettings);
        }

        // Logic for version checking
        private static readonly HttpClient defaultUpdateClient = CreateDefaultUpdateClient();
        private const int MaximumUpdateResponseBytes = 1024 * 1024;
        private const string AutomaticUpdateChecksDisabledMessage = "Automatic update checks are disabled. You can check manually anytime.";
        private static readonly TimeSpan UpdateRequestTimeout = TimeSpan.FromSeconds(20);
        private bool isCheckingForUpdates = false;
        private string updateCheckStatusMessage = String.Empty;
        private string updateCheckStatusTone = "info";
        private Version availableUpdateVersion;
        private string availableUpdateDownloadUrl;

        internal static SocketsHttpHandler CreateDefaultUpdateHttpHandler()
        {
            return new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = SteamNetworkConfiguration.DefaultPooledConnectionLifetime
            };
        }

        private static HttpClient CreateDefaultUpdateClient()
        {
            return new HttpClient(CreateDefaultUpdateHttpHandler());
        }

        internal async Task CheckForUpdatesAsync(bool isStartupCheck)
        {
            if (manifest == null)
                return;

            if (isCheckingForUpdates)
            {
                if (!isStartupCheck)
                    await PublishUpdateCheckStateAsync(true, "A check for updates is already in progress.", "info");
                return;
            }

            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            if (cancellationToken.IsCancellationRequested)
                return;

            if (!ShouldRunUpdateCheck(isStartupCheck, manifest.CheckForUpdates))
            {
                if (String.IsNullOrEmpty(updateCheckStatusMessage))
                    await PublishUpdateCheckStateAsync(false, AutomaticUpdateChecksDisabledMessage, "info");
                return;
            }

            string previousStatusMessage = updateCheckStatusMessage;
            string previousStatusTone = updateCheckStatusTone;
            bool restoreIdleStateAfterAbort = false;
            isCheckingForUpdates = true;

            try
            {
                await PublishUpdateCheckStateAsync(true, "Checking for updates...", "info");
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (!ShouldRunUpdateCheck(isStartupCheck, manifest.CheckForUpdates))
                {
                    restoreIdleStateAfterAbort = true;
                }
                else
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
                            requestCancellationToken.ThrowIfCancellationRequested();

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

                            Version installedVersion = new Version(Application.ProductVersion);
                            await ShowUpdateResultAsync(parsedVersion, installedVersion, downloadUrl, isStartupCheck, cancellationToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The form is closing; cancellation is expected.
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Application update check", ex, "The update service request failed.");
                await PublishUpdateCheckStateAsync(false, "Could not check for updates. Try again.", "error");
                if (!isStartupCheck && !lifetimeCancellationSource.IsCancellationRequested)
                    AstroMessageBox.Show("Failed to check for updates.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                isCheckingForUpdates = false;
            }

            if (restoreIdleStateAfterAbort)
            {
                bool hasPreviousStatus = !String.IsNullOrEmpty(previousStatusMessage);
                await PublishUpdateCheckStateAsync(
                    false,
                    hasPreviousStatus ? previousStatusMessage : AutomaticUpdateChecksDisabledMessage,
                    hasPreviousStatus ? previousStatusTone : "info");
            }
        }

        private async Task ShowUpdateResultAsync(Version latestVersion, Version installedVersion, string downloadUrl, bool isStartupCheck, CancellationToken cancellationToken)
        {
            if (IsUpdateAvailable(latestVersion, installedVersion))
            {
                availableUpdateVersion = latestVersion;
                availableUpdateDownloadUrl = downloadUrl;
                labelUpdate.Text = "Download new version"; // Show the user a new version is available if they press no

                await PublishAvailableUpdateAsync();
                await PublishUpdateCheckStateAsync(false, String.Format("Version {0} is available.", latestVersion), "success");
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldShowUpdateNotification(isStartupCheck, manifest.CheckForUpdates))
                    return;

                string checkboxText = isStartupCheck ? "Don't check for updates on launch" : null;
                (DialogResult updateDialog, bool isChecked) = promptForUpdate(latestVersion, checkboxText);

                if (isStartupCheck && isChecked)
                    DisableStartupUpdateChecks();

                if (updateDialog == DialogResult.Yes)
                    OpenUpdateUrl(downloadUrl);
            }
            else
            {
                availableUpdateVersion = null;
                availableUpdateDownloadUrl = null;
                await PublishAvailableUpdateAsync();
                string latestMessage = String.Format("You are using the latest version: {0}", Application.ProductVersion);
                await PublishUpdateCheckStateAsync(false, latestMessage, "success");
                cancellationToken.ThrowIfCancellationRequested();
                if (!isStartupCheck)
                    AstroMessageBox.Show(latestMessage, "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void DisableStartupUpdateChecks()
        {
            if (manifest == null)
                return;

            StorageResult saveResult = updatePreferenceRevision.DisableStartupUpdateChecks(manifest);
            if (!saveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Application update settings", saveResult.Exception, "The automatic update preference could not be saved.");
                return;
            }

            _ = ExecuteScriptSafelyAsync("setCheckForUpdates(false);", "Update setting UI");
        }

        private async Task PublishAvailableUpdateAsync()
        {
            if (availableUpdateVersion == null || String.IsNullOrWhiteSpace(availableUpdateDownloadUrl))
            {
                await ExecuteScriptSafelyAsync("clearUpdateAvailable();", "Available update UI");
                return;
            }

            string jsVersion = JsonConvert.SerializeObject(availableUpdateVersion.ToString());
            await ExecuteScriptSafelyAsync($"setUpdateAvailable({jsVersion});", "Available update UI");
        }

        private void OpenAvailableUpdate()
        {
            if (!String.IsNullOrWhiteSpace(availableUpdateDownloadUrl))
                OpenUpdateUrl(availableUpdateDownloadUrl);
        }

        private void OpenUpdateUrl(string downloadUrl)
        {
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true })
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

        private async Task PublishUpdateCheckStateAsync(bool checking, string message, string tone)
        {
            updateCheckStatusMessage = message ?? String.Empty;
            updateCheckStatusTone = tone ?? "info";

            if (publishUpdateCheckStateAsync != null)
            {
                await publishUpdateCheckStateAsync(checking, updateCheckStatusMessage, updateCheckStatusTone);
                return;
            }

            if (GetCoreWebView2IfAvailable() == null)
                return;

            string jsMessage = JsonConvert.SerializeObject(updateCheckStatusMessage);
            string jsTone = JsonConvert.SerializeObject(updateCheckStatusTone);
            string jsChecking = checking.ToString().ToLowerInvariant();
            await ExecuteScriptSafelyAsync($"updateCheckState({jsChecking}, {jsMessage}, {jsTone});", "Update status UI");
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
            this.MinimumSize = new Size(440, 560);
            this.MaximumSize = Size.Empty;
            this.Size = new Size(920, 660);
            this.MaximizeBox = true;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.BackColor = AstroTheme.Background;
            this.Text = "Astro SDA";

            // Create loading screen
            Panel loadingPanel = new Panel();
            loadingPanel.Dock = DockStyle.Fill;
            loadingPanel.BackColor = AstroTheme.Background;
            
            Label lblLoading = new Label();
            lblLoading.Text = "Loading Astro UI...";
            lblLoading.ForeColor = AstroTheme.OnSurfaceVariant;
            lblLoading.AutoSize = false;
            lblLoading.Size = new Size(400, 30);
            lblLoading.TextAlign = ContentAlignment.MiddleCenter;
            lblLoading.Font = new Font("Segoe UI", 10F, FontStyle.Regular);
            loadingPanel.Controls.Add(lblLoading);

            int spinnerAngle = 0;
            loadingPanel.Paint += (s, e) => {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int size = 40;
                int x = (loadingPanel.Width - size) / 2;
                int y = (loadingPanel.Height - size) / 2 - 20;
                
                using (Pen bgPen = new Pen(Color.FromArgb(32, 255, 255, 255), 3.5f))
                {
                    e.Graphics.DrawEllipse(bgPen, x, y, size, size);
                }
                using (Pen fgPen = new Pen(AstroTheme.PrimaryAccent, 3.5f))
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
                string savedConfirmationTab = TradeConfirmationClassifier.NormalizeSelectableTab(manifest?.TradeConfirmationSelectedTab);
                _ = ExecuteScriptSafelyAsync($"hydrateConfirmationTab({JsonConvert.SerializeObject(savedConfirmationTab)});", "Trade confirmation tab UI");
                _ = PublishAvailableUpdateAsync();
                _ = PublishUpdateCheckStateAsync(isCheckingForUpdates, updateCheckStatusMessage, updateCheckStatusTone);

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
            settings["checkForUpdates"] = manifest.CheckForUpdates;
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
            settings["manifestEncrypted"] = manifest.Encrypted;
            settings["allowSavedSteamPasswords"] = manifest.Encrypted && manifest.AllowSavedSteamPasswords;
            settings["aesGcmSupported"] = EncryptedCredentialStore.IsSupported;

            _ = ExecuteScriptSafelyAsync($"loadSettings({settings.ToString(Newtonsoft.Json.Formatting.None)})", "Settings UI");
            PublishProxyStatusToWebView();
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

                ProxyTestResult result = await proxyTestAsync(configuration, token);
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

        internal async Task SaveSettingsAsync(JObject payload)
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

                bool tradeConfirmationCustomIntervalEnabled = (bool?)payload["tradeConfirmationCustomIntervalEnabled"] ?? false;
                int tradeConfirmationCheckInterval = Math.Clamp((int?)payload["tradeConfirmationCheckInterval"] ?? 15, 3, 3600);
                bool autoConfirmMarket = (bool?)payload["autoConfirmMarket"] ?? false;
                bool autoConfirmTrades = (bool?)payload["autoConfirmTrades"] ?? false;
                bool minimizeToTray = (bool?)payload["minimizeToTray"] ?? false;
                bool checkForUpdates = (bool?)payload["checkForUpdates"] ?? manifest.CheckForUpdates;
                bool diagnosticLogging = (bool?)payload["diagnosticErrorLoggingEnabled"] ?? false;
                bool loginMonitoring = ((bool?)payload["loginActionMonitoringEnabled"] ?? false) || newLoginActionMode != LoginActionModes.Manual;
                bool requestedAllowSavedPasswords = (bool?)payload["allowSavedSteamPasswords"] ?? manifest.AllowSavedSteamPasswords;
                bool effectiveAllowSavedPasswords = manifest.Encrypted && requestedAllowSavedPasswords;

                if (manifest.AllowSavedSteamPasswords && !effectiveAllowSavedPasswords)
                {
                    DialogResult confirmDisable = AstroMessageBox.Show(
                        "Disabling saved Steam passwords will permanently delete all saved passwords and pause automatic silent session renewal. Continue?",
                        "Disable Saved Steam Passwords",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (confirmDisable != DialogResult.Yes)
                    {
                        await PublishSettingsSaveFailureAsync("Settings were not saved.");
                        return;
                    }
                }

                bool allowSavedPasswordsChanged = effectiveAllowSavedPasswords != manifest.AllowSavedSteamPasswords;
                StorageResult saveResult = await ExecuteSettingsSaveWithUpdaterPreferenceAsync(
                    updatePreferenceRevision,
                    manifest,
                    checkForUpdates,
                    async () =>
                    {
                        if (!proxyConfiguration.Enabled)
                        {
                            CancelProxyOperation();
                        }
                        else
                        {
                            proxySaveSource = BeginProxyOperation();
                            ProxyTestResult proxyResult = await proxyTestAsync(proxyConfiguration, proxySaveSource.Token);
                            if (!proxyResult.Succeeded)
                            {
                                return StorageResult.Failure(
                                    StorageFailureKind.Validation,
                                    proxyResult.Message + " Settings were not saved.");
                            }

                            proxySaveSource.Token.ThrowIfCancellationRequested();
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
                            DialogResult confirmation = confirmAutomaticLoginAction(
                                "This setting will " + actionDescription + " for every managed account, including requests that are already pending. Login monitoring will remain enabled while this rule is active. Continue?");
                            if (confirmation != DialogResult.Yes)
                            {
                                return StorageResult.Failure(StorageFailureKind.Validation, "Settings were not saved.");
                            }
                        }

                        if (proxyConfiguration.Enabled)
                            proxySaveSource.Token.ThrowIfCancellationRequested();

                        return StorageResult.Success();
                    },
                    staged =>
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
                    staged.AllowSavedSteamPasswords = effectiveAllowSavedPasswords;
                });
                if (!saveResult.Succeeded)
                {
                    DiagnosticErrorLogger.Log("Settings storage", saveResult.Exception, "Application settings could not be saved atomically.");
                    await PublishSettingsSaveFailureAsync(saveResult.UserMessage ?? "The application settings could not be saved.");
                    return;
                }

                if (allowSavedPasswordsChanged)
                {
                    if (!effectiveAllowSavedPasswords && saveResult.ObsoleteCleanupPending)
                    {
                        AstroMessageBox.Show(
                            "Saved Steam passwords were disabled. The previous encrypted credential file could not be deleted immediately and will be removed automatically on the next save or restart.",
                            "Saved Steam Passwords",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    loadAccountsList(currentAccount?.Session?.SteamID);
                }

                applyProxy(proxyConfiguration);
                DiagnosticErrorLogger.Configure(manifest.DiagnosticErrorLoggingEnabled);
                ConfigureTradeConfirmationMonitor();
                ConfigureLoginActionsMonitor();
                settingsDirty = false;
                SendSettingsToWebView();
                if (manifest.CheckForUpdates &&
                    !isCheckingForUpdates &&
                    String.Equals(updateCheckStatusMessage, AutomaticUpdateChecksDisabledMessage, StringComparison.Ordinal))
                {
                    await PublishUpdateCheckStateAsync(false, String.Empty, "info");
                }
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

        internal static bool IsUpdateAvailable(Version latestVersion, Version installedVersion)
        {
            if (latestVersion == null || installedVersion == null)
                return false;

            return NormalizeReleaseVersion(latestVersion).CompareTo(NormalizeReleaseVersion(installedVersion)) > 0;
        }

        internal static bool ShouldRunUpdateCheck(bool isStartupCheck, bool checkForUpdates)
        {
            return !isStartupCheck || checkForUpdates;
        }

        // Also re-evaluated after an in-flight startup request finishes in case the user opted out mid-request.
        internal static bool ShouldShowUpdateNotification(bool isStartupCheck, bool checkForUpdates)
        {
            return ShouldRunUpdateCheck(isStartupCheck, checkForUpdates);
        }

        private static Version NormalizeReleaseVersion(Version version)
        {
            return new Version(
                version.Major,
                version.Minor,
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision));
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

        private static Task<string> ReadResponseBodyWithLimitAsync(HttpContent content, CancellationToken cancellationToken)
        {
            return ReadBoundedResponseBodyAsync(
                content,
                MaximumUpdateResponseBytes,
                "The update service returned an oversized response.",
                cancellationToken);
        }

        private static async Task<string> ReadBoundedResponseBodyAsync(
            HttpContent content,
            int maximumBytes,
            string oversizedMessage,
            CancellationToken cancellationToken)
        {
            if (content == null)
                throw new InvalidDataException("The service returned an empty response.");
            if (content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException(oversizedMessage);

            using (Stream responseStream = await content.ReadAsStreamAsync(cancellationToken))
            using (MemoryStream responseBody = new MemoryStream())
            {
                byte[] buffer = new byte[Math.Min(81920, Math.Max(256, maximumBytes + 1))];
                int bytesRead;
                int totalBytes = 0;
                while (true)
                {
                    int maxBytesToRead = Math.Min(buffer.Length, (maximumBytes - totalBytes) + 1);
                    bytesRead = await responseStream.ReadAsync(buffer, 0, maxBytesToRead, cancellationToken);
                    if (bytesRead <= 0)
                        break;
                    if (bytesRead > maximumBytes - totalBytes)
                        throw new InvalidDataException(oversizedMessage);
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
                "minimizeToTray", "checkForUpdates", "diagnosticErrorLoggingEnabled", "loginActionMonitoringEnabled",
                "allowSavedSteamPasswords"
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

        private static bool TryParseAccountSettingsPayload(JObject payload, out ulong steamId, out AccountConfirmationSettings settings)
        {
            steamId = 0;
            settings = null;
            if (payload == null)
                return false;

            if (!TryGetWebMessageString(payload, "steamId", 32, out string steamIdText, required: true) ||
                !ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out steamId) ||
                steamId == 0)
            {
                return false;
            }

            JToken overrideToken = payload["overrideEnabled"];
            JToken tradesToken = payload["autoConfirmTrades"];
            JToken marketToken = payload["autoConfirmMarket"];
            JToken intervalToken = payload["checkInterval"];

            if (overrideToken?.Type != JTokenType.Boolean ||
                tradesToken?.Type != JTokenType.Boolean ||
                marketToken?.Type != JTokenType.Boolean)
            {
                return false;
            }

            int? intervalSeconds = null;
            if (intervalToken != null && intervalToken.Type != JTokenType.Null)
            {
                if (intervalToken.Type != JTokenType.Integer)
                    return false;

                long parsedInterval = intervalToken.Value<long>();
                if (parsedInterval < 3 || parsedInterval > 3600)
                    return false;

                intervalSeconds = (int)parsedInterval;
            }

            settings = new AccountConfirmationSettings
            {
                AutoConfirmOverrideEnabled = overrideToken.Value<bool>(),
                AutoConfirmTrades = tradesToken.Value<bool>(),
                AutoConfirmMarketTransactions = marketToken.Value<bool>(),
                TradeConfirmationCheckInterval = intervalSeconds
            };
            return true;
        }

        private async Task SaveAccountSettingsFromPayloadAsync(JObject payload)
        {
            if (manifest == null)
            {
                await ExecuteScriptSafelyAsync($"accountSettingsSaveFailed({JsonConvert.SerializeObject("No account manifest is loaded.")});", "Account options UI");
                return;
            }

            if (!TryParseAccountSettingsPayload(payload, out ulong steamId, out AccountConfirmationSettings settings))
            {
                await ExecuteScriptSafelyAsync($"accountSettingsSaveFailed({JsonConvert.SerializeObject("The account options message was invalid. Check interval must be between 3 and 3600 seconds.")});", "Account options UI");
                return;
            }

            SteamGuardAccount targetAccount = FindLoadedAccountBySteamId(allAccounts, steamId);
            if (IsPendingEnrollment(targetAccount))
            {
                await ExecuteScriptSafelyAsync($"accountSettingsSaveFailed({JsonConvert.SerializeObject("Per-account auto-confirmation settings cannot be changed while this authenticator is pending Steam activation.")});", "Account options UI");
                return;
            }

            StorageResult result = manifest.SaveAccountConfirmationSettings(steamId, settings);
            if (!result.Succeeded)
            {
                DiagnosticErrorLogger.Log("Account options storage", result.Exception, "Per-account confirmation settings could not be saved.");
                await ExecuteScriptSafelyAsync($"accountSettingsSaveFailed({JsonConvert.SerializeObject(result.UserMessage ?? "The account options could not be saved.")});", "Account options UI");
                return;
            }

            ResolvedAccountConfirmationPolicy resolved = ResolveAccountConfirmationPolicyForMonitor(steamId);
            tradeConfirmationScheduler.OnSettingsChanged(steamId, resolved.Interval);
            AccountScanHealthSnapshot health = tradeConfirmationScheduler.GetHealthSnapshot(steamId);
            var savedPayload = new
            {
                steamId = steamId.ToString(CultureInfo.InvariantCulture),
                autoConfirmOverrideEnabled = settings.AutoConfirmOverrideEnabled,
                autoConfirmTrades = settings.AutoConfirmTrades,
                autoConfirmMarket = settings.AutoConfirmMarketTransactions,
                tradeConfirmationCheckInterval = settings.TradeConfirmationCheckInterval,
                healthState = health.State.ToString().ToLowerInvariant(),
                lastScanError = health.LastErrorSummary
            };
            string json = JsonConvert.SerializeObject(savedPayload);
            await ExecuteScriptSafelyAsync($"accountSettingsSaved({json});", "Account options UI");
        }

        private void PromptSetAccountSavedPassword(ulong steamId)
        {
            if (manifest == null || !manifest.Encrypted || !manifest.AllowSavedSteamPasswords || String.IsNullOrEmpty(passKey))
            {
                AstroMessageBox.Show("Enable manifest encryption and opt into saved Steam passwords in Settings first.", "Saved Steam Password", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SteamGuardAccount target = FindLoadedAccountBySteamId(allAccounts, steamId);
            if (target == null)
            {
                AstroMessageBox.Show("That account is no longer managed by this app.", "Saved Steam Password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (IsPendingEnrollment(target))
            {
                AstroMessageBox.Show("Verify and activate this authenticator on Steam before saving a password for silent renewal.", "Saved Steam Password", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string enteredPassword;
            using (InputForm passwordForm = new InputForm("Enter the Steam password for " + target.AccountName + " to enable silent session renewal:", true))
            {
                passwordForm.ShowInputDialog(this);
                if (passwordForm.Canceled || !passwordForm.UiReady || passwordForm.LoadFailed)
                    return;

                enteredPassword = passwordForm.txtBox.Text;
            }

            if (String.IsNullOrEmpty(enteredPassword))
            {
                AstroMessageBox.Show("A Steam password is required.", "Saved Steam Password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            StorageResult saveResult = manifest.SaveAccountPassword(steamId, enteredPassword, passKey);
            if (!saveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Credential storage", saveResult.Exception, "The saved Steam password could not be stored.");
                AstroMessageBox.Show(saveResult.UserMessage ?? "The Steam password could not be saved.", "Saved Steam Password", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            silentReloginService.ClearAccountRuntimeState(steamId);
            PublishAccountHealthMetadata(steamId);
            if (saveResult.ObsoleteCleanupPending)
            {
                AstroMessageBox.Show("Password saved. The previous encrypted credential file could not be deleted immediately and will be cleaned up automatically on the next save or restart.", "Saved Steam Password", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            if (!IsSessionAvailableForAccountOperations(target))
            {
                DispatchBackgroundSilentRelogin(target);
            }
        }

        private void RemoveAccountSavedPassword(ulong steamId)
        {
            if (manifest == null || String.IsNullOrEmpty(passKey))
                return;

            StorageResult result = manifest.RemoveAccountPassword(steamId, passKey);
            if (!result.Succeeded)
            {
                DiagnosticErrorLogger.Log("Credential storage", result.Exception, "The saved Steam password could not be removed.");
                AstroMessageBox.Show(result.UserMessage ?? "The saved Steam password could not be removed.", "Saved Steam Password", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            silentReloginService.ClearAccountRuntimeState(steamId);
            PublishAccountHealthMetadata(steamId);
            if (result.ObsoleteCleanupPending)
            {
                AstroMessageBox.Show("Saved password removed. The previous encrypted credential file could not be deleted immediately and will be cleaned up automatically on the next save or restart.", "Saved Steam Password", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private bool VerifyManifestPasskeyForSensitiveAction(string actionDescription)
        {
            if (manifest == null || !manifest.Encrypted)
                return true;

            using (InputForm passForm = new InputForm("Enter your manifest passkey to " + actionDescription + ":", true))
            {
                passForm.ShowInputDialog(this);
                if (passForm.Canceled || !passForm.UiReady || passForm.LoadFailed)
                    return false;

                if (!manifest.VerifyPasskey(passForm.txtBox.Text))
                {
                    AstroMessageBox.Show("Incorrect manifest passkey.", "Verification Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }

            return true;
        }

        private bool TryPromptIncludeSession(out bool includeSession)
        {
            includeSession = false;
            DialogResult choice = AstroMessageBox.ShowWithCustomButtons(
                "Do you want to include active Steam session tokens in this export?\n\n" +
                "Strip Tokens (Recommended): Keeps SteamID and authenticator secrets only. Importing will ask you to log in to Steam.\n" +
                "Include Session: Also copies current login tokens for immediate restore.",
                "Export Session Tokens",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question,
                "Strip Tokens",
                "Include Session",
                "Cancel");

            if (choice == DialogResult.Cancel)
                return false;

            includeSession = choice == DialogResult.No;
            return true;
        }

        private bool TryPromptZipExportPasskey(out string exportPassKey)
        {
            exportPassKey = null;
            if (manifest != null && manifest.Encrypted && !String.IsNullOrEmpty(passKey))
            {
                DialogResult useCurrent = AstroMessageBox.ShowWithCustomButtons(
                    "Encrypt this backup ZIP using your current manifest passkey or choose a different passkey?",
                    "Encrypted Backup Passkey",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question,
                    "Use Current Passkey",
                    "Custom Passkey",
                    "Cancel");

                if (useCurrent == DialogResult.Cancel)
                    return false;
                if (useCurrent == DialogResult.Yes)
                {
                    exportPassKey = passKey;
                    return true;
                }
            }

            string first;
            using (InputForm passForm = new InputForm("Enter a passkey to encrypt this backup ZIP:", true))
            {
                passForm.ShowInputDialog(this);
                if (passForm.Canceled || !passForm.UiReady || passForm.LoadFailed)
                    return false;
                first = passForm.txtBox.Text;
            }
            if (String.IsNullOrEmpty(first))
            {
                AstroMessageBox.Show("A non-empty passkey is required for an encrypted ZIP backup.", "Export Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            using (InputForm confirmForm = new InputForm("Confirm the passkey for this backup ZIP:", true))
            {
                confirmForm.ShowInputDialog(this);
                if (confirmForm.Canceled || !confirmForm.UiReady || confirmForm.LoadFailed)
                    return false;
                if (!String.Equals(first, confirmForm.txtBox.Text, StringComparison.Ordinal))
                {
                    AstroMessageBox.Show("The passkeys did not match.", "Export Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }

            exportPassKey = first;
            return true;
        }

        private void ExportSingleAccountFile(ulong steamId, string format)
        {
            SteamGuardAccount target = FindLoadedAccountBySteamId(allAccounts, steamId);
            if (target == null)
            {
                AstroMessageBox.Show("That account is no longer managed by this app.", "Export Account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!VerifyManifestPasskeyForSensitiveAction("export account secrets for " + target.AccountName))
                return;

            if (format == "plain_mafile")
            {
                DialogResult warn = AstroMessageBox.Show(
                    "Exporting a plain .maFile writes your unencrypted authenticator secrets (shared_secret, identity_secret, and revocation_code) to disk.\n\nSaved Steam passwords are never included. Continue?",
                    "Unencrypted Backup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (warn != DialogResult.Yes)
                    return;

                if (!TryPromptIncludeSession(out bool includeSession))
                    return;

                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "Steam Authenticator File (*.maFile)|*.maFile";
                    sfd.FileName = AccountExportService.SanitizeExportFilename(target.AccountName, steamId, extension: ".maFile");
                    if (sfd.ShowDialog(this) != DialogResult.OK)
                        return;

                    try
                    {
                        AccountExportService.ExportPlainMaFile(target, sfd.FileName, includeSession);
                        AstroMessageBox.Show("Account exported to:\n" + sfd.FileName, "Export Account", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Account export", ex, "Could not export account .maFile.");
                        AstroMessageBox.Show(ex.Message, "Export Account", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                return;
            }

            if (format == "encrypted_zip")
            {
                if (!TryPromptIncludeSession(out bool includeSession))
                    return;
                if (!TryPromptZipExportPasskey(out string exportPassKey))
                    return;

                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "Encrypted Backup ZIP (*.zip)|*.zip";
                    sfd.FileName = AccountExportService.SanitizeExportFilename(target.AccountName + "_backup", steamId, extension: ".zip");
                    if (sfd.ShowDialog(this) != DialogResult.OK)
                        return;

                    try
                    {
                        AccountExportService.ExportEncryptedZip(new[] { target }, sfd.FileName, exportPassKey, includeSession);
                        AstroMessageBox.Show("Encrypted SDA backup saved to:\n" + sfd.FileName, "Export Account", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Account export", ex, "Could not export encrypted account backup ZIP.");
                        AstroMessageBox.Show(ex.Message, "Export Account", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ExportAllAccountsArchive(string format)
        {
            SteamGuardAccount[] snapshot = allAccounts ?? Array.Empty<SteamGuardAccount>();
            if (snapshot.Length == 0)
            {
                AstroMessageBox.Show("There are no managed accounts to export.", "Export All Accounts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!VerifyManifestPasskeyForSensitiveAction("export all managed accounts"))
                return;

            if (format == "plain_zip")
            {
                DialogResult warn = AstroMessageBox.Show(
                    "Exporting an unencrypted ZIP writes plaintext authenticator secrets (shared_secret, identity_secret, and revocation_code) for all " + snapshot.Length + " account(s).\n\nSaved Steam passwords are never included. Continue?",
                    "Unencrypted Bulk Backup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (warn != DialogResult.Yes)
                    return;

                if (!TryPromptIncludeSession(out bool includeSession))
                    return;

                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "Backup Archive (*.zip)|*.zip";
                    sfd.FileName = "AstroSDA_accounts_plain_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".zip";
                    if (sfd.ShowDialog(this) != DialogResult.OK)
                        return;

                    try
                    {
                        AccountExportService.ExportPlainZip(snapshot, sfd.FileName, includeSession);
                        AstroMessageBox.Show("Exported " + snapshot.Length + " account(s) to:\n" + sfd.FileName, "Export All Accounts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Bulk account export", ex, "Could not export plain account archive.");
                        AstroMessageBox.Show(ex.Message, "Export All Accounts", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                return;
            }

            if (format == "encrypted_zip")
            {
                if (!TryPromptIncludeSession(out bool includeSession))
                    return;
                if (!TryPromptZipExportPasskey(out string exportPassKey))
                    return;

                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "Encrypted Backup ZIP (*.zip)|*.zip";
                    sfd.FileName = "AstroSDA_accounts_encrypted_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".zip";
                    if (sfd.ShowDialog(this) != DialogResult.OK)
                        return;

                    try
                    {
                        AccountExportService.ExportEncryptedZip(snapshot, sfd.FileName, exportPassKey, includeSession);
                        AstroMessageBox.Show("Encrypted backup of " + snapshot.Length + " account(s) saved to:\n" + sfd.FileName, "Export All Accounts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Bulk account export", ex, "Could not export encrypted account archive.");
                        AstroMessageBox.Show(ex.Message, "Export All Accounts", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OpenSecretQrDialog(ulong steamId)
        {
            SteamGuardAccount target = FindLoadedAccountBySteamId(allAccounts, steamId);
            if (target == null)
            {
                AstroMessageBox.Show("That account is no longer managed by this app.", "Export Secret QR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (IsPendingEnrollment(target))
            {
                AstroMessageBox.Show("This authenticator is pending Steam activation. Verify activation on Steam before exporting its secret QR code.", "Export Secret QR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!VerifyManifestPasskeyForSensitiveAction("view the secret QR code for " + target.AccountName))
                return;

            using (ExportQrForm qrForm = new ExportQrForm(target))
            {
                qrForm.ShowDialog(this);
            }
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
            else if (action == "transfer_account")
            {
                TryBeginInvoke(() => StartAuthenticatorTransferFlow());
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
            else if (action == "cancel_qr")
            {
                qrScanCancelRequested = true;
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
                    btnLoginViaQr.Enabled = IsSessionAvailableForAccountOperations(selectedAccount);
                    menuDeactivateAuthenticator.Enabled = IsSessionAvailableForAccountOperations(selectedAccount);
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
            else if (action == "open_update_available")
            {
                OpenAvailableUpdate();
            }
            else if (action == "check_for_updates")
            {
                _ = CheckForUpdatesAsync(false);
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
            else if (action == "save_account_settings")
            {
                await SaveAccountSettingsFromPayloadAsync(payload);
            }
            else if (action == "set_account_password")
            {
                if (TryGetWebMessageString(payload, "steamId", 32, out string steamIdText, required: true) &&
                    ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) &&
                    steamId != 0)
                {
                    TryBeginInvoke(() => PromptSetAccountSavedPassword(steamId));
                }
            }
            else if (action == "remove_account_password")
            {
                if (TryGetWebMessageString(payload, "steamId", 32, out string steamIdText, required: true) &&
                    ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) &&
                    steamId != 0)
                {
                    TryBeginInvoke(() => RemoveAccountSavedPassword(steamId));
                }
            }
            else if (action == "export_account_file")
            {
                if (TryGetWebMessageString(payload, "steamId", 32, out string steamIdText, required: true) &&
                    ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) &&
                    steamId != 0 &&
                    TryGetWebMessageString(payload, "format", 32, out string format, required: true) &&
                    (format == "plain_mafile" || format == "encrypted_zip"))
                {
                    TryBeginInvoke(() => ExportSingleAccountFile(steamId, format));
                }
            }
            else if (action == "export_all_accounts")
            {
                if (TryGetWebMessageString(payload, "format", 32, out string format, required: true) &&
                    (format == "encrypted_zip" || format == "plain_zip"))
                {
                    TryBeginInvoke(() => ExportAllAccountsArchive(format));
                }
            }
            else if (action == "open_export_qr")
            {
                if (TryGetWebMessageString(payload, "steamId", 32, out string steamIdText, required: true) &&
                    ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) &&
                    steamId != 0)
                {
                    TryBeginInvoke(() => OpenSecretQrDialog(steamId));
                }
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
            else if (action == "confirmation_tab_changed")
            {
                if (!TryGetWebMessageString(payload, "tabName", 32, out string tabName, true))
                    return;
                bool persisted = PersistTradeConfirmationSelectedTab(tabName);
                _ = ExecuteScriptSafelyAsync(
                    $"confirmationTabPersistCompleted({JsonConvert.SerializeObject(tabName)}, {persisted.ToString().ToLowerInvariant()});",
                    "Trade confirmation tab persistence");
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
                if (TryGetWebMessageString(payload, "steamId", 32, out string steamIdText))
                    _ = RefreshLoginActionsAsync(steamIdText);
                else
                    _ = PublishCachedLoginActionsAsync();
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
                // Do not open a modal WebView form while handling the current
                // WebView2 message callback. The nested callback can prevent the
                // secondary WebView2 instance from completing navigation and leave
                // the refresh dialog on its loading screen. Queue the modal work
                // after this callback has returned, like the other form actions.
                TryBeginInvoke(() => _ = RefreshLoginAccountFromWebAsync(steamId));
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

        internal async Task RespondToTradeConfirmationAsync(string confirmationKey, bool accept)
        {
            if (!TryParseTradeConfirmationKey(confirmationKey, out ulong steamId))
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
            int? updatedPendingCount = null;
            try
            {
                if (!loadedTradeConfirmations.TryGetValue(confirmationKey, out LoadedTradeConfirmation entry))
                    throw new InvalidOperationException("This confirmation is no longer available. Refresh the list and try again.");

                SteamGuardAccount account = FindLoadedAccountBySteamId(allAccounts, steamId);
                if (account == null)
                    throw new InvalidOperationException("This account is no longer available. Refresh the account list and try again.");
                entry.Account = account;

                if (TryGetTradeRateLimitMessage(out string rateLimitMessage))
                {
                    if (!lifetimeCancellationSource.IsCancellationRequested)
                        showTradeConfirmationMessage(rateLimitMessage, MessageBoxIcon.Warning);
                    return;
                }

                await confirmationsSemaphore.WaitAsync(lifetimeCancellationSource.Token);
                try
                {
                    if (TryGetTradeRateLimitMessage(out string activeRateLimitMessage))
                        throw new TradeRateLimitedException(activeRateLimitMessage);

                    bool steamAcceptedAction = await respondTradeConfirmationAsync(
                        account,
                        entry.Confirmation,
                        accept,
                        lifetimeCancellationSource.Token);
                    if (!steamAcceptedAction)
                        throw new InvalidOperationException("Steam did not accept that confirmation action. The confirmation remains pending.");
                    actionSucceeded = true;
                    MarkRecentlyResolved(recentlyResolvedTradeConfirmations, confirmationKey);
                    completedTradeActions[confirmationKey] = DateTime.UtcNow;
                    loadedTradeConfirmations.Remove(confirmationKey);
                    ReconcilePendingTradeCountForAccount(steamId);
                    updatedPendingCount = pendingTradeConfirmationCounts.Values.Sum();
                }
                finally
                {
                    confirmationsSemaphore.Release();
                }
            }
            catch (TradeRateLimitedException ex)
            {
                if (!lifetimeCancellationSource.IsCancellationRequested)
                    showTradeConfirmationMessage(ex.Message, MessageBoxIcon.Warning);
            }
            catch (Exception ex) when (IsRateLimitedResponse(ex))
            {
                TradeRateLimitedException rateLimitException = ApplyTradeRateLimit(ex);
                DiagnosticErrorLogger.Log("Trade confirmation action", rateLimitException, accept ? "Accept request rate limited." : "Deny request rate limited.");
                if (!lifetimeCancellationSource.IsCancellationRequested)
                    showTradeConfirmationMessage(rateLimitException.Message, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Trade confirmation action", ex, accept ? "Accept request failed." : "Deny request failed.");
                if (!lifetimeCancellationSource.IsCancellationRequested)
                    showTradeConfirmationMessage(ex.Message, MessageBoxIcon.Error);
            }
            finally
            {
                try
                {
                    if (actionSucceeded)
                    {
                        if (updatedPendingCount.HasValue)
                            UpdateTradePendingCount(updatedPendingCount.Value);
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
                    if (actionSucceeded)
                        _ = ReloadTradesForAccountAsync(steamId);
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

        internal Task LoadTradesAsync(string selectedAccountName = null)
        {
            if (!String.IsNullOrWhiteSpace(selectedAccountName))
            {
                lock (tradeLoadLock)
                {
                    tradeAccountSelection = selectedAccountName;
                }
            }
            return QueueOrRunTradeLoadAsync(targetAccountSteamId: null);
        }

        internal Task ReloadTradesForAccountAsync(ulong steamId)
        {
            if (steamId == 0)
                return Task.CompletedTask;
            return QueueOrRunTradeLoadAsync(targetAccountSteamId: steamId);
        }

        internal async Task WaitForPendingTradeLoadAsync()
        {
            while (true)
            {
                Task pending;
                lock (tradeLoadLock)
                {
                    pending = queuedTradeLoadCompletion?.Task ?? activeTradeLoadCompletion?.Task;
                }
                if (pending == null)
                    return;
                await pending;
            }
        }

        private SteamGuardAccount[] ResolveTradeAccountsToLoad(TradeLoadScope scope, string selectionToLoad)
        {
            SteamGuardAccount[] accountsSnapshot = allAccounts;
            if (accountsSnapshot == null || accountsSnapshot.Length == 0 || scope == null)
                return Array.Empty<SteamGuardAccount>();

            var result = new List<SteamGuardAccount>();
            var seenSteamIds = new HashSet<ulong>();

            void AddAccountIfEligible(SteamGuardAccount candidate)
            {
                if (candidate != null &&
                    IsSessionAvailableForAccountOperations(candidate) &&
                    !IsAccountBusyWithRecoveryOrTransfer(candidate.Session.SteamID) &&
                    seenSteamIds.Add(candidate.Session.SteamID))
                {
                    result.Add(candidate);
                }
            }

            if (scope.IncludeSelection)
            {
                if (selectionToLoad == "all")
                {
                    foreach (SteamGuardAccount account in accountsSnapshot)
                        AddAccountIfEligible(account);
                }
                else
                {
                    foreach (SteamGuardAccount account in accountsSnapshot)
                    {
                        if (account?.Session != null &&
                            (account.Session.SteamID.ToString(CultureInfo.InvariantCulture) == selectionToLoad ||
                             account.AccountName == selectionToLoad))
                        {
                            AddAccountIfEligible(account);
                        }
                    }
                }
            }

            if (scope.AccountSteamIds.Count > 0)
            {
                foreach (SteamGuardAccount account in accountsSnapshot)
                {
                    if (account?.Session != null && scope.AccountSteamIds.Contains(account.Session.SteamID))
                        AddAccountIfEligible(account);
                }
            }

            if (scope.IncludeSelection && result.Count == 0 && scope.AccountSteamIds.Count == 0)
                AddAccountIfEligible(currentAccount);

            return result.ToArray();
        }

        private async Task QueueOrRunTradeLoadAsync(ulong? targetAccountSteamId)
        {
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            if (cancellationToken.IsCancellationRequested)
                return;

            Task waitTask = null;
            long runnerId = 0;
            lock (tradeLoadLock)
            {
                if (!tradeLoadActive)
                {
                    tradeLoadActive = true;
                    tradeLoadScanStarted = false;
                    runnerId = ++tradeLoadRunnerSequence;
                    tradeLoadOwnerId = runnerId;
                    activeTradeLoadScope = new TradeLoadScope();
                    if (targetAccountSteamId.HasValue)
                        activeTradeLoadScope.AddAccount(targetAccountSteamId.Value);
                    else
                        activeTradeLoadScope.AddFullSelection();
                    activeTradeLoadCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else if (!tradeLoadScanStarted)
                {
                    if (targetAccountSteamId.HasValue)
                        activeTradeLoadScope.AddAccount(targetAccountSteamId.Value);
                    else
                        activeTradeLoadScope.AddFullSelection();
                    waitTask = activeTradeLoadCompletion.Task;
                }
                else
                {
                    if (queuedTradeLoadScope == null)
                    {
                        queuedTradeLoadScope = new TradeLoadScope();
                        queuedTradeLoadCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    if (targetAccountSteamId.HasValue)
                        queuedTradeLoadScope.AddAccount(targetAccountSteamId.Value);
                    else
                        queuedTradeLoadScope.AddFullSelection();
                    waitTask = queuedTradeLoadCompletion.Task;
                }
            }

            if (runnerId == 0)
            {
                try
                {
                    await waitTask;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                return;
            }

            try
            {
                await RunCoalescedTradeLoadsAsync(runnerId);
            }
            finally
            {
                List<TaskCompletionSource<bool>> remainingCompletions = null;
                lock (tradeLoadLock)
                {
                    if (tradeLoadOwnerId == runnerId)
                    {
                        tradeLoadActive = false;
                        tradeLoadScanStarted = false;
                        tradeLoadOwnerId = 0;
                        activeTradeLoadScope = null;
                        queuedTradeLoadScope = null;
                        if (activeTradeLoadCompletion != null)
                        {
                            remainingCompletions = remainingCompletions ?? new List<TaskCompletionSource<bool>>();
                            remainingCompletions.Add(activeTradeLoadCompletion);
                            activeTradeLoadCompletion = null;
                        }
                        if (queuedTradeLoadCompletion != null)
                        {
                            remainingCompletions = remainingCompletions ?? new List<TaskCompletionSource<bool>>();
                            remainingCompletions.Add(queuedTradeLoadCompletion);
                            queuedTradeLoadCompletion = null;
                        }
                    }
                }
                if (remainingCompletions != null)
                {
                    foreach (TaskCompletionSource<bool> completion in remainingCompletions)
                        completion.TrySetResult(true);
                }
            }
        }

        private async Task RunCoalescedTradeLoadsAsync(long runnerId)
        {
            CancellationToken cancellationToken = lifetimeCancellationSource.Token;
            while (true)
            {
                TaskCompletionSource<bool> currentCompletion = null;
                string selectionToLoad = GetNormalizedTradeSelection();
                string errorMessage = null;
                int totalPendingCount = 0;
                bool pendingCountComputed = false;
                bool canceled = false;

                try
                {
                    PersistExpiredRefreshTokenStates();
                    DispatchEligibleSilentReloginsForExpiredAccounts(allAccounts);
                    await confirmationsSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        TradeLoadScope scopeToLoad;
                        lock (tradeLoadLock)
                        {
                            tradeLoadScanStarted = true;
                            scopeToLoad = activeTradeLoadScope;
                            currentCompletion = activeTradeLoadCompletion;
                        }

                        selectionToLoad = GetNormalizedTradeSelection();
                        SteamGuardAccount[] accountsToLoad = ResolveTradeAccountsToLoad(scopeToLoad, selectionToLoad);
                        if (accountsToLoad.Length > 0)
                        {
                            var unavailableAccounts = new List<string>();
                            string rateLimitMessage = null;
                            if (!TryGetTradeRateLimitMessage(out rateLimitMessage))
                            {
                                foreach (SteamGuardAccount account in accountsToLoad)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    if (TryGetTradeRateLimitMessage(out rateLimitMessage))
                                        break;
                                    if (silentReloginService.IsRecoveryInProgress(account.Session.SteamID))
                                    {
                                        unavailableAccounts.Add(account.AccountName + " is renewing its session.");
                                        continue;
                                    }
                                    if (!IsSessionAvailableForAccountOperations(account))
                                    {
                                        InvalidateTradeConfirmationCache(account);
                                        continue;
                                    }

                                    try
                                    {
                                        if (TryGetTradeRateLimitMessage(out rateLimitMessage))
                                            break;

                                        Confirmation[] accountConfirmations = await fetchTradeConfirmationsForPageAsync(account, cancellationToken);
                                        if (accountConfirmations == null)
                                        {
                                            unavailableAccounts.Add(account.AccountName + " is busy or renewing its session.");
                                            continue;
                                        }
                                        if (!IsSessionAvailableForAccountOperations(account))
                                        {
                                            InvalidateTradeConfirmationCache(account);
                                            continue;
                                        }
                                        // Manual refresh displays all pending confirmations without postponing the automatic confirmation schedule.
                                        CacheTradeConfirmations(account, accountConfirmations);
                                        tradeConfirmationScheduler.RecordManualRefreshHealth(account.Session.SteamID);
                                        PublishAccountHealthMetadata(account.Session.SteamID);
                                    }
                                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                                    {
                                        throw;
                                    }
                                    catch (TradeRateLimitedException ex)
                                    {
                                        rateLimitMessage = ex.Message;
                                        DiagnosticErrorLogger.Log("Trade confirmation fetch", ex, "Steam rate limited confirmation requests.");
                                        break;
                                    }
                                    catch (Exception ex) when (IsRateLimitedResponse(ex))
                                    {
                                        TradeRateLimitedException rateLimitException = ApplyTradeRateLimit(ex);
                                        rateLimitMessage = rateLimitException.Message;
                                        DiagnosticErrorLogger.Log("Trade confirmation fetch", rateLimitException, "Steam rate limited confirmation requests.");
                                        break;
                                    }
                                    catch (SessionUnavailableException)
                                    {
                                        InvalidateTradeConfirmationCache(account);
                                    }
                                    catch (Exception ex)
                                    {
                                        DiagnosticErrorLogger.Log("Trade confirmation fetch", ex, "A confirmation list request failed after all retries.");
                                        if (IsDefinitiveSessionFailure(ex))
                                            InvalidateTradeConfirmationCache(account);
                                        else
                                        {
                                            ResolvedAccountConfirmationPolicy policy = ResolveAccountConfirmationPolicyForMonitor(account.Session.SteamID);
                                            tradeConfirmationScheduler.RecordTransientFailure(account.Session.SteamID, policy.Interval, "Manual confirmation refresh failed.");
                                            PublishAccountHealthMetadata(account.Session.SteamID);
                                            unavailableAccounts.Add(account.AccountName + " could not be loaded.");
                                        }
                                    }
                                }
                            }

                            errorMessage = !String.IsNullOrWhiteSpace(rateLimitMessage)
                                ? rateLimitMessage
                                : unavailableAccounts.Count == 0
                                    ? null
                                    : "Some account confirmations could not be loaded: " + String.Join(" ", unavailableAccounts);
                        }

                        totalPendingCount = pendingTradeConfirmationCounts.Values.Sum();
                        pendingCountComputed = true;
                    }
                    finally
                    {
                        confirmationsSemaphore.Release();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Trade confirmation load failed: " + ex.Message);
                    DiagnosticErrorLogger.Log("Trade confirmation page", ex, "The confirmation page could not be updated.");
                    errorMessage = "Steam confirmations could not be loaded. Please try refreshing.";
                }

                if (canceled || cancellationToken.IsCancellationRequested)
                    break;

                if (pendingCountComputed)
                    UpdateTradePendingCount(totalPendingCount);

                bool shouldPublish;
                lock (tradeLoadLock)
                {
                    tradeLoadScanStarted = true;
                    if (currentCompletion == null)
                        currentCompletion = activeTradeLoadCompletion;
                    shouldPublish = queuedTradeLoadScope == null &&
                        String.Equals(selectionToLoad, tradeAccountSelection, StringComparison.Ordinal);
                }

                if (shouldPublish && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await PublishCachedTradesAsync(errorMessage, selectionToLoad);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Trade confirmation UI", ex, "The trade confirmation list could not be sent to the UI.");
                    }
                }

                bool continueLoop = false;
                TaskCompletionSource<bool> canceledQueuedCompletion = null;
                lock (tradeLoadLock)
                {
                    bool selectionChanged = !String.Equals(selectionToLoad, tradeAccountSelection, StringComparison.Ordinal);
                    if (!cancellationToken.IsCancellationRequested && (queuedTradeLoadScope != null || selectionChanged))
                    {
                        if (queuedTradeLoadScope == null)
                            queuedTradeLoadScope = new TradeLoadScope();
                        if (selectionChanged)
                            queuedTradeLoadScope.AddFullSelection();

                        activeTradeLoadScope = queuedTradeLoadScope;
                        activeTradeLoadCompletion = queuedTradeLoadCompletion ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        queuedTradeLoadScope = null;
                        queuedTradeLoadCompletion = null;
                        tradeLoadScanStarted = false;
                        continueLoop = true;
                    }
                    else if (tradeLoadOwnerId == runnerId)
                    {
                        tradeLoadActive = false;
                        tradeLoadScanStarted = false;
                        tradeLoadOwnerId = 0;
                        activeTradeLoadScope = null;
                        activeTradeLoadCompletion = null;
                        queuedTradeLoadScope = null;
                        canceledQueuedCompletion = queuedTradeLoadCompletion;
                        queuedTradeLoadCompletion = null;
                    }
                }

                currentCompletion?.TrySetResult(true);
                canceledQueuedCompletion?.TrySetResult(true);

                if (!continueLoop)
                    break;
            }
        }
    }
}
