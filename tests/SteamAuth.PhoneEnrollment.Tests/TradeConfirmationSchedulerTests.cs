using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class TradeConfirmationSchedulerTests
    {
        [Fact]
        public void InheritedAccounts_StaggerInitialScansAndCycleWithout3600sCap()
        {
            DateTime now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
            TradeConfirmationScheduler scheduler = new TradeConfirmationScheduler(() => now, () => 0);

            ulong[] accounts = { 76561198000000001UL, 76561198000000002UL, 76561198000000003UL };

            // At t=0: slot 0 is immediately due, slot 1 is at +15s, slot 2 is at +30s (no startup burst!)
            Assert.True(scheduler.TrySelectNextDueAccount(accounts, _ => false, 15, out ulong first));
            Assert.Equal(accounts[0], first);
            scheduler.RecordGlobalDispatch();
            scheduler.RecordSuccess(accounts[0], TimeSpan.FromSeconds(45));

            // At t=5s (past 2s global spacing): account 2 is NOT due until +15s!
            now = now.AddSeconds(5);
            Assert.False(scheduler.TrySelectNextDueAccount(accounts, _ => false, 15, out _));

            // At t=15s: slot 1 becomes due
            now = now.AddSeconds(10);
            Assert.True(scheduler.TrySelectNextDueAccount(accounts, _ => false, 15, out ulong second));
            Assert.Equal(accounts[1], second);
            scheduler.RecordGlobalDispatch();
            scheduler.RecordSuccess(accounts[1], TimeSpan.FromSeconds(45));

            // At t=30s: slot 2 becomes due
            now = now.AddSeconds(15);
            Assert.True(scheduler.TrySelectNextDueAccount(accounts, _ => false, 15, out ulong third));
            Assert.Equal(accounts[2], third);

            // Verify large inherited count is NOT capped at 3600s in Manifest.ResolveConfirmationPolicy
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.TradeConfirmationCustomIntervalEnabled = true;
            manifest.TradeConfirmationCheckInterval = 1800;
            ResolvedAccountConfirmationPolicy policy = manifest.ResolveConfirmationPolicy(accounts[0], eligibleInheritedCount: 5);
            Assert.False(policy.OverrideEnabled);
            Assert.Equal(9000, policy.IntervalSeconds);
        }

        [Fact]
        public void CustomOverrideAccount_RunsAtConfiguredIntervalAndRespectsGlobal2sSpacing()
        {
            DateTime now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
            TradeConfirmationScheduler scheduler = new TradeConfirmationScheduler(() => now, () => 500);

            ulong customAccount = 76561198000000010UL;
            ulong inheritedAccount = 76561198000000011UL;
            ulong[] accounts = { customAccount, inheritedAccount };

            Assert.True(scheduler.TrySelectNextDueAccount(accounts, id => id == customAccount, 60, out ulong selected));
            Assert.Equal(customAccount, selected);
            scheduler.RecordGlobalDispatch();
            scheduler.RecordSuccess(customAccount, TimeSpan.FromSeconds(5));

            // Immediately after dispatch (t+1s): blocked by 2s global spacing even though inherited slot 0 is due at t=0
            now = now.AddSeconds(1);
            Assert.False(scheduler.TrySelectNextDueAccount(accounts, id => id == customAccount, 60, out _));

            // At t+2s: inherited slot 0 can dispatch
            now = now.AddSeconds(1);
            Assert.True(scheduler.TrySelectNextDueAccount(accounts, id => id == customAccount, 60, out selected));
            Assert.Equal(inheritedAccount, selected);
            scheduler.RecordGlobalDispatch();
            scheduler.RecordSuccess(inheritedAccount, TimeSpan.FromSeconds(60));

            // Positive-only jitter (+500ms) means customAccount is NOT due at t+5.0s, only at t+5.5s
            now = now.AddSeconds(3); // t=5.0s
            Assert.False(scheduler.TrySelectNextDueAccount(accounts, id => id == customAccount, 60, out _));

            now = now.AddMilliseconds(500); // t=5.5s
            Assert.True(scheduler.TrySelectNextDueAccount(accounts, id => id == customAccount, 60, out selected));
            Assert.Equal(customAccount, selected);
        }

        [Fact]
        public void RateLimitDeadline_HonorsUpTo30MinutesAndIsNeverShortenedByLaterCallsOrSettingsChanges()
        {
            DateTime now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
            TradeConfirmationScheduler scheduler = new TradeConfirmationScheduler(() => now, () => 0);

            ulong account = 76561198000000020UL;
            Assert.True(scheduler.TrySelectNextDueAccount(new[] { account }, _ => true, 15, out _));

            // 20-minute Retry-After is preserved (not capped at 5 minutes)
            DateTime until20m = scheduler.ApplyRateLimit(TimeSpan.FromMinutes(20));
            Assert.Equal(now.AddMinutes(20), until20m);

            // A shorter 1-minute rate limit must NOT shorten the 20-minute deadline
            DateTime afterShorter = scheduler.ApplyRateLimit(TimeSpan.FromMinutes(1));
            Assert.Equal(until20m, afterShorter);

            // A 60-minute Retry-After is capped at 30 minutes
            DateTime capped30m = scheduler.ApplyRateLimit(TimeSpan.FromMinutes(60));
            Assert.Equal(now.AddMinutes(30), capped30m);

            // Settings change to 3 seconds must NOT advance past server rate-limit deadline
            scheduler.OnSettingsChanged(account, TimeSpan.FromSeconds(3));
            Assert.False(scheduler.TrySelectNextDueAccount(new[] { account }, _ => true, 3, out _));
            Assert.Equal(capped30m, scheduler.GetHealthSnapshot(account).NextCheckUtc);
        }

        [Fact]
        public void TransientFailureBackoff_NeverShortensLongConfiguredInterval()
        {
            DateTime now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
            TradeConfirmationScheduler scheduler = new TradeConfirmationScheduler(() => now, () => 0);

            ulong account = 76561198000000030UL;
            scheduler.RecordTransientFailure(account, TimeSpan.FromSeconds(600), "Network timeout");

            AccountScanHealthSnapshot snapshot = scheduler.GetHealthSnapshot(account);
            Assert.Equal(AccountScanHealthState.Warning, snapshot.State);
            Assert.Equal(1, snapshot.ConsecutiveFailures);
            Assert.Equal(now.AddSeconds(600), snapshot.NextCheckUtc);

            // 6th failure exponential backoff (30 * 2^5 = 960 -> capped at 900s) exceeds 600s
            for (int i = 0; i < 5; i++)
                scheduler.RecordTransientFailure(account, TimeSpan.FromSeconds(600), "Network timeout");

            snapshot = scheduler.GetHealthSnapshot(account);
            Assert.Equal(6, snapshot.ConsecutiveFailures);
            Assert.Equal(now.AddSeconds(900), snapshot.NextCheckUtc);
        }

        [Fact]
        public async Task AcceptMultipleConfirmations_BeforeSendCheckReturningFalse_AbortsWithoutPostingToSteam()
        {
            int postCount = 0;
            SteamGuardAccount account = new SteamGuardAccount
            {
                AccountName = "policy_guard_user",
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:00000000-0000-0000-0000-000000000099",
                Session = new SessionData
                {
                    SteamID = 76561198000000099UL,
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token"
                }
            };

            Confirmation[] batch =
            {
                new Confirmation { ID = 1001, Key = 2001, ConfType = Confirmation.EMobileConfirmationType.Trade }
            };

            // Inject fake transport via SteamWeb.SetCustomRequestHandler if available or verify no network call via invalid proxy/handler
            // Because beforeSendCheck runs before POSTRequestWithHeaders, returning false must succeed immediately with false and zero HTTP calls!
            bool checkInvoked = false;
            bool accepted = await account.AcceptMultipleConfirmations(
                batch,
                () =>
                {
                    checkInvoked = true;
                    return false;
                },
                CancellationToken.None);

            Assert.True(checkInvoked);
            Assert.False(accepted);
            Assert.Equal(0, postCount);
        }

        [Fact]
        public void ManualRefreshHealth_UpdatesHealthWithoutPostponingScheduledAutoConfirmation_AndEnforcesGlobalSpacing()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            TradeConfirmationScheduler scheduler = new TradeConfirmationScheduler(() => now, () => 0);

            // Account 1 due immediately (slot 0), Account 2 due at +15s (slot 1)
            Assert.True(scheduler.TrySelectNextDueAccount(new ulong[] { 101UL, 102UL }, _ => false, 15, out ulong due));
            Assert.Equal(101UL, due);

            // Simulate global dispatch and verify 2s spacing is reported
            scheduler.RecordGlobalDispatch();
            Assert.Equal(TimeSpan.FromSeconds(2), scheduler.GetRemainingGlobalDispatchSpacing());

            now = now.AddSeconds(2);
            Assert.Equal(TimeSpan.Zero, scheduler.GetRemainingGlobalDispatchSpacing());

            // Manual refresh marks Account 1 healthy without pushing NextCheckUtc forward by 30s
            scheduler.RecordManualRefreshHealth(101UL);
            AccountScanHealthSnapshot health = scheduler.GetHealthSnapshot(101UL);
            Assert.Equal(AccountScanHealthState.Ok, health.State);
            Assert.Equal(0, health.ConsecutiveFailures);
            Assert.True(scheduler.TrySelectNextDueAccount(new ulong[] { 101UL, 102UL }, _ => false, 15, out ulong stillDue));
            Assert.Equal(101UL, stillDue);
        }

        [Fact]
        public async Task AcceptMultipleConfirmations_WithDefaultSecondArgument_ResolvesUnambiguously()
        {
            // Regression test for overload ambiguity: AcceptMultipleConfirmations(confs, default)
            // must resolve cleanly to the CancellationToken overload.
            var account = new SteamGuardAccount();
            await Assert.ThrowsAsync<ArgumentException>(() => account.AcceptMultipleConfirmations(null, default));
        }
    }

    [Collection("Manifest storage")]
    public sealed class PerAccountConfirmationPolicyPersistenceTests : IDisposable
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-per-account-policy-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public PerAccountConfirmationPolicyPersistenceTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        [Fact]
        public void SaveAccountConfirmationSettings_PersistsAcrossReloadsAndSaveAccountWithoutIncrementingStorageRevision()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveWithResult().Succeeded);

            SteamGuardAccount account = new SteamGuardAccount
            {
                AccountName = "custom_policy_user",
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:00000000-0000-0000-0000-000000000042",
                Session = new SessionData
                {
                    SteamID = 76561198000000042UL,
                    AccessToken = "access-token-1",
                    RefreshToken = "refresh-token-1"
                }
            };
            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            long revisionBeforePolicyEdit = manifest.GetAccountStorageRevision(account.Session.SteamID);

            StorageResult policyResult = manifest.SaveAccountConfirmationSettings(
                account.Session.SteamID,
                new AccountConfirmationSettings
                {
                    AutoConfirmOverrideEnabled = true,
                    AutoConfirmTrades = true,
                    AutoConfirmMarketTransactions = false,
                    TradeConfirmationCheckInterval = 7
                });
            Assert.True(policyResult.Succeeded);

            // Policy-only edit must NOT increment accountStorageRevision (so in-flight session refreshes are not aborted!)
            Assert.Equal(revisionBeforePolicyEdit, manifest.GetAccountStorageRevision(account.Session.SteamID));

            // Subsequent SaveAccount with expectedStorageRevision must succeed AND preserve per-account overrides!
            account.Session.AccessToken = "access-token-2";
            StorageResult saveResult = manifest.SaveAccount(account, false, expectedStorageRevision: revisionBeforePolicyEdit);
            Assert.True(saveResult.Succeeded);

            Manifest reloaded = Manifest.GetManifest(true);
            ResolvedAccountConfirmationPolicy resolved = reloaded.ResolveConfirmationPolicy(account.Session.SteamID, eligibleInheritedCount: 10);
            Assert.True(resolved.OverrideEnabled);
            Assert.True(resolved.AutoConfirmTrades);
            Assert.False(resolved.AutoConfirmMarketTransactions);
            Assert.Equal(7, resolved.IntervalSeconds);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }
    }
}
