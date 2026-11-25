using Newtonsoft.Json;
using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.IO;
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

        [Fact]
        public async Task TryVerifyLegacyDeviceCodeAcceptanceAsync_RequiresAffirmativeOkAndRejectsDuplicateRequest()
        {
            SteamGuardAccount legacyUpgraded = CreateAccount(76561198000009015UL, "upgraded-legacy-dup", fullyEnrolled: false);
            legacyUpgraded.TokenGID = null;
            legacyUpgraded.DeviceID = null;

            Assert.True(LoginForm.RequiresLegacyDeviceCodeProof(LoginForm.LoginType.Refresh, legacyUpgraded));
            Assert.False(LoginForm.RequiresLegacyDeviceCodeProof(LoginForm.LoginType.Initial, legacyUpgraded));

            // Affirmative EResult.OK proves Steam accepted this local record's TOTP code.
            bool okAccepted = await LoginForm.TryVerifyLegacyDeviceCodeAcceptanceAsync(
                _ => Task.FromResult("ABCDE"),
                (_, _) => Task.FromResult(SteamKit2.EResult.OK),
                CancellationToken.None);
            Assert.True(okAccepted);

            // EResult.DuplicateRequest (e.g., mobile approval completing sign-in ahead of local code)
            // must NOT count as affirmative code acceptance and must leave legacy recovery Unverified.
            bool duplicateAccepted = await LoginForm.TryVerifyLegacyDeviceCodeAcceptanceAsync(
                _ => Task.FromResult("ABCDE"),
                (_, _) => Task.FromResult(SteamKit2.EResult.DuplicateRequest),
                CancellationToken.None);
            Assert.False(duplicateAccepted);

            bool mismatchAccepted = await LoginForm.TryVerifyLegacyDeviceCodeAcceptanceAsync(
                _ => Task.FromResult("ABCDE"),
                (_, _) => Task.FromResult(SteamKit2.EResult.TwoFactorCodeMismatch),
                CancellationToken.None);
            Assert.False(mismatchAccepted);

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
            Assert.Equal(
                AuthenticatorActivationStatus.Unverified,
                SteamGuardAccount.EvaluateActivationStatusResponse(
                    legacyUpgraded,
                    activeStatus,
                    allowUncorroboratedActiveWhenDeviceCodeVerified: duplicateAccepted));
            Assert.Equal(
                AuthenticatorActivationStatus.ActiveForThisRecord,
                SteamGuardAccount.EvaluateActivationStatusResponse(
                    legacyUpgraded,
                    activeStatus,
                    allowUncorroboratedActiveWhenDeviceCodeVerified: okAccepted));
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
