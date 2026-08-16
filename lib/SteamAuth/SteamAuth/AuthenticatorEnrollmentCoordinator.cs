using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamAuth
{
    public sealed class PhoneEnrollmentDetails
    {
        public PhoneEnrollmentDetails(string phoneNumber, string countryCode, bool continueWithoutPhone = false)
        {
            PhoneNumber = phoneNumber;
            CountryCode = countryCode;
            ContinueWithoutPhone = continueWithoutPhone;
        }

        public string PhoneNumber { get; private set; }
        public string CountryCode { get; private set; }
        public bool ContinueWithoutPhone { get; private set; }
    }

    /// <summary>
    /// Supplies user interaction for the phone-enrollment portion of authenticator setup.
    /// </summary>
    public interface IPhoneEnrollmentInteraction
    {
        PhoneEnrollmentDetails RequestPhoneNumber();
        bool ConfirmPhoneRequired();
        bool ConfirmEmail(string confirmationEmailAddress);
        string RequestSmsCode(bool previousCodeWasInvalid);
    }

    public enum AuthenticatorEnrollmentResult
    {
        AwaitingFinalization,
        Canceled,
        AuthenticatorPresent,
        Failed
    }

    public sealed class AuthenticatorEnrollmentOutcome
    {
        public AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult result, string errorMessage = null)
        {
            Result = result;
            ErrorMessage = errorMessage;
        }

        public AuthenticatorEnrollmentResult Result { get; private set; }
        public string ErrorMessage { get; private set; }
    }

    /// <summary>
    /// Coordinates authenticator enrollment without depending on a particular UI framework.
    /// </summary>
    public sealed class AuthenticatorEnrollmentCoordinator
    {
        private const int MaxPhoneEnrollmentAttempts = 10;
        private static readonly TimeSpan PhoneEnrollmentRetryDelay = TimeSpan.FromSeconds(1);
        private readonly AuthenticatorLinker linker;
        private readonly IPhoneEnrollmentInteraction interaction;

        public AuthenticatorEnrollmentCoordinator(AuthenticatorLinker linker, IPhoneEnrollmentInteraction interaction)
        {
            this.linker = linker ?? throw new ArgumentNullException(nameof(linker));
            this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        }

        public async Task<AuthenticatorEnrollmentOutcome> StartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                AuthenticatorLinker.LinkResult linkResult = await linker.AddAuthenticator(cancellationToken);
                if (linkResult == AuthenticatorLinker.LinkResult.AwaitingFinalization)
                    return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AwaitingFinalization);

                if (linkResult == AuthenticatorLinker.LinkResult.AuthenticatorPresent)
                    return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AuthenticatorPresent);

                if (linkResult == AuthenticatorLinker.LinkResult.MustProvidePhoneNumber)
                    return await EnrollPhoneAsync(cancellationToken, new PhoneEnrollmentState());

                return FailedLinkRequest();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.Canceled);
            }
            catch (Exception ex)
            {
                SteamAuthDiagnostics.Log(ex, "Authenticator enrollment failed.");
                return Failed("Steam could not complete the authenticator setup. Please try again later.");
            }
        }

        private async Task<AuthenticatorEnrollmentOutcome> EnrollPhoneAsync(CancellationToken cancellationToken, PhoneEnrollmentState enrollmentState)
        {
            bool previousCodeWasInvalid = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previousCodeWasInvalid)
                    await Task.Delay(PhoneEnrollmentRetryDelay, cancellationToken);

                AuthenticatorLinker.PhoneLinkResult phoneResult = await linker.AddPhoneNumber(cancellationToken);
                switch (phoneResult)
                {
                    case AuthenticatorLinker.PhoneLinkResult.MustProvidePhoneNumber:
                        PhoneEnrollmentDetails phone = interaction.RequestPhoneNumber();
                        if (phone == null)
                            return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.Canceled);

                        if (phone.ContinueWithoutPhone)
                            return await RetryWithoutPhoneAsync(cancellationToken, enrollmentState);

                        if (string.IsNullOrWhiteSpace(phone.PhoneNumber))
                            return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.Canceled);

                        linker.PhoneNumber = phone.PhoneNumber;
                        linker.PhoneCountryCode = phone.CountryCode;
                        previousCodeWasInvalid = false;
                        break;

                    case AuthenticatorLinker.PhoneLinkResult.MustConfirmEmail:
                        if (!interaction.ConfirmEmail(linker.ConfirmationEmailAddress))
                            return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.Canceled);
                        break;

                    case AuthenticatorLinker.PhoneLinkResult.MustConfirmSMS:
                    case AuthenticatorLinker.PhoneLinkResult.InvalidSMSCode:
                        if (phoneResult == AuthenticatorLinker.PhoneLinkResult.InvalidSMSCode &&
                            ++enrollmentState.Attempts >= MaxPhoneEnrollmentAttempts)
                        {
                            return Failed("Steam did not accept the SMS code after several attempts. Please request a new code and try again later.");
                        }

                        string code = interaction.RequestSmsCode(previousCodeWasInvalid || phoneResult == AuthenticatorLinker.PhoneLinkResult.InvalidSMSCode);
                        if (string.IsNullOrWhiteSpace(code))
                            return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.Canceled);

                        linker.PhoneSMSCode = code;
                        previousCodeWasInvalid = phoneResult == AuthenticatorLinker.PhoneLinkResult.InvalidSMSCode;
                        break;

                    case AuthenticatorLinker.PhoneLinkResult.PhoneAdded:
                        return await AddAuthenticatorAsync(cancellationToken);

                    case AuthenticatorLinker.PhoneLinkResult.FailureAddingPhone:
                    default:
                        return Failed(String.IsNullOrWhiteSpace(linker.LastErrorMessage)
                            ? "Steam could not complete phone registration. Please try again later."
                            : linker.LastErrorMessage);
                }
            }

        }

        private async Task<AuthenticatorEnrollmentOutcome> AddAuthenticatorAsync(CancellationToken cancellationToken)
        {
            AuthenticatorLinker.LinkResult result = await linker.AddAuthenticator(cancellationToken);
            if (result == AuthenticatorLinker.LinkResult.AwaitingFinalization)
                return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AwaitingFinalization);

            if (result == AuthenticatorLinker.LinkResult.AuthenticatorPresent)
                return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AuthenticatorPresent);

            return FailedLinkRequest();
        }

        private async Task<AuthenticatorEnrollmentOutcome> RetryWithoutPhoneAsync(CancellationToken cancellationToken, PhoneEnrollmentState enrollmentState)
        {
            AuthenticatorLinker.LinkResult result = await linker.AddAuthenticator(cancellationToken);
            if (result == AuthenticatorLinker.LinkResult.AwaitingFinalization)
                return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AwaitingFinalization);

            if (result == AuthenticatorLinker.LinkResult.AuthenticatorPresent)
                return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AuthenticatorPresent);

            if (result == AuthenticatorLinker.LinkResult.MustProvidePhoneNumber)
            {
                if (!interaction.ConfirmPhoneRequired())
                    return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.Canceled);

                return await EnrollPhoneAsync(cancellationToken, enrollmentState);
            }

            return FailedLinkRequest();
        }

        private AuthenticatorEnrollmentOutcome FailedLinkRequest()
        {
            return Failed(String.IsNullOrWhiteSpace(linker.LastErrorMessage)
                ? "Steam did not accept the authenticator enrollment request. Please try again later."
                : linker.LastErrorMessage);
        }

        private static AuthenticatorEnrollmentOutcome Failed(string message)
        {
            return new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.Failed, message);
        }

        private sealed class PhoneEnrollmentState
        {
            public int Attempts { get; set; }
        }
    }
}
