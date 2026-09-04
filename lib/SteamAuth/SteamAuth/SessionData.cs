using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamAuth.Protocol;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamAuth
{
    public class SessionData
    {
        private const int MaximumTokenLength = 16 * 1024;
        private const int MaximumTokenComponentLength = 8 * 1024;
        private readonly IAuthenticatorProtocolTransport protocolTransport;

        public SessionData()
            : this(new SteamProtobufAuthenticatorTransport())
        {
        }

        public SessionData(IAuthenticatorProtocolTransport protocolTransport)
        {
            this.protocolTransport = protocolTransport ?? throw new ArgumentNullException(nameof(protocolTransport));
        }

        public ulong SteamID { get; set; }

        public string AccessToken { get; set; }

        public string RefreshToken { get; set; }

        public string SessionID { get; set; }

        /// <summary>
        /// Refresh your access token, optionally also getting a new refresh token
        /// </summary>
        /// <param name="allowRenewal">Allow getting a new refresh token as well. If one is returned, this.RefreshToken will be overwritten. You must save this new token!</param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task RefreshAccessToken(bool allowRenewal = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(this.RefreshToken))
                throw new Exception("Refresh token is empty");

            if (IsTokenExpired(this.RefreshToken))
                throw new Exception("Refresh token is expired");

            try
            {
                SteamProtocolResponse<CAuthentication_AccessToken_GenerateForApp_Response> response = await protocolTransport.SendAsync(
                    "IAuthenticationService",
                    "GenerateAccessTokenForApp",
                    new CAuthentication_AccessToken_GenerateForApp_Request
                    {
                        RefreshToken = RefreshToken,
                        Steamid = SteamID,
                        RenewalType = allowRenewal ? ETokenRenewalType.KEtokenRenewalTypeAllow : ETokenRenewalType.KEtokenRenewalTypeNone
                    },
                    AccessToken,
                    CAuthentication_AccessToken_GenerateForApp_Response.Parser,
                    cancellationToken: cancellationToken);
                if (response == null || response.Result != 1 || response.Body == null || String.IsNullOrWhiteSpace(response.Body.AccessToken))
                {
                    string detail = response != null && (response.Result == 84 || response.Result == 87)
                        ? "Steam is rate limiting access-token refresh requests. Wait a while before trying again."
                        : response != null && !String.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? response.ErrorMessage
                        : "Steam returned result " + (response == null ? 0 : response.Result) + ".";
                    throw new Exception(detail);
                }

                AccessToken = response.Body.AccessToken;
                if (!String.IsNullOrEmpty(response.Body.RefreshToken))
                    RefreshToken = response.Body.RefreshToken;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SteamAuthDiagnostics.Log(ex, "Access-token refresh failed.");
                throw new Exception("Failed to refresh token: " + ex.Message);
            }
        }

        public bool IsAccessTokenExpired()
        {
            if (string.IsNullOrEmpty(this.AccessToken))
                return true;

            return IsTokenExpired(this.AccessToken);
        }

        public bool IsRefreshTokenExpired()
        {
            if (string.IsNullOrEmpty(this.RefreshToken))
                return true;

            return IsTokenExpired(this.RefreshToken);
        }

        private bool IsTokenExpired(string token)
        {
            // Compare expire time of the token to the current time
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > GetTokenExpirationTime(token);
        }

        /// <summary>
        /// If the token is going to expire within the next 24h.
        /// </summary>
        /// <returns></returns>
        public bool IsRefreshTokenAboutToExpire()
        {
            return IsRefreshTokenExpired() || IsTokenAboutToExpire(RefreshToken);
        }

        /// <summary>
        /// Returns if the token will expire
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private bool IsTokenAboutToExpire(string token)
        {
            // Compare expire time of the token to the current time
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (24 * 60 * 60) > GetTokenExpirationTime(token);
        }

        /// <summary>
        /// Fetches JWT expiration time.
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private long GetTokenExpirationTime(string token)
        {
            if (String.IsNullOrWhiteSpace(token))
                return 0;
            if (token.Length > MaximumTokenLength)
                return 0;

            string[] tokenComponents = token.Split('.');
            if (tokenComponents.Length != 3 || tokenComponents.Any(String.IsNullOrWhiteSpace) ||
                tokenComponents.Any(component => component.Length > MaximumTokenComponentLength))
                return 0;

            try
            {
                // Fix up base64url to normal base64.
                string payloadComponent = tokenComponents[1];
                if (payloadComponent.Any(character => !Char.IsLetterOrDigit(character) && character != '-' && character != '_'))
                    return 0;

                string base64 = payloadComponent.Replace('-', '+').Replace('_', '/');
                if (base64.Length % 4 == 1)
                    return 0;

                if (base64.Length % 4 != 0)
                    base64 += new string('=', 4 - base64.Length % 4);

                byte[] payloadBytes = Convert.FromBase64String(base64);
                JObject payload;
                using (StringReader stringReader = new StringReader(new UTF8Encoding(false, true).GetString(payloadBytes)))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                {
                    payload = JObject.Load(jsonReader);
                }
                JToken expirationToken = payload["exp"];
                return expirationToken?.Type == JTokenType.Integer &&
                    Int64.TryParse(expirationToken.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long expiration) &&
                    expiration > 0
                    ? expiration
                    : 0;
            }
            catch (FormatException)
            {
                return 0;
            }
            catch (JsonException)
            {
                return 0;
            }
            catch (DecoderFallbackException)
            {
                return 0;
            }
        }

        public CookieContainer GetCookies()
        {
            if (this.SessionID == null)
                this.SessionID = GenerateSessionID();

            var cookies = new CookieContainer();
            foreach (string domain in new string[] { "steamcommunity.com", "store.steampowered.com" })
            {
                cookies.Add(new Cookie("steamLoginSecure", this.GetSteamLoginSecure(), "/", domain));
                cookies.Add(new Cookie("sessionid", this.SessionID, "/", domain));
                cookies.Add(new Cookie("mobileClient", "android", "/", domain));
                cookies.Add(new Cookie("mobileClientVersion", "777777 3.6.4", "/", domain));
            }
            return cookies;
        }

        private string GetSteamLoginSecure()
        {
            return this.SteamID.ToString() + "%7C%7C" + this.AccessToken;
        }

        private static string GenerateSessionID()
        {
            return GetRandomHexNumber(32);
        }

        private static string GetRandomHexNumber(int digits)
        {
            int bytesNeeded = (digits + 1) / 2;
            byte[] buffer = new byte[bytesNeeded];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            string result = String.Concat(buffer.Select(x => x.ToString("X2")).ToArray());
            if (digits % 2 == 0)
                return result;
            return result.Substring(0, digits);
        }

    }
}
