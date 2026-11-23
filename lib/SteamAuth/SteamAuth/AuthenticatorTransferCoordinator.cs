using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamAuth
{
    public enum TransferCoordinatorResultKind
    {
        Succeeded,
        Canceled,
        PhoneNumberRequired,
        BlockedByPendingTransfer,
        RateLimited,
        Failed,
        OutcomeUnknown,
        IncompleteReplacement,
        ReplacementReceivedSavePending
    }

    public sealed class TransferCoordinatorOutcome
    {
        public TransferCoordinatorResultKind Kind { get; set; }
        public SteamGuardAccount LinkedAccount { get; set; }
        public string ErrorMessage { get; set; }
        public string StagedRecoveryPath { get; set; }
        public bool StagingFailed { get; set; }
    }

    public interface IAuthenticatorTransferInteraction
    {
        bool HasUnresolvedPendingTransfer(ulong steamId);
        bool WritePreDispatchMarker(ulong steamId, string accountName, string deviceId);
        void ClearPreDispatchMarker(ulong steamId);
        string RequestTransferSmsCode(bool previousCodeWasInvalid);
        string TryStageProtectedReplacement(SteamGuardAccount replacement, out bool stagingFailed);
        void CleanupStagedReplacement(string stagedRecoveryPath);
        bool PersistReplacementAccount(SteamGuardAccount replacement, out string failureMessage);
        void PersistEnrichedDeviceId(SteamGuardAccount replacement);
    }

    public sealed class AuthenticatorTransferCoordinator
    {
        private readonly AuthenticatorLinker linker;
        private readonly IAuthenticatorTransferInteraction interaction;
        private readonly ulong steamId;
        private readonly string accountName;

        public AuthenticatorTransferCoordinator(
            AuthenticatorLinker linker,
            IAuthenticatorTransferInteraction interaction,
            ulong steamId,
            string accountName)
        {
            this.linker = linker ?? throw new ArgumentNullException(nameof(linker));
            this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
            this.steamId = steamId;
            this.accountName = accountName ?? String.Empty;
        }

        public async Task<TransferCoordinatorOutcome> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            if (interaction.HasUnresolvedPendingTransfer(steamId))
            {
                return new TransferCoordinatorOutcome
                {
                    Kind = TransferCoordinatorResultKind.BlockedByPendingTransfer,
                    ErrorMessage = "This account already has an unresolved authenticator transfer pending. Resolve or dismiss the previous transfer notice before starting another transfer."
                };
            }

            AuthenticatorLinker.TransferStartStatus startStatus = await linker.StartAuthenticatorTransferAsync(cancellationToken);
            switch (startStatus)
            {
                case AuthenticatorLinker.TransferStartStatus.SmsSent:
                    break;
                case AuthenticatorLinker.TransferStartStatus.PhoneNumberRequired:
                    return new TransferCoordinatorOutcome
                    {
                        Kind = TransferCoordinatorResultKind.PhoneNumberRequired,
                        ErrorMessage = linker.LastErrorMessage
                    };
                case AuthenticatorLinker.TransferStartStatus.RateLimited:
                    return new TransferCoordinatorOutcome
                    {
                        Kind = TransferCoordinatorResultKind.RateLimited,
                        ErrorMessage = linker.LastErrorMessage
                    };
                default:
                    return new TransferCoordinatorOutcome
                    {
                        Kind = TransferCoordinatorResultKind.Failed,
                        ErrorMessage = linker.LastErrorMessage ?? "Could not start the authenticator transfer."
                    };
            }

            bool previousCodeWasInvalid = false;
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new TransferCoordinatorOutcome { Kind = TransferCoordinatorResultKind.Canceled };
                }

                string smsCode = interaction.RequestTransferSmsCode(previousCodeWasInvalid);
                if (smsCode == null)
                {
                    return new TransferCoordinatorOutcome { Kind = TransferCoordinatorResultKind.Canceled };
                }

                if (String.IsNullOrWhiteSpace(smsCode))
                {
                    previousCodeWasInvalid = true;
                    continue;
                }

                // Write secret-free durable marker BEFORE dispatching Continue!
                if (!interaction.WritePreDispatchMarker(steamId, accountName, linker.DeviceID))
                {
                    return new TransferCoordinatorOutcome
                    {
                        Kind = TransferCoordinatorResultKind.Failed,
                        ErrorMessage = "Could not write the safety marker before transferring the authenticator. No changes were sent to Steam."
                    };
                }

                AuthenticatorLinker.TransferFinishStatus finishStatus = await linker.FinishAuthenticatorTransferAsync(smsCode, cancellationToken);
                switch (finishStatus)
                {
                    case AuthenticatorLinker.TransferFinishStatus.CanceledBeforeDispatch:
                        interaction.ClearPreDispatchMarker(steamId);
                        return new TransferCoordinatorOutcome { Kind = TransferCoordinatorResultKind.Canceled };

                    case AuthenticatorLinker.TransferFinishStatus.BadSmsCode:
                        interaction.ClearPreDispatchMarker(steamId);
                        previousCodeWasInvalid = true;
                        continue;

                    case AuthenticatorLinker.TransferFinishStatus.RateLimited:
                        interaction.ClearPreDispatchMarker(steamId);
                        return new TransferCoordinatorOutcome
                        {
                            Kind = TransferCoordinatorResultKind.RateLimited,
                            ErrorMessage = linker.LastErrorMessage
                        };

                    case AuthenticatorLinker.TransferFinishStatus.Rejected:
                        interaction.ClearPreDispatchMarker(steamId);
                        return new TransferCoordinatorOutcome
                        {
                            Kind = TransferCoordinatorResultKind.Failed,
                            ErrorMessage = linker.LastErrorMessage ?? "Steam rejected the authenticator transfer."
                        };

                    case AuthenticatorLinker.TransferFinishStatus.OutcomeUnknown:
                        // Retain marker! Never auto-retry!
                        return new TransferCoordinatorOutcome
                        {
                            Kind = TransferCoordinatorResultKind.OutcomeUnknown,
                            ErrorMessage = linker.LastErrorMessage
                        };

                    case AuthenticatorLinker.TransferFinishStatus.IncompleteReplacement:
                        // Retain marker! Never auto-retry!
                        return new TransferCoordinatorOutcome
                        {
                            Kind = TransferCoordinatorResultKind.IncompleteReplacement,
                            ErrorMessage = linker.LastErrorMessage
                        };

                    case AuthenticatorLinker.TransferFinishStatus.Success:
                        SteamGuardAccount replacement = linker.LinkedAccount;
                        // Stage protected recovery copy IMMEDIATELY upon receiving valid replacement BEFORE SaveAccount!
                        string stagedPath = interaction.TryStageProtectedReplacement(replacement, out bool stagingFailed);

                        if (!interaction.PersistReplacementAccount(replacement, out string saveFailure))
                        {
                            return new TransferCoordinatorOutcome
                            {
                                Kind = TransferCoordinatorResultKind.ReplacementReceivedSavePending,
                                LinkedAccount = replacement,
                                ErrorMessage = saveFailure ?? "The transferred authenticator could not be saved locally.",
                                StagedRecoveryPath = stagedPath,
                                StagingFailed = stagingFailed
                            };
                        }

                        // SaveAccount succeeded -> clean up staging and pre-dispatch marker immediately!
                        if (!String.IsNullOrEmpty(stagedPath))
                            interaction.CleanupStagedReplacement(stagedPath);
                        interaction.ClearPreDispatchMarker(steamId);

                        // Optional short-timeout non-fatal DeviceID enrichment AFTER durable save
                        if (await linker.TryEnrichTransferredDeviceIdAsync(replacement, cancellationToken))
                        {
                            try
                            {
                                interaction.PersistEnrichedDeviceId(replacement);
                            }
                            catch (Exception ex)
                            {
                                SteamAuthDiagnostics.Log(ex, "Optional post-transfer DeviceID persistence failed; initial saved replacement remains intact.");
                            }
                        }

                        return new TransferCoordinatorOutcome
                        {
                            Kind = TransferCoordinatorResultKind.Succeeded,
                            LinkedAccount = replacement
                        };
                }
            }
        }
    }
}
