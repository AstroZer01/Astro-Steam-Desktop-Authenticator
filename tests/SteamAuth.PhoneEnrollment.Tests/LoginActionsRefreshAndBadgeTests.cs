using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using SteamAuth;
using SteamAuth.Protocol;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class LoginActionsRefreshAndBadgeTests
    {
        [Theory]
        [InlineData(EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory, "First-time login", true)]
        [InlineData(EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryUsedPreviously, "Previously seen login", false)]
        [InlineData(EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryInvalid, "Unknown login history", false)]
        public async Task FetchPendingRequestsAsync_SetsSecurityHistoryAndIsNewLoginFlag(
            EAuthSessionSecurityHistory loginHistory,
            string expectedLabel,
            bool expectedIsNewLogin)
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account = CreateAccount(76561198000001001UL, "acct_history", transport);
            transport.SetPendingRequest(account.Session.AccessToken, 5001UL, new CAuthentication_GetAuthSessionInfo_Response
            {
                Ip = "203.0.113.10",
                City = "Seattle",
                Country = "US",
                DeviceFriendlyName = "Desktop PC",
                PlatformType = EAuthTokenPlatformType.KEauthTokenPlatformTypeSteamClient,
                Version = 1,
                LoginHistory = loginHistory,
                RequestedPersistence = ESessionPersistence.KEsessionPersistencePersistent
            });

            var service = new LoginApprovalService(_ => true, transport);
            LoginApprovalFetchResult result = await service.FetchPendingRequestsAsync(account);

            Assert.Equal(LoginApprovalErrorKind.None, result.ErrorKind);
            PendingLoginRequest request = Assert.Single(result.Requests);
            Assert.Equal(expectedLabel, request.SecurityHistory);
            Assert.Equal(expectedIsNewLogin, request.IsNewLogin);
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_WithMonitoringEnabled_RefreshesAllEligibleAccountsEvenWithinMonitorCooldown()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account1 = CreateAccount(76561198000002001UL, "acct_one", transport);
            SteamGuardAccount account2 = CreateAccount(76561198000002002UL, "acct_two", transport);
            transport.SetPendingRequest(account1.Session.AccessToken, 6001UL, CreateSessionInfo("PC-1", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));
            transport.SetPendingRequest(account2.Session.AccessToken, 6002UL, CreateSessionInfo("PC-2", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryUsedPreviously));

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;
            manifest.LoginActionMode = LoginActionModes.Manual;

            var notifications = new List<string>();
            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (title, message, icon) => notifications.Add(title + "|" + message),
                initialAccounts: new[] { account1, account2 }))
            {
                // Run two monitor passes so both accounts enter their 12s monitor cooldown.
                await form.MonitorLoginActionsAsync();
                await form.MonitorLoginActionsAsync();
                Assert.Equal(2, transport.SessionListFetchCount);
                Assert.Equal(2, notifications.Count);

                // A third monitor pass skips both accounts while on cooldown.
                await form.MonitorLoginActionsAsync();
                Assert.Equal(2, transport.SessionListFetchCount);

                // Update pending requests on both accounts and invoke manual Refresh ("all").
                transport.SetPendingRequest(account1.Session.AccessToken, 6011UL, CreateSessionInfo("PC-1B", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));
                transport.SetPendingRequest(account2.Session.AccessToken, 6012UL, CreateSessionInfo("PC-2B", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryUsedPreviously));

                await form.RefreshLoginActionsAsync("all");

                // Manual refresh must check both eligible accounts even though monitoring is enabled and both are on cooldown.
                Assert.Equal(4, transport.SessionListFetchCount);
                Assert.Equal(2, transport.GetSessionListFetchCount(account1.Session.AccessToken));
                Assert.Equal(2, transport.GetSessionListFetchCount(account2.Session.AccessToken));
                Assert.Equal(2, notifications.Count);

                JObject payload = JObject.Parse(form.BuildCachedLoginActionsPayloadJson());
                JArray requests = (JArray)payload["requests"];
                Assert.Equal(2, requests.Count);
                Assert.Contains(requests, item => item.Value<string>("clientId") == "6011" && item.Value<bool>("isNewLogin"));
                Assert.Contains(requests, item => item.Value<string>("clientId") == "6012" && !item.Value<bool>("isNewLogin"));

                // Manual refresh resets each scanned account's monitor cooldown so the next immediate monitor tick does not re-fetch.
                await form.MonitorLoginActionsAsync();
                Assert.Equal(4, transport.SessionListFetchCount);
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_WithSpecificAccountSelection_RefreshesOnlySelectedAccount()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account1 = CreateAccount(76561198000003001UL, "acct_sel_1", transport);
            SteamGuardAccount account2 = CreateAccount(76561198000003002UL, "acct_sel_2", transport);
            transport.SetPendingRequest(account1.Session.AccessToken, 7001UL, CreateSessionInfo("PC-1", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryUsedPreviously));
            transport.SetPendingRequest(account2.Session.AccessToken, 7002UL, CreateSessionInfo("PC-2", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;

            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (_, __, ___) => { },
                initialAccounts: new[] { account1, account2 }))
            {
                await form.RefreshLoginActionsAsync(account2.Session.SteamID.ToString(CultureInfo.InvariantCulture));

                Assert.Equal(0, transport.GetSessionListFetchCount(account1.Session.AccessToken));
                Assert.Equal(1, transport.GetSessionListFetchCount(account2.Session.AccessToken));

                JObject payload = JObject.Parse(form.BuildCachedLoginActionsPayloadJson());
                JArray requests = (JArray)payload["requests"];
                JToken single = Assert.Single(requests);
                Assert.Equal("7002", single.Value<string>("clientId"));
                Assert.True(single.Value<bool>("isNewLogin"));
                Assert.Equal("First-time login", single.Value<string>("securityHistory"));
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_WaitsForInFlightMonitorPassInsteadOfDroppingRefresh()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account1 = CreateAccount(76561198000004001UL, "acct_wait_1", transport);
            SteamGuardAccount account2 = CreateAccount(76561198000004002UL, "acct_wait_2", transport);
            transport.SetPendingRequest(account1.Session.AccessToken, 8001UL, CreateSessionInfo("PC-1", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryUsedPreviously));
            transport.SetPendingRequest(account2.Session.AccessToken, 8002UL, CreateSessionInfo("PC-2", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            var monitorEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseMonitor = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.OnSessionListFetchAsync = async (token, count, _) =>
            {
                if (token == account1.Session.AccessToken && count == 1)
                {
                    monitorEntered.TrySetResult(true);
                    await releaseMonitor.Task;
                }
            };

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;

            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (_, __, ___) => { },
                initialAccounts: new[] { account1, account2 }))
            {
                Task monitorTask = form.MonitorLoginActionsAsync();
                await monitorEntered.Task;

                Task refreshTask = form.RefreshLoginActionsAsync("all");
                releaseMonitor.SetResult(true);

                await Task.WhenAll(monitorTask, refreshTask);

                // Monitor fetched account1 once, then manual refresh ran after it and refreshed both account1 and account2.
                Assert.Equal(2, transport.GetSessionListFetchCount(account1.Session.AccessToken));
                Assert.Equal(1, transport.GetSessionListFetchCount(account2.Session.AccessToken));
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_CoalescesOverlappingRefreshesAndQueuesUncoveredSelection()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account1 = CreateAccount(76561198000005001UL, "acct_coal_1", transport);
            SteamGuardAccount account2 = CreateAccount(76561198000005002UL, "acct_coal_2", transport);
            transport.SetPendingRequest(account1.Session.AccessToken, 9001UL, CreateSessionInfo("PC-1", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryUsedPreviously));
            transport.SetPendingRequest(account2.Session.AccessToken, 9002UL, CreateSessionInfo("PC-2", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            var firstRefreshEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.OnSessionListFetchAsync = async (token, count, _) =>
            {
                if (token == account1.Session.AccessToken && count == 1)
                {
                    firstRefreshEntered.TrySetResult(true);
                    await releaseFirstRefresh.Task;
                }
            };

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;

            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (_, __, ___) => { },
                initialAccounts: new[] { account1, account2 }))
            {
                string account1Key = account1.Session.SteamID.ToString(CultureInfo.InvariantCulture);
                string account2Key = account2.Session.SteamID.ToString(CultureInfo.InvariantCulture);

                Task first = form.RefreshLoginActionsAsync(account1Key);
                await firstRefreshEntered.Task;

                // Overlapping request for the same account joins the active pass; repeated requests for account2 coalesce into one follow-up pass.
                Task duplicateSameAccount = form.RefreshLoginActionsAsync(account1Key);
                Task secondAccountA = form.RefreshLoginActionsAsync(account2Key);
                Task secondAccountB = form.RefreshLoginActionsAsync(account2Key);

                releaseFirstRefresh.SetResult(true);
                await Task.WhenAll(first, duplicateSameAccount, secondAccountA, secondAccountB);

                Assert.Equal(1, transport.GetSessionListFetchCount(account1.Session.AccessToken));
                Assert.Equal(1, transport.GetSessionListFetchCount(account2.Session.AccessToken));
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_RespectsGlobalRateLimitAndStopsEarlyOnRateLimitedResponse()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account1 = CreateAccount(76561198000006001UL, "acct_rl_1", transport);
            SteamGuardAccount account2 = CreateAccount(76561198000006002UL, "acct_rl_2", transport);
            transport.SetSessionListResult(account1.Session.AccessToken, 84);
            transport.SetPendingRequest(account2.Session.AccessToken, 10002UL, CreateSessionInfo("PC-2", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;

            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (_, __, ___) => { },
                initialAccounts: new[] { account1, account2 }))
            {
                await form.RefreshLoginActionsAsync("all");

                // Account 1 hit EResult 84 (RateLimited), so account 2 must not be queried.
                Assert.Equal(1, transport.GetSessionListFetchCount(account1.Session.AccessToken));
                Assert.Equal(0, transport.GetSessionListFetchCount(account2.Session.AccessToken));

                // Subsequent manual refresh during the active global rate-limit window makes zero requests.
                await form.RefreshLoginActionsAsync("all");
                Assert.Equal(1, transport.SessionListFetchCount);
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_WithAutomaticDenyMode_ProcessesAutomatedActionsOnDiscoveredRequests()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account = CreateAccount(76561198000007001UL, "acct_auto_deny", transport);
            transport.SetPendingRequest(account.Session.AccessToken, 11001UL, CreateSessionInfo("Unknown PC", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;
            manifest.LoginActionMode = LoginActionModes.Deny;

            var notifications = new List<string>();
            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (title, message, icon) => notifications.Add(title + "|" + message),
                initialAccounts: new[] { account }))
            {
                await form.RefreshLoginActionsAsync("all");

                var decision = Assert.Single(transport.ActionDecisions);
                Assert.False(decision.Confirm);

                JObject payload = JObject.Parse(form.BuildCachedLoginActionsPayloadJson());
                Assert.Empty((JArray)payload["requests"]);
                JToken recentAttempt = Assert.Single((JArray)payload["recentAttempts"]);
                Assert.Equal("Denied automatically", recentAttempt.Value<string>("outcome"));
                Assert.True(recentAttempt.Value<bool>("isNewLogin"));
                Assert.Equal("First-time login", recentAttempt.Value<string>("securityHistory"));
                Assert.Single(notifications);
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_DoesNotSuppressFirstMonitoringNotificationForPendingRequest()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account = CreateAccount(76561198000008001UL, "acct_notify_once", transport);
            transport.SetPendingRequest(account.Session.AccessToken, 12001UL, CreateSessionInfo("Laptop", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;
            manifest.LoginActionMode = LoginActionModes.Manual;

            var notifications = new List<string>();
            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (title, message, icon) => notifications.Add(title + "|" + message),
                initialAccounts: new[] { account }))
            {
                // Manual refresh discovers the request without popping a notification or suppressing future monitoring notification.
                await form.RefreshLoginActionsAsync("all");
                Assert.Empty(notifications);

                // Expire the account's monitor schedule and run two monitor passes.
                ClearLoginMonitorSchedules(form);
                await form.MonitorLoginActionsAsync();
                Assert.Single(notifications);

                ClearLoginMonitorSchedules(form);
                await form.MonitorLoginActionsAsync();
                Assert.Single(notifications);
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_RefreshQueuedDuringPublishRunsWithoutBeingClobbered()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account1 = CreateAccount(76561198000009001UL, "acct_pub_1", transport);
            SteamGuardAccount account2 = CreateAccount(76561198000009002UL, "acct_pub_2", transport);
            transport.SetPendingRequest(account1.Session.AccessToken, 13001UL, CreateSessionInfo("PC-1", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryUsedPreviously));
            transport.SetPendingRequest(account2.Session.AccessToken, 13002UL, CreateSessionInfo("PC-2", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            int publishCount = 0;
            var firstPublishEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstPublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;

            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (_, __, ___) => { },
                initialAccounts: new[] { account1, account2 },
                publishLoginActionsAsync: async () =>
                {
                    if (Interlocked.Increment(ref publishCount) == 1)
                    {
                        firstPublishEntered.TrySetResult(true);
                        await releaseFirstPublish.Task;
                    }
                }))
            {
                string account1Key = account1.Session.SteamID.ToString(CultureInfo.InvariantCulture);
                string account2Key = account2.Session.SteamID.ToString(CultureInfo.InvariantCulture);

                Task firstRefresh = form.RefreshLoginActionsAsync(account1Key);
                await firstPublishEntered.Task;

                // While pass 1 is awaiting WebView publication, a refresh for account2 and a repeat refresh for account1 arrive.
                Task refreshDuringPublishAccount2 = form.RefreshLoginActionsAsync(account2Key);
                Task refreshDuringPublishAccount1 = form.RefreshLoginActionsAsync(account1Key);

                releaseFirstPublish.SetResult(true);
                await Task.WhenAll(firstRefresh, refreshDuringPublishAccount2, refreshDuringPublishAccount1);

                Assert.Equal(2, transport.GetSessionListFetchCount(account1.Session.AccessToken));
                Assert.Equal(1, transport.GetSessionListFetchCount(account2.Session.AccessToken));
                Assert.Equal(2, Volatile.Read(ref publishCount));
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_CancellationCompletesActiveAndQueuedCallersAndReleasesSemaphore()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account1 = CreateAccount(76561198000010001UL, "acct_cancel_1", transport);
            SteamGuardAccount account2 = CreateAccount(76561198000010002UL, "acct_cancel_2", transport);
            transport.SetPendingRequest(account1.Session.AccessToken, 14001UL, CreateSessionInfo("PC-1", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryUsedPreviously));
            transport.SetPendingRequest(account2.Session.AccessToken, 14002UL, CreateSessionInfo("PC-2", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.OnSessionListFetchAsync = async (token, count, ct) =>
            {
                if (token == account1.Session.AccessToken && count == 1)
                {
                    firstEntered.TrySetResult(true);
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
            };

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;

            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (_, __, ___) => { },
                initialAccounts: new[] { account1, account2 }))
            {
                string account1Key = account1.Session.SteamID.ToString(CultureInfo.InvariantCulture);
                string account2Key = account2.Session.SteamID.ToString(CultureInfo.InvariantCulture);

                Task activeRefresh = form.RefreshLoginActionsAsync(account1Key);
                await firstEntered.Task;
                Task queuedRefresh = form.RefreshLoginActionsAsync(account2Key);

                // Cancel form lifetime token while activeRefresh is in flight and queuedRefresh is waiting.
                var ctsField = typeof(MainForm).GetField("lifetimeCancellationSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(ctsField);
                ((CancellationTokenSource)ctsField.GetValue(form)).Cancel();

                Task allRefreshes = Task.WhenAll(activeRefresh, queuedRefresh);
                Task completed = await Task.WhenAny(allRefreshes, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.Same(allRefreshes, completed);
                await allRefreshes;

                var semField = typeof(MainForm).GetField("loginActionsSemaphore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(semField);
                Assert.Equal(1, ((SemaphoreSlim)semField.GetValue(form)).CurrentCount);
            }
        }

        [Fact]
        public async Task MonitorLoginActionsAsync_EvaluatesAutomaticRuleEnabledWhileFetchIsInFlight()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account = CreateAccount(76561198000011001UL, "acct_live_enable", transport);
            transport.SetPendingRequest(account.Session.AccessToken, 15001UL, CreateSessionInfo("Unknown PC", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            var fetchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFetch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.OnSessionListFetchAsync = async (token, count, _) =>
            {
                if (token == account.Session.AccessToken && count == 1)
                {
                    fetchEntered.TrySetResult(true);
                    await releaseFetch.Task;
                }
            };

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;
            manifest.LoginActionMode = LoginActionModes.Manual;

            var notifications = new List<string>();
            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (title, message, icon) => notifications.Add(title + "|" + message),
                initialAccounts: new[] { account }))
            {
                Task monitorTask = form.MonitorLoginActionsAsync();
                await fetchEntered.Task;

                // User enables automatic denial while the monitor fetch is in flight.
                manifest.LoginActionMode = LoginActionModes.Deny;
                releaseFetch.SetResult(true);

                await monitorTask;

                var decision = Assert.Single(transport.ActionDecisions);
                Assert.False(decision.Confirm);
                Assert.DoesNotContain(notifications, item => item.StartsWith("Steam login approval needed|", StringComparison.Ordinal));
                Assert.Contains(notifications, item => item.StartsWith("Automatically denied Steam login|", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_DoesNotSendAutomaticDecisionWhenSwitchedToManualDuringPublicIpLookup()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account = CreateAccount(76561198000012001UL, "acct_live_manual", transport);
            transport.SetPendingRequest(account.Session.AccessToken, 16001UL, CreateSessionInfo("Desktop PC", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            var ipLookupEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseIpLookup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;
            manifest.LoginActionMode = LoginActionModes.Deny;
            manifest.LoginActionAutoAllowIpEnabled = true;
            manifest.LoginActionAutoAllowCurrentDeviceIp = true;

            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (_, __, ___) => { },
                initialAccounts: new[] { account },
                getCurrentPublicIpv4Async: async _ =>
                {
                    ipLookupEntered.TrySetResult(true);
                    await releaseIpLookup.Task;
                    return "203.0.113.99";
                }))
            {
                Task refreshTask = form.RefreshLoginActionsAsync("all");
                await ipLookupEntered.Task;

                // User switches back to Manual mode while public IP lookup is in flight.
                manifest.LoginActionMode = LoginActionModes.Manual;
                releaseIpLookup.SetResult(true);

                await refreshTask;

                Assert.Empty(transport.ActionDecisions);
                JObject payload = JObject.Parse(form.BuildCachedLoginActionsPayloadJson());
                JToken pending = Assert.Single((JArray)payload["requests"]);
                Assert.Equal("16001", pending.Value<string>("clientId"));
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_DoesNotSubmitAutomaticDecisionWhenSwitchedToManualWhileWaitingForAccountLock()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account = CreateAccount(76561198000013001UL, "acct_lock_manual", transport);
            transport.SetPendingRequest(account.Session.AccessToken, 17001UL, CreateSessionInfo("Desktop PC", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));

            var accountLockHeld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAccountLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;
            manifest.LoginActionMode = LoginActionModes.Deny;
            manifest.LoginActionAutoAllowIpEnabled = true;
            manifest.LoginActionAutoAllowCurrentDeviceIp = true;

            var notifications = new List<string>();
            var service = new LoginApprovalService(_ => true, transport);
            MainForm form = null;
            Task blockerTask = null;
            using (form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (title, message, icon) => notifications.Add(title + "|" + message),
                initialAccounts: new[] { account },
                getCurrentPublicIpv4Async: async _ =>
                {
                    // Acquire the per-account operation lock before the refresh proceeds to RespondAsync.
                    var coordField = typeof(MainForm).GetField("steamAccountOperationCoordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    Assert.NotNull(coordField);
                    var coordinator = (SteamAccountOperationCoordinator)coordField.GetValue(form);
                    blockerTask = coordinator.RunAsync(account.Session.SteamID, async () =>
                    {
                        accountLockHeld.TrySetResult(true);
                        await releaseAccountLock.Task;
                        return true;
                    }, null);
                    await accountLockHeld.Task;
                    return "203.0.113.99";
                }))
            {
                Task refreshTask = form.RefreshLoginActionsAsync("all");
                await accountLockHeld.Task;

                // Wait briefly so refreshTask queues on the per-account operation lock, then switch to Manual.
                await Task.Delay(50);
                manifest.LoginActionMode = LoginActionModes.Manual;
                releaseAccountLock.SetResult(true);

                await Task.WhenAll(refreshTask, blockerTask);

                Assert.Empty(transport.ActionDecisions);
                Assert.Empty(notifications);
                JObject payload = JObject.Parse(form.BuildCachedLoginActionsPayloadJson());
                JToken pending = Assert.Single((JArray)payload["requests"]);
                Assert.Equal("17001", pending.Value<string>("clientId"));
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_DoesNotSubmitAutomaticRetryWhenSwitchedToManualDuringUnauthorizedTokenRefresh()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account = CreateAccount(76561198000014001UL, "acct_retry_manual", transport);
            transport.SetPendingRequest(account.Session.AccessToken, 18001UL, CreateSessionInfo("Desktop PC", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));
            transport.ActionResultOverride = 21; // Unauthorized -> triggers GenerateAccessTokenForApp retry

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;
            manifest.LoginActionMode = LoginActionModes.Deny;

            transport.OnTokenRefreshAsync = () =>
            {
                // User switches to Manual while the unauthorized retry is refreshing the access token.
                manifest.LoginActionMode = LoginActionModes.Manual;
                return Task.CompletedTask;
            };

            var notifications = new List<string>();
            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (title, message, icon) => notifications.Add(title + "|" + message),
                initialAccounts: new[] { account }))
            {
                await form.RefreshLoginActionsAsync("all");

                // Initial attempt hit Unauthorized (result 21), token refresh ran, and retry submission was skipped.
                Assert.Single(transport.ActionDecisions);
                Assert.Equal(1, transport.TokenRefreshCount);
                Assert.Empty(notifications);
                JObject payload = JObject.Parse(form.BuildCachedLoginActionsPayloadJson());
                JToken pending = Assert.Single((JArray)payload["requests"]);
                Assert.Equal("18001", pending.Value<string>("clientId"));
            }
        }

        [Fact]
        public async Task RefreshLoginActionsAsync_SuppressesStaleAutomaticFailureNotificationWhenTokenRefreshFailsAfterSwitchToManual()
        {
            var transport = new StubLoginTransport();
            SteamGuardAccount account = CreateAccount(76561198000015001UL, "acct_retry_fail_manual", transport);
            transport.SetPendingRequest(account.Session.AccessToken, 19001UL, CreateSessionInfo("Desktop PC", EAuthSessionSecurityHistory.KEauthSessionSecurityHistoryNoPriorHistory));
            transport.ActionResultOverride = 21; // Unauthorized -> triggers GenerateAccessTokenForApp retry

            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.LoginActionMonitoringEnabled = true;
            manifest.LoginActionMode = LoginActionModes.Deny;

            transport.OnTokenRefreshAsync = () =>
            {
                // User switches to Manual while the unauthorized retry's token refresh fails transiently.
                manifest.LoginActionMode = LoginActionModes.Manual;
                throw new System.Net.Http.HttpRequestException("Transient network error during token refresh.");
            };

            var notifications = new List<string>();
            var service = new LoginApprovalService(_ => true, transport);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                loginApprovalService: service,
                notifyLoginAction: (title, message, icon) => notifications.Add(title + "|" + message),
                initialAccounts: new[] { account }))
            {
                await form.RefreshLoginActionsAsync("all");

                Assert.Single(transport.ActionDecisions);
                Assert.Equal(1, transport.TokenRefreshCount);
                Assert.Empty(notifications);
                JObject payload = JObject.Parse(form.BuildCachedLoginActionsPayloadJson());
                JToken pending = Assert.Single((JArray)payload["requests"]);
                Assert.Equal("19001", pending.Value<string>("clientId"));
            }
        }

        private static void ClearLoginMonitorSchedules(MainForm form)
        {
            var lockField = typeof(MainForm).GetField("sessionStateLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var schedulesField = typeof(MainForm).GetField("loginMonitorSchedules", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(lockField);
            Assert.NotNull(schedulesField);
            lock (lockField.GetValue(form))
            {
                var schedules = schedulesField.GetValue(form);
                schedules.GetType().GetMethod("Clear").Invoke(schedules, null);
            }
        }

        [Fact]
        public void LoginActionsUi_ChecksStructuredNewLoginFlagAndSendsAccountSelectionOnRefresh()
        {
            string uiPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "Steam Desktop Authenticator", "ui", "index.html"));
            Assert.True(File.Exists(uiPath), "Expected UI file at " + uiPath);

            string html = File.ReadAllText(uiPath);
            Assert.DoesNotContain("request.securityHistory === 'New or unknown login'", html);
            Assert.Contains("if (request.isNewLogin || request.securityHistory === 'First-time login') badges.appendChild(loginBadge('New login', 'warning'));", html);
            Assert.Contains("action: 'refresh_login_actions'", html);
            Assert.Contains("steamId: loginSelect ? (loginSelect.value || 'all') : 'all'", html);
        }

        private static SteamGuardAccount CreateAccount(ulong steamId, string accountName, IAuthenticatorProtocolTransport transport)
        {
            return new SteamGuardAccount
            {
                AccountName = accountName,
                SharedSecret = Convert.ToBase64String(new byte[20]),
                Session = new SessionData(transport)
                {
                    SteamID = steamId,
                    AccessToken = CreateUnexpiredToken(steamId),
                    RefreshToken = CreateUnexpiredToken(steamId + 100000UL)
                }
            };
        }

        private static CAuthentication_GetAuthSessionInfo_Response CreateSessionInfo(string deviceName, EAuthSessionSecurityHistory history)
        {
            return new CAuthentication_GetAuthSessionInfo_Response
            {
                Ip = "198.51.100.25",
                City = "Austin",
                Country = "US",
                DeviceFriendlyName = deviceName,
                PlatformType = EAuthTokenPlatformType.KEauthTokenPlatformTypeWebBrowser,
                Version = 1,
                LoginHistory = history,
                RequestedPersistence = ESessionPersistence.KEsessionPersistencePersistent
            };
        }

        private static string CreateUnexpiredToken(ulong sub)
        {
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"" + sub + "\",\"exp\":4102444800}"))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return "header." + payload + ".signature";
        }

        private sealed class StubLoginTransport : IAuthenticatorProtocolTransport
        {
            private readonly ConcurrentDictionary<string, (ulong ClientId, CAuthentication_GetAuthSessionInfo_Response Details)> requestsByToken =
                new ConcurrentDictionary<string, (ulong, CAuthentication_GetAuthSessionInfo_Response)>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, int> sessionListResultByToken =
                new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, int> sessionListFetchCountByToken =
                new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            private int totalSessionListFetches;
            private int tokenRefreshCount;

            public Func<string, int, CancellationToken, Task> OnSessionListFetchAsync { get; set; }
            public Func<Task> OnTokenRefreshAsync { get; set; }
            public int ActionResultOverride { get; set; } = 1;
            public List<(ulong ClientId, bool Confirm)> ActionDecisions { get; } = new List<(ulong, bool)>();
            public int SessionListFetchCount => Volatile.Read(ref totalSessionListFetches);
            public int TokenRefreshCount => Volatile.Read(ref tokenRefreshCount);

            public int GetSessionListFetchCount(string accessToken)
            {
                return sessionListFetchCountByToken.TryGetValue(accessToken, out int count) ? count : 0;
            }

            public void SetPendingRequest(string accessToken, ulong clientId, CAuthentication_GetAuthSessionInfo_Response details)
            {
                requestsByToken[accessToken] = (clientId, details);
            }

            public void SetSessionListResult(string accessToken, int steamResult)
            {
                sessionListResultByToken[accessToken] = steamResult;
            }

            public async Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
                string service,
                string method,
                TRequest request,
                string accessToken,
                MessageParser<TResponse> responseParser,
                SteamProtocolRequestMethod requestMethod = SteamProtocolRequestMethod.Post,
                CancellationToken cancellationToken = default)
                where TRequest : class, IMessage<TRequest>
                where TResponse : class, IMessage<TResponse>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (method == "GetAuthSessionsForAccount")
                {
                    Interlocked.Increment(ref totalSessionListFetches);
                    int perAccountCount = sessionListFetchCountByToken.AddOrUpdate(accessToken, 1, (_, existing) => existing + 1);
                    if (OnSessionListFetchAsync != null)
                        await OnSessionListFetchAsync(accessToken, perAccountCount, cancellationToken);

                    int resultCode = sessionListResultByToken.TryGetValue(accessToken, out int overrideResult) ? overrideResult : 1;
                    var listResponse = new CAuthentication_GetAuthSessionsForAccount_Response();
                    if (resultCode == 1 && requestsByToken.TryGetValue(accessToken, out var entry))
                        listResponse.ClientIds.Add(entry.ClientId);

                    return new SteamProtocolResponse<TResponse>
                    {
                        Result = resultCode,
                        Body = listResponse as TResponse
                    };
                }

                if (method == "GetAuthSessionInfo")
                {
                    if (requestsByToken.TryGetValue(accessToken, out var entry))
                    {
                        return new SteamProtocolResponse<TResponse>
                        {
                            Result = 1,
                            Body = entry.Details as TResponse
                        };
                    }

                    return new SteamProtocolResponse<TResponse> { Result = 27 };
                }

                if (method == "GenerateAccessTokenForApp")
                {
                    Interlocked.Increment(ref tokenRefreshCount);
                    if (OnTokenRefreshAsync != null)
                        await OnTokenRefreshAsync();
                    return new SteamProtocolResponse<TResponse>
                    {
                        Result = 1,
                        Body = new CAuthentication_AccessToken_GenerateForApp_Response
                        {
                            AccessToken = CreateUnexpiredToken(999999UL)
                        } as TResponse
                    };
                }

                if (method == "UpdateAuthSessionWithMobileConfirmation" &&
                    request is CAuthentication_UpdateAuthSessionWithMobileConfirmation_Request updateRequest)
                {
                    lock (ActionDecisions)
                    {
                        ActionDecisions.Add((updateRequest.ClientId, updateRequest.Confirm));
                    }
                    return new SteamProtocolResponse<TResponse>
                    {
                        Result = ActionResultOverride,
                        Body = new CAuthentication_UpdateAuthSessionWithMobileConfirmation_Response() as TResponse
                    };
                }

                return new SteamProtocolResponse<TResponse> { Result = 2 };
            }
        }
    }
}
