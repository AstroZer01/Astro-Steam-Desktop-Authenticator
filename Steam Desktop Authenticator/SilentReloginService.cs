using SteamAuth;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Steam_Desktop_Authenticator
{
    public enum SilentReloginOutcomeKind
    {
        NotEligible,
        AlreadyInProgress,
        Succeeded,
        InvalidPassword,
        RateLimited,
        TransientFailure,
        GuardRejected,
        InteractiveRequired,
        AccountMismatch,
        PersistenceRejected,
        Canceled
    }

    public sealed class SilentReloginTransportResult
    {
        public SilentReloginOutcomeKind Kind { get; set; }
        public SessionData Session { get; set; }
        public EResult? SteamResult { get; set; }
    }

    public sealed class SilentReloginResult
    {
        public SilentReloginOutcomeKind Kind { get; set; }
        public ulong SteamId { get; set; }
        public SessionData Session { get; set; }
        public bool RejectionPersistFailed { get; set; }
        public bool ObsoleteCleanupPending { get; set; }
    }

    public interface ISteamCredentialLoginTransport
    {
        Task<SilentReloginTransportResult> LoginAsync(
            SteamGuardAccount account,
            string password,
            CancellationToken cancellationToken);
    }

    internal sealed class SilentReloginAbortException : Exception
    {
        public SilentReloginOutcomeKind OutcomeKind { get; }

        public SilentReloginAbortException(SilentReloginOutcomeKind outcomeKind, string message)
            : base(message)
        {
            OutcomeKind = outcomeKind;
        }
    }

    internal sealed class HeadlessGuardAuthenticator : IAuthenticator
    {
        private readonly SteamGuardAccount account;
        private readonly Func<CancellationToken, Task<long>> getSteamTimeAsync;
        private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
        private readonly CancellationToken cancellationToken;
        private long? lastSubmittedChunk;
        private bool retriedAfterMismatch;

        public HeadlessGuardAuthenticator(
            SteamGuardAccount account,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task<long>> getSteamTimeAsync = null,
            Func<TimeSpan, CancellationToken, Task> delayAsync = null)
        {
            this.account = account ?? throw new ArgumentNullException(nameof(account));
            this.cancellationToken = cancellationToken;
            this.getSteamTimeAsync = getSteamTimeAsync ?? TimeAligner.GetSteamTimeAsync;
            this.delayAsync = delayAsync ?? Task.Delay;
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            return Task.FromResult(false);
        }

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(account.SharedSecret))
                throw new SilentReloginAbortException(SilentReloginOutcomeKind.InteractiveRequired, "Missing shared secret for silent login.");

            if (previousCodeWasIncorrect)
            {
                if (retriedAfterMismatch)
                    throw new SilentReloginAbortException(SilentReloginOutcomeKind.GuardRejected, "Steam rejected consecutive generated Steam Guard codes.");

                retriedAfterMismatch = true;
            }

            long steamTime = await getSteamTimeAsync(cancellationToken);
            long chunk = steamTime / 30L;

            // Waiting for the next 30-second TOTP window boundary does NOT consume
            // the single previousCodeWasIncorrect retry budget.
            if (lastSubmittedChunk.HasValue && chunk <= lastSubmittedChunk.Value)
            {
                long nextBoundarySeconds = (lastSubmittedChunk.Value + 1L) * 30L;
                long waitSeconds = Math.Max(1L, Math.Min(35L, nextBoundarySeconds - steamTime + 1L));
                await delayAsync(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                steamTime = await getSteamTimeAsync(cancellationToken);
                chunk = steamTime / 30L;
                if (chunk <= lastSubmittedChunk.Value)
                {
                    // Guard against backwards clock jumps by stepping forward once.
                    chunk = lastSubmittedChunk.Value + 1L;
                    steamTime = chunk * 30L;
                }
            }

            string code = account.GenerateSteamGuardCodeForTime(steamTime);
            if (String.IsNullOrWhiteSpace(code))
                throw new SilentReloginAbortException(SilentReloginOutcomeKind.InteractiveRequired, "Could not generate a Steam Guard code.");

            lastSubmittedChunk = chunk;
            return code;
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            throw new SilentReloginAbortException(SilentReloginOutcomeKind.InteractiveRequired, "Steam requested an email verification code.");
        }
    }

    internal sealed class SteamKitCredentialLoginTransport : ISteamCredentialLoginTransport
    {
        private static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(20);

        public async Task<SilentReloginTransportResult> LoginAsync(
            SteamGuardAccount account,
            string password,
            CancellationToken cancellationToken)
        {
            if (account == null || String.IsNullOrWhiteSpace(account.AccountName) || String.IsNullOrEmpty(password))
                return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.InteractiveRequired };

            using (CancellationTokenSource deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadlineCts.CancelAfter(OverallDeadline);
                CancellationToken linkedToken = deadlineCts.Token;

                SteamClient steamClient;
                try
                {
                    steamClient = new SteamClient(ProxyService.CreateSteamKitConfiguration());
                }
                catch (InvalidOperationException)
                {
                    return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.TransientFailure };
                }

                using (linkedToken.Register(() =>
                {
                    try { steamClient.Disconnect(); } catch { }
                }))
                {
                    try
                    {
                        steamClient.Connect();
                        DateTime connectDeadline = DateTime.UtcNow.Add(ConnectionTimeout);
                        while (!steamClient.IsConnected)
                        {
                            linkedToken.ThrowIfCancellationRequested();
                            if (DateTime.UtcNow >= connectDeadline)
                                return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.TransientFailure };

                            await Task.Delay(250, linkedToken);
                        }

                        HeadlessGuardAuthenticator authenticator = new HeadlessGuardAuthenticator(account, linkedToken);
                        CredentialsAuthSession authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                        {
                            Username = account.AccountName,
                            Password = password,
                            DeviceFriendlyName = "AstroSDA",
                            IsPersistentSession = true,
                            PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                            ClientOSType = EOSType.Android9,
                            Authenticator = authenticator
                        });

                        linkedToken.ThrowIfCancellationRequested();
                        AuthPollResult pollResponse = await authSession.PollingWaitForResultAsync(linkedToken);
                        linkedToken.ThrowIfCancellationRequested();

                        if (pollResponse == null || String.IsNullOrWhiteSpace(pollResponse.AccessToken) || String.IsNullOrWhiteSpace(pollResponse.RefreshToken))
                            return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.TransientFailure };

                        return new SilentReloginTransportResult
                        {
                            Kind = SilentReloginOutcomeKind.Succeeded,
                            Session = new SessionData
                            {
                                SteamID = authSession.SteamID.ConvertToUInt64(),
                                AccessToken = pollResponse.AccessToken,
                                RefreshToken = pollResponse.RefreshToken
                            }
                        };
                    }
                    catch (SilentReloginAbortException ex)
                    {
                        return new SilentReloginTransportResult { Kind = ex.OutcomeKind };
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.Canceled };
                    }
                    catch (OperationCanceledException)
                    {
                        return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.TransientFailure };
                    }
                    catch (AuthenticationException ex)
                    {
                        return ClassifyAuthenticationException(ex);
                    }
                    catch (Exception)
                    {
                        return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.TransientFailure };
                    }
                    finally
                    {
                        try { steamClient.Disconnect(); } catch { }
                    }
                }
            }
        }

        private static SilentReloginTransportResult ClassifyAuthenticationException(AuthenticationException ex)
        {
            switch (ex.Result)
            {
                case EResult.InvalidPassword:
                    return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.InvalidPassword, SteamResult = ex.Result };

                case EResult.RateLimitExceeded:
                case EResult.AccountLoginDeniedThrottle:
                    return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.RateLimited, SteamResult = ex.Result };

                case EResult.TwoFactorCodeMismatch:
                case EResult.InvalidLoginAuthCode:
                    return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.GuardRejected, SteamResult = ex.Result };

                case EResult.AccountLogonDenied:
                case EResult.AccessDenied:
                case EResult.AccountDisabled:
                case EResult.AccountLockedDown:
                case EResult.AccountLoginDeniedNeedTwoFactor:
                    return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.InteractiveRequired, SteamResult = ex.Result };

                default:
                    return new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.TransientFailure, SteamResult = ex.Result };
            }
        }
    }

    public sealed class SilentReloginService
    {
        public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan TransientFailureCooldown = TimeSpan.FromMinutes(5);

        private readonly ISteamCredentialLoginTransport transport;
        private readonly Func<DateTime> utcNowProvider;
        private readonly ConcurrentDictionary<ulong, byte> activeRecoveries = new ConcurrentDictionary<ulong, byte>();
        private readonly ConcurrentDictionary<ulong, string> inMemoryRejectedGenerations = new ConcurrentDictionary<ulong, string>();
        private readonly ConcurrentDictionary<ulong, string> blockedGenerations = new ConcurrentDictionary<ulong, string>();
        private readonly ConcurrentDictionary<ulong, DateTime> cooldownUntilUtc = new ConcurrentDictionary<ulong, DateTime>();

        public SilentReloginService(
            ISteamCredentialLoginTransport transport = null,
            Func<DateTime> utcNowProvider = null)
        {
            this.transport = transport ?? new SteamKitCredentialLoginTransport();
            this.utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
        }

        public bool IsRecoveryInProgress(ulong steamId)
        {
            return steamId != 0 && activeRecoveries.ContainsKey(steamId);
        }

        public bool IsGenerationRejectedInMemory(ulong steamId, string generationId)
        {
            return steamId != 0 &&
                !String.IsNullOrEmpty(generationId) &&
                inMemoryRejectedGenerations.TryGetValue(steamId, out string rejectedGen) &&
                String.Equals(rejectedGen, generationId, StringComparison.Ordinal);
        }

        public void ClearAccountRuntimeState(ulong steamId)
        {
            if (steamId == 0)
                return;
            inMemoryRejectedGenerations.TryRemove(steamId, out _);
            blockedGenerations.TryRemove(steamId, out _);
            cooldownUntilUtc.TryRemove(steamId, out _);
        }

        public void ClearTransientCooldown(ulong steamId)
        {
            if (steamId == 0)
                return;
            cooldownUntilUtc.TryRemove(steamId, out _);
        }

        public bool CanAttemptRecovery(Manifest manifest, ulong steamId, string passKey)
        {
            if (manifest == null || steamId == 0 || String.IsNullOrEmpty(passKey))
                return false;
            if (!manifest.Encrypted || !manifest.AllowSavedSteamPasswords)
                return false;
            if (PendingTransferMarker.HasMarker(steamId))
                return false;
            if (IsRecoveryInProgress(steamId))
                return false;

            DateTime now = utcNowProvider();
            if (cooldownUntilUtc.TryGetValue(steamId, out DateTime cooldownEnd) && now < cooldownEnd)
                return false;

            SavedCredentialStatus status = manifest.GetSavedCredentialStatus(steamId, passKey);
            if (!status.HasSavedPassword || String.IsNullOrEmpty(status.CredentialGenerationId))
                return false;
            if (status.PasswordRejected)
                return false;
            if (IsGenerationRejectedInMemory(steamId, status.CredentialGenerationId))
                return false;
            if (blockedGenerations.TryGetValue(steamId, out string blockedGen) &&
                String.Equals(blockedGen, status.CredentialGenerationId, StringComparison.Ordinal))
                return false;

            return true;
        }

        public async Task<SilentReloginResult> TryRecoverAccountSessionAsync(
            Manifest manifest,
            SteamGuardAccount account,
            string passKey,
            CancellationToken cancellationToken)
        {
            ulong steamId = account?.Session?.SteamID ?? 0;
            if (manifest == null || account == null || !account.FullyEnrolled || steamId == 0 || String.IsNullOrEmpty(passKey))
                return new SilentReloginResult { Kind = SilentReloginOutcomeKind.NotEligible, SteamId = steamId };

            if (!manifest.Encrypted || !manifest.AllowSavedSteamPasswords)
                return new SilentReloginResult { Kind = SilentReloginOutcomeKind.NotEligible, SteamId = steamId };
            if (PendingTransferMarker.HasMarker(steamId))
                return new SilentReloginResult { Kind = SilentReloginOutcomeKind.NotEligible, SteamId = steamId };

            if (!activeRecoveries.TryAdd(steamId, 0))
                return new SilentReloginResult { Kind = SilentReloginOutcomeKind.AlreadyInProgress, SteamId = steamId };

            try
            {
                DateTime now = utcNowProvider();
                if (cooldownUntilUtc.TryGetValue(steamId, out DateTime cooldownEnd) && now < cooldownEnd)
                    return new SilentReloginResult { Kind = SilentReloginOutcomeKind.NotEligible, SteamId = steamId };

                SavedCredentialRecord record = manifest.GetSavedCredentialRecord(steamId, passKey);
                if (record == null || String.IsNullOrEmpty(record.Password) || String.IsNullOrEmpty(record.CredentialGenerationId))
                    return new SilentReloginResult { Kind = SilentReloginOutcomeKind.NotEligible, SteamId = steamId };
                if (record.PasswordRejected || IsGenerationRejectedInMemory(steamId, record.CredentialGenerationId))
                    return new SilentReloginResult { Kind = SilentReloginOutcomeKind.InvalidPassword, SteamId = steamId };
                if (blockedGenerations.TryGetValue(steamId, out string blockedGen) &&
                    String.Equals(blockedGen, record.CredentialGenerationId, StringComparison.Ordinal))
                    return new SilentReloginResult { Kind = SilentReloginOutcomeKind.NotEligible, SteamId = steamId };

                string capturedGenerationId = record.CredentialGenerationId;
                string capturedPassword = record.Password;
                long capturedAccountRevision = manifest.GetAccountStorageRevision(steamId);

                SilentReloginTransportResult transportResult = await transport.LoginAsync(account, capturedPassword, cancellationToken);
                if (transportResult == null)
                    transportResult = new SilentReloginTransportResult { Kind = SilentReloginOutcomeKind.TransientFailure };

                switch (transportResult.Kind)
                {
                    case SilentReloginOutcomeKind.Succeeded:
                        if (transportResult.Session == null || transportResult.Session.SteamID != steamId)
                        {
                            blockedGenerations[steamId] = capturedGenerationId;
                            DiagnosticErrorLogger.Log("Silent re-login", new InvalidOperationException("Authenticated SteamID did not match target account."), "Silent re-login returned a mismatched Steam identity.");
                            return new SilentReloginResult { Kind = SilentReloginOutcomeKind.AccountMismatch, SteamId = steamId };
                        }

                        SteamGuardAccount candidate = CloneAccountWithSession(account, transportResult.Session);
                        StorageResult saveResult = manifest.SaveAccount(
                            candidate,
                            encrypt: true,
                            passKey: passKey,
                            expectedStorageRevision: capturedAccountRevision,
                            allowCreate: false,
                            requireAllowSavedPasswords: true,
                            expectedCredentialGenerationId: capturedGenerationId,
                            credentialPassKey: passKey);

                        if (!saveResult.Succeeded)
                        {
                            string currentGenerationId = manifest.GetSavedCredentialStatus(steamId, passKey).CredentialGenerationId;
                            if (String.IsNullOrEmpty(currentGenerationId) ||
                                String.Equals(currentGenerationId, capturedGenerationId, StringComparison.Ordinal))
                            {
                                cooldownUntilUtc[steamId] = utcNowProvider().Add(TransientFailureCooldown);
                            }
                            DiagnosticErrorLogger.Log("Silent re-login", saveResult.Exception, "Silent re-login session could not be committed atomically.");
                            return new SilentReloginResult { Kind = SilentReloginOutcomeKind.PersistenceRejected, SteamId = steamId };
                        }

                        account.Session = transportResult.Session;
                        cooldownUntilUtc.TryRemove(steamId, out _);
                        return new SilentReloginResult
                        {
                            Kind = SilentReloginOutcomeKind.Succeeded,
                            SteamId = steamId,
                            Session = transportResult.Session,
                            ObsoleteCleanupPending = saveResult.ObsoleteCleanupPending
                        };

                    case SilentReloginOutcomeKind.InvalidPassword:
                        // Unconditional in-process zero-retry even if disk commit fails.
                        inMemoryRejectedGenerations[steamId] = capturedGenerationId;
                        StorageResult rejectResult = manifest.MarkSavedPasswordRejected(passKey, steamId, capturedGenerationId, utcNowProvider());
                        bool persistFailed = !rejectResult.Succeeded;
                        if (persistFailed)
                        {
                            DiagnosticErrorLogger.Log("Silent re-login", new InvalidOperationException("Failed to persist saved-password rejection flag."), "Silent re-login rejected password state could not be saved to disk; in-memory lockout remains active.");
                        }
                        else
                        {
                            DiagnosticErrorLogger.Log("Silent re-login", new InvalidOperationException("Steam rejected the saved password."), "Silent re-login paused after invalid password rejection.");
                        }
                        return new SilentReloginResult
                        {
                            Kind = SilentReloginOutcomeKind.InvalidPassword,
                            SteamId = steamId,
                            RejectionPersistFailed = persistFailed,
                            ObsoleteCleanupPending = rejectResult.ObsoleteCleanupPending
                        };

                    case SilentReloginOutcomeKind.RateLimited:
                        cooldownUntilUtc[steamId] = utcNowProvider().Add(RateLimitCooldown);
                        DiagnosticErrorLogger.Log("Silent re-login", new InvalidOperationException("Steam rate limited silent re-login."), "Silent re-login backed off due to Steam rate limiting.");
                        return new SilentReloginResult { Kind = SilentReloginOutcomeKind.RateLimited, SteamId = steamId };

                    case SilentReloginOutcomeKind.GuardRejected:
                    case SilentReloginOutcomeKind.InteractiveRequired:
                        blockedGenerations[steamId] = capturedGenerationId;
                        DiagnosticErrorLogger.Log("Silent re-login", new InvalidOperationException("Silent re-login requires interactive login."), "Silent re-login could not complete headlessly; interactive login required.");
                        return new SilentReloginResult { Kind = transportResult.Kind, SteamId = steamId };

                    case SilentReloginOutcomeKind.Canceled:
                        return new SilentReloginResult { Kind = SilentReloginOutcomeKind.Canceled, SteamId = steamId };

                    default:
                        cooldownUntilUtc[steamId] = utcNowProvider().Add(TransientFailureCooldown);
                        DiagnosticErrorLogger.Log("Silent re-login", new InvalidOperationException("Silent re-login failed transiently."), "Silent re-login encountered a temporary error and will retry later.");
                        return new SilentReloginResult { Kind = SilentReloginOutcomeKind.TransientFailure, SteamId = steamId };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SilentReloginResult { Kind = SilentReloginOutcomeKind.Canceled, SteamId = steamId };
            }
            catch (Exception)
            {
                cooldownUntilUtc[steamId] = utcNowProvider().Add(TransientFailureCooldown);
                DiagnosticErrorLogger.Log("Silent re-login", new InvalidOperationException("Silent re-login failed unexpectedly."), "Silent re-login encountered an unexpected error.");
                return new SilentReloginResult { Kind = SilentReloginOutcomeKind.TransientFailure, SteamId = steamId };
            }
            finally
            {
                activeRecoveries.TryRemove(steamId, out _);
            }
        }

        private static SteamGuardAccount CloneAccountWithSession(SteamGuardAccount source, SessionData newSession)
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(source);
            SteamGuardAccount clone = Newtonsoft.Json.JsonConvert.DeserializeObject<SteamGuardAccount>(json);
            clone.Session = newSession;
            clone.FullyEnrolled = source.FullyEnrolled;
            return clone;
        }
    }
}
