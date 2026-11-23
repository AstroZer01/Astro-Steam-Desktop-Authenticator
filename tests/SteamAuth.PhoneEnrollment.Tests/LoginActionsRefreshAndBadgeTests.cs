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
            transport.OnSessionListFetchAsync = async (token, count) =>
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
            transport.OnSessionListFetchAsync = async (token, count) =>
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

            public Func<string, int, Task> OnSessionListFetchAsync { get; set; }
            public List<(ulong ClientId, bool Confirm)> ActionDecisions { get; } = new List<(ulong, bool)>();
            public int SessionListFetchCount => Volatile.Read(ref totalSessionListFetches);

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
                        await OnSessionListFetchAsync(accessToken, perAccountCount);

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

                if (method == "UpdateAuthSessionWithMobileConfirmation" &&
                    request is CAuthentication_UpdateAuthSessionWithMobileConfirmation_Request updateRequest)
                {
                    lock (ActionDecisions)
                    {
                        ActionDecisions.Add((updateRequest.ClientId, updateRequest.Confirm));
                    }
                    return new SteamProtocolResponse<TResponse>
                    {
                        Result = 1,
                        Body = new CAuthentication_UpdateAuthSessionWithMobileConfirmation_Response() as TResponse
                    };
                }

                return new SteamProtocolResponse<TResponse> { Result = 2 };
            }
        }
    }
}
