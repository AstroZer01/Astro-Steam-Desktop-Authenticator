using Newtonsoft.Json.Linq;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
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
        private const string AuthenticationServiceBase = "https://api.steampowered.com/IAuthenticationService/";
        private readonly Func<SteamGuardAccount, bool> persistAccount;

        public LoginApprovalService(Func<SteamGuardAccount, bool> persistAccount)
        {
            this.persistAccount = persistAccount ?? throw new ArgumentNullException(nameof(persistAccount));
        }

        public async Task<LoginApprovalFetchResult> FetchPendingRequestsAsync(SteamGuardAccount account)
        {
            var result = new LoginApprovalFetchResult();
            try
            {
                result.Requests.AddRange(await FetchPendingRequestsCoreAsync(account, false));
            }
            catch (LoginApprovalException ex) when (ex.Kind == LoginApprovalErrorKind.Unauthorized)
            {
                try
                {
                    result.Requests.AddRange(await FetchPendingRequestsCoreAsync(account, true));
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

        private async Task<List<PendingLoginRequest>> FetchPendingRequestsCoreAsync(SteamGuardAccount account, bool forceRefresh)
        {
            await EnsureAccessTokenAsync(account, forceRefresh);
            var sessionsResponse = await SendGetAsync(account, "GetAuthSessionsForAccount", "{}");
            ThrowIfSteamFailed(sessionsResponse);
            if (GetResponseObject(sessionsResponse.Body) == null)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an invalid pending login request response.");

            var requests = new List<PendingLoginRequest>();
            foreach (ulong clientId in ParseClientIds(sessionsResponse.Body))
            {
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
            var body = new JObject { ["client_id"] = clientId.ToString() }.ToString(Newtonsoft.Json.Formatting.None);
            var response = await SendPostAsync(account, "GetAuthSessionInfo", body);
            ThrowIfSteamFailed(response);

            JObject details = GetResponseObject(response.Body);
            if (details == null)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unknown, "Steam returned an invalid login request response.");

            return new PendingLoginRequest()
            {
                AccountName = account.AccountName,
                SteamId = account.Session.SteamID,
                ClientId = clientId,
                Version = ReadInt(details, "version", 1),
                IPAddress = ReadString(details, "ip"),
                Geolocation = ReadString(details, "geoloc"),
                City = ReadString(details, "city"),
                State = ReadString(details, "state"),
                Country = ReadString(details, "country"),
                Platform = PlatformName(ReadInt(details, "platform_type", 0)),
                DeviceName = ReadString(details, "device_friendly_name"),
                RequestedPersistence = PersistenceName(ReadInt(details, "requested_persistence", -1)),
                SecurityHistory = SecurityHistoryName(ReadInt(details, "login_history", 0)),
                LocationMismatch = ReadBool(details, "requestor_location_mismatch"),
                HighUsageLogin = ReadBool(details, "high_usage_login")
            };
        }

        private async Task SubmitDecisionAsync(
            SteamGuardAccount account,
            PendingLoginRequest request,
            LoginApprovalDecision decision)
        {
            int version = request.Version > 0 ? request.Version : 1;
            byte[] signature = BuildMobileConfirmationSignature(account.SharedSecret, (ushort)version, request.ClientId, account.Session.SteamID);
            var body = new JObject
            {
                ["version"] = version,
                ["client_id"] = request.ClientId.ToString(),
                ["steamid"] = account.Session.SteamID.ToString(),
                ["signature"] = Convert.ToBase64String(signature),
                ["confirm"] = decision == LoginApprovalDecision.ApprovePersistent,
                ["persistence"] = 1 // Steam's persistent session value.
            }.ToString(Newtonsoft.Json.Formatting.None);

            var response = await SendPostAsync(account, "UpdateAuthSessionWithMobileConfirmation", body);
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

        private async Task<SteamWebResponse> SendGetAsync(SteamGuardAccount account, string method, string inputJson)
        {
            string url = AuthenticationServiceBase + method + "/v1/?access_token=" + Uri.EscapeDataString(account.Session.AccessToken) + "&input_json=" + Uri.EscapeDataString(inputJson);
            try
            {
                return await SteamWeb.GETRequestWithHeaders(url, null);
            }
            catch (WebException ex)
            {
                throw CreateWebException(ex);
            }
        }

        private async Task<SteamWebResponse> SendPostAsync(SteamGuardAccount account, string method, string inputJson)
        {
            string url = AuthenticationServiceBase + method + "/v1/?access_token=" + Uri.EscapeDataString(account.Session.AccessToken);
            var form = new NameValueCollection();
            form.Add("input_json", inputJson);
            try
            {
                return await SteamWeb.POSTRequestWithHeaders(url, null, form);
            }
            catch (WebException ex)
            {
                throw CreateWebException(ex);
            }
        }

        private static List<ulong> ParseClientIds(string responseBody)
        {
            var ids = new List<ulong>();
            JObject response = GetResponseObject(responseBody);
            var values = response?["client_ids"] as JArray;
            if (values == null)
                return ids;

            foreach (JToken value in values)
            {
                if (ulong.TryParse(value.ToString(), out ulong id))
                    ids.Add(id);
            }
            return ids;
        }

        private static JObject GetResponseObject(string body)
        {
            if (String.IsNullOrWhiteSpace(body))
                return null;

            try
            {
                JObject document = JObject.Parse(body);
                return document["response"] as JObject ?? document;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        private static void ThrowIfSteamFailed(SteamWebResponse response)
        {
            string value = response?.Headers?["X-eresult"];
            if (String.IsNullOrEmpty(value) || !Int32.TryParse(value, out int result) || result == 1)
                return;

            string message = response.Headers?["X-error_message"];
            if (result == 15 || result == 21)
                throw new LoginApprovalException(LoginApprovalErrorKind.Unauthorized, "Steam authorization expired.");
            if (result == 27 || result == 29)
                throw new LoginApprovalException(LoginApprovalErrorKind.ExpiredOrDuplicate, "This login request has already expired or was already handled.");
            if (result == 84)
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

        private static LoginApprovalException CreateWebException(WebException exception)
        {
            if (exception.Response is HttpWebResponse response && response.StatusCode == HttpStatusCode.Unauthorized)
                return new LoginApprovalException(LoginApprovalErrorKind.Unauthorized, "Steam authorization expired.");
            if (exception.Response is HttpWebResponse rateLimited && (int)rateLimited.StatusCode == 429)
                return new LoginApprovalException(LoginApprovalErrorKind.RateLimited, "Steam is rate limiting login actions. Try again shortly.");
            return new LoginApprovalException(LoginApprovalErrorKind.Network, "Steam could not be reached while checking login requests.");
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

        private static int ReadInt(JObject value, string name, int fallback)
        {
            return Int32.TryParse(value?[name]?.ToString(), out int parsed) ? parsed : fallback;
        }

        private static bool ReadBool(JObject value, string name)
        {
            return Boolean.TryParse(value?[name]?.ToString(), out bool parsed) && parsed;
        }

        private static string ReadString(JObject value, string name)
        {
            return value?[name]?.ToString() ?? String.Empty;
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
            return history == 0 ? "New or unknown login" : "Previously seen login";
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
