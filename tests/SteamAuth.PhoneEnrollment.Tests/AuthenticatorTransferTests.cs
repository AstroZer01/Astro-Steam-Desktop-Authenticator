using Google.Protobuf;
using Steam_Desktop_Authenticator;
using SteamAuth;
using SteamAuth.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class AuthenticatorTransferTests : IDisposable
    {
        private readonly string tempDir;
        private readonly string previousDataDir;

        public AuthenticatorTransferTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "asda-transfer-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            previousDataDir = Environment.GetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable);
            Environment.SetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable, tempDir);
        }

        public void Dispose()
        {
            try
            {
                string recoveryDir = PendingTransferMarker.GetRecoveryDirectoryForCurrentDataDirectory();
                if (Directory.Exists(recoveryDir))
                    Directory.Delete(recoveryDir, true);
            }
            catch
            {
            }

            Environment.SetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable, previousDataDir);
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
            }
        }

        [Fact]
        public void Protobuf_RemoveAuthenticatorViaChallengeMessages_RoundTripCleanly()
        {
            var continueReq = new CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Request
            {
                SmsCode = "12345",
                GenerateNewToken = true,
                Version = 2,
                CallerVersion = 3
            };
            byte[] reqBytes = continueReq.ToByteArray();
            var parsedReq = CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Request.Parser.ParseFrom(reqBytes);
            Assert.Equal("12345", parsedReq.SmsCode);
            Assert.True(parsedReq.GenerateNewToken);
            Assert.Equal(2u, parsedReq.Version);
            Assert.Equal(3u, parsedReq.CallerVersion);

            byte[] shared = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
            byte[] identity = Enumerable.Range(21, 20).Select(i => (byte)i).ToArray();
            var continueResp = new CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Response
            {
                Success = true,
                ReplacementToken = new CRemoveAuthenticatorViaChallengeContinue_Replacement_Token
                {
                    SharedSecret = ByteString.CopyFrom(shared),
                    IdentitySecret = ByteString.CopyFrom(identity),
                    SerialNumber = 123456789UL,
                    RevocationCode = "R12345",
                    Uri = "otpauth://totp/Steam:alice",
                    ServerTime = 1700000000UL,
                    AccountName = "alice",
                    TokenGid = "gid123",
                    Status = 1,
                    Steamid = 76561198000000011UL
                }
            };
            var parsedResp = CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Response.Parser.ParseFrom(continueResp.ToByteArray());
            Assert.True(parsedResp.HasSuccess && parsedResp.Success);
            Assert.Equal(shared, parsedResp.ReplacementToken.SharedSecret.ToByteArray());
            Assert.Equal(identity, parsedResp.ReplacementToken.IdentitySecret.ToByteArray());
            Assert.Equal("R12345", parsedResp.ReplacementToken.RevocationCode);
            Assert.Equal(76561198000000011UL, parsedResp.ReplacementToken.Steamid);
        }

        [Fact]
        public async Task StartAuthenticatorTransferAsync_RequiresPhone_AndDistinguishesAbsentVsFalseSuccess()
        {
            // No phone -> PhoneNumberRequired
            var noPhoneTransport = new FakeTransferTransport(
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }));
            var noPhoneLinker = CreateLinker(noPhoneTransport, 76561198000000011UL);
            Assert.Equal(AuthenticatorLinker.TransferStartStatus.PhoneNumberRequired, await noPhoneLinker.StartAuthenticatorTransferAsync());
            Assert.Equal(new[] { "AccountPhoneStatus" }, noPhoneTransport.Methods);

            // Absent Body.HasSuccess with EResult 1 -> SmsSent
            var absentSuccessTransport = new FakeTransferTransport(
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = true }),
                Response(1, new CTwoFactor_RemoveAuthenticatorViaChallengeStart_Response()));
            var okLinker = CreateLinker(absentSuccessTransport, 76561198000000011UL);
            Assert.Equal(AuthenticatorLinker.TransferStartStatus.SmsSent, await okLinker.StartAuthenticatorTransferAsync());

            // Explicit Body.Success == false -> Rejected
            var explicitFalseTransport = new FakeTransferTransport(
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = true }),
                Response(1, new CTwoFactor_RemoveAuthenticatorViaChallengeStart_Response { Success = false }));
            var rejectedLinker = CreateLinker(explicitFalseTransport, 76561198000000011UL);
            Assert.Equal(AuthenticatorLinker.TransferStartStatus.Rejected, await rejectedLinker.StartAuthenticatorTransferAsync());
        }

        [Fact]
        public async Task FinishAuthenticatorTransferAsync_SendsGenerateNewTokenVersion2_AndValidatesCompleteReplacement()
        {
            ulong steamId = 76561198000000022UL;
            var transport = new FakeTransferTransport(
                Response(1, BuildValidReplacementResponse(steamId, "alice")));
            var linker = CreateLinker(transport, steamId);

            AuthenticatorLinker.TransferFinishStatus status = await linker.FinishAuthenticatorTransferAsync("ABCDE");

            Assert.Equal(AuthenticatorLinker.TransferFinishStatus.Success, status);
            Assert.NotNull(linker.LinkedAccount);
            Assert.True(linker.LinkedAccount.FullyEnrolled);
            Assert.True(linker.Finalized);
            Assert.Equal(steamId, linker.LinkedAccount.Session.SteamID);
            Assert.Equal("R99887", linker.LinkedAccount.RevocationCode);
            Assert.Equal(new[] { "RemoveAuthenticatorViaChallengeContinue" }, transport.Methods);

            var sent = Assert.IsType<CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Request>(transport.Requests[0]);
            Assert.Equal("ABCDE", sent.SmsCode);
            Assert.True(sent.GenerateNewToken);
            Assert.Equal(2u, sent.Version);
        }

        [Theory]
        [InlineData("missing_identity")]
        [InlineData("missing_shared")]
        [InlineData("missing_revocation")]
        [InlineData("steamid_mismatch")]
        public async Task FinishAuthenticatorTransferAsync_IncompleteOrMismatchedReplacement_ReturnsIncompleteReplacement(string defect)
        {
            ulong steamId = 76561198000000033UL;
            var resp = BuildValidReplacementResponse(steamId, "bob");
            switch (defect)
            {
                case "missing_identity":
                    resp.ReplacementToken.ClearIdentitySecret();
                    break;
                case "missing_shared":
                    resp.ReplacementToken.ClearSharedSecret();
                    break;
                case "missing_revocation":
                    resp.ReplacementToken.RevocationCode = "";
                    break;
                case "steamid_mismatch":
                    resp.ReplacementToken.Steamid = steamId + 1;
                    break;
            }

            var linker = CreateLinker(new FakeTransferTransport(Response(1, resp)), steamId);
            Assert.Equal(AuthenticatorLinker.TransferFinishStatus.IncompleteReplacement, await linker.FinishAuthenticatorTransferAsync("12345"));
            Assert.Null(linker.LinkedAccount);
        }

        [Theory]
        [InlineData("timeout")]
        [InlineData("io")]
        [InlineData("invalid_data")]
        [InlineData("canceled")]
        public async Task FinishAuthenticatorTransferAsync_PostDispatchException_ReturnsOutcomeUnknownNeverAutoRetried(string failureKind)
        {
            Exception ex = failureKind switch
            {
                "timeout" => new TimeoutException("timed out"),
                "io" => new IOException("connection reset"),
                "invalid_data" => new InvalidDataException("truncated protobuf"),
                _ => new OperationCanceledException("canceled mid-flight")
            };

            var transport = new FakeTransferTransport(ex);
            var linker = CreateLinker(transport, 76561198000000044UL);
            Assert.Equal(AuthenticatorLinker.TransferFinishStatus.OutcomeUnknown, await linker.FinishAuthenticatorTransferAsync("99999"));
            Assert.Single(transport.Methods);
        }

        [Fact]
        public async Task TransferCoordinator_WritesMarkerBeforeDispatch_RetainsOnOutcomeUnknown_AndPersistsBeforeOptionalQueryStatus()
        {
            ulong steamId = 76561198000000055UL;
            var events = new List<string>();

            // Scenario 1: OutcomeUnknown retains marker and never auto-retries
            var unknownTransport = new FakeTransferTransport(
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = true }),
                Response(1, new CTwoFactor_RemoveAuthenticatorViaChallengeStart_Response { Success = true }),
                new InvalidDataException("malformed response"));
            var unknownInteraction = new FakeTransferInteraction(events, smsCodes: new[] { "11111" }, persistSucceeds: true);
            var unknownCoordinator = new AuthenticatorTransferCoordinator(CreateLinker(unknownTransport, steamId), unknownInteraction, steamId, "carol");

            TransferCoordinatorOutcome unknownOutcome = await unknownCoordinator.ExecuteAsync();
            Assert.Equal(TransferCoordinatorResultKind.OutcomeUnknown, unknownOutcome.Kind);
            Assert.True(unknownInteraction.HasMarker);
            Assert.Equal(1, unknownTransport.Methods.Count(m => m == "RemoveAuthenticatorViaChallengeContinue"));

            // Blocked if trying again while marker exists
            var blockedOutcome = await unknownCoordinator.ExecuteAsync();
            Assert.Equal(TransferCoordinatorResultKind.BlockedByPendingTransfer, blockedOutcome.Kind);

            // Scenario 2: BadSmsCode clears marker and retries; Success stages -> persists -> cleans up -> enriches DeviceID
            events.Clear();
            var successTransport = new FakeTransferTransport(
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = true }),
                Response(1, new CTwoFactor_RemoveAuthenticatorViaChallengeStart_Response { Success = true }),
                Response(89, new CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Response()),
                Response(1, BuildValidReplacementResponse(steamId, "carol")),
                Response(1, new CTwoFactor_Status_Response { DeviceIdentifier = "android:steam-reported-device" }));
            var successInteraction = new FakeTransferInteraction(events, smsCodes: new[] { "WRONG", "RIGHT" }, persistSucceeds: true);
            var successCoordinator = new AuthenticatorTransferCoordinator(CreateLinker(successTransport, steamId), successInteraction, steamId, "carol");

            TransferCoordinatorOutcome successOutcome = await successCoordinator.ExecuteAsync();
            Assert.Equal(TransferCoordinatorResultKind.Succeeded, successOutcome.Kind);
            Assert.False(successInteraction.HasMarker);
            Assert.Equal("android:steam-reported-device", successOutcome.LinkedAccount.DeviceID);
            Assert.True(events.IndexOf("stage") < events.IndexOf("persist"));
            Assert.True(events.IndexOf("persist") < events.IndexOf("enrich"));
        }

        [Fact]
        public async Task TransferCoordinator_WhenLocalSaveFails_ReturnsReplacementReceivedSavePendingWithStagedRecovery()
        {
            ulong steamId = 76561198000000066UL;
            var events = new List<string>();
            var transport = new FakeTransferTransport(
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = true }),
                Response(1, new CTwoFactor_RemoveAuthenticatorViaChallengeStart_Response { Success = true }),
                Response(1, BuildValidReplacementResponse(steamId, "dave")));
            var interaction = new FakeTransferInteraction(events, smsCodes: new[] { "12345" }, persistSucceeds: false);
            var coordinator = new AuthenticatorTransferCoordinator(CreateLinker(transport, steamId), interaction, steamId, "dave");

            TransferCoordinatorOutcome outcome = await coordinator.ExecuteAsync();

            Assert.Equal(TransferCoordinatorResultKind.ReplacementReceivedSavePending, outcome.Kind);
            Assert.NotNull(outcome.LinkedAccount);
            Assert.Equal("staged-path", outcome.StagedRecoveryPath);
            Assert.True(interaction.HasMarker);
            Assert.DoesNotContain("QueryStatus", transport.Methods);
        }

        [Fact]
        public void PendingTransferMarker_PerSteamIdIsolation_AndProtectedRecoveryReconciliation()
        {
            ulong steamId1 = 76561198000000071UL;
            ulong steamId2 = 76561198000000072UL;

            Assert.True(PendingTransferMarker.TryWriteMarker(steamId1, "acct1", "android:1", "attempt-acct1"));
            Assert.True(PendingTransferMarker.TryWriteMarker(steamId2, "acct2", "android:2", "attempt-acct2"));
            Assert.True(PendingTransferMarker.HasMarker(steamId1));
            Assert.True(PendingTransferMarker.HasMarker(steamId2));

            List<PendingTransferMarkerRecord> markers = PendingTransferMarker.EnumerateMarkers();
            Assert.Equal(2, markers.Count);
            PendingTransferMarker.DeleteMarkerBestEffort(steamId1);
            Assert.False(PendingTransferMarker.HasMarker(steamId1));
            Assert.True(PendingTransferMarker.HasMarker(steamId2));

            // Protected recovery staging (AES-GCM + DPAPI)
            SteamGuardAccount stagedAccount = new SteamGuardAccount
            {
                AccountName = "acct2",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "R54321",
                TokenGID = "tokengid-2",
                SerialNumber = "999111",
                DeviceID = "android:2",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId2 }
            };

            string stagedPath = PendingTransferMarker.TryStageProtectedReplacement(stagedAccount, manifestEncrypted: true, passKey: "strong-passkey", out bool stagingFailed, transferAttemptId: "attempt-acct2");
            Assert.False(stagingFailed);
            Assert.NotNull(stagedPath);
            Assert.True(File.Exists(stagedPath));

            // Without passkey -> RequiresPasskey = true
            var lockedRecords = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(Array.Empty<SteamGuardAccount>(), passKey: null);
            Assert.Single(lockedRecords);
            Assert.True(lockedRecords[0].RequiresPasskey);

            // With passkey, not yet committed -> loads full replacement
            var loadedRecords = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(Array.Empty<SteamGuardAccount>(), passKey: "strong-passkey");
            Assert.Single(loadedRecords);
            Assert.NotNull(loadedRecords[0].Account);
            Assert.Equal("R54321", loadedRecords[0].Account.RevocationCode);

            // When identical replacement is already committed -> silently cleans up both staged recovery & marker
            var afterCommitRecords = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(new[] { stagedAccount }, passKey: "strong-passkey");
            Assert.Empty(afterCommitRecords);
            Assert.False(File.Exists(stagedPath));
            Assert.False(PendingTransferMarker.HasMarker(steamId2));
        }

        [Fact]
        public void EncryptedFirstAccountTransfer_RecoversIndependentlyAfterRestartWhenManifestClearedEncryption()
        {
            const string chosenPassKey = "FirstAccountTransferPass!42";
            ulong steamId = 76561198000000075UL;
            SteamGuardAccount replacement = new SteamGuardAccount
            {
                AccountName = "first_transfer",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "R77777",
                TokenGID = "gid-first",
                SerialNumber = "111222",
                DeviceID = "android:first",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId, AccessToken = "at", RefreshToken = "rt" }
            };

            string attemptId = Guid.NewGuid().ToString("N");
            string stagedPath = PendingTransferMarker.TryStageProtectedReplacement(replacement, manifestEncrypted: true, passKey: chosenPassKey, out bool stagingFailed, transferAttemptId: attemptId);
            Assert.False(stagingFailed);
            Assert.NotNull(stagedPath);

            // Simulate restart with empty manifest (encryption cleared because 0 entries were committed)
            Manifest reloadedEmpty = Manifest.GenerateNewManifest(false);
            Assert.False(reloadedEmpty.Encrypted);

            var pending = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(Array.Empty<SteamGuardAccount>(), passKey: null, manifest: reloadedEmpty);
            Assert.Single(pending);
            Assert.True(pending[0].RequiresPasskey);

            // Unlock staged recovery independently with user-entered passkey and commit encrypted
            StagedTransferRecoveryRecord unlocked = PendingTransferMarker.TryLoadStagedRecoveryWithPasskey(pending[0].FilePath, chosenPassKey);
            Assert.NotNull(unlocked?.Account);
            Assert.Equal("R77777", unlocked.Account.RevocationCode);

            StorageResult commitResult = reloadedEmpty.SaveAccount(
                unlocked.Account,
                encrypt: true,
                passKey: chosenPassKey,
                transferAttemptId: unlocked.TransferAttemptId,
                expectedPreviousTransferAttemptId: unlocked.PreviousCommittedTransferAttemptId,
                enforceTransferAttemptBase: true);
            Assert.True(commitResult.Succeeded);

            Manifest afterRestart = Manifest.GetManifest(true);
            Assert.True(afterRestart.Encrypted);
            SteamGuardAccount committed = Assert.Single(afterRestart.GetAllAccounts(chosenPassKey));
            Assert.Equal("R77777", committed.RevocationCode);
        }

        [Fact]
        public void SecondTransferAfterPreviousCommittedTransfer_PreservesStagedRecoveryAndAllowsRetryCommit()
        {
            ulong steamId = 76561198000000078UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);

            SteamGuardAccount firstCommitted = new SteamGuardAccount
            {
                AccountName = "second_transfer_user",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "R10001",
                TokenGID = "gid-1",
                SerialNumber = "1001",
                DeviceID = "android:1",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId, AccessToken = "at1", RefreshToken = "rt1" }
            };
            Assert.True(manifest.SaveAccount(firstCommitted, encrypt: false, transferAttemptId: "attempt-1").Succeeded);

            // Second transfer captures baseline "attempt-1" and stages "attempt-2" before initial SaveAccount fails/crashes
            string baselineAttemptId = manifest.GetLastTransferAttemptId(steamId);
            Assert.Equal("attempt-1", baselineAttemptId);

            SteamGuardAccount secondStaged = new SteamGuardAccount
            {
                AccountName = "second_transfer_user",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(41, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(61, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "R20002",
                TokenGID = "gid-2",
                SerialNumber = "2002",
                DeviceID = "android:2",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId, AccessToken = "at2", RefreshToken = "rt2" }
            };

            string stagedPath = PendingTransferMarker.TryStageProtectedReplacement(
                secondStaged,
                manifestEncrypted: false,
                passKey: null,
                out bool stagingFailed,
                transferAttemptId: "attempt-2",
                previousCommittedTransferAttemptId: baselineAttemptId);
            Assert.False(stagingFailed);
            Assert.True(File.Exists(stagedPath));

            // Restart reconciliation MUST preserve "attempt-2" (NOT delete it as superseded!)
            var pending = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(new[] { firstCommitted }, passKey: null, manifest: manifest);
            StagedTransferRecoveryRecord recovered = Assert.Single(pending);
            Assert.Equal("attempt-2", recovered.TransferAttemptId);
            Assert.Equal("attempt-1", recovered.PreviousCommittedTransferAttemptId);
            Assert.Equal("R20002", recovered.Account.RevocationCode);

            // Retry Local Save MUST succeed because baseline matches "attempt-1"
            StorageResult retryResult = manifest.SaveAccount(
                recovered.Account,
                encrypt: false,
                transferAttemptId: recovered.TransferAttemptId,
                expectedPreviousTransferAttemptId: recovered.PreviousCommittedTransferAttemptId,
                enforceTransferAttemptBase: true);
            Assert.True(retryResult.Succeeded);

            SteamGuardAccount persisted = Assert.Single(Manifest.GetManifest(true).GetAllAccounts());
            Assert.Equal("R20002", persisted.RevocationCode);
        }

        [Fact]
        public void OlderStagedRecovery_IsSupersededAfterNewerTransferCommits_AndCannotOverwriteNewerAuthenticator()
        {
            ulong steamId = 76561198000000076UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);

            SteamGuardAccount oldStaged = new SteamGuardAccount
            {
                AccountName = "supersede_user",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "R11111",
                TokenGID = "gid-old",
                SerialNumber = "1001",
                DeviceID = "android:old",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId, AccessToken = "old-at", RefreshToken = "old-rt" }
            };

            SteamGuardAccount newerCommitted = new SteamGuardAccount
            {
                AccountName = "supersede_user",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(51, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(71, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "R22222",
                TokenGID = "gid-new",
                SerialNumber = "2002",
                DeviceID = "android:new",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId, AccessToken = "new-at", RefreshToken = "new-rt" }
            };

            string olderAttemptId = "attempt-1";
            string newerAttemptId = "attempt-2";
            string olderStagedPath = PendingTransferMarker.TryStageProtectedReplacement(
                oldStaged,
                manifestEncrypted: false,
                passKey: null,
                out bool stagingFailed,
                transferAttemptId: olderAttemptId,
                previousCommittedTransferAttemptId: null);
            Assert.False(stagingFailed);
            Assert.True(File.Exists(olderStagedPath));

            // Newer transfer commits cleanly with newerAttemptId
            Assert.True(manifest.SaveAccount(newerCommitted, encrypt: false, transferAttemptId: newerAttemptId).Succeeded);

            // Reconcile must see committedAttemptId ("attempt-2") != older baseline (null), delete obsolete staged copy, and offer NOTHING!
            var pending = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(new[] { newerCommitted }, passKey: null, manifest: manifest);
            Assert.Empty(pending);
            Assert.False(File.Exists(olderStagedPath));

            // Even a direct save attempt with enforceTransferAttemptBase must be rejected!
            StorageResult overwriteAttempt = manifest.SaveAccount(
                oldStaged,
                encrypt: false,
                transferAttemptId: olderAttemptId,
                expectedPreviousTransferAttemptId: null,
                enforceTransferAttemptBase: true);
            Assert.False(overwriteAttempt.Succeeded);

            SteamGuardAccount persisted = Assert.Single(Manifest.GetManifest(true).GetAllAccounts());
            Assert.Equal("R22222", persisted.RevocationCode);
        }

        [Fact]
        public void UnresolvedTransferMarker_BlocksAccountOperationsUntilDismissed()
        {
            ulong steamId = 76561198000000077UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = new SteamGuardAccount
            {
                AccountName = "blocked_user",
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:blocked",
                FullyEnrolled = true,
                Session = new SessionData
                {
                    SteamID = steamId,
                    AccessToken = CreateJwtToken(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()),
                    RefreshToken = CreateJwtToken(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds())
                }
            };
            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            Assert.True(PendingTransferMarker.TryWriteMarker(steamId, "blocked_user", "android:blocked", "attempt-unresolved"));

            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                form.RefreshUnresolvedTransferMarkers();
                var isAvailMethod = typeof(MainForm).GetMethod("IsSessionAvailableForAccountOperations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(isAvailMethod);

                // Blocked while unresolved marker exists
                Assert.False((bool)isAvailMethod.Invoke(form, new object[] { account }));

                // Unblocked after marker is explicitly dismissed and refreshed
                PendingTransferMarker.DeleteMarkerBestEffort(steamId);
                form.RefreshUnresolvedTransferMarkers();
                Assert.True((bool)isAvailMethod.Invoke(form, new object[] { account }));
            }
        }

        [Fact]
        public void StagedRecoveryFromAttemptA_IsDiscardedWhenNewerAttemptBMarkerExists_AndMarkerDeleteRequiresMatchingAttemptId()
        {
            ulong steamId = 76561198000000079UL;
            SteamGuardAccount attemptAAccount = new SteamGuardAccount
            {
                AccountName = "dual_attempt",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "RAAAAA",
                DeviceID = "android:a",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId }
            };

            string stagedA = PendingTransferMarker.TryStageProtectedReplacement(
                attemptAAccount,
                manifestEncrypted: false,
                passKey: null,
                out bool stagingFailed,
                transferAttemptId: "attempt-A",
                previousCommittedTransferAttemptId: null);
            Assert.False(stagingFailed);
            Assert.True(File.Exists(stagedA));

            // Newer attempt B leaves an unresolved marker
            Assert.True(PendingTransferMarker.TryWriteMarker(steamId, "dual_attempt", "android:b", "attempt-B"));

            // Deleting marker with attempt-A MUST NOT delete attempt-B's marker!
            PendingTransferMarker.DeleteMarkerBestEffort(steamId, "attempt-A");
            Assert.True(PendingTransferMarker.HasMarker(steamId));

            // Reconciliation MUST keep staged A out of automatic recovery while attempt B's marker is unresolved,
            // WITHOUT deleting staged A on disk (in case B crashed before Steam received Continue),
            // and MUST preserve attempt B's marker!
            var pending = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(Array.Empty<SteamGuardAccount>(), passKey: null);
            Assert.Empty(pending);
            Assert.True(File.Exists(stagedA));
            Assert.True(PendingTransferMarker.HasMarker(steamId));
        }

        [Fact]
        public void ChangedManifestPasskey_SurfacesStagedRecoveryAsRequiresPasskeyInsteadOfDroppingIt()
        {
            ulong steamId = 76561198000000080UL;
            SteamGuardAccount stagedAccount = new SteamGuardAccount
            {
                AccountName = "rekey_user",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "RKEY01",
                DeviceID = "android:key",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId }
            };

            string stagedPath = PendingTransferMarker.TryStageProtectedReplacement(
                stagedAccount,
                manifestEncrypted: true,
                passKey: "old-passkey-A",
                out bool stagingFailed,
                transferAttemptId: "attempt-rekey");
            Assert.False(stagingFailed);

            // Reconciling with a DIFFERENT current manifest passkey B must NOT silently drop the staged copy!
            var discovered = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(Array.Empty<SteamGuardAccount>(), passKey: "new-passkey-B");
            StagedTransferRecoveryRecord record = Assert.Single(discovered);
            Assert.True(record.RequiresPasskey);
            Assert.Equal(stagedPath, record.FilePath);

            // Unlocking with the original passkey A succeeds!
            StagedTransferRecoveryRecord unlocked = PendingTransferMarker.TryLoadStagedRecoveryWithPasskey(record.FilePath, "old-passkey-A");
            Assert.NotNull(unlocked?.Account);
            Assert.Equal("RKEY01", unlocked.Account.RevocationCode);
        }

        [Theory]
        [InlineData(2)]  // Fail
        [InlineData(16)] // Timeout
        [InlineData(20)] // ServiceUnavailable
        [InlineData(50)] // RemoteCallFailed
        public async Task FinishAuthenticatorTransferAsync_AmbiguousNonOkEResult_ReturnsOutcomeUnknownToPreserveMarker(int ambiguousResult)
        {
            var transport = new FakeTransferTransport(
                Response(ambiguousResult, new CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Response()));
            var linker = CreateLinker(transport, 76561198000000081UL);

            Assert.Equal(AuthenticatorLinker.TransferFinishStatus.OutcomeUnknown, await linker.FinishAuthenticatorTransferAsync("12345"));
        }

        [Fact]
        public void PruneOrphanedSavedCredentials_RemovesSavedPasswordForMissingMaFileWhileKeepingActiveAccount()
        {
            const string passKey = "StrongPasskey!Prune1";
            ulong activeId = 76561198000000082UL;
            ulong prunedId = 76561198000000083UL;

            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount activeAcc = new SteamGuardAccount
            {
                AccountName = "active_acct",
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:act",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = activeId }
            };
            SteamGuardAccount prunedAcc = new SteamGuardAccount
            {
                AccountName = "pruned_acct",
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:prn",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = prunedId }
            };
            Assert.True(manifest.SaveAccount(activeAcc, true, passKey).Succeeded);
            Assert.True(manifest.SaveAccount(prunedAcc, true, passKey).Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(activeId, "pw-active", passKey).Succeeded);
            Assert.True(manifest.SaveAccountPassword(prunedId, "pw-pruned", passKey).Succeeded);

            // Simulate missing .maFile for prunedAcc and startup reload
            string prunedFile = manifest.Entries.Single(e => e.SteamID == prunedId).Filename;
            File.Delete(Path.Combine(tempDir, "maFiles", prunedFile));

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Single(reloaded.Entries);
            Assert.True(reloaded.PruneOrphanedSavedCredentials(passKey).Succeeded);

            Assert.True(reloaded.GetSavedCredentialStatus(activeId, passKey).HasSavedPassword);
            Assert.False(reloaded.GetSavedCredentialStatus(prunedId, passKey).HasSavedPassword);
        }

        [Fact]
        public async Task GenerationFence_DrainsInFlightOperation_AndLoginApprovalServicePassesCapturedStorageRevision()
        {
            ulong steamId = 76561198000000088UL;
            var coordinator = new SteamAccountOperationCoordinator();
            var opEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<int> inFlight = coordinator.RunAsync(steamId, async () =>
            {
                opEntered.SetResult(true);
                await releaseOp.Task;
                return 42;
            }, null);
            await opEntered.Task;

            Task<IDisposable> fenceTask = coordinator.AcquireExclusiveLockAsync(steamId);
            Assert.False(fenceTask.IsCompleted);

            releaseOp.SetResult(true);
            Assert.Equal(42, await inFlight);

            using (IDisposable fence = await fenceTask)
            {
                (bool acquired, _) = await coordinator.TryRunNowAsync(steamId, () => Task.FromResult(1), null);
                Assert.False(acquired);
            }

            (bool acquiredAfter, int valAfter) = await coordinator.TryRunNowAsync(steamId, () => Task.FromResult(7), null);
            Assert.True(acquiredAfter);
            Assert.Equal(7, valAfter);

            // Verify LoginApprovalService captures storage revision BEFORE RefreshAccessToken and passes it to persistAccountWithRevision
            long? capturedRevisionArg = null;
            var transport = new RefreshOnlyProtocolTransport();
            var loginService = new LoginApprovalService(
                persistAccount: null,
                protocolTransport: transport,
                getStorageRevision: _ => 99L,
                persistAccountWithRevision: (acc, session, expectedRev) =>
                {
                    capturedRevisionArg = expectedRev;
                    return true;
                });

            var account = new SteamGuardAccount
            {
                SharedSecret = Convert.ToBase64String(new byte[20]),
                Session = new SessionData(transport)
                {
                    SteamID = steamId,
                    AccessToken = CreateJwtToken(DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()),
                    RefreshToken = CreateJwtToken(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds())
                }
            };

            LoginApprovalFetchResult fetchResult = await loginService.FetchPendingRequestsAsync(account);
            Assert.Equal(LoginApprovalErrorKind.None, fetchResult.ErrorKind);
            Assert.Equal(99L, capturedRevisionArg);
        }

        private static AuthenticatorLinker CreateLinker(IAuthenticatorProtocolTransport transport, ulong steamId)
        {
            return new AuthenticatorLinker(new SessionData
            {
                SteamID = steamId,
                AccessToken = "access-token",
                RefreshToken = "refresh-token"
            }, transport);
        }

        private static ScriptedProtocolResponse Response(int result, IMessage body)
        {
            return new ScriptedProtocolResponse(result, body, null);
        }

        private sealed class ScriptedProtocolResponse
        {
            public int Result { get; }
            public IMessage Body { get; }
            public string ErrorMessage { get; }

            public ScriptedProtocolResponse(int result, IMessage body, string errorMessage)
            {
                Result = result;
                Body = body;
                ErrorMessage = errorMessage;
            }
        }

        private static CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Response BuildValidReplacementResponse(ulong steamId, string accountName)
        {
            return new CTwoFactor_RemoveAuthenticatorViaChallengeContinue_Response
            {
                Success = true,
                ReplacementToken = new CRemoveAuthenticatorViaChallengeContinue_Replacement_Token
                {
                    SharedSecret = ByteString.CopyFrom(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                    IdentitySecret = ByteString.CopyFrom(Enumerable.Range(31, 20).Select(i => (byte)i).ToArray()),
                    SerialNumber = 1234567890UL,
                    RevocationCode = "R99887",
                    Uri = "otpauth://totp/Steam:" + accountName,
                    ServerTime = 1700000100UL,
                    AccountName = accountName,
                    TokenGid = "gid-abc",
                    Status = 1,
                    Steamid = steamId
                }
            };
        }

        private static string CreateJwtToken(long expUnix)
        {
            string header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":" + expUnix + "}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return header + "." + payload + ".sig";
        }

        private sealed class FakeTransferTransport : IAuthenticatorProtocolTransport
        {
            private readonly Queue<object> script;
            public List<string> Methods { get; } = new List<string>();
            public List<IMessage> Requests { get; } = new List<IMessage>();

            public FakeTransferTransport(params object[] scriptedOutcomes)
            {
                script = new Queue<object>(scriptedOutcomes);
            }

            public Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
                string serviceName,
                string methodName,
                TRequest request,
                string accessToken,
                MessageParser<TResponse> responseParser,
                SteamProtocolRequestMethod httpMethod = SteamProtocolRequestMethod.Post,
                CancellationToken cancellationToken = default)
                where TRequest : class, IMessage<TRequest>
                where TResponse : class, IMessage<TResponse>
            {
                Methods.Add(methodName);
                Requests.Add(request);
                object next = script.Dequeue();
                if (next is Exception ex)
                    throw ex;

                ScriptedProtocolResponse raw = (ScriptedProtocolResponse)next;
                return Task.FromResult(new SteamProtocolResponse<TResponse>
                {
                    Result = raw.Result,
                    Body = (TResponse)raw.Body,
                    ErrorMessage = raw.ErrorMessage
                });
            }
        }

        private sealed class FakeTransferInteraction : IAuthenticatorTransferInteraction
        {
            private readonly List<string> events;
            private readonly Queue<string> smsCodes;
            private readonly bool persistSucceeds;

            public bool HasMarker { get; private set; }

            public FakeTransferInteraction(List<string> events, string[] smsCodes, bool persistSucceeds)
            {
                this.events = events;
                this.smsCodes = new Queue<string>(smsCodes);
                this.persistSucceeds = persistSucceeds;
            }

            public bool HasUnresolvedPendingTransfer(ulong steamId) => HasMarker;

            public bool WritePreDispatchMarker(ulong steamId, string accountName, string deviceId, string transferAttemptId)
            {
                HasMarker = true;
                events.Add("marker_write");
                return true;
            }

            public void ClearPreDispatchMarker(ulong steamId, string expectedTransferAttemptId)
            {
                HasMarker = false;
                events.Add("marker_clear");
            }

            public IDisposable EnterIrreversibleContinueSection(out CancellationToken continueCancellationToken)
            {
                continueCancellationToken = CancellationToken.None;
                return new NoopDisposable();
            }

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose() { }
            }

            public string RequestTransferSmsCode(bool previousCodeWasInvalid)
            {
                return smsCodes.Count > 0 ? smsCodes.Dequeue() : null;
            }

            public string TryStageProtectedReplacement(SteamGuardAccount replacement, string transferAttemptId, out bool stagingFailed)
            {
                stagingFailed = false;
                events.Add("stage");
                return "staged-path";
            }

            public void CleanupStagedReplacement(string stagedRecoveryPath)
            {
                events.Add("cleanup_stage");
            }

            public bool PersistReplacementAccount(SteamGuardAccount replacement, string transferAttemptId, out string failureMessage)
            {
                events.Add("persist");
                failureMessage = persistSucceeds ? null : "Disk full";
                return persistSucceeds;
            }

            public void PersistEnrichedDeviceId(SteamGuardAccount replacement, string transferAttemptId)
            {
                events.Add("enrich");
            }
        }

        private sealed class RefreshOnlyProtocolTransport : IAuthenticatorProtocolTransport
        {
            public Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
                string serviceName,
                string methodName,
                TRequest request,
                string accessToken,
                MessageParser<TResponse> responseParser,
                SteamProtocolRequestMethod httpMethod = SteamProtocolRequestMethod.Post,
                CancellationToken cancellationToken = default)
                where TRequest : class, IMessage<TRequest>
                where TResponse : class, IMessage<TResponse>
            {
                if (methodName == "GenerateAccessTokenForApp")
                {
                    var resp = new CAuthentication_AccessToken_GenerateForApp_Response
                    {
                        AccessToken = CreateJwtToken(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds())
                    };
                    return Task.FromResult(new SteamProtocolResponse<TResponse> { Result = 1, Body = (TResponse)(IMessage)resp });
                }

                if (methodName == "GetAuthSessionsForAccount")
                {
                    var resp = new CAuthentication_GetAuthSessionsForAccount_Response();
                    return Task.FromResult(new SteamProtocolResponse<TResponse> { Result = 1, Body = (TResponse)(IMessage)resp });
                }

                throw new InvalidOperationException("Unexpected method: " + methodName);
            }
        }

        [Fact]
        public void DeleteMarkerBestEffort_FailsClosedOnCorruptMarkerWhenAttemptScoped_ButDeletesWhenUnscoped()
        {
            ulong steamId = 76561198000000088UL;
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            Directory.CreateDirectory(maDir);
            string markerPath = PendingTransferMarker.GetMarkerPath(steamId);
            File.WriteAllText(markerPath, "{ corrupt json without attempt id");

            // Attempt-scoped cleanup MUST NOT delete a corrupt/unreadable marker
            PendingTransferMarker.DeleteMarkerBestEffort(steamId, "attempt-old");
            Assert.True(File.Exists(markerPath));
            Assert.True(PendingTransferMarker.HasMarker(steamId));

            // Unscoped user dismissal MUST delete the corrupt marker
            PendingTransferMarker.DeleteMarkerBestEffort(steamId);
            Assert.False(File.Exists(markerPath));
            Assert.False(PendingTransferMarker.HasMarker(steamId));
        }

        [Fact]
        public async Task TransferFenceDispose_HandsOffToUnresolvedMarkerSetAtomicallyBeforeUnlocking()
        {
            ulong steamId = 76561198000000089UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                var beginFenceMethod = typeof(MainForm).GetMethod(
                    "BeginTransferFenceAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var isBusyMethod = typeof(MainForm).GetMethod(
                    "IsAccountBusyWithRecoveryOrTransfer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var unresolvedField = typeof(MainForm).GetField(
                    "unresolvedTransferMarkerSteamIds",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

                Assert.NotNull(beginFenceMethod);
                Assert.NotNull(isBusyMethod);
                Assert.NotNull(unresolvedField);

                var unresolvedDict = (System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>)unresolvedField.GetValue(form);
                Assert.False(unresolvedDict.ContainsKey(steamId));

                Task<IDisposable> fenceTask = (Task<IDisposable>)beginFenceMethod.Invoke(form, new object[] { steamId, CancellationToken.None });
                IDisposable fence = await fenceTask;
                Assert.NotNull(fence);
                Assert.True((bool)isBusyMethod.Invoke(form, new object[] { steamId }));

                // Simulate OutcomeUnknown leaving a pending marker on disk before fence disposal
                Assert.True(PendingTransferMarker.TryWriteMarker(steamId, "fence_user", "android:fence", "attempt-unknown"));

                // Disposing the fence MUST populate unresolvedTransferMarkerSteamIds immediately
                // even before LoginForm closes or StartAuthenticatorTransferFlow calls RefreshUnresolvedTransferMarkers()
                fence.Dispose();
                Assert.True(unresolvedDict.ContainsKey(steamId));
                Assert.True((bool)isBusyMethod.Invoke(form, new object[] { steamId }));

                // Clean up marker and re-verify that a subsequent fence dispose clears unresolvedDict when no marker exists
                PendingTransferMarker.DeleteMarkerBestEffort(steamId, "attempt-unknown");
                IDisposable secondFence = await (Task<IDisposable>)beginFenceMethod.Invoke(form, new object[] { steamId, CancellationToken.None });
                secondFence.Dispose();
                Assert.False(unresolvedDict.ContainsKey(steamId));
                Assert.False((bool)isBusyMethod.Invoke(form, new object[] { steamId }));
            }
        }

        [Fact]
        public void CommittedTransferWithNoStagedFile_ReconcileClearsMatchingMarkerDirectly_AndPreservesMismatchedMarker()
        {
            ulong committedSteamId = 76561198000000091UL;
            ulong pendingSteamId = 76561198000000092UL;

            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount committedAccount = new SteamGuardAccount
            {
                AccountName = "committed_no_stage",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                DeviceID = "android:committed",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = committedSteamId, AccessToken = "at1", RefreshToken = "rt1" }
            };
            SteamGuardAccount otherAccount = new SteamGuardAccount
            {
                AccountName = "pending_new_attempt",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(31, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(51, 20).Select(i => (byte)i).ToArray()),
                DeviceID = "android:other",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = pendingSteamId, AccessToken = "at2", RefreshToken = "rt2" }
            };

            Assert.True(manifest.SaveAccount(committedAccount, encrypt: false, transferAttemptId: "attempt-committed-1").Succeeded);
            Assert.True(manifest.SaveAccount(otherAccount, encrypt: false, transferAttemptId: "attempt-older-1").Succeeded);

            // Simulate leftover markers with NO staged files:
            // - committedSteamId has an exact-match committed marker -> MUST be cleared on startup reconcile
            // - pendingSteamId has a NEWER uncommitted marker -> MUST remain fenced!
            Assert.True(PendingTransferMarker.TryWriteMarker(committedSteamId, "committed_no_stage", "android:committed", "attempt-committed-1"));
            Assert.True(PendingTransferMarker.TryWriteMarker(pendingSteamId, "pending_new_attempt", "android:other", "attempt-newer-2"));

            var recoveries = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(
                new[] { committedAccount, otherAccount },
                passKey: null,
                manifest: manifest);
            Assert.Empty(recoveries);
            Assert.False(PendingTransferMarker.HasMarker(committedSteamId));
            Assert.True(PendingTransferMarker.HasMarker(pendingSteamId));
        }

        [Fact]
        public void LoginForm_AndMainForm_CancelApplicationExitWhileIrreversibleTransferContinueInFlight()
        {
            bool callbackSeenTrue = false;
            bool callbackSeenFalse = false;
            using (LoginForm loginForm = new LoginForm(
                LoginForm.LoginType.Transfer,
                onIrreversibleTransferStateChanged: inFlight =>
                {
                    if (inFlight) callbackSeenTrue = true;
                    else callbackSeenFalse = true;
                }))
            {
                var onFormClosing = typeof(LoginForm).GetMethod(
                    "OnFormClosing",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(onFormClosing);

                loginForm.SetIrreversibleTransferInFlight(true);
                Assert.True(callbackSeenTrue);

                var appExitArgs = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.ApplicationExitCall, false);
                onFormClosing.Invoke(loginForm, new object[] { appExitArgs });
                Assert.True(appExitArgs.Cancel);

                var ownerCloseArgs = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.FormOwnerClosing, false);
                onFormClosing.Invoke(loginForm, new object[] { ownerCloseArgs });
                Assert.True(ownerCloseArgs.Cancel);

                var shutdownArgs = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.WindowsShutDown, false);
                onFormClosing.Invoke(loginForm, new object[] { shutdownArgs });
                Assert.False(shutdownArgs.Cancel);

                loginForm.SetIrreversibleTransferInFlight(false);
                Assert.True(callbackSeenFalse);
            }

            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm mainForm = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                var countField = typeof(MainForm).GetField("activeIrreversibleTransferCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var deferredField = typeof(MainForm).GetField("exitAfterTransferRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var closingMethod = typeof(MainForm).GetMethod("MainForm_FormClosing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(countField);
                Assert.NotNull(deferredField);
                Assert.NotNull(closingMethod);

                countField.SetValue(mainForm, 1);
                var mainExitArgs = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.ApplicationExitCall, false);
                closingMethod.Invoke(mainForm, new object[] { mainForm, mainExitArgs });
                Assert.True(mainExitArgs.Cancel);
                Assert.True((bool)deferredField.GetValue(mainForm));

                countField.SetValue(mainForm, 0);
            }
        }

        [Fact]
        public void IsTransferFenceOrMarkerActive_BlocksManualRenewalWhenUnresolvedMarkerExistsOnDisk()
        {
            ulong steamId = 76561198000000093UL;
            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                Assert.False(form.IsTransferFenceOrMarkerActive(steamId));

                Assert.True(PendingTransferMarker.TryWriteMarker(steamId, "manual_refresh_blocked", "android:m", "attempt-m1"));
                Assert.True(form.IsTransferFenceOrMarkerActive(steamId));

                PendingTransferMarker.DeleteMarkerBestEffort(steamId, "attempt-m1");
                Assert.False(form.IsTransferFenceOrMarkerActive(steamId));
            }
        }

        [Fact]
        public void CorruptPendingMarker_SuppressesOlderStagedRecoveryWithoutDeletingStagedFile()
        {
            ulong steamId = 76561198000000094UL;
            SteamGuardAccount olderStaged = new SteamGuardAccount
            {
                AccountName = "corrupt_marker_user",
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                RevocationCode = "RCM001",
                DeviceID = "android:cm",
                FullyEnrolled = true,
                Session = new SessionData { SteamID = steamId }
            };

            string stagedPath = PendingTransferMarker.TryStageProtectedReplacement(
                olderStaged,
                manifestEncrypted: false,
                passKey: null,
                out bool stagingFailed,
                transferAttemptId: "attempt-old-cm");
            Assert.False(stagingFailed);
            Assert.True(File.Exists(stagedPath));

            // Write an unreadable/corrupt marker (no valid TransferAttemptId)
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            Directory.CreateDirectory(maDir);
            string markerPath = PendingTransferMarker.GetMarkerPath(steamId);
            File.WriteAllText(markerPath, "{ corrupt unreadable marker");

            // Fail closed: MUST NOT offer olderStaged while corrupt marker is unresolved,
            // AND MUST preserve stagedPath and markerPath on disk.
            var pending = PendingTransferMarker.ReconcileAndLoadStagedRecoveries(Array.Empty<SteamGuardAccount>(), passKey: null);
            Assert.Empty(pending);
            Assert.True(File.Exists(stagedPath));
            Assert.True(File.Exists(markerPath));
        }

        [Fact]
        public void LoginForm_ShouldCloseForDeferredExit_ReturnsTrueAfterDeferredApplicationOrTrayExit()
        {
            bool ownerDeferredExit = false;
            using (LoginForm loginForm = new LoginForm(
                LoginForm.LoginType.Transfer,
                isDeferredExitRequested: () => ownerDeferredExit))
            {
                Assert.False(loginForm.ShouldCloseForDeferredExit());

                // Owner tray/menu exit deferred during Continue
                ownerDeferredExit = true;
                Assert.True(loginForm.ShouldCloseForDeferredExit());
                ownerDeferredExit = false;
                Assert.False(loginForm.ShouldCloseForDeferredExit());

                // ApplicationExitCall canceled during Continue also marks deferred close directly on LoginForm
                var onFormClosing = typeof(LoginForm).GetMethod(
                    "OnFormClosing",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                loginForm.SetIrreversibleTransferInFlight(true);
                var appExitArgs = new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.ApplicationExitCall, false);
                onFormClosing.Invoke(loginForm, new object[] { appExitArgs });
                Assert.True(appExitArgs.Cancel);
                Assert.True(loginForm.ShouldCloseForDeferredExit());
                loginForm.SetIrreversibleTransferInFlight(false);
            }
        }
    }
}
