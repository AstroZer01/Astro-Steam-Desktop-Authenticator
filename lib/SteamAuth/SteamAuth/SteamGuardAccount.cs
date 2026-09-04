using Google.Protobuf;
using Newtonsoft.Json;
using SteamAuth.Protocol;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SteamAuth
{
    public class SteamGuardAccount
    {
        [JsonProperty("shared_secret")]
        public string SharedSecret { get; set; }

        [JsonProperty("serial_number")]
        public string SerialNumber { get; set; }

        [JsonProperty("revocation_code")]
        public string RevocationCode { get; set; }

        [JsonProperty("uri")]
        public string URI { get; set; }

        [JsonProperty("server_time")]
        public long ServerTime { get; set; }

        [JsonProperty("account_name")]
        public string AccountName { get; set; }

        [JsonProperty("token_gid")]
        public string TokenGID { get; set; }

        [JsonProperty("identity_secret")]
        public string IdentitySecret { get; set; }

        [JsonProperty("secret_1")]
        public string Secret1 { get; set; }

        [JsonProperty("status")]
        public int Status { get; set; }

        // Deprecated?
        [JsonProperty("device_id")]
        public string DeviceID { get; set; }

        [JsonProperty("phone_number_hint")]
        public string PhoneNumberHint { get; set; }

        [JsonProperty("confirm_type")]
        public int ConfirmType { get; set; }

        /// <summary>
        /// Set to true if the authenticator has actually been applied to the account.
        /// </summary>
        [JsonProperty("fully_enrolled")]
        public bool FullyEnrolled { get; set; }

        public SessionData Session { get; set; }

        [JsonIgnore]
        public string LastAuthenticatorOperationError { get; private set; }

        private static byte[] steamGuardCodeTranslations = new byte[] { 50, 51, 52, 53, 54, 55, 56, 57, 66, 67, 68, 70, 71, 72, 74, 75, 77, 78, 80, 81, 82, 84, 86, 87, 88, 89 };
        private const int MaximumSecretTextLength = 4096;
        private const int MaximumSecretBytes = 64;
        private static readonly JsonSerializerSettings steamResponseJsonSettings = new JsonSerializerSettings
        {
            MaxDepth = 16,
            DateParseHandling = DateParseHandling.None
        };

        /// <summary>
        /// Remove steam guard from this account
        /// </summary>
        /// <param name="scheme">1 = Return to email codes, 2 = Remove completley</param>
        /// <returns></returns>
        public async Task<bool> DeactivateAuthenticator(int scheme = 1, CancellationToken cancellationToken = default)
        {
            LastAuthenticatorOperationError = null;
            if (String.IsNullOrWhiteSpace(RevocationCode))
            {
                LastAuthenticatorOperationError = "This account does not contain a Steam Guard recovery code, so Steam Guard cannot be removed.";
                return false;
            }
            if (scheme != 1 && scheme != 2)
            {
                LastAuthenticatorOperationError = "The requested Steam Guard removal method is invalid.";
                return false;
            }
            if (Session == null || Session.SteamID == 0 || String.IsNullOrWhiteSpace(Session.AccessToken))
            {
                LastAuthenticatorOperationError = "The saved Steam session has expired. Sign in again before removing Steam Guard.";
                return false;
            }

            try
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport();
                SteamProtocolResponse<CTwoFactor_RemoveAuthenticator_Response> response = await transport.SendAsync(
                    "ITwoFactorService",
                    "RemoveAuthenticator",
                    new CTwoFactor_RemoveAuthenticator_Request
                    {
                        RevocationCode = RevocationCode,
                        RevocationReason = 1,
                        SteamguardScheme = (uint)scheme
                    },
                    Session.AccessToken,
                    CTwoFactor_RemoveAuthenticator_Response.Parser,
                    cancellationToken: cancellationToken);

                if (response == null || response.Result != 1 || response.Body == null || !response.Body.Success)
                {
                    int result = response?.Result ?? 0;
                    SteamSessionFailureKind failureKind = SteamSessionFailureClassifier.ClassifyResult(result, expiredResultInvalidatesSession: true);
                    if (failureKind == SteamSessionFailureKind.InvalidSession)
                    {
                        throw new SteamSessionException(
                            SteamSessionFailureKind.InvalidSession,
                            "Steam rejected the saved account session while removing Steam Guard.",
                            result);
                    }
                    if (SteamSessionFailureClassifier.IsRateLimitedResult(result))
                        LastAuthenticatorOperationError = "Steam is rate limiting Steam Guard removal. Wait a while before trying again.";
                    else if (response != null && response.Result == 1 && response.Body != null && !response.Body.Success)
                        LastAuthenticatorOperationError = "Steam did not confirm Steam Guard removal. Please try again later.";
                    else if (response != null && !String.IsNullOrWhiteSpace(response.ErrorMessage))
                        LastAuthenticatorOperationError = "Steam could not remove Steam Guard: " + response.ErrorMessage;
                    else
                        LastAuthenticatorOperationError = "Steam could not remove Steam Guard (result " + (response == null ? 0 : response.Result) + ").";
                    SteamAuthDiagnostics.Log(new InvalidOperationException(LastAuthenticatorOperationError), "Steam Guard removal was rejected.");
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SteamSessionException)
            {
                throw;
            }
            catch (SteamWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new SteamSessionException(
                    SteamSessionFailureKind.InvalidSession,
                    "Steam rejected the saved account session while removing Steam Guard.",
                    0,
                    ex);
            }
            catch (Exception ex)
            {
                SteamAuthDiagnostics.Log(ex, "Steam Guard removal request failed.");
                LastAuthenticatorOperationError = "Steam Guard removal could not be completed. Check your connection and try again.";
                return false;
            }
        }

        public string GenerateSteamGuardCode()
        {
            return GenerateSteamGuardCodeForTime(TimeAligner.GetSteamTime());
        }

        public async Task<string> GenerateSteamGuardCodeAsync(CancellationToken cancellationToken = default)
        {
            return GenerateSteamGuardCodeForTime(await TimeAligner.GetSteamTimeAsync(cancellationToken));
        }

        public async Task<string> SignInViaQR(string idOfQR, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            if (Session == null || Session.SteamID == 0 || String.IsNullOrWhiteSpace(Session.AccessToken))
                throw new InvalidOperationException("The saved Steam session has expired. Log in again before approving a QR login.");
            if (!UInt64.TryParse(idOfQR, out ulong clientId) || clientId == 0)
                throw new ArgumentException("Steam provided an invalid QR login identifier.", nameof(idOfQR));
            if (String.IsNullOrWhiteSpace(SharedSecret))
                throw new InvalidOperationException("The saved authenticator is missing its shared secret. Re-import or re-link the account before approving a QR login.");

            byte[] sharedSecretBytes = DecodeSecret(this.SharedSecret, "shared authenticator secret");
            byte[] signatureData = new byte[18];
            
            // version (uint16 LE)
            signatureData[0] = 1;
            signatureData[1] = 0;

            // client_id (uint64 LE)
            byte[] clientBytes = BitConverter.GetBytes(clientId);
            Buffer.BlockCopy(clientBytes, 0, signatureData, 2, 8);

            // steamid (uint64 LE)
            byte[] steamBytes = BitConverter.GetBytes(this.Session.SteamID);
            Buffer.BlockCopy(steamBytes, 0, signatureData, 10, 8);

            using (var hmac = new System.Security.Cryptography.HMACSHA256(sharedSecretBytes))
            {
                byte[] signature = hmac.ComputeHash(signatureData);
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport();
                SteamProtocolResponse<CAuthentication_UpdateAuthSessionWithMobileConfirmation_Response> response = await transport.SendAsync(
                    "IAuthenticationService",
                    "UpdateAuthSessionWithMobileConfirmation",
                    new CAuthentication_UpdateAuthSessionWithMobileConfirmation_Request
                    {
                        Version = 1,
                        ClientId = clientId,
                        Steamid = Session.SteamID,
                        Signature = ByteString.CopyFrom(signature),
                        Confirm = true,
                        Persistence = ESessionPersistence.KEsessionPersistencePersistent
                    },
                    Session.AccessToken,
                    CAuthentication_UpdateAuthSessionWithMobileConfirmation_Response.Parser,
                    cancellationToken: cancellationToken);
                int result = response?.Result ?? 0;
                if (result != 1)
                {
                    string detail = response != null && !String.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? response.ErrorMessage
                        : "Steam rejected the QR login approval with result " + result + ".";
                    throw new SteamSessionException(
                        SteamSessionFailureClassifier.ClassifyResult(result),
                        detail,
                        result);
                }
                return result.ToString();
            }
        }

        public string GenerateSteamGuardCodeForTime(long time)
        {
            if (this.SharedSecret == null || this.SharedSecret.Length == 0)
            {
                return "";
            }

            byte[] sharedSecretArray = DecodeSecret(this.SharedSecret, "shared authenticator secret");
            byte[] timeArray = new byte[8];

            time /= 30L;

            for (int i = 8; i > 0; i--)
            {
                timeArray[i - 1] = (byte)time;
                time >>= 8;
            }

            byte[] hashedData;
            using (HMACSHA1 hmacGenerator = new HMACSHA1())
            {
                hmacGenerator.Key = sharedSecretArray;
                hashedData = hmacGenerator.ComputeHash(timeArray);
            }
            byte[] codeArray = new byte[5];
            byte b = (byte)(hashedData[19] & 0xF);
            int codePoint = (hashedData[b] & 0x7F) << 24 | (hashedData[b + 1] & 0xFF) << 16 | (hashedData[b + 2] & 0xFF) << 8 | (hashedData[b + 3] & 0xFF);

            for (int i = 0; i < 5; ++i)
            {
                codeArray[i] = steamGuardCodeTranslations[codePoint % steamGuardCodeTranslations.Length];
                codePoint /= steamGuardCodeTranslations.Length;
            }
            return Encoding.UTF8.GetString(codeArray);
        }

        public Confirmation[] FetchConfirmations()
        {
            string url = this.GenerateConfirmationURL();
            string response = SteamWeb.GETRequest(url, this.Session.GetCookies()).Result;
            return FetchConfirmationInternal(response);
        }

        public async Task<Confirmation[]> FetchConfirmationsAsync(CancellationToken cancellationToken = default)
        {
            string url = await GenerateConfirmationURLAsync("conf", cancellationToken);
            string response = await SteamWeb.GETRequest(url, this.Session.GetCookies(), cancellationToken);
            return FetchConfirmationInternal(response);
        }

        private Confirmation[] FetchConfirmationInternal(string response)
        {
            var confirmationsResponse = JsonConvert.DeserializeObject<ConfirmationsResponse>(response, steamResponseJsonSettings);

            if (confirmationsResponse == null)
                throw new InvalidOperationException("Steam returned an invalid confirmation response.");

            // Steam returns { success: false, needauth: true } for an expired or
            // rejected Community session. Check it before the general failure so
            // callers can refresh the access token and retry safely.
            if (confirmationsResponse.NeedAuthentication)
                throw new WGTokenInvalidException();

            if (!confirmationsResponse.Success)
                throw new Exception(String.IsNullOrWhiteSpace(confirmationsResponse.Message)
                    ? "Steam could not load confirmations."
                    : confirmationsResponse.Message);

            if (confirmationsResponse.Confirmations == null || confirmationsResponse.Confirmations.Length > 1000 ||
                confirmationsResponse.Confirmations.Any(confirmation => confirmation == null || confirmation.ID == 0 || confirmation.Key == 0))
                throw new InvalidOperationException("Steam returned an invalid confirmation list.");

            return confirmationsResponse.Confirmations;
        }

        /// <summary>
        /// Deprecated. Simply returns conf.Creator.
        /// </summary>
        /// <param name="conf"></param>
        /// <returns>The Creator field of conf</returns>
        public long GetConfirmationTradeOfferID(Confirmation conf)
        {
            if (conf.ConfType != Confirmation.EMobileConfirmationType.Trade)
                throw new ArgumentException("conf must be a trade confirmation.");

            return (long)conf.Creator;
        }

        public async Task<bool> AcceptMultipleConfirmations(Confirmation[] confs, CancellationToken cancellationToken = default)
        {
            ValidateConfirmations(confs);
            return await _sendMultiConfirmationAjax(confs, "allow", cancellationToken);
        }

        public async Task<bool> DenyMultipleConfirmations(Confirmation[] confs, CancellationToken cancellationToken = default)
        {
            ValidateConfirmations(confs);
            return await _sendMultiConfirmationAjax(confs, "cancel", cancellationToken);
        }

        public async Task<bool> AcceptConfirmation(Confirmation conf, CancellationToken cancellationToken = default)
        {
            ValidateConfirmation(conf);
            return await _sendConfirmationAjax(conf, "allow", cancellationToken);
        }

        public async Task<bool> DenyConfirmation(Confirmation conf, CancellationToken cancellationToken = default)
        {
            ValidateConfirmation(conf);
            return await _sendConfirmationAjax(conf, "cancel", cancellationToken);
        }

        private async Task<bool> _sendConfirmationAjax(Confirmation conf, string op, CancellationToken cancellationToken)
        {
            string url = APIEndpoints.COMMUNITY_BASE + "/mobileconf/ajaxop";
            // tag is different from op now
            string tag = op == "allow" ? "accept" : "reject";
            NameValueCollection queryParams = await GenerateConfirmationQueryParamsAsNVCAsync(tag, cancellationToken);
            string queryString = "?op=" + WebUtility.UrlEncode(op) + "&" + EncodeQueryParameters(queryParams);
            queryString += "&cid=" + conf.ID + "&ck=" + conf.Key;
            url += queryString;

            string response = await SteamWeb.GETRequest(url, this.Session.GetCookies(), cancellationToken);
            if (response == null) return false;

            try
            {
                SendConfirmationResponse confResponse = JsonConvert.DeserializeObject<SendConfirmationResponse>(response, steamResponseJsonSettings);
                if (confResponse?.NeedAuthentication == true)
                    throw new WGTokenInvalidException();
                return confResponse?.Success ?? false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private async Task<bool> _sendMultiConfirmationAjax(Confirmation[] confs, string op, CancellationToken cancellationToken)
        {
            string url = APIEndpoints.COMMUNITY_BASE + "/mobileconf/multiajaxop";
            // tag is different from op now
            string tag = op == "allow" ? "accept" : "reject";
            NameValueCollection body = await GenerateConfirmationQueryParamsAsNVCAsync(tag, cancellationToken);
            body.Add("op", op);
            foreach (var conf in confs)
            {
                body.Add("cid[]", conf.ID.ToString());
                body.Add("ck[]", conf.Key.ToString());
            }

            SteamWebResponse steamResponse = await SteamWeb.POSTRequestWithHeaders(
                url,
                this.Session.GetCookies(),
                body,
                new System.Collections.Generic.Dictionary<string, string> { ["Origin"] = APIEndpoints.COMMUNITY_BASE },
                cancellationToken: cancellationToken);
            string response = steamResponse?.Body;
            if (response == null) return false;

            try
            {
                SendConfirmationResponse confResponse = JsonConvert.DeserializeObject<SendConfirmationResponse>(response, steamResponseJsonSettings);
                if (confResponse?.NeedAuthentication == true)
                    throw new WGTokenInvalidException();
                return confResponse?.Success ?? false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public string GenerateConfirmationURL(string tag = "conf")
        {
            string endpoint = APIEndpoints.COMMUNITY_BASE + "/mobileconf/getlist?";
            string queryString = GenerateConfirmationQueryParams(tag);
            return endpoint + queryString;
        }

        public string GenerateConfirmationQueryParams(string tag)
        {
            if (String.IsNullOrEmpty(DeviceID))
                throw new ArgumentException("Device ID is not present");

            var queryParams = GenerateConfirmationQueryParamsAsNVC(tag);
            return EncodeQueryParameters(queryParams);
        }

        public NameValueCollection GenerateConfirmationQueryParamsAsNVC(string tag)
        {
            return GenerateConfirmationQueryParamsAsNVC(tag, TimeAligner.GetSteamTime());
        }

        private async Task<string> GenerateConfirmationURLAsync(string tag, CancellationToken cancellationToken)
        {
            NameValueCollection queryParams = await GenerateConfirmationQueryParamsAsNVCAsync(tag, cancellationToken);
            return APIEndpoints.COMMUNITY_BASE + "/mobileconf/getlist?" + EncodeQueryParameters(queryParams);
        }

        private async Task<NameValueCollection> GenerateConfirmationQueryParamsAsNVCAsync(string tag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long time = await TimeAligner.GetSteamTimeAsync(cancellationToken);
            return GenerateConfirmationQueryParamsAsNVC(tag, time);
        }

        private NameValueCollection GenerateConfirmationQueryParamsAsNVC(string tag, long time)
        {
            if (String.IsNullOrEmpty(DeviceID))
                throw new ArgumentException("Device ID is not present");
            if (Session == null || Session.SteamID == 0)
                throw new InvalidOperationException("A valid Steam session is required for confirmation requests.");

            var ret = new NameValueCollection();
            ret.Add("p", this.DeviceID);
            ret.Add("a", this.Session.SteamID.ToString());
            ret.Add("k", _generateConfirmationHashForTime(time, tag));
            ret.Add("t", time.ToString());
            ret.Add("m", "react");
            ret.Add("tag", tag);

            return ret;
        }

        private static string EncodeQueryParameters(NameValueCollection queryParams)
        {
            return string.Join("&", queryParams.AllKeys.Select(key =>
                WebUtility.UrlEncode(key) + "=" + WebUtility.UrlEncode(queryParams[key])));
        }

        private static void ValidateConfirmation(Confirmation confirmation)
        {
            if (confirmation == null || confirmation.ID == 0 || confirmation.Key == 0)
                throw new ArgumentException("A valid Steam confirmation is required.", nameof(confirmation));
        }

        private static void ValidateConfirmations(Confirmation[] confirmations)
        {
            if (confirmations == null || confirmations.Length == 0 || confirmations.Length > 1000)
                throw new ArgumentException("The confirmation list is invalid.", nameof(confirmations));
            foreach (Confirmation confirmation in confirmations)
                ValidateConfirmation(confirmation);
        }

        private static byte[] DecodeSecret(string value, string fieldName)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > MaximumSecretTextLength)
                throw new InvalidOperationException("The account contains an invalid " + fieldName + ".");

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(Regex.Unescape(value));
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("The account contains an invalid " + fieldName + ".", ex);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("The account contains an invalid " + fieldName + ".", ex);
            }

            if (decoded.Length == 0 || decoded.Length > MaximumSecretBytes)
                throw new InvalidOperationException("The account contains an invalid " + fieldName + ".");
            return decoded;
        }

        private string _generateConfirmationHashForTime(long time, string tag)
        {
            byte[] decode = DecodeSecret(this.IdentitySecret, "identity secret");
            int n2 = 8;
            if (tag != null)
            {
                if (tag.Length > 32)
                {
                    n2 = 8 + 32;
                }
                else
                {
                    n2 = 8 + tag.Length;
                }
            }
            byte[] array = new byte[n2];
            int n3 = 8;
            while (true)
            {
                int n4 = n3 - 1;
                if (n3 <= 0)
                {
                    break;
                }
                array[n4] = (byte)time;
                time >>= 8;
                n3 = n4;
            }
            if (tag != null)
            {
                Array.Copy(Encoding.UTF8.GetBytes(tag), 0, array, 8, n2 - 8);
            }

            using (HMACSHA1 hmacGenerator = new HMACSHA1())
            {
                hmacGenerator.Key = decode;
                byte[] hashedData = hmacGenerator.ComputeHash(array);
                return Convert.ToBase64String(hashedData, Base64FormattingOptions.None);
            }
        }

        public class WGTokenInvalidException : Exception
        {
            public WGTokenInvalidException() : base("Steam needs authentication for this account session.")
            {
            }
        }

        public class WGTokenExpiredException : Exception
        {
            public WGTokenExpiredException() : base("The Steam account session token expired.")
            {
            }
        }

        private class SendConfirmationResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("needauth")]
            public bool NeedAuthentication { get; set; }
        }

        private class ConfirmationDetailsResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("html")]
            public string HTML { get; set; }
        }
    }
}
