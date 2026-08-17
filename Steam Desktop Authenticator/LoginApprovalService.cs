using Google.Protobuf;
using SteamAuth;
using SteamAuth.Protocol;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
            IReadOnlyDictionary<ulong, PendingLoginRequest> knownRequests = null)
        {
            var result = new LoginApprovalFetchResult();
            try
            {
                result.Requests.AddRange(await FetchPendingRequestsCoreAsync(account, false, knownRequests));
            }
            catch (LoginApprovalException ex) when (ex.Kind == LoginApprovalErrorKind.Unauthorized)
            {
                try
                {
                    result.Requests.AddRange(await FetchPendingRequestsCoreAsync(account, true, knownRequests));
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
            IReadOnlyDictionary<ulong, PendingLoginRequest> knownRequests)
        {
            await EnsureAccessTokenAsync(account, forceRefresh);
            SteamProtocolResponse<CAuthentication_GetAuthSessionsForAccount_Response> sessionsResponse = await SendAsync(
                account,
                "GetAuthSessionsForAccount",
                new CAuthentication_GetAuthSessionsForAccount_Request(),
                CAuthentication_GetAuthSessionsForAccount_Response.Parser);
            ThrowIfSteamFailed(sessionsResponse);
            if (sessionsResponse.Body == null)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an invalid pending login request response.");

            var requests = new List<PendingLoginRequest>();
            foreach (ulong clientId in sessionsResponse.Body.ClientIds)
            {
                if (knownRequests != null && knownRequests.TryGetValue(clientId, out PendingLoginRequest existingRequest) &&
                    DateTime.UtcNow - existingRequest.FetchedAtUtc < RequestDetailsCacheLifetime)
                {
                    requests.Add(existingRequest);
                    continue;
                }
                try
                {
                    var request = await FetchRequestDetailsAsync(account, clientId);
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
            LoginApprovalDecision decision)
        {
            try
            {
                await EnsureAccessTokenAsync(account, false);
                await SubmitDecisionAsync(account, request, decision);
                return new LoginApprovalActionResult() { Succeeded = true };
            }
            catch (LoginApprovalException ex) when (ex.Kind == LoginApprovalErrorKind.Unauthorized)
            {
                try
                {
                    await EnsureAccessTokenAsync(account, true);
                    await SubmitDecisionAsync(account, request, decision);
                    return new LoginApprovalActionResult() { Succeeded = true };
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

        private async Task<PendingLoginRequest> FetchRequestDetailsAsync(SteamGuardAccount account, ulong clientId)
        {
            SteamProtocolResponse<CAuthentication_GetAuthSessionInfo_Response> response = await SendAsync(
                account,
                "GetAuthSessionInfo",
                new CAuthentication_GetAuthSessionInfo_Request { ClientId = clientId },
                CAuthentication_GetAuthSessionInfo_Response.Parser);
            ThrowIfSteamFailed(response);

            CAuthentication_GetAuthSessionInfo_Response details = response.Body;
            if (details == null)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an invalid login request response.");

            return new PendingLoginRequest()
            {
                FetchedAtUtc = DateTime.UtcNow,
                AccountName = account.AccountName,
                SteamId = account.Session.SteamID,
                ClientId = clientId,
                Version = details.Version > 0 ? details.Version : 1,
                IPAddress = details.Ip ?? String.Empty,
                Geolocation = details.Geoloc ?? String.Empty,
                City = details.City ?? String.Empty,
                State = details.State ?? String.Empty,
                Country = details.Country ?? String.Empty,
                Platform = PlatformName((int)details.PlatformType),
                DeviceName = details.DeviceFriendlyName ?? String.Empty,
                RequestedPersistence = PersistenceName((int)details.RequestedPersistence),
                SecurityHistory = SecurityHistoryName((int)details.LoginHistory),
                LocationMismatch = details.RequestorLocationMismatch,
                HighUsageLogin = details.HighUsageLogin
            };
        }

        private async Task SubmitDecisionAsync(
            SteamGuardAccount account,
            PendingLoginRequest request,
            LoginApprovalDecision decision)
        {
            int version = request.Version > 0 ? request.Version : 1;
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
                CAuthentication_UpdateAuthSessionWithMobileConfirmation_Response.Parser);
            ThrowIfSteamFailed(response);
        }

        private async Task EnsureAccessTokenAsync(SteamGuardAccount account, bool forceRefresh)
        {
            if (account?.Session == null || account.Session.IsRefreshTokenExpired())
                throw new LoginApprovalException(LoginApprovalErrorKind.SessionExpired, "The account session has expired. Log in again to review login requests.");

            if (forceRefresh || account.Session.IsAccessTokenExpired())
            {
                try
                {
                    await account.Session.RefreshAccessToken();
                    if (!persistAccount(account))
                        throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam refreshed the session, but Astro SDA could not save it securely.");
                }
                catch (LoginApprovalException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw new LoginApprovalException(LoginApprovalErrorKind.SessionExpired, "Steam could not refresh this account session. Log in again and retry.");
                }
            }
        }

        private async Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
            SteamGuardAccount account,
            string method,
            TRequest request,
            MessageParser<TResponse> responseParser,
            SteamProtocolRequestMethod requestMethod = SteamProtocolRequestMethod.Post)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            try
            {
                return await protocolTransport.SendAsync("IAuthenticationService", method, request, account.Session.AccessToken, responseParser, requestMethod);
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
            if (result == 15 || result == 21)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unauthorized, "Steam authorization expired.");
            if (result == 27 || result == 29)
                throw new LoginApprovalException(LoginApprovalErrorKind.ExpiredOrDuplicate, "This login request has already expired or was already handled.");
            if (result == 84 || result == 87)
                throw new LoginApprovalException(LoginApprovalErrorKind.RateLimited, "Steam is rate limiting login actions. Try again shortly.");

            throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, String.IsNullOrWhiteSpace(message) ? "Steam rejected the login action." : message);
        }

        internal static byte[] BuildMobileConfirmationSignature(string sharedSecret, ushort version, ulong clientId, ulong steamId)
        {
            if (String.IsNullOrWhiteSpace(sharedSecret))
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "The account does not contain a shared authenticator secret.");

            byte[] data = new byte[18];
            data[0] = (byte)version;
            data[1] = (byte)(version >> 8);
            WriteUInt64LittleEndian(data, 2, clientId);
            WriteUInt64LittleEndian(data, 10, steamId);

            using (var hmac = new HMACSHA256(Convert.FromBase64String(Regex.Unescape(sharedSecret))))
            {
                return hmac.ComputeHash(data);
            }
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
        }
    }
}
