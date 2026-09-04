using Google.Protobuf;
using SteamAuth;
using SteamAuth.Protocol;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Steam_Desktop_Authenticator
{
    internal enum LoginApprovalDecision
    {
        ApprovePersistent,
        Deny
    }

    internal enum LoginApprovalErrorKind
    {
        None,
        SessionExpired,
        Unauthorized,
        ExpiredOrDuplicate,
        RateLimited,
        Network,
        Unknown
    }

    internal sealed class PendingLoginRequest
    {
        internal DateTime FetchedAtUtc { get; set; }
        public string AccountName { get; set; }
        public ulong SteamId { get; set; }
        public ulong ClientId { get; set; }
        public int Version { get; set; }
        public string IPAddress { get; set; }
        public string Geolocation { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string Country { get; set; }
        public string Platform { get; set; }
        public string DeviceName { get; set; }
        public string RequestedPersistence { get; set; }
        public string SecurityHistory { get; set; }
        public bool LocationMismatch { get; set; }
        public bool HighUsageLogin { get; set; }
    }

    internal sealed class LoginApprovalFetchResult
    {
        public List<PendingLoginRequest> Requests { get; } = new List<PendingLoginRequest>();
        public LoginApprovalErrorKind ErrorKind { get; set; }
        public string ErrorMessage { get; set; }
    }

    internal sealed class LoginApprovalActionResult
    {
        public bool Succeeded { get; set; }
        public LoginApprovalErrorKind ErrorKind { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Implements Steam's pending-login mobile confirmation flow. The requests mirror the
    /// IAuthenticationService protocol used by Steam's mobile clients and steamguard-cli.
    /// </summary>
    internal sealed class LoginApprovalService
    {
        private static readonly TimeSpan RequestDetailsCacheLifetime = TimeSpan.FromSeconds(30);
        private const int MaximumPendingLoginRequests = 1000;
        private const int MaximumLoginTextLength = 256;
        private const int MaximumGeolocationLength = 512;
        private readonly Func<SteamGuardAccount, bool> persistAccount;
        private readonly IAuthenticatorProtocolTransport protocolTransport;

        public LoginApprovalService(Func<SteamGuardAccount, bool> persistAccount)
            : this(persistAccount, new SteamProtobufAuthenticatorTransport())
        {
        }

        internal LoginApprovalService(Func<SteamGuardAccount, bool> persistAccount, IAuthenticatorProtocolTransport protocolTransport)
        {
            this.persistAccount = persistAccount ?? throw new ArgumentNullException(nameof(persistAccount));
            this.protocolTransport = protocolTransport ?? throw new ArgumentNullException(nameof(protocolTransport));
        }

        public async Task<LoginApprovalFetchResult> FetchPendingRequestsAsync(
            SteamGuardAccount account,
            IReadOnlyDictionary<ulong, PendingLoginRequest> knownRequests = null,
            CancellationToken cancellationToken = default)
        {
            var result = new LoginApprovalFetchResult();
            try
            {
                result.Requests.AddRange(await FetchPendingRequestsCoreAsync(account, false, knownRequests, cancellationToken));
            }
            catch (LoginApprovalException ex) when (ex.Kind == LoginApprovalErrorKind.Unauthorized)
            {
                try
                {
                    result.Requests.AddRange(await FetchPendingRequestsCoreAsync(account, true, knownRequests, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (LoginApprovalException retryException)
                {
                    DiagnosticErrorLogger.Log("Login approval fetch", retryException, "The refreshed session could not load pending login requests.");
                    result.ErrorKind = retryException.Kind;
                    result.ErrorMessage = retryException.Message;
                }
                catch (Exception unexpectedException)
                {
                    DiagnosticErrorLogger.Log("Login approval fetch", unexpectedException, "The refreshed session returned an unexpected error.");
                    result.ErrorKind = LoginApprovalErrorKind.Unknown;
                    result.ErrorMessage = "Steam could not load pending login requests.";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LoginApprovalException ex)
            {
                DiagnosticErrorLogger.Log("Login approval fetch", ex, "Steam rejected the pending-login request query.");
                result.ErrorKind = ex.Kind;
                result.ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Login approval fetch", ex, "The pending-login request query returned an unexpected error.");
                result.ErrorKind = LoginApprovalErrorKind.Unknown;
                result.ErrorMessage = "Steam could not load pending login requests.";
            }
            return result;
        }

        private async Task<List<PendingLoginRequest>> FetchPendingRequestsCoreAsync(
            SteamGuardAccount account,
            bool forceRefresh,
            IReadOnlyDictionary<ulong, PendingLoginRequest> knownRequests,
            CancellationToken cancellationToken)
        {
            await EnsureAccessTokenAsync(account, forceRefresh, cancellationToken);
            SteamProtocolResponse<CAuthentication_GetAuthSessionsForAccount_Response> sessionsResponse = await SendAsync(
                account,
                "GetAuthSessionsForAccount",
                new CAuthentication_GetAuthSessionsForAccount_Request(),
                CAuthentication_GetAuthSessionsForAccount_Response.Parser,
                cancellationToken: cancellationToken);
            ThrowIfSteamFailed(sessionsResponse);
            if (sessionsResponse.Body == null)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an invalid pending login request response.");
            if (sessionsResponse.Body.ClientIds.Count > MaximumPendingLoginRequests)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned too many pending login requests.");

            var requests = new List<PendingLoginRequest>();
            var clientIds = new HashSet<ulong>();
            foreach (ulong clientId in sessionsResponse.Body.ClientIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clientId == 0)
                    throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an invalid pending login request identifier.");
                if (!clientIds.Add(clientId))
                    continue;
                if (knownRequests != null && knownRequests.TryGetValue(clientId, out PendingLoginRequest existingRequest) &&
                    DateTime.UtcNow - existingRequest.FetchedAtUtc < RequestDetailsCacheLifetime)
                {
                    requests.Add(existingRequest);
                    continue;
                }
                try
                {
                    var request = await FetchRequestDetailsAsync(account, clientId, cancellationToken);
                    if (request != null)
                        requests.Add(request);
                }
                catch (LoginApprovalException ex) when (ex.Kind == LoginApprovalErrorKind.ExpiredOrDuplicate)
                {
                    // The request disappeared while the list was being loaded. It is no longer actionable.
                }
            }
            return requests;
        }

        public async Task<LoginApprovalActionResult> RespondAsync(
            SteamGuardAccount account,
            PendingLoginRequest request,
            LoginApprovalDecision decision,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (account?.Session == null || account.Session.SteamID == 0 || request == null ||
                    request.ClientId == 0 || request.SteamId != account.Session.SteamID ||
                    !Enum.IsDefined(typeof(LoginApprovalDecision), decision))
                {
                    throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The login request is invalid or belongs to another account.");
                }
                await EnsureAccessTokenAsync(account, false, cancellationToken);
                await SubmitDecisionAsync(account, request, decision, cancellationToken);
                return new LoginApprovalActionResult() { Succeeded = true };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LoginApprovalException ex) when (ex.Kind == LoginApprovalErrorKind.Unauthorized)
            {
                try
                {
                    await EnsureAccessTokenAsync(account, true, cancellationToken);
                    await SubmitDecisionAsync(account, request, decision, cancellationToken);
                    return new LoginApprovalActionResult() { Succeeded = true };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (LoginApprovalException retryException)
                {
                    DiagnosticErrorLogger.Log("Login approval action", retryException, "The refreshed session could not complete a login action.");
                    return FailedAction(retryException);
                }
                catch (Exception unexpectedException)
                {
                    DiagnosticErrorLogger.Log("Login approval action", unexpectedException, "The refreshed session returned an unexpected login-action error.");
                    return UnknownActionFailure();
                }
            }
            catch (LoginApprovalException ex)
            {
                DiagnosticErrorLogger.Log("Login approval action", ex, "Steam rejected the login action.");
                return FailedAction(ex);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Login approval action", ex, "The login action returned an unexpected error.");
                return UnknownActionFailure();
            }
        }

        private async Task<PendingLoginRequest> FetchRequestDetailsAsync(SteamGuardAccount account, ulong clientId, CancellationToken cancellationToken)
        {
            SteamProtocolResponse<CAuthentication_GetAuthSessionInfo_Response> response = await SendAsync(
                account,
                "GetAuthSessionInfo",
                new CAuthentication_GetAuthSessionInfo_Request { ClientId = clientId },
                CAuthentication_GetAuthSessionInfo_Response.Parser,
                cancellationToken: cancellationToken);
            ThrowIfSteamFailed(response);

            CAuthentication_GetAuthSessionInfo_Response details = response.Body;
            if (details == null)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an invalid login request response.");
            if (details.Version < 0 || details.Version > UInt16.MaxValue)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an invalid login request version.");

            return new PendingLoginRequest()
            {
                FetchedAtUtc = DateTime.UtcNow,
                AccountName = account.AccountName,
                SteamId = account.Session.SteamID,
                ClientId = clientId,
                Version = details.Version > 0 ? details.Version : 1,
                IPAddress = ValidateResponseText(details.Ip, MaximumLoginTextLength, "IP address"),
                Geolocation = ValidateResponseText(details.Geoloc, MaximumGeolocationLength, "geolocation"),
                City = ValidateResponseText(details.City, MaximumLoginTextLength, "city"),
                State = ValidateResponseText(details.State, MaximumLoginTextLength, "state"),
                Country = ValidateResponseText(details.Country, MaximumLoginTextLength, "country"),
                Platform = PlatformName((int)details.PlatformType),
                DeviceName = ValidateResponseText(details.DeviceFriendlyName, MaximumLoginTextLength, "device name"),
                RequestedPersistence = PersistenceName((int)details.RequestedPersistence),
                SecurityHistory = SecurityHistoryName((int)details.LoginHistory),
                LocationMismatch = details.RequestorLocationMismatch,
                HighUsageLogin = details.HighUsageLogin
            };
        }

        private async Task SubmitDecisionAsync(
            SteamGuardAccount account,
            PendingLoginRequest request,
            LoginApprovalDecision decision,
            CancellationToken cancellationToken)
        {
            if (account?.Session == null || account.Session.SteamID == 0 || request == null ||
                request.ClientId == 0 || request.SteamId != account.Session.SteamID)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The login request is invalid or belongs to another account.");
            int version = request.Version > 0 ? request.Version : 1;
            if (version > UInt16.MaxValue)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The login request version is invalid.");
            byte[] signature = BuildMobileConfirmationSignature(account.SharedSecret, (ushort)version, request.ClientId, account.Session.SteamID);
            SteamProtocolResponse<CAuthentication_UpdateAuthSessionWithMobileConfirmation_Response> response = await SendAsync(
                account,
                "UpdateAuthSessionWithMobileConfirmation",
                new CAuthentication_UpdateAuthSessionWithMobileConfirmation_Request
                {
                    Version = version,
                    ClientId = request.ClientId,
                    Steamid = account.Session.SteamID,
                    Signature = ByteString.CopyFrom(signature),
                    Confirm = decision == LoginApprovalDecision.ApprovePersistent,
                    Persistence = ESessionPersistence.KEsessionPersistencePersistent
                },
                CAuthentication_UpdateAuthSessionWithMobileConfirmation_Response.Parser,
                cancellationToken: cancellationToken);
            ThrowIfSteamFailed(response);
        }

        private async Task EnsureAccessTokenAsync(SteamGuardAccount account, bool forceRefresh, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (account?.Session == null || account.Session.IsRefreshTokenExpired())
                throw new LoginApprovalException(LoginApprovalErrorKind.SessionExpired, "The account session has expired. Log in again to review login requests.");

            if (forceRefresh || account.Session.IsAccessTokenExpired())
            {
                string previousAccessToken = account.Session.AccessToken;
                string previousRefreshToken = account.Session.RefreshToken;
                try
                {
                    await account.Session.RefreshAccessToken(false, cancellationToken);
                    if (!persistAccount(account))
                    {
                        RestoreSessionTokens(account.Session, previousAccessToken, previousRefreshToken);
                        throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam refreshed the session, but Astro SDA could not save it securely.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RestoreSessionTokens(account.Session, previousAccessToken, previousRefreshToken);
                    throw;
                }
                catch (LoginApprovalException)
                {
                    throw;
                }
                catch (Exception ex) when (IsDefinitiveRefreshFailure(ex))
                {
                    RestoreSessionTokens(account.Session, previousAccessToken, previousRefreshToken);
                    throw new LoginApprovalException(
                        LoginApprovalErrorKind.SessionExpired,
                        "Steam rejected this saved account session. Log in again and retry.",
                        ex);
                }
                catch (Exception ex) when (IsTransientRefreshFailure(ex))
                {
                    RestoreSessionTokens(account.Session, previousAccessToken, previousRefreshToken);
                    throw new LoginApprovalException(
                        IsRefreshRateLimited(ex) ? LoginApprovalErrorKind.RateLimited : LoginApprovalErrorKind.Network,
                        IsRefreshRateLimited(ex)
                            ? "Steam is rate limiting login-action session refreshes. Try again shortly."
                            : "Steam could not be reached while refreshing the login-action session.",
                        ex);
                }
                catch (Exception ex)
                {
                    RestoreSessionTokens(account.Session, previousAccessToken, previousRefreshToken);
                    throw new LoginApprovalException(
                        LoginApprovalErrorKind.Unknown,
                        "Steam could not refresh this account session. Try again later.",
                        ex);
                }
            }
        }

        private static void RestoreSessionTokens(SessionData session, string accessToken, string refreshToken)
        {
            if (session == null)
                return;
            session.AccessToken = accessToken;
            session.RefreshToken = refreshToken;
        }

        private static bool IsDefinitiveRefreshFailure(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamSessionException sessionException && sessionException.Kind == SteamSessionFailureKind.InvalidSession)
                    return true;
                if (current is SteamWebRequestException steamWebException &&
                    (steamWebException.StatusCode == System.Net.HttpStatusCode.Unauthorized || steamWebException.StatusCode == System.Net.HttpStatusCode.Forbidden))
                    return true;
            }
            return false;
        }

        private static bool IsRefreshRateLimited(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamSessionException sessionException && sessionException.Kind == SteamSessionFailureKind.RateLimited)
                    return true;
                string message = current.Message ?? String.Empty;
                if (message.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("result 84", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("result 87", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsTransientRefreshFailure(Exception exception)
        {
            if (IsRefreshRateLimited(exception))
                return true;
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SteamSessionException sessionException &&
                    (sessionException.Kind == SteamSessionFailureKind.RateLimited || sessionException.Kind == SteamSessionFailureKind.Transient))
                    return true;
                if (current is SteamWebRequestException steamWebException &&
                    (steamWebException.StatusCode == System.Net.HttpStatusCode.Unauthorized || steamWebException.StatusCode == System.Net.HttpStatusCode.Forbidden))
                    continue;
                if (current is System.Net.Http.HttpRequestException || current is TimeoutException || current is TaskCanceledException)
                    return true;
            }
            return false;
        }

        private async Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
            SteamGuardAccount account,
            string method,
            TRequest request,
            MessageParser<TResponse> responseParser,
            SteamProtocolRequestMethod requestMethod = SteamProtocolRequestMethod.Post,
            CancellationToken cancellationToken = default)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            try
            {
                return await protocolTransport.SendAsync("IAuthenticationService", method, request, account.Session.AccessToken, responseParser, requestMethod, cancellationToken);
            }
            catch (SteamWebRequestException exception) when (
                exception.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new LoginApprovalException(LoginApprovalErrorKind.Unauthorized, "Steam rejected this account session. Log in again and retry.");
            }
            catch (SteamWebRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                throw new LoginApprovalException(LoginApprovalErrorKind.RateLimited, "Steam is rate limiting login actions. Try again shortly.");
            }
            catch (System.Net.Http.HttpRequestException exception)
            {
                DiagnosticErrorLogger.Log("Login approval transport", exception, "A login approval request failed at the transport layer.");
                throw CreateNetworkException(null);
            }
            catch (TimeoutException exception)
            {
                DiagnosticErrorLogger.Log("Login approval transport", exception, "A login approval request timed out.");
                throw CreateNetworkException("Steam did not respond in time.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                throw CreateNetworkException("Steam did not respond in time.");
            }
        }

        private static void ThrowIfSteamFailed<TResponse>(SteamProtocolResponse<TResponse> response)
            where TResponse : class, IMessage<TResponse>
        {
            if (response != null && response.Result == 1)
                return;

            int result = response?.Result ?? 0;
            string message = response?.ErrorMessage;
            if (SteamSessionFailureClassifier.IsInvalidSessionResult(result))
                throw new LoginApprovalException(LoginApprovalErrorKind.Unauthorized, "Steam authorization expired.");
            if (result == 27 || result == 29)
                throw new LoginApprovalException(LoginApprovalErrorKind.ExpiredOrDuplicate, "This login request has already expired or was already handled.");
            if (result == 84 || result == 87)
                throw new LoginApprovalException(LoginApprovalErrorKind.RateLimited, "Steam is rate limiting login actions. Try again shortly.");

            throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, String.IsNullOrWhiteSpace(message) ? "Steam rejected the login action." : message);
        }

        internal static byte[] BuildMobileConfirmationSignature(string sharedSecret, ushort version, ulong clientId, ulong steamId)
        {
            if (clientId == 0 || steamId == 0)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The login request contains an invalid identity.");
            if (String.IsNullOrWhiteSpace(sharedSecret) || sharedSecret.Length > 4096)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The account does not contain a shared authenticator secret.");

            byte[] data = new byte[18];
            data[0] = (byte)version;
            data[1] = (byte)(version >> 8);
            WriteUInt64LittleEndian(data, 2, clientId);
            WriteUInt64LittleEndian(data, 10, steamId);

            byte[] sharedSecretBytes;
            try
            {
                sharedSecretBytes = Convert.FromBase64String(Regex.Unescape(sharedSecret));
            }
            catch (FormatException ex)
            {
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The account contains an invalid shared authenticator secret.", ex);
            }
            catch (ArgumentException ex)
            {
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The account contains an invalid shared authenticator secret.", ex);
            }
            if (sharedSecretBytes.Length == 0 || sharedSecretBytes.Length > 64)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The account contains an invalid shared authenticator secret.");

            using (var hmac = new HMACSHA256(sharedSecretBytes))
            {
                return hmac.ComputeHash(data);
            }
        }

        private static string ValidateResponseText(string value, int maximumLength, string fieldName)
        {
            value = value ?? String.Empty;
            if (value.Length > maximumLength)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an oversized login request " + fieldName + ".");
            return value;
        }

        private static void WriteUInt64LittleEndian(byte[] target, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                target[offset + i] = (byte)(value >> (8 * i));
        }

        private static LoginApprovalException CreateNetworkException(string detail)
        {
            return new LoginApprovalException(LoginApprovalErrorKind.Network, String.IsNullOrWhiteSpace(detail)
                ? "Steam could not be reached while checking login requests."
                : detail);
        }

        private static LoginApprovalActionResult FailedAction(LoginApprovalException exception)
        {
            return new LoginApprovalActionResult()
            {
                Succeeded = false,
                ErrorKind = exception.Kind,
                ErrorMessage = exception.Message
            };
        }

        private static LoginApprovalActionResult UnknownActionFailure()
        {
            return new LoginApprovalActionResult()
            {
                Succeeded = false,
                ErrorKind = LoginApprovalErrorKind.Unknown,
                ErrorMessage = "Steam could not complete the login action. Try again."
            };
        }

        private static string PlatformName(int platform)
        {
            switch (platform)
            {
                case 1: return "Steam Client";
                case 2: return "Web Browser";
                case 3: return "Mobile App";
                default: return "Unknown platform";
            }
        }

        private static string PersistenceName(int persistence)
        {
            switch (persistence)
            {
                case 0: return "Temporary";
                case 1: return "Persistent";
                default: return "Not specified";
            }
        }

        private static string SecurityHistoryName(int history)
        {
            switch (history)
            {
                case 0:
                    return "Unknown login history";
                case 1:
                    return "Previously seen login";
                case 2:
                    return "First-time login";
                default:
                    return "Unknown login history";
            }
        }

        private sealed class LoginApprovalException : Exception
        {
            public LoginApprovalErrorKind Kind { get; }

            public LoginApprovalException(LoginApprovalErrorKind kind, string message) : base(message)
            {
                Kind = kind;
            }

            public LoginApprovalException(LoginApprovalErrorKind kind, string message, Exception innerException) : base(message, innerException)
            {
                Kind = kind;
            }
        }
    }
}
