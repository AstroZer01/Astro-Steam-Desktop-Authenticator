using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Trade confirmations")]
    public sealed class TradeCountsAndRefreshScopeTests
    {
        [Fact]
        public async Task LoadTradesAsync_ReconcilesPerAccountPendingBadgeCounts()
        {
            SteamGuardAccount account1 = CreateAccount(76561198000021001UL, "trade_acct_1");
            SteamGuardAccount account2 = CreateAccount(76561198000021002UL, "trade_acct_2");

            var confirmationsByAccount = new ConcurrentDictionary<ulong, Confirmation[]>
            {
                [account1.Session.SteamID] = new[]
                {
                    CreateConfirmation(101UL),
                    CreateConfirmation(102UL)
                },
                [account2.Session.SteamID] = new[]
                {
                    CreateConfirmation(201UL)
                }
            };

            var reportedBadgeCounts = new List<int>();
            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                initialAccounts: new[] { account1, account2 },
                fetchTradeConfirmationsForPageAsync: (account, _) =>
                    Task.FromResult(confirmationsByAccount[account.Session.SteamID]),
                updateTradePendingCount: count => reportedBadgeCounts.Add(count),
                publishTradesAsync: (_, __) => Task.CompletedTask))
            {
                await form.LoadTradesAsync("all");
                Assert.Equal(3, reportedBadgeCounts[^1]);

                // Update account1 to have 1 remaining confirmation and refresh only account1.
                confirmationsByAccount[account1.Session.SteamID] = new[] { CreateConfirmation(102UL) };
                await form.LoadTradesAsync(account1.Session.SteamID.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(2, reportedBadgeCounts[^1]);

                // Update account2 to have 0 remaining confirmations and refresh all.
                confirmationsByAccount[account2.Session.SteamID] = Array.Empty<Confirmation>();
                await form.LoadTradesAsync("all");
                Assert.Equal(1, reportedBadgeCounts[^1]);
            }
        }

        [Fact]
        public async Task RespondToTradeConfirmationAsync_OnSuccessReconcilesBadgeAndNarrowsReloadToAffectedAccountWithoutChangingAllSelection()
        {
            SteamGuardAccount account1 = CreateAccount(76561198000022001UL, "trade_narrow_1");
            SteamGuardAccount account2 = CreateAccount(76561198000022002UL, "trade_narrow_2");
            SteamGuardAccount account3 = CreateAccount(76561198000022003UL, "trade_narrow_3");

            var confirmationsByAccount = new ConcurrentDictionary<ulong, Confirmation[]>
            {
                [account1.Session.SteamID] = new[] { CreateConfirmation(301UL), CreateConfirmation(302UL) },
                [account2.Session.SteamID] = new[] { CreateConfirmation(401UL) },
                [account3.Session.SteamID] = new[] { CreateConfirmation(501UL) }
            };
            var fetchCounts = new ConcurrentDictionary<ulong, int>();
            var reportedBadgeCounts = new List<int>();
            var publishedSelections = new List<string>();

            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                initialAccounts: new[] { account1, account2, account3 },
                fetchTradeConfirmationsForPageAsync: (account, _) =>
                {
                    fetchCounts.AddOrUpdate(account.Session.SteamID, 1, (_, existing) => existing + 1);
                    return Task.FromResult(confirmationsByAccount[account.Session.SteamID]);
                },
                respondTradeConfirmationAsync: (account, confirmation, accept, _) =>
                {
                    confirmationsByAccount[account.Session.SteamID] = new[] { CreateConfirmation(302UL) };
                    return Task.FromResult(true);
                },
                updateTradePendingCount: count => reportedBadgeCounts.Add(count),
                publishTradesAsync: (_, selection) =>
                {
                    publishedSelections.Add(selection);
                    return Task.CompletedTask;
                },
                showTradeConfirmationMessage: (_, __) => { }))
            {
                await form.LoadTradesAsync("all");
                Assert.Equal(4, reportedBadgeCounts[^1]);
                fetchCounts.Clear();
                publishedSelections.Clear();

                string confirmationKey = account1.Session.SteamID.ToString(CultureInfo.InvariantCulture) + ":301";
                await form.RespondToTradeConfirmationAsync(confirmationKey, accept: true);
                await form.WaitForPendingTradeLoadAsync();

                // Only the affected account (account1) should be refetched; account2 and account3 must not be refetched.
                Assert.Equal(1, fetchCounts.GetValueOrDefault(account1.Session.SteamID));
                Assert.Equal(0, fetchCounts.GetValueOrDefault(account2.Session.SteamID));
                Assert.Equal(0, fetchCounts.GetValueOrDefault(account3.Session.SteamID));

                // Badge count must be reconciled from 4 down to 3, and view selection must remain "all".
                Assert.Equal(3, reportedBadgeCounts[^1]);
                Assert.Contains("all", publishedSelections);
                Assert.All(publishedSelections, sel => Assert.True(sel == null || sel == "all"));
            }
        }

        [Fact]
        public async Task RespondToTradeConfirmationAsync_OnFailureDoesNotQueueTradeReloadOrDecrementBadge()
        {
            SteamGuardAccount account1 = CreateAccount(76561198000023001UL, "trade_fail_1");
            SteamGuardAccount account2 = CreateAccount(76561198000023002UL, "trade_fail_2");

            var fetchCounts = new ConcurrentDictionary<ulong, int>();
            var reportedBadgeCounts = new List<int>();
            var errorMessages = new List<string>();

            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                initialAccounts: new[] { account1, account2 },
                fetchTradeConfirmationsForPageAsync: (account, _) =>
                {
                    fetchCounts.AddOrUpdate(account.Session.SteamID, 1, (_, existing) => existing + 1);
                    return Task.FromResult(new[] { CreateConfirmation(account.Session.SteamID == account1.Session.SteamID ? 601UL : 701UL) });
                },
                respondTradeConfirmationAsync: (_, __, ___, ____) => Task.FromResult(false),
                updateTradePendingCount: count => reportedBadgeCounts.Add(count),
                publishTradesAsync: (_, __) => Task.CompletedTask,
                showTradeConfirmationMessage: (message, _) => errorMessages.Add(message)))
            {
                await form.LoadTradesAsync("all");
                Assert.Equal(2, reportedBadgeCounts[^1]);
                fetchCounts.Clear();

                string confirmationKey = account1.Session.SteamID.ToString(CultureInfo.InvariantCulture) + ":601";
                await form.RespondToTradeConfirmationAsync(confirmationKey, accept: true);
                await form.WaitForPendingTradeLoadAsync();

                Assert.Single(errorMessages);
                Assert.Empty(fetchCounts);
                Assert.Equal(2, reportedBadgeCounts[^1]);
            }
        }

        [Fact]
        public async Task LoadTradesAsync_StopsSerialLoopEarlyOnRateLimitAndRespectsActiveRateLimitGuard()
        {
            SteamGuardAccount account1 = CreateAccount(76561198000024001UL, "trade_rl_1");
            SteamGuardAccount account2 = CreateAccount(76561198000024002UL, "trade_rl_2");
            SteamGuardAccount account3 = CreateAccount(76561198000024003UL, "trade_rl_3");

            var fetchedAccounts = new List<ulong>();
            var reportedBadgeCounts = new List<int>();
            var publishedErrors = new List<string>();

            Manifest manifest = Manifest.GenerateNewManifest(false);
            MainForm form = null;
            using (form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                initialAccounts: new[] { account1, account2, account3 },
                fetchTradeConfirmationsForPageAsync: (account, _) =>
                {
                    fetchedAccounts.Add(account.Session.SteamID);
                    if (account.Session.SteamID == account1.Session.SteamID)
                        return Task.FromResult(new[] { CreateConfirmation(801UL) });

                    throw form.ApplyTradeRateLimit(new SteamWebRequestException(
                        "Steam returned HTTP 429.",
                        HttpStatusCode.TooManyRequests,
                        new WebHeaderCollection()));
                },
                updateTradePendingCount: count => reportedBadgeCounts.Add(count),
                publishTradesAsync: (errorMessage, _) =>
                {
                    publishedErrors.Add(errorMessage);
                    return Task.CompletedTask;
                }))
            {
                await form.LoadTradesAsync("all");

                // Account 1 succeeded, Account 2 hit HTTP 429, so Account 3 must NOT be fetched.
                Assert.Equal(new[] { account1.Session.SteamID, account2.Session.SteamID }, fetchedAccounts);
                Assert.Equal(1, reportedBadgeCounts[^1]);
                Assert.NotNull(publishedErrors[^1]);
                Assert.Contains("temporarily limiting confirmation checks", publishedErrors[^1], StringComparison.OrdinalIgnoreCase);

                // Subsequent load while local rate-limit guard is active must make zero fetch calls and still publish the rate-limit error.
                fetchedAccounts.Clear();
                await form.LoadTradesAsync("all");
                Assert.Empty(fetchedAccounts);
                Assert.Contains("temporarily limiting confirmation checks", publishedErrors[^1], StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task LoadTradesAsync_FullRefreshQueuedDuringNarrowedReloadRunsAfterNarrowedReloadCompletes()
        {
            SteamGuardAccount account1 = CreateAccount(76561198000025001UL, "trade_queue_1");
            SteamGuardAccount account2 = CreateAccount(76561198000025002UL, "trade_queue_2");

            var narrowedEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseNarrowed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetchCounts = new ConcurrentDictionary<ulong, int>();

            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                initialAccounts: new[] { account1, account2 },
                fetchTradeConfirmationsForPageAsync: async (account, _) =>
                {
                    int count = fetchCounts.AddOrUpdate(account.Session.SteamID, 1, (_, existing) => existing + 1);
                    if (account.Session.SteamID == account1.Session.SteamID && count == 1)
                    {
                        narrowedEntered.TrySetResult(true);
                        await releaseNarrowed.Task;
                    }
                    return new[] { CreateConfirmation(account.Session.SteamID == account1.Session.SteamID ? 901UL : 902UL) };
                },
                updateTradePendingCount: _ => { },
                publishTradesAsync: (_, __) => Task.CompletedTask))
            {
                Task narrowedReload = form.ReloadTradesForAccountAsync(account1.Session.SteamID);
                await narrowedEntered.Task;

                // While account1's narrowed reload is in flight, user triggers an explicit full refresh for "all".
                Task fullRefresh = form.LoadTradesAsync("all");
                releaseNarrowed.SetResult(true);

                await Task.WhenAll(narrowedReload, fullRefresh);

                Assert.Equal(2, fetchCounts.GetValueOrDefault(account1.Session.SteamID));
                Assert.Equal(1, fetchCounts.GetValueOrDefault(account2.Session.SteamID));
            }
        }

        [Fact]
        public async Task LoadTradesAsync_RefreshQueuedDuringPublishRunsWithoutBeingClobbered()
        {
            SteamGuardAccount account1 = CreateAccount(76561198000026001UL, "trade_pub_1");
            SteamGuardAccount account2 = CreateAccount(76561198000026002UL, "trade_pub_2");

            int publishCount = 0;
            var firstPublishEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstPublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetchCounts = new ConcurrentDictionary<ulong, int>();

            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                initialAccounts: new[] { account1, account2 },
                fetchTradeConfirmationsForPageAsync: (account, _) =>
                {
                    fetchCounts.AddOrUpdate(account.Session.SteamID, 1, (_, existing) => existing + 1);
                    return Task.FromResult(new[] { CreateConfirmation(account.Session.SteamID == account1.Session.SteamID ? 1001UL : 1002UL) });
                },
                updateTradePendingCount: _ => { },
                publishTradesAsync: async (_, __) =>
                {
                    if (Interlocked.Increment(ref publishCount) == 1)
                    {
                        firstPublishEntered.TrySetResult(true);
                        await releaseFirstPublish.Task;
                    }
                }))
            {
                Task firstReload = form.ReloadTradesForAccountAsync(account1.Session.SteamID);
                await firstPublishEntered.Task;

                // Queue account2 narrowed reload while pass 1 is awaiting WebView publication.
                Task secondReload = form.ReloadTradesForAccountAsync(account2.Session.SteamID);
                releaseFirstPublish.SetResult(true);

                await Task.WhenAll(firstReload, secondReload);

                Assert.Equal(1, fetchCounts.GetValueOrDefault(account1.Session.SteamID));
                Assert.Equal(1, fetchCounts.GetValueOrDefault(account2.Session.SteamID));
                Assert.Equal(2, Volatile.Read(ref publishCount));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task LoadTradesAsync_CoalescingSingleAccountRefreshWithOtherAccountReloadFetchesBothAccounts(bool singleAccountQueuedFirst)
        {
            SteamGuardAccount account1 = CreateAccount(76561198000027001UL, "trade_union_1");
            SteamGuardAccount account2 = CreateAccount(76561198000027002UL, "trade_union_2");
            SteamGuardAccount account3 = CreateAccount(76561198000027003UL, "trade_union_3");

            int publishCount = 0;
            var firstPublishEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstPublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetchCounts = new ConcurrentDictionary<ulong, int>();
            var publishedSelections = new List<string>();

            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                initialAccounts: new[] { account1, account2, account3 },
                fetchTradeConfirmationsForPageAsync: (account, _) =>
                {
                    fetchCounts.AddOrUpdate(account.Session.SteamID, 1, (_, existing) => existing + 1);
                    return Task.FromResult(new[] { CreateConfirmation(account.Session.SteamID % 10000UL) });
                },
                updateTradePendingCount: _ => { },
                publishTradesAsync: async (_, selection) =>
                {
                    publishedSelections.Add(selection);
                    if (Interlocked.Increment(ref publishCount) == 1)
                    {
                        firstPublishEntered.TrySetResult(true);
                        await releaseFirstPublish.Task;
                    }
                }))
            {
                string account2Selection = account2.Session.SteamID.ToString(CultureInfo.InvariantCulture);

                Task initialLoad = form.ReloadTradesForAccountAsync(account3.Session.SteamID);
                await firstPublishEntered.Task;
                fetchCounts.Clear();

                // While pass 1 is publishing, queue BOTH a single-account refresh for account2 AND a narrowed reload for account1.
                Task queuedA;
                Task queuedB;
                if (singleAccountQueuedFirst)
                {
                    queuedA = form.LoadTradesAsync(account2Selection);
                    queuedB = form.ReloadTradesForAccountAsync(account1.Session.SteamID);
                }
                else
                {
                    queuedA = form.ReloadTradesForAccountAsync(account1.Session.SteamID);
                    queuedB = form.LoadTradesAsync(account2Selection);
                }

                releaseFirstPublish.SetResult(true);
                await Task.WhenAll(initialLoad, queuedA, queuedB);

                // Both account1 (explicit post-action reload) and account2 (selected view) must be fetched; account3 must not.
                Assert.Equal(1, fetchCounts.GetValueOrDefault(account1.Session.SteamID));
                Assert.Equal(1, fetchCounts.GetValueOrDefault(account2.Session.SteamID));
                Assert.Equal(0, fetchCounts.GetValueOrDefault(account3.Session.SteamID));
                Assert.Equal(account2Selection, publishedSelections[^1]);
            }
        }

        private static SteamGuardAccount CreateAccount(ulong steamId, string accountName)
        {
            return new SteamGuardAccount
            {
                AccountName = accountName,
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:00000000-0000-0000-0000-000000000001",
                FullyEnrolled = true,
                Session = new SessionData
                {
                    SteamID = steamId,
                    AccessToken = CreateUnexpiredToken(steamId),
                    RefreshToken = CreateUnexpiredToken(steamId + 100000UL)
                }
            };
        }

        private static Confirmation CreateConfirmation(ulong id)
        {
            return new Confirmation
            {
                ID = id,
                Key = id + 1000UL,
                Creator = id + 2000UL,
                ConfType = Confirmation.EMobileConfirmationType.Trade,
                TypeName = "Trade",
                Headline = "Trade #" + id
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
    }
}
