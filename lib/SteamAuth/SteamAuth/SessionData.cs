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
        private string accessToken;
        private string refreshToken;
        private ParsedTokenExpiration cachedAccessTokenExpiration;
        private ParsedTokenExpiration cachedRefreshTokenExpiration;
        private long tokenExpirationParseCount;

        public SessionData()
            : this(new SteamProtobufAuthenticatorTransport())
        {
        }

        public SessionData(IAuthenticatorProtocolTransport protocolTransport)
        {
            this.protocolTransport = protocolTransport ?? throw new ArgumentNullException(nameof(protocolTransport));
        }

        public ulong SteamID { get; set; }

        public string AccessToken
        {
            get => Volatile.Read(ref accessToken);
            set
            {
                string previous = Interlocked.Exchange(ref accessToken, value);
                if (!String.Equals(previous, value, StringComparison.Ordinal))
                    Volatile.Write(ref cachedAccessTokenExpiration, null);
            }
        }

        public string RefreshToken
        {
            get => Volatile.Read(ref refreshToken);
            set
            {
                string previous = Interlocked.Exchange(ref refreshToken, value);
                if (!String.Equals(previous, value, StringComparison.Ordinal))
                    Volatile.Write(ref cachedRefreshTokenExpiration, null);
            }
        }

        public string SessionID { get; set; }

        internal long TokenExpirationParseCount => Volatile.Read(ref tokenExpirationParseCount);

        /// <summary>
        /// Refresh your access token, optionally also getting a new refresh token
        /// </summary>
        /// <param name="allowRenewal">Allow getting a new refresh token as well. If one is returned, this.RefreshToken will be overwritten. You must save this new token!</param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task RefreshAccessToken(bool allowRenewal = false, CancellationToken cancellationToken = default)
        {
            string currentRefreshToken = this.RefreshToken;
            if (string.IsNullOrEmpty(currentRefreshToken))
                throw new SteamSessionException(SteamSessionFailureKind.InvalidSession, "Refresh token is empty.");

            if (IsTokenExpired(currentRefreshToken, ref refreshToken, ref cachedRefreshTokenExpiration))
                throw new SteamSessionException(SteamSessionFailureKind.InvalidSession, "Refresh token is expired.");

            try
            {
                SteamProtocolResponse<CAuthentication_AccessToken_GenerateForApp_Response> response = await protocolTransport.SendAsync(
                    "IAuthenticationService",
                    "GenerateAccessTokenForApp",
                    new CAuthentication_AccessToken_GenerateForApp_Request
                    {
                        RefreshToken = currentRefreshToken,
                        Steamid = SteamID,
                        RenewalType = allowRenewal ? ETokenRenewalType.KEtokenRenewalTypeAllow : ETokenRenewalType.KEtokenRenewalTypeNone
                    },
                    AccessToken,
                    CAuthentication_AccessToken_GenerateForApp_Response.Parser,
                    cancellationToken: cancellationToken);
                if (response == null || response.Result != 1 || response.Body == null || String.IsNullOrWhiteSpace(response.Body.AccessToken))
                {
                    int result = response?.Result ?? 0;
                    string detail = SteamSessionFailureClassifier.IsRateLimitedResult(result)
                        ? "Steam is rate limiting access-token refresh requests. Wait a while before trying again."
                        : response != null && !String.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? response.ErrorMessage
                        : "Steam returned result " + result + ".";
                    throw new SteamSessionException(
                        SteamSessionFailureClassifier.ClassifyRefreshResult(result),
                        detail,
                        result);
                }

                AccessToken = response.Body.AccessToken;
                if (!String.IsNullOrEmpty(response.Body.RefreshToken))
                    RefreshToken = response.Body.RefreshToken;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SteamSessionException ex)
            {
                SteamAuthDiagnostics.Log(ex, "Access-token refresh failed.");
                throw;
            }
            catch (Exception ex)
            {
                SteamAuthDiagnostics.Log(ex, "Access-token refresh failed.");
                throw new Exception("Failed to refresh token: " + ex.Message, ex);
            }
        }

        public bool IsAccessTokenExpired()
        {
            string token = this.AccessToken;
            if (string.IsNullOrEmpty(token))
                return true;

            return IsTokenExpired(token, ref accessToken, ref cachedAccessTokenExpiration);
        }

        public bool IsRefreshTokenExpired()
        {
            string token = this.RefreshToken;
            if (string.IsNullOrEmpty(token))
                return true;

            return IsTokenExpired(token, ref refreshToken, ref cachedRefreshTokenExpiration);
        }

        private bool IsTokenExpired(string token, ref string tokenField, ref ParsedTokenExpiration cacheSlot)
        {
            // Compare expire time of the token to the current time
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > GetTokenExpirationTime(token, ref tokenField, ref cacheSlot);
        }

        /// <summary>
        /// If the token is going to expire within the next 24h.
        /// </summary>
        /// <returns></returns>
        public bool IsRefreshTokenAboutToExpire()
        {
            string token = this.RefreshToken;
            if (string.IsNullOrEmpty(token))
                return true;

            long expiration = GetTokenExpirationTime(token, ref refreshToken, ref cachedRefreshTokenExpiration);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return now > expiration || now + (24 * 60 * 60) > expiration;
        }

        /// <summary>
        /// Fetches JWT expiration time, caching the parsed timestamp keyed to the exact token value.
        /// </summary>
        private long GetTokenExpirationTime(string token, ref string tokenField, ref ParsedTokenExpiration cacheSlot)
        {
            if (String.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
                return 0;

            ParsedTokenExpiration cached = Volatile.Read(ref cacheSlot);
            if (cached != null && String.Equals(cached.Token, token, StringComparison.Ordinal))
                return cached.ExpirationUnixSeconds;

            long expiration = ParseTokenExpirationTime(token);
            Interlocked.Increment(ref tokenExpirationParseCount);
            if (String.Equals(Volatile.Read(ref tokenField), token, StringComparison.Ordinal))
                Volatile.Write(ref cacheSlot, new ParsedTokenExpiration(token, expiration));
            return expiration;
        }

        /// <summary>
        /// Parses JWT expiration time from a raw token string.
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private static long ParseTokenExpirationTime(string token)
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

        private sealed class ParsedTokenExpiration
        {
            internal ParsedTokenExpiration(string token, long expirationUnixSeconds)
            {
                Token = token;
                ExpirationUnixSeconds = expirationUnixSeconds;
            }

            internal string Token { get; }

            internal long ExpirationUnixSeconds { get; }
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
