using Steam_Desktop_Authenticator;
using SteamKit2;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class SilentReloginServiceTests : IDisposable
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-silent-relogin-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public SilentReloginServiceTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        private sealed class FakeCredentialLoginTransport : ISteamCredentialLoginTransport
        {
            public Func<SteamGuardAccount, string, CancellationToken, Task<SilentReloginTransportResult>> Handler { get; set; }
            public int CallCount { get; private set; }

            public Task<SilentReloginTransportResult> LoginAsync(SteamGuardAccount account, string password, CancellationToken cancellationToken)
            {
                CallCount++;
                return Handler(account, password, cancellationToken);
            }
        }

        private static SteamGuardAccount CreateAccount(ulong steamId, string name)
        {
            return new SteamGuardAccount
            {
                AccountName = name,
                SharedSecret = Convert.ToBase64String(Enumerable.Repeat((byte)0x2A, 20).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Repeat((byte)0x3B, 20).ToArray()),
                DeviceID = "android:" + Guid.NewGuid().ToString("D"),
                FullyEnrolled = true,
                Session = new SessionData
                {
                    SteamID = steamId,
                    AccessToken = "expired-access",
                    RefreshToken = "expired-refresh"
                }
            };
        }

        [Fact]
        public async Task HeadlessGuardAuthenticator_WaitsForNext30sWindowWithoutConsumingMismatchRetryAndAbortsOnSecondMismatch()
        {
            SteamGuardAccount account = CreateAccount(76561198000003001UL, "totp_user");
            long fakeSteamTime = 1_700_000_010L; // chunk = 56666667
            int delayCalls = 0;

            HeadlessGuardAuthenticator authenticator = new HeadlessGuardAuthenticator(
                account,
                CancellationToken.None,
                _ => Task.FromResult(fakeSteamTime),
                (delay, ct) =>
                {
                    delayCalls++;
                    fakeSteamTime += (long)Math.Ceiling(delay.TotalSeconds);
                    return Task.CompletedTask;
                });

            Assert.False(await authenticator.AcceptDeviceConfirmationAsync());

            // 1st code in chunk N
            string code1 = await authenticator.GetDeviceCodeAsync(previousCodeWasIncorrect: false);
            Assert.Equal(5, code1.Length);
            Assert.Equal(0, delayCalls);

            // 2nd call in same chunk (without previousCodeWasIncorrect) waits for next chunk N+1 without consuming mismatch retry!
            string code2 = await authenticator.GetDeviceCodeAsync(previousCodeWasIncorrect: false);
            Assert.Equal(5, code2.Length);
            Assert.Equal(1, delayCalls);

            // 1st mismatch retry is allowed (waits for chunk N+2)
            string code3 = await authenticator.GetDeviceCodeAsync(previousCodeWasIncorrect: true);
            Assert.Equal(5, code3.Length);
            Assert.Equal(2, delayCalls);

            // 2nd mismatch aborts cleanly with GuardRejected
            SilentReloginAbortException ex = await Assert.ThrowsAsync<SilentReloginAbortException>(
                () => authenticator.GetDeviceCodeAsync(previousCodeWasIncorrect: true));
            Assert.Equal(SilentReloginOutcomeKind.GuardRejected, ex.OutcomeKind);

            // Email code request aborts cleanly with InteractiveRequired
            SilentReloginAbortException emailEx = await Assert.ThrowsAsync<SilentReloginAbortException>(
                () => authenticator.GetEmailCodeAsync("a@b.com", false));
            Assert.Equal(SilentReloginOutcomeKind.InteractiveRequired, emailEx.OutcomeKind);
        }

        [Fact]
        public async Task InvalidPassword_LocksOutImmediatelyWithZeroRetriesAndPersistsAcrossRestart()
        {
            const string passKey = "StrongPasskey!99";
            ulong steamId = 76561198000003002UL;
            SteamGuardAccount account = CreateAccount(steamId, "bad_pw_user");

            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(account, true, passKey).Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId, "wrong-steam-password", passKey).Succeeded);

            DateTime now = new DateTime(2026, 11, 22, 15, 0, 0, DateTimeKind.Utc);
            var transport = new FakeCredentialLoginTransport
            {
                Handler = (acc, pw, ct) => Task.FromResult(new SilentReloginTransportResult
                {
                    Kind = SilentReloginOutcomeKind.InvalidPassword,
                    SteamResult = EResult.InvalidPassword
                })
            };

            SilentReloginService service = new SilentReloginService(transport, () => now);
            Assert.True(service.CanAttemptRecovery(manifest, steamId, passKey));

            SilentReloginResult first = await service.TryRecoverAccountSessionAsync(manifest, account, passKey, CancellationToken.None);
            Assert.Equal(SilentReloginOutcomeKind.InvalidPassword, first.Kind);
            Assert.False(first.RejectionPersistFailed);
            Assert.Equal(1, transport.CallCount);

            // Second attempt must NOT call Steam again!
            Assert.False(service.CanAttemptRecovery(manifest, steamId, passKey));
            SilentReloginResult second = await service.TryRecoverAccountSessionAsync(manifest, account, passKey, CancellationToken.None);
            Assert.Equal(SilentReloginOutcomeKind.InvalidPassword, second.Kind);
            Assert.Equal(1, transport.CallCount);

            // Reload from disk -> rejection persists across restart!
            Manifest reloaded = Manifest.GetManifest(true);
            SavedCredentialRecord reloadedRecord = reloaded.GetSavedCredentialRecord(steamId, passKey);
            Assert.NotNull(reloadedRecord);
            Assert.True(reloadedRecord.PasswordRejected);

            // Interactive session save WITHOUT updating password must NOT clear PasswordRejected!
            account.Session = new SessionData { SteamID = steamId, AccessToken = "manual-at", RefreshToken = "manual-rt" };
            Assert.True(reloaded.SaveAccount(account, true, passKey).Succeeded);
            Assert.True(reloaded.GetSavedCredentialRecord(steamId, passKey).PasswordRejected);

            // Updating saved password generates a fresh CredentialGenerationId and re-enables recovery
            Assert.True(reloaded.SaveAccountPassword(steamId, "new-correct-password", passKey).Succeeded);
            service.ClearAccountRuntimeState(steamId);
            Assert.True(service.CanAttemptRecovery(reloaded, steamId, passKey));
        }

        [Fact]
        public async Task RateLimitedAndTransientFailures_ApplyCooldownWithoutMarkingPasswordRejected()
        {
            const string passKey = "StrongPasskey!88";
            ulong steamId = 76561198000003003UL;
            SteamGuardAccount account = CreateAccount(steamId, "throttled_user");

            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(account, true, passKey).Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId, "good-password", passKey).Succeeded);

            DateTime now = new DateTime(2026, 11, 22, 16, 0, 0, DateTimeKind.Utc);
            var transport = new FakeCredentialLoginTransport
            {
                Handler = (acc, pw, ct) => Task.FromResult(new SilentReloginTransportResult
                {
                    Kind = SilentReloginOutcomeKind.RateLimited,
                    SteamResult = EResult.RateLimitExceeded
                })
            };

            SilentReloginService service = new SilentReloginService(transport, () => now);
            SilentReloginResult res = await service.TryRecoverAccountSessionAsync(manifest, account, passKey, CancellationToken.None);
            Assert.Equal(SilentReloginOutcomeKind.RateLimited, res.Kind);
            Assert.False(manifest.GetSavedCredentialRecord(steamId, passKey).PasswordRejected);

            // Blocked during 15-minute cooldown
            now = now.AddMinutes(10);
            Assert.False(service.CanAttemptRecovery(manifest, steamId, passKey));

            // Eligible again after 15-minute cooldown
            now = now.AddMinutes(6);
            Assert.True(service.CanAttemptRecovery(manifest, steamId, passKey));
        }

        [Fact]
        public async Task OptOutDuringInFlightLogin_RejectsSessionCommitAtomically()
        {
            const string passKey = "StrongPasskey!77";
            ulong steamId = 76561198000003004UL;
            SteamGuardAccount account = CreateAccount(steamId, "optout_race_user");

            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(account, true, passKey).Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId, "good-password", passKey).Succeeded);

            var transport = new FakeCredentialLoginTransport
            {
                Handler = (acc, pw, ct) =>
                {
                    // User disables saved passwords while network login is in flight!
                    Assert.True(manifest.SetAllowSavedSteamPasswords(false).Succeeded);
                    return Task.FromResult(new SilentReloginTransportResult
                    {
                        Kind = SilentReloginOutcomeKind.Succeeded,
                        Session = new SessionData
                        {
                            SteamID = steamId,
                            AccessToken = "new-access",
                            RefreshToken = "new-refresh"
                        }
                    });
                }
            };

            SilentReloginService service = new SilentReloginService(transport);
            SilentReloginResult result = await service.TryRecoverAccountSessionAsync(manifest, account, passKey, CancellationToken.None);
            Assert.Equal(SilentReloginOutcomeKind.PersistenceRejected, result.Kind);
            Assert.Equal("expired-access", account.Session.AccessToken);
        }

        [Fact]
        public async Task AccountMismatch_RejectsSessionWithoutOverwritingAccount()
        {
            const string passKey = "StrongPasskey!66";
            ulong steamId = 76561198000003005UL;
            SteamGuardAccount account = CreateAccount(steamId, "mismatch_user");

            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(account, true, passKey).Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId, "good-password", passKey).Succeeded);

            var transport = new FakeCredentialLoginTransport
            {
                Handler = (acc, pw, ct) => Task.FromResult(new SilentReloginTransportResult
                {
                    Kind = SilentReloginOutcomeKind.Succeeded,
                    Session = new SessionData
                    {
                        SteamID = steamId + 999UL,
                        AccessToken = "wrong-acct-at",
                        RefreshToken = "wrong-acct-rt"
                    }
                })
            };

            SilentReloginService service = new SilentReloginService(transport);
            SilentReloginResult result = await service.TryRecoverAccountSessionAsync(manifest, account, passKey, CancellationToken.None);
            Assert.Equal(SilentReloginOutcomeKind.AccountMismatch, result.Kind);
            Assert.Equal("expired-access", account.Session.AccessToken);
            Assert.False(service.CanAttemptRecovery(manifest, steamId, passKey));
        }

        [Fact]
        public async Task PausedSilentRenewal_DoesNotOverwriteAuthenticatorReplacedByTransfer()
        {
            const string passKey = "StrongPasskey!77";
            ulong steamId = 76561198000003006UL;
            SteamGuardAccount oldAccount = CreateAccount(steamId, "race_user");
            oldAccount.SharedSecret = Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 20).ToArray());
            oldAccount.RevocationCode = "R11111";

            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(oldAccount, true, passKey).Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId, "good-password", passKey).Succeeded);

            var enteredLogin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLogin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new FakeCredentialLoginTransport
            {
                Handler = async (acc, pw, ct) =>
                {
                    enteredLogin.SetResult(true);
                    await releaseLogin.Task;
                    return new SilentReloginTransportResult
                    {
                        Kind = SilentReloginOutcomeKind.Succeeded,
                        Session = new SessionData
                        {
                            SteamID = steamId,
                            AccessToken = "stale-renewal-at",
                            RefreshToken = "stale-renewal-rt"
                        }
                    };
                }
            };

            SilentReloginService service = new SilentReloginService(transport);
            Task<SilentReloginResult> renewalTask = service.TryRecoverAccountSessionAsync(manifest, oldAccount, passKey, CancellationToken.None);
            await enteredLogin.Task;

            // While renewal is paused, a transfer commits new replacement secrets!
            SteamGuardAccount transferredReplacement = CreateAccount(steamId, "race_user");
            transferredReplacement.SharedSecret = Convert.ToBase64String(Enumerable.Repeat((byte)0x99, 20).ToArray());
            transferredReplacement.RevocationCode = "R99999";
            Assert.True(manifest.SaveAccount(transferredReplacement, true, passKey, transferAttemptId: "transfer-new").Succeeded);

            // Release the paused renewal -> MUST be rejected by expectedStorageRevision!
            releaseLogin.SetResult(true);
            SilentReloginResult renewalResult = await renewalTask;
            Assert.Equal(SilentReloginOutcomeKind.PersistenceRejected, renewalResult.Kind);

            SteamGuardAccount persisted = Assert.Single(Manifest.GetManifest(true).GetAllAccounts(passKey));
            Assert.Equal("R99999", persisted.RevocationCode);
            Assert.Equal(transferredReplacement.SharedSecret, persisted.SharedSecret);
        }

        [Fact]
        public void SaveSettingsWithResult_AtomicallyCommitsSettingsAndRetiresCredentialStoreInOneTransaction()
        {
            const string passKey = "StrongPasskey!88";
            ulong steamId = 76561198000003007UL;
            SteamGuardAccount account = CreateAccount(steamId, "atomic_settings_user");

            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(account, true, passKey).Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId, "secret-pw", passKey).Succeeded);
            string credFile = manifest.CredentialsStoreFilename;
            Assert.False(String.IsNullOrEmpty(credFile));

            // Single transaction updates proxy settings AND disables saved passwords together
            StorageResult result = manifest.SaveSettingsWithResult(staged =>
            {
                staged.ProxyEnabled = true;
                staged.ProxyHost = "127.0.0.1";
                staged.ProxyPort = 8080;
                staged.AllowSavedSteamPasswords = false;
            });
            Assert.True(result.Succeeded);
            Assert.False(manifest.AllowSavedSteamPasswords);
            Assert.Null(manifest.CredentialsStoreFilename);
            Assert.True(manifest.ProxyEnabled);
            Assert.False(File.Exists(Path.Combine(dataDirectory, "maFiles", credFile)));

            // Cached metadata also reflects empty store cleanly
            Assert.False(manifest.GetSavedCredentialStatus(steamId, passKey).HasSavedPassword);
        }

        [Fact]
        public async Task PersistenceRejected_EntersTransientCooldownWhenGenerationUnchanged_PreventingLoginStorm()
        {
            const string passKey = "StrongPasskey!88";
            ulong steamId = 76561198000003008UL;
            SteamGuardAccount account = CreateAccount(steamId, "persist_reject_user");

            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(account, true, passKey).Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId, "valid-pw", passKey).Succeeded);

            DateTime now = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
            var transport = new FakeCredentialLoginTransport
            {
                Handler = (acc, pw, ct) =>
                {
                    // Mutate the account's storage revision mid-login so SaveAccount rejects with PersistenceRejected
                    // while keeping the saved credential generation unchanged.
                    Assert.True(manifest.SaveAccount(CreateAccount(steamId, "persist_reject_user"), true, passKey).Succeeded);
                    return Task.FromResult(new SilentReloginTransportResult
                    {
                        Kind = SilentReloginOutcomeKind.Succeeded,
                        Session = new SessionData
                        {
                            SteamID = steamId,
                            AccessToken = "new-at",
                            RefreshToken = "new-rt"
                        }
                    });
                }
            };

            SilentReloginService service = new SilentReloginService(transport, () => now);
            SilentReloginResult first = await service.TryRecoverAccountSessionAsync(manifest, account, passKey, CancellationToken.None);
            Assert.Equal(SilentReloginOutcomeKind.PersistenceRejected, first.Kind);
            Assert.Equal(1, transport.CallCount);

            // Must enter cooldown immediately so 1s timer ticks cannot trigger a login storm
            Assert.False(service.CanAttemptRecovery(manifest, steamId, passKey));
            SilentReloginResult duringCooldown = await service.TryRecoverAccountSessionAsync(manifest, account, passKey, CancellationToken.None);
            Assert.Equal(SilentReloginOutcomeKind.NotEligible, duringCooldown.Kind);
            Assert.Equal(1, transport.CallCount);

            // After TransientFailureCooldown elapses, recovery is eligible again
            now = now.Add(SilentReloginService.TransientFailureCooldown).AddSeconds(1);
            Assert.True(service.CanAttemptRecovery(manifest, steamId, passKey));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }
    }
}
