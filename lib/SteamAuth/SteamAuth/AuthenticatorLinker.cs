using Google.Protobuf;
using SteamAuth.Protocol;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamAuth
{
    /// <summary>
    /// Handles the typed Steam Web API flow for linking a new mobile authenticator.
    /// </summary>
    public class AuthenticatorLinker
    {
        private const int EResultOK = 1;
        private const int EResultPending = 22;
        private const int EResultAccessDenied = 15;
        private const int EResultRateLimitExceeded = 84;
        private const int EResultAccountLoginDeniedThrottle = 87;
        private const int EResultActivationCodeMismatch = 89;
        private const int MaximumFinalizationAttempts = 10;
        private readonly SessionData session;
        private readonly IAuthenticatorProtocolTransport transport;
        private readonly Func<CancellationToken, Task<long>> steamTimeProvider;
        private readonly Func<TimeSpan, Task> waitForNextAuthenticatorCodeAsync;

        public string PhoneNumber = null;
        public string PhoneCountryCode = null;
        public string PhoneSMSCode = null;

        public string DeviceID { get; private set; }
        public SteamGuardAccount LinkedAccount { get; private set; }
        public bool Finalized = false;
        public string ConfirmationEmailAddress;
        public string LastErrorMessage { get; private set; }
        public event Action<string> FinalizationProgress;

        public ConfirmationCodeType FinalizationConfirmationType
        {
            get
            {
                if (LinkedAccount == null)
                    return ConfirmationCodeType.Unknown;

                return LinkedAccount.ConfirmType == 1
                    ? ConfirmationCodeType.SMS
                    : LinkedAccount.ConfirmType == 3
                        ? ConfirmationCodeType.Email
                        : ConfirmationCodeType.Unknown;
            }
        }

        private PhoneLinkStep phoneLinkStep;

        public AuthenticatorLinker(SessionData sessionData)
            : this(sessionData, new SteamProtobufAuthenticatorTransport(), cancellationToken => TimeAligner.GetSteamTimeAsync(cancellationToken), delay => Task.Delay(delay))
        {
        }

        public AuthenticatorLinker(SessionData sessionData, IAuthenticatorProtocolTransport transport)
            : this(sessionData, transport, cancellationToken => TimeAligner.GetSteamTimeAsync(cancellationToken), delay => Task.Delay(delay))
        {
        }

        public AuthenticatorLinker(SessionData sessionData, IAuthenticatorProtocolTransport transport, Func<long> steamTimeProvider)
            : this(sessionData, transport, WrapSteamTimeProvider(steamTimeProvider), delay => Task.Delay(delay))
        {
        }

        public AuthenticatorLinker(SessionData sessionData, IAuthenticatorProtocolTransport transport, Func<long> steamTimeProvider, Func<TimeSpan, Task> waitForNextAuthenticatorCodeAsync)
            : this(sessionData, transport, WrapSteamTimeProvider(steamTimeProvider), waitForNextAuthenticatorCodeAsync)
        {
        }

        public AuthenticatorLinker(SessionData sessionData, IAuthenticatorProtocolTransport transport, Func<Task<long>> steamTimeProvider)
            : this(sessionData, transport, WrapSteamTimeProvider(steamTimeProvider), delay => Task.Delay(delay))
        {
        }

        private AuthenticatorLinker(SessionData sessionData, IAuthenticatorProtocolTransport transport, Func<CancellationToken, Task<long>> steamTimeProvider, Func<TimeSpan, Task> waitForNextAuthenticatorCodeAsync)
        {
            session = sessionData ?? throw new ArgumentNullException(nameof(sessionData));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.steamTimeProvider = steamTimeProvider ?? throw new ArgumentNullException(nameof(steamTimeProvider));
            this.waitForNextAuthenticatorCodeAsync = waitForNextAuthenticatorCodeAsync ?? throw new ArgumentNullException(nameof(waitForNextAuthenticatorCodeAsync));
            DeviceID = GenerateDeviceID();
        }

        public async Task<PhoneLinkResult> AddPhoneNumber(CancellationToken cancellationToken = default)
        {
            switch (phoneLinkStep)
            {
                case PhoneLinkStep.None:
                    SteamProtocolResponse<CPhone_AccountPhoneStatus_Response> phoneStatus = await SendAsync(
                        "IPhoneService", "AccountPhoneStatus", new CPhone_AccountPhoneStatus_Request(), CPhone_AccountPhoneStatus_Response.Parser, cancellationToken);
                    if (!HasResult(phoneStatus, EResultOK))
                        return PhoneFailure(phoneStatus, "check whether a phone number is already attached");

                    if (phoneStatus.Body.HasPhone)
                        return PhoneLinkResult.PhoneAdded;

                    if (String.IsNullOrWhiteSpace(PhoneNumber))
                    {
                        phoneLinkStep = PhoneLinkStep.PhoneNumberRequired;
                        return PhoneLinkResult.MustProvidePhoneNumber;
                    }

                    return await SubmitPhoneNumber(cancellationToken);

                case PhoneLinkStep.PhoneNumberRequired:
                    return String.IsNullOrWhiteSpace(PhoneNumber)
                        ? PhoneLinkResult.MustProvidePhoneNumber
                        : await SubmitPhoneNumber(cancellationToken);

                case PhoneLinkStep.ConfirmationEmailSent:
                    SteamProtocolResponse<CPhone_IsAccountWaitingForEmailConfirmation_Response> emailStatus = await SendAsync(
                        "IPhoneService", "IsAccountWaitingForEmailConfirmation", new CPhone_IsAccountWaitingForEmailConfirmation_Request(), CPhone_IsAccountWaitingForEmailConfirmation_Response.Parser, cancellationToken);
                    if (!HasResult(emailStatus, EResultOK))
                        return PhoneFailure(emailStatus, "check the email confirmation status");
                    if (emailStatus.Body.AwaitingEmailConfirmation)
                        return PhoneLinkResult.MustConfirmEmail;

                    SteamProtocolResponse<CPhone_SendPhoneVerificationCode_Response> sendSms = await SendAsync(
                        "IPhoneService", "SendPhoneVerificationCode", new CPhone_SendPhoneVerificationCode_Request { Language = 0 }, CPhone_SendPhoneVerificationCode_Response.Parser, cancellationToken);
                    if (!HasResult(sendSms, EResultOK))
                        return PhoneFailure(sendSms, "send the phone verification code");

                    phoneLinkStep = PhoneLinkStep.SMSCodeSent;
                    return PhoneLinkResult.MustConfirmSMS;

                case PhoneLinkStep.SMSCodeSent:
                    if (String.IsNullOrWhiteSpace(PhoneSMSCode))
                        return PhoneLinkResult.MustConfirmSMS;

                    SteamProtocolResponse<CPhone_VerifyAccountPhoneWithCode_Response> verifySms = await SendAsync(
                        "IPhoneService", "VerifyAccountPhoneWithCode", new CPhone_VerifyAccountPhoneWithCode_Request { Code = PhoneSMSCode }, CPhone_VerifyAccountPhoneWithCode_Response.Parser, cancellationToken);
                    if (HasResult(verifySms, EResultOK))
                    {
                        PhoneSMSCode = null;
                        phoneLinkStep = PhoneLinkStep.Verified;
                        return PhoneLinkResult.PhoneAdded;
                    }
                    if (HasResultCode(verifySms, EResultActivationCodeMismatch))
                    {
                        PhoneSMSCode = null;
                        return PhoneLinkResult.InvalidSMSCode;
                    }

                    return PhoneFailure(verifySms, "verify the phone code");

                case PhoneLinkStep.Verified:
                    return PhoneLinkResult.PhoneAdded;

                default:
                    LastErrorMessage = "Steam returned an unsupported phone enrollment state.";
                    return PhoneLinkResult.FailureAddingPhone;
            }
        }

        public async Task<LinkResult> AddAuthenticator(CancellationToken cancellationToken = default)
        {
            LastErrorMessage = null;
            SteamProtocolResponse<CTwoFactor_AddAuthenticator_Response> response = await SendAsync(
                "ITwoFactorService",
                "AddAuthenticator",
                new CTwoFactor_AddAuthenticator_Request
                {
                    Steamid = session.SteamID,
                    AuthenticatorType = 1,
                    DeviceIdentifier = DeviceID,
                    SmsPhoneId = "1",
                    Version = 2
                },
                CTwoFactor_AddAuthenticator_Response.Parser,
                cancellationToken);

            if (IsRateLimited(response))
            {
                LastErrorMessage = "Steam is rate limiting authenticator setup attempts. Wait a while before trying again.";
                return LinkResult.RateLimited;
            }
            if (!HasResult(response, EResultOK))
            {
                LastErrorMessage = DescribeFailure(response, "start authenticator enrollment");
                return LinkResult.GeneralFailure;
            }

            if (response.Body.Status == 2)
                return LinkResult.MustProvidePhoneNumber;
            if (response.Body.Status == 29)
                return LinkResult.AuthenticatorPresent;
            if (response.Body.Status != 1)
            {
                LastErrorMessage = "Steam could not start authenticator enrollment (status " + response.Body.Status + "). Please try again later.";
                return LinkResult.GeneralFailure;
            }
            if (response.Body.SharedSecret == null || response.Body.SharedSecret.Length == 0 ||
                String.IsNullOrWhiteSpace(response.Body.RevocationCode))
            {
                LastErrorMessage = "Steam returned incomplete authenticator data. Please try adding the authenticator again.";
                return LinkResult.GeneralFailure;
            }

            LinkedAccount = new SteamGuardAccount
            {
                SharedSecret = Convert.ToBase64String(response.Body.SharedSecret.ToByteArray()),
                SerialNumber = response.Body.SerialNumber.ToString(),
                RevocationCode = response.Body.RevocationCode,
                URI = response.Body.Uri,
                ServerTime = (long)response.Body.ServerTime,
                AccountName = response.Body.AccountName,
                TokenGID = response.Body.TokenGid,
                IdentitySecret = Convert.ToBase64String(response.Body.IdentitySecret.ToByteArray()),
                Secret1 = Convert.ToBase64String(response.Body.Secret1.ToByteArray()),
                Status = response.Body.Status,
                PhoneNumberHint = response.Body.PhoneNumberHint,
                ConfirmType = response.Body.ConfirmType,
                DeviceID = DeviceID,
                Session = session
            };
            return LinkResult.AwaitingFinalization;
        }

        public async Task<FinalizeResult> FinalizeAddAuthenticator(string confirmationCode, CancellationToken cancellationToken = default)
        {
            LastErrorMessage = null;
            if (LinkedAccount == null)
            {
                LastErrorMessage = "Authenticator enrollment has not been started.";
                return FinalizeResult.GeneralFailure;
            }

            long authenticatorTime = await steamTimeProvider(cancellationToken);
            for (int tries = 0; tries < MaximumFinalizationAttempts; tries++)
            {
                SteamProtocolResponse<CTwoFactor_FinalizeAddAuthenticator_Response> response = await SendAsync(
                    "ITwoFactorService",
                    "FinalizeAddAuthenticator",
                    new CTwoFactor_FinalizeAddAuthenticator_Request
                    {
                        Steamid = session.SteamID,
                        AuthenticatorCode = LinkedAccount.GenerateSteamGuardCodeForTime(authenticatorTime),
                        AuthenticatorTime = (ulong)authenticatorTime,
                        ActivationCode = confirmationCode,
                        ValidateSmsCode = true
                    },
                        CTwoFactor_FinalizeAddAuthenticator_Response.Parser,
                        cancellationToken);

                if (response == null)
                {
                    LastErrorMessage = "Steam did not return a finalization response. Please try again.";
                    return FinalizeResult.GeneralFailure;
                }
                if (HasResultCode(response, EResultActivationCodeMismatch))
                {
                    LastErrorMessage = "Steam did not accept that confirmation code. Enter the current code sent to your email or phone and try again.";
                    return FinalizeResult.BadConfirmationCode;
                }
                if (IsRateLimited(response))
                {
                    LastErrorMessage = "Steam is rate limiting authenticator finalization. Wait a while before trying again.";
                    return FinalizeResult.RateLimited;
                }
                if (!HasResult(response, EResultOK))
                {
                    LastErrorMessage = DescribeFailure(response, "finalize the authenticator");
                    return FinalizeResult.GeneralFailure;
                }
                if (response.Body.WantMore)
                {
                    if (response.Body.ServerTime == 0 || response.Body.ServerTime > (ulong)Int64.MaxValue)
                    {
                        LastErrorMessage = "Steam requested another authenticator code but did not provide its current time. Please try again.";
                        return FinalizeResult.GeneralFailure;
                    }

                    long serverTime = (long)response.Body.ServerTime;
                    long nextAuthenticatorWindow = ((serverTime / 30) + 1) * 30;
                    TimeSpan retryDelay = TimeSpan.FromSeconds(Math.Max(2, nextAuthenticatorWindow - serverTime));
                    ReportFinalizationProgress("Steam is verifying the authenticator. Retrying in " + Math.Ceiling(retryDelay.TotalSeconds) + " seconds. You can close this window to cancel safely.");
                    await WaitForNextAuthenticatorCodeAsync(retryDelay, cancellationToken);
                    authenticatorTime = nextAuthenticatorWindow;
                    continue;
                }
                if (!response.Body.Success)
                {
                    LastErrorMessage = "Steam did not finalize the authenticator (status " + response.Body.Status + ").";
                    return FinalizeResult.GeneralFailure;
                }

                FinalizeResult statusResult;
                try
                {
                    statusResult = await VerifyFinalizedAuthenticatorAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SteamAuthDiagnostics.Log(ex, "Steam finalized the authenticator but its status could not be verified.");
                    statusResult = FinalizeResult.GeneralFailure;
                }

                if (statusResult == FinalizeResult.GeneralFailure || statusResult == FinalizeResult.RateLimited)
                {
                    LinkedAccount.FullyEnrolled = true;
                    Finalized = true;
                    LastErrorMessage = "Steam accepted authenticator finalization, but its activation status could not be verified. The authenticator will be saved locally.";
                    return FinalizeResult.FinalizedStatusUnverified;
                }

                return statusResult;
            }

            LastErrorMessage = "Steam could not verify enough authenticator codes to finalize setup. Please try again later.";
            return FinalizeResult.UnableToGenerateCorrectCodes;
        }

        private async Task<FinalizeResult> VerifyFinalizedAuthenticatorAsync(CancellationToken cancellationToken)
        {
            SteamProtocolResponse<CTwoFactor_Status_Response> response = await SendAsync(
                "ITwoFactorService", "QueryStatus", new CTwoFactor_Status_Request { Steamid = session.SteamID }, CTwoFactor_Status_Response.Parser, cancellationToken);
            if (IsRateLimited(response))
            {
                LastErrorMessage = "Steam is rate limiting the authenticator status check. Wait a while before trying again.";
                return FinalizeResult.RateLimited;
            }
            if (!HasResult(response, EResultOK))
            {
                LastErrorMessage = DescribeFailure(response, "verify authenticator activation");
                return FinalizeResult.GeneralFailure;
            }
            if (response.Body.State == 0)
            {
                LastErrorMessage = "Steam has not confirmed that the authenticator is active yet. Enter a newer confirmation code to try again.";
                return FinalizeResult.NotFinalized;
            }

            LinkedAccount.FullyEnrolled = true;
            Finalized = true;
            return FinalizeResult.Success;
        }

        private async Task<PhoneLinkResult> SubmitPhoneNumber(CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(PhoneCountryCode))
            {
                LastErrorMessage = "Choose a country or enter its two-letter country code before submitting the phone number.";
                return PhoneLinkResult.FailureAddingPhone;
            }

            SteamProtocolResponse<CPhone_SetAccountPhoneNumber_Response> response = await SendAsync(
                "IPhoneService",
                "SetAccountPhoneNumber",
                new CPhone_SetAccountPhoneNumber_Request { PhoneNumber = PhoneNumber, PhoneCountryCode = PhoneCountryCode },
                CPhone_SetAccountPhoneNumber_Response.Parser,
                cancellationToken);
            if (!HasResult(response, EResultPending) || String.IsNullOrWhiteSpace(response.Body.ConfirmationEmailAddress))
                return PhoneFailure(response, "submit the phone number");

            ConfirmationEmailAddress = response.Body.ConfirmationEmailAddress;
            phoneLinkStep = PhoneLinkStep.ConfirmationEmailSent;
            return PhoneLinkResult.MustConfirmEmail;
        }

        private async Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
            string service,
            string method,
            TRequest request,
            MessageParser<TResponse> responseParser,
            CancellationToken cancellationToken)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            return await transport.SendAsync(service, method, request, session.AccessToken, responseParser, cancellationToken: cancellationToken);
        }

        private async Task WaitForNextAuthenticatorCodeAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cancellationToken.CanBeCanceled)
            {
                await waitForNextAuthenticatorCodeAsync(delay);
                return;
            }

            using (CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task delayTask = waitForNextAuthenticatorCodeAsync(delay);
                Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, waitCancellation.Token);
                try
                {
                    if (await Task.WhenAny(delayTask, cancellationTask) == cancellationTask)
                        cancellationToken.ThrowIfCancellationRequested();

                    await delayTask;
                }
                finally
                {
                    waitCancellation.Cancel();
                }
            }
        }

        private PhoneLinkResult PhoneFailure<TResponse>(SteamProtocolResponse<TResponse> response, string action)
            where TResponse : class, IMessage<TResponse>
        {
            LastErrorMessage = DescribeFailure(response, action);
            return PhoneLinkResult.FailureAddingPhone;
        }

        private static bool HasResult<TResponse>(SteamProtocolResponse<TResponse> response, int expectedResult)
            where TResponse : class, IMessage<TResponse>
        {
            return HasResultCode(response, expectedResult) && response.Body != null;
        }

        private static bool HasResultCode<TResponse>(SteamProtocolResponse<TResponse> response, int expectedResult)
            where TResponse : class, IMessage<TResponse>
        {
            return response != null && response.Result == expectedResult;
        }

        private static Func<CancellationToken, Task<long>> WrapSteamTimeProvider(Func<long> steamTimeProvider)
        {
            if (steamTimeProvider == null)
                throw new ArgumentNullException(nameof(steamTimeProvider));

            return _ => Task.FromResult(steamTimeProvider());
        }

        private static Func<CancellationToken, Task<long>> WrapSteamTimeProvider(Func<Task<long>> steamTimeProvider)
        {
            if (steamTimeProvider == null)
                throw new ArgumentNullException(nameof(steamTimeProvider));

            return _ => steamTimeProvider();
        }

        private void ReportFinalizationProgress(string message)
        {
            try
            {
                FinalizationProgress?.Invoke(message);
            }
            catch (Exception ex)
            {
                SteamAuthDiagnostics.Log(ex, "The authenticator finalization progress callback failed.");
            }
        }

        private static bool IsRateLimited(int result)
        {
            return result == EResultRateLimitExceeded || result == EResultAccountLoginDeniedThrottle;
        }

        private static bool IsRateLimited<TResponse>(SteamProtocolResponse<TResponse> response)
            where TResponse : class, IMessage<TResponse>
        {
            return response != null && IsRateLimited(response.Result);
        }

        private static string DescribeFailure<TResponse>(SteamProtocolResponse<TResponse> response, string action)
            where TResponse : class, IMessage<TResponse>
        {
            if (response == null)
                return "Steam did not return a response while trying to " + action + ".";
            if (IsRateLimited(response.Result))
                return "Steam is rate limiting requests while trying to " + action + ". Wait a while before trying again.";
            if (response.Result == EResultAccessDenied)
                return "Steam denied the request to " + action + ". Check the account status and try again later.";
            if (!String.IsNullOrWhiteSpace(response.ErrorMessage))
                return "Steam could not " + action + ": " + response.ErrorMessage;
            return "Steam could not " + action + " (result " + response.Result + "). Please try again later.";
        }

        public enum LinkResult
        {
            MustProvidePhoneNumber,
            MustRemovePhoneNumber,
            MustConfirmEmail,
            AwaitingFinalization,
            GeneralFailure,
            RateLimited,
            AuthenticatorPresent,
            FailureAddingPhone
        }

        public enum PhoneLinkResult
        {
            MustProvidePhoneNumber,
            MustConfirmEmail,
            FailureAddingPhone,
            PhoneAdded,
            MustConfirmSMS,
            InvalidSMSCode
        }

        public enum FinalizeResult
        {
            BadConfirmationCode,
            [Obsolete("Use BadConfirmationCode. Finalization accepts either an email or SMS confirmation code.")]
            BadSMSCode = BadConfirmationCode,
            UnableToGenerateCorrectCodes,
            Success,
            GeneralFailure,
            RateLimited,
            NotFinalized,
            FinalizedStatusUnverified
        }

        public enum ConfirmationCodeType
        {
            Unknown,
            SMS,
            Email
        }

        private enum PhoneLinkStep
        {
            None,
            PhoneNumberRequired,
            ConfirmationEmailSent,
            SMSCodeSent,
            Verified
        }

        public static string GenerateDeviceID()
        {
            return "android:" + Guid.NewGuid().ToString();
        }
    }
}
