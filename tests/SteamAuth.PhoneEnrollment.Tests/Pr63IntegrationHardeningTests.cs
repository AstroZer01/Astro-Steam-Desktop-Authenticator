using Newtonsoft.Json;
using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public class Pr63IntegrationHardeningTests : IDisposable
    {
        private readonly string tempDir;
        private readonly string previousDataDirectory;

        public Pr63IntegrationHardeningTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            tempDir = Path.Combine(Path.GetTempPath(), "AstroSDA_Pr63Hardening_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", tempDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
            }
        }

        private static SteamGuardAccount CreateAccount(ulong steamId, string accountName, bool fullyEnrolled = true)
        {
            return new SteamGuardAccount
            {
                AccountName = accountName,
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                RevocationCode = "R12345",
                TokenGID = "gid-" + steamId,
                DeviceID = "android:11111111-1111-1111-1111-111111111111",
                FullyEnrolled = fullyEnrolled,
                Session = new SessionData
                {
                    SteamID = steamId,
                    AccessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()),
                    RefreshToken = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds())
                }
            };
        }

        private static string CreateJwt(long expUnixSeconds)
        {
            string header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"EdDSA\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"exp\":" + expUnixSeconds + "}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return header + "." + payload + ".signature";
        }

        private sealed class FakeSilentReloginTransport : ISteamCredentialLoginTransport
        {
            public int CallCount { get; private set; }
            public SessionData SessionToReturn { get; set; }

            public Task<SilentReloginTransportResult> LoginAsync(SteamGuardAccount account, string password, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(new SilentReloginTransportResult
                {
                    Kind = SilentReloginOutcomeKind.Succeeded,
                    Session = SessionToReturn ?? new SessionData
                    {
                        SteamID = account.Session.SteamID,
                        AccessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds()),
                        RefreshToken = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds())
                    }
                });
            }
        }

        [Fact]
        public async Task SilentRelogin_RejectsPendingAccount_EvenWithSavedPasswordAndSucceedingTransport()
        {
            const string passKey = "VaultPasskey!123";
            const ulong steamId = 76561198000009001UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount pending = CreateAccount(steamId, "pending-silent", fullyEnrolled: false);
            StorageResult saveResult = manifest.SaveAccount(pending, encrypt: true, passKey: passKey);
            Assert.True(saveResult.Succeeded);

            manifest.AllowSavedSteamPasswords = true;
            Assert.True(manifest.Save());
            StorageResult credResult = manifest.SaveAccountPassword(steamId, "SuperSecretPassword!", passKey);
            Assert.True(credResult.Succeeded);

            FakeSilentReloginTransport transport = new FakeSilentReloginTransport();
            SilentReloginService service = new SilentReloginService(transport, () => DateTime.UtcNow);

            SilentReloginResult outcome = await service.TryRecoverAccountSessionAsync(manifest, pending, passKey, CancellationToken.None);

            Assert.Equal(SilentReloginOutcomeKind.NotEligible, outcome.Kind);
            Assert.Equal(0, transport.CallCount);
            Assert.False(pending.FullyEnrolled);

            SteamGuardAccount[] reloaded = manifest.GetAllAccounts(passKey);
            SteamGuardAccount persisted = Assert.Single(reloaded);
            Assert.False(persisted.FullyEnrolled);
        }

        [Fact]
        public void TryResolveManifestPassKey_EmptyVaultFailedPrompt_AbortsWithoutDisablingEncryption()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.Encrypted = true;
            Assert.True(manifest.Save());

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                suppliedPassKey: null,
                promptSetupPassKey: _ => (Manifest.SetupPassKeyStatus.Failed, null),
                promptCurrentPassKey: (_, _) => (true, null),
                showInvalidPassKeyMessage: _ => { },
                out string resolvedPassKey);

            Assert.False(resolved);
            Assert.Null(resolvedPassKey);
            Assert.True(manifest.Encrypted);
        }

        [Fact]
        public void ShouldEncryptAccountWrite_HandlesEmptyEncryptedVaultAndActiveVaultConsistently()
        {
            Manifest emptyEncrypted = Manifest.GenerateNewManifest(false);
            emptyEncrypted.Encrypted = true;

            Assert.False(LoginForm.ShouldEncryptAccountWrite(emptyEncrypted, null));
            Assert.False(LoginForm.ShouldEncryptAccountWrite(emptyEncrypted, string.Empty));
            Assert.True(LoginForm.ShouldEncryptAccountWrite(emptyEncrypted, "NewPasskey!1"));

            SteamGuardAccount existing = CreateAccount(76561198000009002UL, "existing-user");
            Assert.True(emptyEncrypted.SaveAccount(existing, encrypt: true, passKey: "NewPasskey!1").Succeeded);
            Assert.True(LoginForm.ShouldEncryptAccountWrite(emptyEncrypted, null));
        }

        [Fact]
        public void EnrollmentRevisionGuard_PreventsFinalSaveAndCancelFromOverwritingOrDeletingReplacement()
        {
            const string passKey = "VaultPasskey!456";
            const ulong steamId = 76561198000009003UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);

            SteamGuardAccount pending = CreateAccount(steamId, "user-original", fullyEnrolled: false);
            pending.RevocationCode = "R-OLD-001";
            StorageResult initialSave = manifest.SaveAccount(pending, encrypt: true, passKey: passKey);
            Assert.True(initialSave.Succeeded);
            Assert.True(initialSave.CommittedStorageRevision.HasValue);
            long pendingRevision = initialSave.CommittedStorageRevision.Value;
            Assert.Equal(manifest.GetAccountStorageRevision(steamId), pendingRevision);

            // Simulate concurrent delete and reimport of a replacement record while modal dialog is open.
            Assert.True(manifest.RemoveAccount(pending, passKey));
            SteamGuardAccount replacement = CreateAccount(steamId, "user-replacement", fullyEnrolled: true);
            replacement.RevocationCode = "R-NEW-999";
            StorageResult reimportSave = manifest.SaveAccount(replacement, encrypt: true, passKey: passKey);
            Assert.True(reimportSave.Succeeded);
            Assert.NotEqual(pendingRevision, reimportSave.CommittedStorageRevision);

            // Finalization save bound to pendingRevision must be rejected without touching replacement.
            pending.FullyEnrolled = true;
            StorageResult staleFinalSave = manifest.SaveAccount(
                pending,
                encrypt: true,
                passKey: passKey,
                expectedStorageRevision: pendingRevision,
                allowCreate: false);
            Assert.False(staleFinalSave.Succeeded);

            // Explicit Cancel removal bound to pendingRevision must refuse to delete replacement.
            Assert.False(manifest.RemoveAccount(pending, passKey, expectedStorageRevision: pendingRevision));

            SteamGuardAccount persisted = Assert.Single(manifest.GetAllAccounts(passKey));
            Assert.Equal("R-NEW-999", persisted.RevocationCode);
            Assert.True(persisted.FullyEnrolled);
        }

        [Fact]
        public void RemoveAccount_WithPasskeyAndRevision_SucceedsWhenCredentialStoreAndOtherAccountExist()
        {
            const string passKey = "VaultPasskey!789";
            const ulong existingSteamId = 76561198000009004UL;
            const ulong pendingSteamId = 76561198000009005UL;

            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount existing = CreateAccount(existingSteamId, "kept-account", fullyEnrolled: true);
            Assert.True(manifest.SaveAccount(existing, encrypt: true, passKey: passKey).Succeeded);

            manifest.AllowSavedSteamPasswords = true;
            Assert.True(manifest.Save());
            Assert.True(manifest.SaveAccountPassword(existingSteamId, "KeptPassword!1", passKey).Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(manifest.CredentialsStoreFilename));

            SteamGuardAccount pending = CreateAccount(pendingSteamId, "canceled-pending", fullyEnrolled: false);
            StorageResult pendingSave = manifest.SaveAccount(pending, encrypt: true, passKey: passKey);
            Assert.True(pendingSave.Succeeded);
            Assert.True(pendingSave.CommittedStorageRevision.HasValue);

            // Removing with the resolved passkey and bound revision must succeed cleanly.
            Assert.True(manifest.RemoveAccount(pending, passKey, expectedStorageRevision: pendingSave.CommittedStorageRevision));
            SteamGuardAccount remaining = Assert.Single(manifest.GetAllAccounts(passKey));
            Assert.Equal(existingSteamId, remaining.Session.SteamID);
            Assert.True(manifest.GetSavedCredentialStatus(existingSteamId, passKey).HasSavedPassword);
        }

        [Fact]
        public async Task PendingRecovery_AtomicSnapshotAndStaleRevisionCheck_PreventsStaleVerificationAndOverwrite()
        {
            const ulong steamId = 76561198000009006UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);

            SteamGuardAccount oldPending = CreateAccount(steamId, "pending-old", fullyEnrolled: false);
            oldPending.RevocationCode = "R-OLD";
            StorageResult firstSave = manifest.SaveAccount(oldPending, encrypt: false);
            Assert.True(firstSave.Succeeded);
            long oldRevision = firstSave.CommittedStorageRevision.Value;

            // Replace record during GetAllAccountsWithRevisions file read; retry loop must return consistent newer snapshot.
            bool replaced = false;
            manifest.OnBeforeGetAllAccountsFileReadForTest = () =>
            {
                if (replaced) return;
                replaced = true;
                SteamGuardAccount newerPending = CreateAccount(steamId, "pending-new", fullyEnrolled: false);
                newerPending.RevocationCode = "R-NEW";
                Assert.True(manifest.SaveAccount(newerPending, encrypt: false).Succeeded);
            };

            (SteamGuardAccount Account, long StorageRevision)[] snapshots = manifest.GetAllAccountsWithRevisions();
            manifest.OnBeforeGetAllAccountsFileReadForTest = null;
            Assert.True(replaced);
            (SteamGuardAccount snapAccount, long snapRevision) = Assert.Single(snapshots);
            Assert.Equal("R-NEW", snapAccount.RevocationCode);
            Assert.Equal(manifest.GetAccountStorageRevision(steamId), snapRevision);
            Assert.NotEqual(oldRevision, snapRevision);

            // Stale expectedStorageRevision must fail closed BEFORE calling verifyActivationAsync.
            int verifyCalls = 0;
            LoginForm.PendingAuthenticatorRecoveryResult staleResult = await LoginForm.TryRecoverPendingAccountWithFreshSessionAsync(
                manifest,
                oldPending,
                oldPending.Session,
                passKey: null,
                verifyActivationAsync: (_, _) =>
                {
                    verifyCalls++;
                    return Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord);
                },
                CancellationToken.None,
                expectedStorageRevision: oldRevision);

            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.SaveFailed, staleResult.Status);
            Assert.Equal(0, verifyCalls);
        }

        [Fact]
        public void DeserializeAccountJson_DistinguishesMissingOrNullLegacyFullyEnrolledFromExplicitFalse()
        {
            const ulong legacySteamId = 76561198000009007UL;
            const ulong nullLegacySteamId = 76561198000009012UL;
            const ulong explicitPendingSteamId = 76561198000009008UL;
            string sharedSecret = Convert.ToBase64String(new byte[20]);
            string refresh = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
            string access = CreateJwt(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds());

            // Legacy .maFile with no fully_enrolled field (and no token_gid / device_id).
            string legacyJson = JsonConvert.SerializeObject(new
            {
                shared_secret = sharedSecret,
                identity_secret = sharedSecret,
                account_name = "legacy-imported",
                revocation_code = "R11111",
                Session = new { SteamID = legacySteamId, AccessToken = access, RefreshToken = refresh }
            });

            // Legacy .maFile with explicit null fully_enrolled field must not throw on non-nullable bool.
            string nullLegacyJson = JsonConvert.SerializeObject(new
            {
                shared_secret = sharedSecret,
                identity_secret = sharedSecret,
                account_name = "legacy-null-imported",
                revocation_code = "R11112",
                fully_enrolled = (bool?)null,
                Session = new { SteamID = nullLegacySteamId, AccessToken = access, RefreshToken = refresh }
            });

            // Explicit pending record with fully_enrolled: false.
            string pendingJson = JsonConvert.SerializeObject(new
            {
                shared_secret = sharedSecret,
                identity_secret = sharedSecret,
                account_name = "explicit-pending",
                revocation_code = "R22222",
                fully_enrolled = false,
                Session = new { SteamID = explicitPendingSteamId, AccessToken = access, RefreshToken = refresh }
            });

            SteamGuardAccount legacyAccount = Manifest.DeserializeAccountJson(legacyJson, null);
            SteamGuardAccount nullLegacyAccount = Manifest.DeserializeAccountJson(nullLegacyJson, null);
            SteamGuardAccount pendingAccount = Manifest.DeserializeAccountJson(pendingJson, null);

            Assert.NotNull(legacyAccount);
            Assert.True(legacyAccount.FullyEnrolled);
            Assert.NotNull(nullLegacyAccount);
            Assert.True(nullLegacyAccount.FullyEnrolled);
            Assert.NotNull(pendingAccount);
            Assert.False(pendingAccount.FullyEnrolled);

            // Persist all three through Manifest and reload from disk.
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(legacyAccount, encrypt: false).Succeeded);
            Assert.True(manifest.SaveAccount(nullLegacyAccount, encrypt: false).Succeeded);
            Assert.True(manifest.SaveAccount(pendingAccount, encrypt: false).Succeeded);

            SteamGuardAccount[] reloaded = manifest.GetAllAccounts();
            SteamGuardAccount reloadedLegacy = Assert.Single(reloaded, a => a.Session.SteamID == legacySteamId);
            SteamGuardAccount reloadedNullLegacy = Assert.Single(reloaded, a => a.Session.SteamID == nullLegacySteamId);
            SteamGuardAccount reloadedPending = Assert.Single(reloaded, a => a.Session.SteamID == explicitPendingSteamId);

            Assert.True(reloadedLegacy.FullyEnrolled);
            Assert.False(MainForm.IsPendingEnrollment(reloadedLegacy));
            Assert.Equal(5, MainForm.GetDisplayLoginToken(reloadedLegacy, 1700000000L).Length);

            Assert.True(reloadedNullLegacy.FullyEnrolled);
            Assert.False(MainForm.IsPendingEnrollment(reloadedNullLegacy));
            Assert.Equal(5, MainForm.GetDisplayLoginToken(reloadedNullLegacy, 1700000000L).Length);

            Assert.False(reloadedPending.FullyEnrolled);
            Assert.True(MainForm.IsPendingEnrollment(reloadedPending));
            Assert.Equal(string.Empty, MainForm.GetDisplayLoginToken(reloadedPending, 1700000000L));
        }

        [Fact]
        public void EvaluateActivationStatusResponse_RecoversPreviouslyImportedLegacyRecordOnlyWhenDeviceCodeVerified()
        {
            SteamGuardAccount legacyUpgraded = CreateAccount(76561198000009013UL, "upgraded-legacy", fullyEnrolled: false);
            legacyUpgraded.TokenGID = null;
            legacyUpgraded.DeviceID = null;

            var activeStatus = new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
            {
                Result = 1,
                Body = new SteamAuth.Protocol.CTwoFactor_Status_Response
                {
                    State = 1,
                    TokenGid = "gid-from-steam",
                    DeviceIdentifier = "android:from-steam"
                }
            };

            // Without device-code login proof, a record lacking identifiers stays Unverified.
            Assert.Equal(
                AuthenticatorActivationStatus.Unverified,
                SteamGuardAccount.EvaluateActivationStatusResponse(legacyUpgraded, activeStatus, allowUncorroboratedActiveWhenDeviceCodeVerified: false));

            // With cryptographic device-code login proof AND State == 1, a legacy record lacking identifiers recovers cleanly.
            Assert.Equal(
                AuthenticatorActivationStatus.ActiveForThisRecord,
                SteamGuardAccount.EvaluateActivationStatusResponse(legacyUpgraded, activeStatus, allowUncorroboratedActiveWhenDeviceCodeVerified: true));

            // If Steam reports State == 0, even device-code flag never promotes it.
            var inactiveStatus = new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
            {
                Result = 1,
                Body = new SteamAuth.Protocol.CTwoFactor_Status_Response { State = 0 }
            };
            Assert.Equal(
                AuthenticatorActivationStatus.NotActive,
                SteamGuardAccount.EvaluateActivationStatusResponse(legacyUpgraded, inactiveStatus, allowUncorroboratedActiveWhenDeviceCodeVerified: true));

            // A genuine pending record that HAS local TokenGID/DeviceID still strictly rejects mismatch even with device-code flag.
            SteamGuardAccount pendingWithIdentity = CreateAccount(76561198000009014UL, "pending-with-id", fullyEnrolled: false);
            pendingWithIdentity.TokenGID = "gid-local";
            Assert.Equal(
                AuthenticatorActivationStatus.ActiveForDifferentAuthenticator,
                SteamGuardAccount.EvaluateActivationStatusResponse(pendingWithIdentity, activeStatus, allowUncorroboratedActiveWhenDeviceCodeVerified: true));
        }

        private static SteamKit2.Authentication.AuthPollResult CreateAuthPollResult(string accessToken, string refreshToken)
        {
            var proto = new SteamKit2.Internal.CAuthentication_PollAuthSessionStatus_Response
            {
                access_token = accessToken,
                refresh_token = refreshToken
            };
            return (SteamKit2.Authentication.AuthPollResult)Activator.CreateInstance(
                typeof(SteamKit2.Authentication.AuthPollResult),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new object[] { proto },
                null);
        }

        [Fact]
        public async Task CompleteLegacyDeviceCodeLoginAsync_MismatchThenOk_RecoversLegacyAccountAndBackfillsDeviceIdForConfirmations()
        {
            const ulong steamId = 76561198000009015UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount legacyPending = CreateAccount(steamId, "upgraded-legacy-retry", fullyEnrolled: false);
            legacyPending.TokenGID = null;
            legacyPending.DeviceID = null;
            Assert.True(manifest.SaveAccount(legacyPending, encrypt: false, passKey: null).Succeeded);
            long expectedRevision = manifest.GetAccountStorageRevision(steamId);

            Assert.True(LoginForm.RequiresLegacyDeviceCodeProof(LoginForm.LoginType.Refresh, legacyPending));
            Assert.False(LoginForm.RequiresLegacyDeviceCodeProof(LoginForm.LoginType.Initial, legacyPending));

            var generatedCodes = new Queue<string>(new[] { "OLD01", "NEW02" });
            var submittedCodes = new List<string>();
            int pollCalls = 0;
            string freshAccess = CreateJwt(DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds());
            string freshRefresh = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());

            LoginForm.LegacyDeviceCodeLoginResult loginResult = await LoginForm.CompleteLegacyDeviceCodeLoginAsync(
                _ => Task.FromResult(generatedCodes.Dequeue()),
                (code, _) =>
                {
                    submittedCodes.Add(code);
                    return Task.FromResult(code == "OLD01"
                        ? SteamKit2.EResult.TwoFactorCodeMismatch
                        : SteamKit2.EResult.OK);
                },
                _ =>
                {
                    pollCalls++;
                    // First poll happens right after mismatch (mobile has not approved); second poll completes after OK.
                    return Task.FromResult(pollCalls >= 2
                        ? CreateAuthPollResult(freshAccess, freshRefresh)
                        : null);
                },
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None,
                deadline: TimeSpan.FromSeconds(5),
                delayAsync: (_, _) => Task.CompletedTask);

            Assert.NotNull(loginResult.PollResult);
            Assert.True(loginResult.DeviceCodeAffirmativelyAccepted);
            Assert.Equal(new[] { "OLD01", "NEW02" }, submittedCodes);

            var activeStatus = new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
            {
                Result = 1,
                Body = new SteamAuth.Protocol.CTwoFactor_Status_Response
                {
                    State = 1,
                    TokenGid = "gid-from-steam",
                    DeviceIdentifier = "android:from-steam"
                }
            };

            SessionData freshSession = new SessionData
            {
                SteamID = steamId,
                AccessToken = loginResult.PollResult.AccessToken,
                RefreshToken = loginResult.PollResult.RefreshToken
            };

            LoginForm.PendingAuthenticatorRecoveryResult recovery = await LoginForm.TryRecoverPendingAccountWithFreshSessionAsync(
                manifest,
                legacyPending,
                freshSession,
                passKey: null,
                (candidate, _) => Task.FromResult(SteamGuardAccount.EvaluateActivationStatusDetails(
                    candidate,
                    activeStatus,
                    allowUncorroboratedActiveWhenDeviceCodeVerified: loginResult.DeviceCodeAffirmativelyAccepted)),
                CancellationToken.None,
                expectedStorageRevision: expectedRevision);

            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.Recovered, recovery.Status);

            SteamGuardAccount reloaded = Assert.Single(Manifest.GetManifest(true).GetAllAccounts());
            Assert.True(reloaded.FullyEnrolled);
            Assert.Equal("android:from-steam", reloaded.DeviceID);
            Assert.Equal("gid-from-steam", reloaded.TokenGID);

            // Trade confirmation request construction must succeed on the reloaded record without throwing "Device ID is not present".
            NameValueCollection queryParams = reloaded.GenerateConfirmationQueryParamsAsNVC("conf");
            Assert.Equal("android:from-steam", queryParams["p"]);
            Assert.Equal(steamId.ToString(), queryParams["a"]);
        }

        [Fact]
        public async Task CompleteLegacyDeviceCodeLoginAsync_DuplicateRequest_PollsWithoutResubmittingAndNeverPromotesLegacyRecord()
        {
            const ulong steamId = 76561198000009016UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount legacyPending = CreateAccount(steamId, "upgraded-legacy-dup", fullyEnrolled: false);
            legacyPending.TokenGID = null;
            legacyPending.DeviceID = null;
            Assert.True(manifest.SaveAccount(legacyPending, encrypt: false, passKey: null).Succeeded);

            int submitCalls = 0;
            int pollCalls = 0;
            string freshAccess = CreateJwt(DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds());
            string freshRefresh = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());

            // EResult.DuplicateRequest (mobile approval racing ahead) must poll directly without resubmitting
            // and keep DeviceCodeAffirmativelyAccepted == false.
            LoginForm.LegacyDeviceCodeLoginResult loginResult = await LoginForm.CompleteLegacyDeviceCodeLoginAsync(
                _ => Task.FromResult("ABCDE"),
                (_, _) =>
                {
                    submitCalls++;
                    return Task.FromResult(SteamKit2.EResult.DuplicateRequest);
                },
                _ =>
                {
                    pollCalls++;
                    return Task.FromResult(pollCalls == 2 ? CreateAuthPollResult(freshAccess, freshRefresh) : null);
                },
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None,
                deadline: TimeSpan.FromSeconds(5),
                delayAsync: (_, _) => Task.CompletedTask);

            Assert.NotNull(loginResult.PollResult);
            Assert.False(loginResult.DeviceCodeAffirmativelyAccepted);
            Assert.Equal(1, submitCalls);
            Assert.Equal(2, pollCalls);

            var activeStatus = new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
            {
                Result = 1,
                Body = new SteamAuth.Protocol.CTwoFactor_Status_Response
                {
                    State = 1,
                    TokenGid = "gid-from-steam",
                    DeviceIdentifier = "android:from-steam"
                }
            };

            LoginForm.PendingAuthenticatorRecoveryResult recovery = await LoginForm.TryRecoverPendingAccountWithFreshSessionAsync(
                manifest,
                legacyPending,
                new SessionData { SteamID = steamId, AccessToken = freshAccess, RefreshToken = freshRefresh },
                passKey: null,
                (candidate, _) => Task.FromResult(SteamGuardAccount.EvaluateActivationStatusDetails(
                    candidate,
                    activeStatus,
                    allowUncorroboratedActiveWhenDeviceCodeVerified: loginResult.DeviceCodeAffirmativelyAccepted)),
                CancellationToken.None);

            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.Inconclusive, recovery.Status);
            Assert.False(legacyPending.FullyEnrolled);
            Assert.False(Assert.Single(Manifest.GetManifest(true).GetAllAccounts()).FullyEnrolled);
        }

        [Fact]
        public async Task CompleteLegacyDeviceCodeLoginAsync_EnforcesLinkedDeadlineAndPreservesNonCallerCancellation()
        {
            // 1. Linked deadline cancels waiting delay/poll and throws TimeoutException (not OperationCanceledException).
            await Assert.ThrowsAsync<TimeoutException>(() => LoginForm.CompleteLegacyDeviceCodeLoginAsync(
                _ => Task.FromResult("ABCDE"),
                (_, _) => Task.FromResult(SteamKit2.EResult.OK),
                async ct =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return null;
                },
                TimeSpan.FromSeconds(5),
                CancellationToken.None,
                deadline: TimeSpan.FromMilliseconds(60)));

            // 2. Caller cancellation throws OperationCanceledException with the caller token.
            using (var callerCts = new CancellationTokenSource())
            {
                callerCts.Cancel();
                OperationCanceledException canceledEx = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    LoginForm.CompleteLegacyDeviceCodeLoginAsync(
                        _ => Task.FromResult("ABCDE"),
                        (_, _) => Task.FromResult(SteamKit2.EResult.OK),
                        _ => Task.FromResult<SteamKit2.Authentication.AuthPollResult>(null),
                        TimeSpan.FromMilliseconds(5),
                        callerCts.Token,
                        deadline: TimeSpan.FromSeconds(5)));
                Assert.True(callerCts.IsCancellationRequested);
                Assert.NotNull(canceledEx);
            }

            // 3. Non-caller TaskCanceledException (e.g., SteamKit2 AsyncJob or TimeAligner HTTP timeout)
            // propagates untouched instead of being converted to a silent caller cancel or swallowed.
            await Assert.ThrowsAsync<TaskCanceledException>(() => LoginForm.CompleteLegacyDeviceCodeLoginAsync(
                _ => Task.FromException<string>(new TaskCanceledException("Simulated SteamKit/TimeAligner timeout")),
                (_, _) => Task.FromResult(SteamKit2.EResult.OK),
                _ => Task.FromResult<SteamKit2.Authentication.AuthPollResult>(null),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None,
                deadline: TimeSpan.FromSeconds(5)));

            // 4. Mobile approval arriving while polling after a mismatch returns without local device-code proof.
            string freshAccess = CreateJwt(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds());
            string freshRefresh = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
            LoginForm.LegacyDeviceCodeLoginResult racedAfterMismatch = await LoginForm.CompleteLegacyDeviceCodeLoginAsync(
                _ => Task.FromResult("WRONG"),
                (_, _) => Task.FromResult(SteamKit2.EResult.TwoFactorCodeMismatch),
                _ => Task.FromResult(CreateAuthPollResult(freshAccess, freshRefresh)),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None,
                deadline: TimeSpan.FromSeconds(5),
                delayAsync: (_, _) => Task.CompletedTask);
            Assert.NotNull(racedAfterMismatch.PollResult);
            Assert.False(racedAfterMismatch.DeviceCodeAffirmativelyAccepted);
        }

        [Fact]
        public async Task LegacyRecovery_KeepsAccountPendingWhenSteamOmitsDeviceId_AndRollsBackBackfillOnRevisionConflict()
        {
            const ulong steamId = 76561198000009017UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount legacyPending = CreateAccount(steamId, "legacy-no-device-id", fullyEnrolled: false);
            legacyPending.TokenGID = null;
            legacyPending.DeviceID = null;
            Assert.True(manifest.SaveAccount(legacyPending, encrypt: false, passKey: null).Succeeded);

            SessionData freshSession = new SessionData
            {
                SteamID = steamId,
                AccessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds()),
                RefreshToken = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds())
            };

            // If Steam omits DeviceIdentifier and the local record has no DeviceID, keep pending instead of promoting an unusable record.
            var statusWithoutDeviceId = new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
            {
                Result = 1,
                Body = new SteamAuth.Protocol.CTwoFactor_Status_Response
                {
                    State = 1,
                    TokenGid = "gid-only"
                }
            };
            LoginForm.PendingAuthenticatorRecoveryResult omittedDeviceResult = await LoginForm.TryRecoverPendingAccountWithFreshSessionAsync(
                manifest,
                legacyPending,
                freshSession,
                passKey: null,
                (candidate, _) => Task.FromResult(SteamGuardAccount.EvaluateActivationStatusDetails(
                    candidate,
                    statusWithoutDeviceId,
                    allowUncorroboratedActiveWhenDeviceCodeVerified: true)),
                CancellationToken.None);

            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.Inconclusive, omittedDeviceResult.Status);
            Assert.False(legacyPending.FullyEnrolled);
            Assert.Null(legacyPending.DeviceID);
            Assert.Null(legacyPending.TokenGID);

            // If SaveAccount fails due to a concurrent storage revision change, roll back backfilled DeviceID and TokenGID too.
            long staleRevision = manifest.GetAccountStorageRevision(steamId);
            var statusWithDeviceId = new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
            {
                Result = 1,
                Body = new SteamAuth.Protocol.CTwoFactor_Status_Response
                {
                    State = 1,
                    TokenGid = "gid-rollback",
                    DeviceIdentifier = "android:rollback"
                }
            };
            LoginForm.PendingAuthenticatorRecoveryResult conflictResult = await LoginForm.TryRecoverPendingAccountWithFreshSessionAsync(
                manifest,
                legacyPending,
                freshSession,
                passKey: null,
                (candidate, _) =>
                {
                    // Simulate concurrent modification while QueryStatus was in flight.
                    SteamGuardAccount concurrent = CreateAccount(steamId, "legacy-no-device-id", fullyEnrolled: false);
                    Assert.True(manifest.SaveAccount(concurrent, encrypt: false, passKey: null).Succeeded);
                    return Task.FromResult(SteamGuardAccount.EvaluateActivationStatusDetails(
                        candidate,
                        statusWithDeviceId,
                        allowUncorroboratedActiveWhenDeviceCodeVerified: true));
                },
                CancellationToken.None,
                expectedStorageRevision: staleRevision);

            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.SaveFailed, conflictResult.Status);
            Assert.False(legacyPending.FullyEnrolled);
            Assert.Null(legacyPending.DeviceID);
            Assert.Null(legacyPending.TokenGID);
        }

        [Fact]
        public void LegacyNullFullyEnrolledVault_SupportsManifestScanEncryptionChangeAndColdEncryptedWrite()
        {
            const ulong steamId = 76561198000009018UL;
            const string initialPassKey = "InitialPasskey!123";
            const string rotatedPassKey = "RotatedPasskey!456";

            string maDir = Path.Combine(tempDir, "maFiles");
            Directory.CreateDirectory(maDir);
            string maFilePath = Path.Combine(maDir, steamId + ".maFile");

            SteamGuardAccount seed = CreateAccount(steamId, "legacy-null-vault", fullyEnrolled: true);
            string legacyJson = JsonConvert.SerializeObject(seed)
                .Replace("\"fully_enrolled\":true", "\"fully_enrolled\":null");
            Assert.Contains("\"fully_enrolled\":null", legacyJson);
            File.WriteAllText(maFilePath, legacyJson);

            // 1. Pre-manifest reconstruction (GenerateNewManifest(scanDir: true)) accepts legacy `"fully_enrolled": null`.
            Manifest scanned = Manifest.GenerateNewManifest(scanDir: true, promptForEncryption: false);
            Assert.NotNull(scanned);
            Assert.Single(scanned.Entries);
            Assert.True(Assert.Single(scanned.GetAllAccounts()).FullyEnrolled);

            // 2. Enabling encryption via ChangeEncryptionKey succeeds on the legacy `"fully_enrolled": null` file.
            StorageResult enableResult = scanned.ChangeEncryptionKey(null, initialPassKey);
            Assert.True(enableResult.Succeeded, enableResult.UserMessage);
            Assert.True(scanned.Encrypted);

            // 3. Rotating the encryption passkey via ChangeEncryptionKey also succeeds.
            StorageResult rotateResult = scanned.ChangeEncryptionKey(initialPassKey, rotatedPassKey);
            Assert.True(rotateResult.Succeeded, rotateResult.UserMessage);

            // 4. Cold-start encrypted write (fresh Manifest without calling VerifyPasskey first)
            // exercises IsPasskeyValidForEncryptedWriteLocked on the encrypted `"fully_enrolled": null` file.
            Manifest coldManifest = Manifest.GetManifest(true);
            SteamGuardAccount secondAccount = CreateAccount(76561198000009019UL, "second-after-cold-start", fullyEnrolled: true);
            StorageResult coldSave = coldManifest.SaveAccount(secondAccount, encrypt: true, passKey: rotatedPassKey);
            Assert.True(coldSave.Succeeded, coldSave.UserMessage);

            SteamGuardAccount[] reloaded = Manifest.GetManifest(true).GetAllAccounts(rotatedPassKey);
            Assert.Equal(2, reloaded.Length);
            Assert.All(reloaded, account => Assert.True(account.FullyEnrolled));
        }

        [Fact]
        public void DeserializeAccountJson_RejectsTrailingJsonTokens()
        {
            SteamGuardAccount seed = CreateAccount(76561198000009020UL, "trailing-json-user", fullyEnrolled: true);
            string validJson = JsonConvert.SerializeObject(seed);

            Assert.Throws<JsonReaderException>(() => Manifest.DeserializeAccountJson(validJson + " {\"extra\":true}", null));
            Assert.Throws<JsonReaderException>(() => Manifest.DeserializeAccountJson(validJson + " trailing", null));
        }

        [Fact]
        public async Task PersistRecoveredPendingActivation_BackfillsMissingDeviceIdAcrossUiOnlyReload()
        {
            const string passKey = "ui-reload-backfill-passkey";
            const ulong steamId = 76561198000009021UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount originalAccount = CreateAccount(steamId, "ui-reload-backfill-user", fullyEnrolled: false);
            originalAccount.TokenGID = "gid-ui-backfill";
            originalAccount.DeviceID = null;
            Assert.True(manifest.SaveAccount(originalAccount, encrypt: true, passKey: passKey).Succeeded);

            SessionData originalSession = originalAccount.Session;
            long revisionAtCheck = manifest.GetAccountStorageRevision(steamId);

            using (MainForm form = new MainForm(manifest, null, null, null))
            {
                form.SetEncryptionKey(passKey);
                FieldInfo allAccountsField = typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic);
                allAccountsField.SetValue(form, new[] { originalAccount });

                bool recovered = await MainForm.TryRecoverPendingAccountActivationAsync(
                    originalAccount,
                    _ =>
                    {
                        // Simulate WebView2 NavigationCompleted -> loadAccountsList() replacing the in-memory object
                        // while QueryStatus is in flight and the on-disk storage revision is unchanged.
                        SteamGuardAccount[] reloadedInstances = manifest.GetAllAccounts(passKey);
                        Assert.NotSame(originalAccount, reloadedInstances[0]);
                        Assert.Null(reloadedInstances[0].DeviceID);
                        allAccountsField.SetValue(form, reloadedInstances);
                        return Task.FromResult(new AuthenticatorActivationVerificationResult(
                            AuthenticatorActivationStatus.ActiveForThisRecord,
                            remoteTokenGid: "gid-ui-backfill",
                            remoteDeviceIdentifier: "android:backfilled-across-reload"));
                    },
                    (activated, verification) => form.PersistRecoveredPendingActivation(activated, originalSession, revisionAtCheck, verification),
                    CancellationToken.None);

                Assert.True(recovered);

                SteamGuardAccount currentInMemory = Assert.Single((SteamGuardAccount[])allAccountsField.GetValue(form));
                Assert.True(currentInMemory.FullyEnrolled);
                Assert.Equal("android:backfilled-across-reload", currentInMemory.DeviceID);
                Assert.True(originalAccount.FullyEnrolled);
                Assert.Equal("android:backfilled-across-reload", originalAccount.DeviceID);

                SteamGuardAccount persisted = Assert.Single(Manifest.GetManifest(true).GetAllAccounts(passKey));
                Assert.True(persisted.FullyEnrolled);
                Assert.Equal("android:backfilled-across-reload", persisted.DeviceID);
                NameValueCollection queryParams = persisted.GenerateConfirmationQueryParamsAsNVC("conf");
                Assert.Equal("android:backfilled-across-reload", queryParams["p"]);
            }
        }

        [Fact]
        public async Task CompleteLegacyDeviceCodeLoginAsync_PreservesRateLimitThrottleWithoutFallback()
        {
            int submitCalls = 0;
            int pollCalls = 0;

            SteamKit2.Authentication.AuthenticationException ex = await Assert.ThrowsAsync<SteamKit2.Authentication.AuthenticationException>(() =>
                LoginForm.CompleteLegacyDeviceCodeLoginAsync(
                    _ => Task.FromResult("ABCDE"),
                    (_, _) =>
                    {
                        submitCalls++;
                        return Task.FromResult(SteamKit2.EResult.RateLimitExceeded);
                    },
                    _ =>
                    {
                        pollCalls++;
                        return Task.FromResult<SteamKit2.Authentication.AuthPollResult>(null);
                    },
                    TimeSpan.FromMilliseconds(5),
                    CancellationToken.None,
                    deadline: TimeSpan.FromSeconds(5),
                    delayAsync: (_, _) => Task.CompletedTask));

            Assert.Equal(SteamKit2.EResult.RateLimitExceeded, ex.Result);
            Assert.Equal(1, submitCalls);
            Assert.Equal(0, pollCalls);
        }

        [Fact]
        public void LoginPromptDismissal_ThrowsLoginPromptCanceledAndIsHandledDistinctlyFromTimeouts()
        {
            using (var activeCts = new CancellationTokenSource())
            {
                // Dismissing a prompt while the login attempt is still active throws LoginPromptCanceledException,
                // which LoginForm treats as expected silent cancellation without showing a connection error.
                LoginPromptCanceledException promptCanceled = Assert.Throws<LoginPromptCanceledException>(() =>
                    UserFormAuthenticator.ThrowForPromptDismissal(activeCts.Token));
                Assert.True(LoginForm.IsExpectedLoginCancellation(promptCanceled, activeCts.Token));

                // Non-caller TaskCanceledException (e.g., SteamKit2 AsyncJob or TimeAligner timeout) is NOT treated as silent cancel.
                Assert.False(LoginForm.IsExpectedLoginCancellation(new TaskCanceledException("SteamKit timeout"), activeCts.Token));

                // Once the owning login token is canceled, OperationCanceledException is treated as expected cancellation.
                activeCts.Cancel();
                OperationCanceledException callerCanceled = Assert.Throws<OperationCanceledException>(() =>
                    UserFormAuthenticator.ThrowForPromptDismissal(activeCts.Token));
                Assert.True(LoginForm.IsExpectedLoginCancellation(callerCanceled, activeCts.Token));
            }
        }

        [Fact]
        public async Task CompleteLegacyDeviceCodeLoginAsync_GivesPostAcceptancePollingFullBudgetIndependentOfSubmissionRollover()
        {
            var codes = new Queue<string>(new[] { "OLD01", "NEW02" });
            int pollCalls = 0;
            string freshAccess = CreateJwt(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds());
            string freshRefresh = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());

            // Submission consumes 80ms of a 120ms submission deadline, while post-acceptance polling
            // takes 90ms (exceeding the remaining 40ms of the submission deadline) and still succeeds
            // within its independent 250ms post-acceptance polling budget.
            LoginForm.LegacyDeviceCodeLoginResult result = await LoginForm.CompleteLegacyDeviceCodeLoginAsync(
                _ => Task.FromResult(codes.Dequeue()),
                async (code, ct) =>
                {
                    if (code == "OLD01")
                    {
                        await Task.Delay(80, ct);
                        return SteamKit2.EResult.TwoFactorCodeMismatch;
                    }
                    return SteamKit2.EResult.OK;
                },
                async ct =>
                {
                    pollCalls++;
                    if (pollCalls == 1)
                        return null; // poll right after mismatch
                    await Task.Delay(90, ct);
                    return CreateAuthPollResult(freshAccess, freshRefresh);
                },
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None,
                deadline: TimeSpan.FromMilliseconds(120),
                delayAsync: (_, _) => Task.CompletedTask,
                postAcceptancePollDeadline: TimeSpan.FromMilliseconds(250));

            Assert.NotNull(result.PollResult);
            Assert.True(result.DeviceCodeAffirmativelyAccepted);
        }

        [Fact]
        public async Task PendingActivationRecovery_CancelingDuringVerificationNeverSavesOrPromotes()
        {
            const ulong steamId = 76561198000009022UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount pending = CreateAccount(steamId, "cancel-during-verify", fullyEnrolled: false);
            SessionData originalSession = pending.Session;
            Assert.True(manifest.SaveAccount(pending, encrypt: false, passKey: null).Succeeded);

            // 1. Interactive recovery: canceling token while verifyActivationAsync runs rolls back Session and does not save/promote.
            using (var interactiveCts = new CancellationTokenSource())
            {
                SessionData freshSession = new SessionData
                {
                    SteamID = steamId,
                    AccessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds()),
                    RefreshToken = CreateJwt(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds())
                };

                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    LoginForm.TryRecoverPendingAccountWithFreshSessionAsync(
                        manifest,
                        pending,
                        freshSession,
                        passKey: null,
                        (_, _) =>
                        {
                            interactiveCts.Cancel();
                            return Task.FromResult(new AuthenticatorActivationVerificationResult(
                                AuthenticatorActivationStatus.ActiveForThisRecord,
                                pending.TokenGID,
                                pending.DeviceID));
                        },
                        interactiveCts.Token));

                Assert.False(pending.FullyEnrolled);
                Assert.Same(originalSession, pending.Session);
                Assert.False(Assert.Single(Manifest.GetManifest(true).GetAllAccounts()).FullyEnrolled);
            }

            // 2. Startup recovery: canceling token while verifyActivationAsync runs throws without calling persistActivatedAccount.
            using (var startupCts = new CancellationTokenSource())
            {
                bool persistCalled = false;
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    MainForm.TryRecoverPendingAccountActivationAsync(
                        pending,
                        _ =>
                        {
                            startupCts.Cancel();
                            return Task.FromResult(new AuthenticatorActivationVerificationResult(
                                AuthenticatorActivationStatus.ActiveForThisRecord,
                                pending.TokenGID,
                                pending.DeviceID));
                        },
                        (_, _) =>
                        {
                            persistCalled = true;
                            return true;
                        },
                        startupCts.Token));

                Assert.False(persistCalled);
                Assert.False(pending.FullyEnrolled);
            }
        }

        [Fact]
        public void ShouldDeletePendingOnExplicitCancel_PreservesPendingRecordAfterSteamAcceptsFinalization()
        {
            Assert.True(LoginForm.ShouldDeletePendingOnExplicitCancel(finalizationAcceptedBySteam: false));
            Assert.False(LoginForm.ShouldDeletePendingOnExplicitCancel(finalizationAcceptedBySteam: true));
        }

        [Fact]
        public void SaveAccount_EmptyEncryptedVaultToUnencrypted_ClearsSavedPasswordMetadataAndCredentialFile()
        {
            const string passKey = "VaultPasskey!999";
            const ulong firstSteamId = 76561198000009009UL;
            const ulong secondSteamId = 76561198000009010UL;

            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount first = CreateAccount(firstSteamId, "first-user");
            Assert.True(manifest.SaveAccount(first, encrypt: true, passKey: passKey).Succeeded);
            manifest.AllowSavedSteamPasswords = true;
            Assert.True(manifest.Save());
            Assert.True(manifest.SaveAccountPassword(firstSteamId, "SavedPass!1", passKey).Succeeded);

            string credFilename = manifest.CredentialsStoreFilename;
            Assert.False(string.IsNullOrWhiteSpace(credFilename));
            string credPath = Path.Combine(tempDir, "maFiles", credFilename);
            Assert.True(File.Exists(credPath));

            // Simulate empty encrypted manifest left with stale saved-password metadata.
            manifest.Entries.Clear();
            manifest.Encrypted = true;
            manifest.AllowSavedSteamPasswords = true;
            manifest.CredentialsStoreFilename = credFilename;
            Assert.True(manifest.Save());

            // Saving a new account unencrypted must clear Encrypted, AllowSavedSteamPasswords, CredentialsStoreFilename, and remove old cred file.
            SteamGuardAccount second = CreateAccount(secondSteamId, "unencrypted-user");
            StorageResult saveResult = manifest.SaveAccount(second, encrypt: false, passKey: null);
            Assert.True(saveResult.Succeeded);
            Assert.False(manifest.Encrypted);
            Assert.False(manifest.AllowSavedSteamPasswords);
            Assert.Null(manifest.CredentialsStoreFilename);
            Assert.False(File.Exists(credPath));
        }

        [Fact]
        public void RecomputeExistingEntries_PreservesEntriesOnIndeterminateLookup_AndClearsCredentialsWhenConfirmedMissing()
        {
            const string passKey = "VaultPasskey!321";
            const ulong steamId = 76561198000009011UL;

            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount(steamId, "recompute-user");
            Assert.True(manifest.SaveAccount(account, encrypt: true, passKey: passKey).Succeeded);
            manifest.AllowSavedSteamPasswords = true;
            Assert.True(manifest.Save());
            Assert.True(manifest.SaveAccountPassword(steamId, "SavedPass!2", passKey).Succeeded);

            string maDir = Path.Combine(tempDir, "maFiles");
            string validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);

            // Indeterminate lookup (returns false = not confirmed missing) preserves entries, encryption, and credential metadata.
            manifest.RecomputeExistingEntries(_ => false, validatedRoot);
            Assert.Single(manifest.Entries);
            Assert.True(manifest.Encrypted);
            Assert.True(manifest.AllowSavedSteamPasswords);
            Assert.False(string.IsNullOrWhiteSpace(manifest.CredentialsStoreFilename));

            // Confirmed missing (returns true) prunes entries and clears encryption + saved-password metadata.
            manifest.RecomputeExistingEntries(_ => true, validatedRoot);
            Assert.Empty(manifest.Entries);
            Assert.False(manifest.Encrypted);
            Assert.False(manifest.AllowSavedSteamPasswords);
            Assert.Null(manifest.CredentialsStoreFilename);
        }

        [Fact]
        public void IndexHtml_GatesPendingActivationAccountsInAccountOptionsAndSecretQrExport()
        {
            string indexHtmlPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "Steam Desktop Authenticator", "ui", "index.html"));
            string html = File.ReadAllText(indexHtmlPath);

            Assert.Contains("Verify and activate this authenticator on Steam before saving a password for silent renewal.", html);
            Assert.Contains("Pending Backup Export", html);
            Assert.Contains("Verify Steam activation before exporting an active secret QR code.", html);
            Assert.Contains("Auto-confirmation monitoring stays paused until Steam confirms activation.", html);
        }
    }
}
