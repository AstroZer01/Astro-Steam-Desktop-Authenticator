using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamAuth;
using SteamKit2;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Steam_Desktop_Authenticator
{
    public sealed class ProxyConfiguration
    {
        private const string PasswordKeep = "keep";
        private const string PasswordReplace = "replace";
        private const string PasswordClear = "clear";

        private ProxyConfiguration(bool enabled, string scheme, string host, int port, string username, string password)
        {
            Enabled = enabled;
            Scheme = scheme;
            Host = host;
            Port = port;
            Username = username;
            Password = password;
        }

        public bool Enabled { get; }
        public string Scheme { get; }
        public string Host { get; }
        public int Port { get; }
        public string Username { get; }
        public string Password { get; }

        public static bool TryFromPayload(JObject payload, Manifest currentSettings, bool requireEndpoint, out ProxyConfiguration configuration, out string error)
        {
            configuration = null;
            error = null;
            if (payload == null)
            {
                error = "Proxy settings were not provided.";
                return false;
            }

            if (!HasTokenType(payload, "proxyEnabled", JTokenType.Boolean) ||
                !HasTokenType(payload, "proxyScheme", JTokenType.String) ||
                !HasTokenType(payload, "proxyHost", JTokenType.String) ||
                !HasTokenType(payload, "proxyPort", JTokenType.Integer, JTokenType.String) ||
                !HasTokenType(payload, "proxyUsername", JTokenType.String) ||
                !HasTokenType(payload, "proxyPasswordAction", JTokenType.String) ||
                !HasTokenType(payload, "proxyPassword", JTokenType.String))
            {
                error = "Proxy settings contained an invalid value type.";
                return false;
            }

            bool enabled = (bool?)payload["proxyEnabled"] ?? false;
            string scheme = NormalizeScheme((string)payload["proxyScheme"]);
            string host = NormalizeHost((string)payload["proxyHost"]);
            int port = 0;
            JToken portToken = payload["proxyPort"];
            if (portToken != null && portToken.Type != JTokenType.Null &&
                !String.IsNullOrWhiteSpace(portToken.ToString()) &&
                !Int32.TryParse(portToken.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            {
                error = "Enter a proxy port between 1 and 65535.";
                return false;
            }
            string username = ((string)payload["proxyUsername"] ?? String.Empty).Trim();
            string passwordAction = ((string)payload["proxyPasswordAction"] ?? PasswordKeep).Trim().ToLowerInvariant();
            if (scheme.Length > 16 || host.Length > 256 || username.Length > 256 || passwordAction.Length > 16)
            {
                error = "Proxy settings are too long.";
                return false;
            }

            string password;
            switch (passwordAction)
            {
                case PasswordKeep:
                    password = currentSettings?.ProxyPassword ?? String.Empty;
                    break;
                case PasswordReplace:
                    password = (string)payload["proxyPassword"] ?? String.Empty;
                    if (password.Length > 4096)
                    {
                        error = "The proxy password is too long.";
                        return false;
                    }
                    break;
                case PasswordClear:
                    password = String.Empty;
                    break;
                default:
                    error = "The proxy password action is invalid.";
                    return false;
            }

            if (password.Length > 4096)
            {
                error = "The proxy password is too long.";
                return false;
            }

            if (requireEndpoint || enabled)
            {
                if (!IsSupportedScheme(scheme))
                {
                    error = "Select HTTP or HTTPS for the proxy type.";
                    return false;
                }
                if (String.IsNullOrWhiteSpace(host))
                {
                    error = "Enter a proxy host.";
                    return false;
                }
                if (port < 1 || port > 65535)
                {
                    error = "Enter a proxy port between 1 and 65535.";
                    return false;
                }
                if (!IsValidHost(host))
                {
                    error = "Enter a valid proxy hostname or IP address without a URL scheme or path.";
                    return false;
                }
            }

            if ((requireEndpoint || enabled) && !String.IsNullOrEmpty(password) && String.IsNullOrEmpty(username))
            {
                error = "Enter a proxy username when using a proxy password.";
                return false;
            }

            configuration = new ProxyConfiguration(enabled, scheme, host, port, username, password);
            return true;
        }

        private static bool HasTokenType(JObject payload, string propertyName, params JTokenType[] expectedTypes)
        {
            JToken token = payload[propertyName];
            if (token == null || token.Type == JTokenType.Null)
                return true;

            foreach (JTokenType expectedType in expectedTypes)
            {
                if (token.Type == expectedType)
                    return true;
            }

            return false;
        }

        public static bool TryFromManifest(Manifest manifest, out ProxyConfiguration configuration, out string error)
        {
            if (manifest == null)
            {
                configuration = null;
                error = "Application settings are unavailable.";
                return false;
            }

            JObject payload = new JObject
            {
                ["proxyEnabled"] = manifest.ProxyEnabled,
                ["proxyScheme"] = manifest.ProxyScheme,
                ["proxyHost"] = manifest.ProxyHost,
                ["proxyPort"] = manifest.ProxyPort,
                ["proxyUsername"] = manifest.ProxyUsername,
                ["proxyPasswordAction"] = PasswordKeep
            };
            return TryFromPayload(payload, manifest, manifest.ProxyEnabled, out configuration, out error);
        }

        public IWebProxy CreateWebProxy()
        {
            if (String.IsNullOrWhiteSpace(Host) || Port < 1)
                return null;

            // HTTP and HTTPS proxies support HTTPS Steam endpoints through CONNECT tunneling.
            WebProxy proxy = new WebProxy(new UriBuilder(Scheme, FormatUriHost(Host), Port).Uri)
            {
                BypassProxyOnLocal = false,
                UseDefaultCredentials = false
            };
            if (!String.IsNullOrEmpty(Username))
                proxy.Credentials = new NetworkCredential(Username, Password ?? String.Empty);
            return proxy;
        }

        private static string NormalizeHost(string value)
        {
            string host = (value ?? String.Empty).Trim();
            if (host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']')
                host = host.Substring(1, host.Length - 2);
            return host;
        }

        private static string NormalizeScheme(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? Uri.UriSchemeHttp : value.Trim().ToLowerInvariant();
        }

        private static bool IsSupportedScheme(string scheme)
        {
            return String.Equals(scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
                String.Equals(scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        }

        private static string FormatUriHost(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress address) && address.AddressFamily == AddressFamily.InterNetworkV6)
                return "[" + address + "]";
            return host;
        }

        private static bool IsValidHost(string host)
        {
            if (host.IndexOf("://", StringComparison.Ordinal) >= 0 || host.IndexOf('/') >= 0 || host.IndexOf('\\') >= 0)
                return false;
            return Uri.CheckHostName(host) != UriHostNameType.Unknown;
        }
    }

    public sealed class ProxyTestResult
    {
        public bool Succeeded { get; set; }
        public string Message { get; set; }
        public string ExitIp { get; set; }
    }

    public static class ProxyService
    {
        private const string SteamServerInfoUrl = "https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/";
        private const string ExitIpUrl = "https://api.ipify.org";
        private const int MaximumProxyTestResponseBytes = 1024 * 1024;
        private static readonly object configurationLock = new object();
        private static ProxyConfiguration activeConfiguration;
        private static bool steamTrafficBlocked;

        public static bool ApplySavedConfiguration(Manifest manifest, out string error)
        {
            if (!ProxyConfiguration.TryFromManifest(manifest, out ProxyConfiguration configuration, out error))
            {
                lock (configurationLock)
                {
                    activeConfiguration = null;
                    steamTrafficBlocked = true;
                    SteamNetworkConfiguration.BlockSteamTraffic();
                }
                return false;
            }

            Apply(configuration);
            return true;
        }

        public static void Apply(ProxyConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            lock (configurationLock)
            {
                SteamNetworkConfiguration.ConfigureProxy(configuration.Enabled ? configuration.CreateWebProxy() : null);
                activeConfiguration = configuration;
                steamTrafficBlocked = false;
            }
        }

        public static HttpClient CreateActiveHttpClient()
        {
            lock (configurationLock)
            {
                if (steamTrafficBlocked)
                    throw new InvalidOperationException("Steam networking is blocked until the proxy settings are corrected.");
                return SteamNetworkConfiguration.CreateHttpClient(
                    activeConfiguration != null && activeConfiguration.Enabled
                        ? activeConfiguration.CreateWebProxy()
                        : null);
            }
        }

        public static SteamConfiguration CreateSteamKitConfiguration()
        {
            ProxyConfiguration snapshot;
            lock (configurationLock)
            {
                if (steamTrafficBlocked)
                    throw new InvalidOperationException("Steam networking is blocked until the proxy settings are corrected.");
                snapshot = activeConfiguration;
            }

            return SteamConfiguration.Create(builder => builder
                .WithProtocolTypes(ProtocolTypes.WebSocket)
                .WithHttpClientFactory(_ => SteamNetworkConfiguration.CreateHttpClient(
                    snapshot != null && snapshot.Enabled ? snapshot.CreateWebProxy() : null,
                    true,
                    false)));
        }

        public static async Task<ProxyTestResult> TestAsync(ProxyConfiguration configuration, CancellationToken cancellationToken)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (HttpClient client = SteamNetworkConfiguration.CreateHttpClient(configuration.CreateWebProxy()))
            {
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    using (HttpRequestMessage steamRequest = CreateProxyTestRequest(SteamServerInfoUrl))
                    using (HttpResponseMessage steamResponse = await client.SendAsync(
                        steamRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutSource.Token))
                    {
                        if (steamResponse.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                            return Failure("Proxy authentication failed. Check the username and password.");
                        if (!steamResponse.IsSuccessStatusCode)
                            return Failure("The proxy connected, but Steam returned HTTP " + (int)steamResponse.StatusCode + ".");

                        string body = await ReadResponseBodyWithLimitAsync(steamResponse.Content, timeoutSource.Token);
                        try
                        {
                            JObject steamPayload;
                            using (StringReader stringReader = new StringReader(body))
                            using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                            {
                                steamPayload = JObject.Load(jsonReader);
                            }
                            JToken serverTime = steamPayload.GetValue("servertime", StringComparison.OrdinalIgnoreCase);
                            if (serverTime == null ||
                                (serverTime.Type != JTokenType.Integer && serverTime.Type != JTokenType.String))
                                return Failure("The proxy connected, but Steam returned an unexpected response.");
                            string serverTimeText = serverTime.Type == JTokenType.String
                                ? serverTime.Value<string>()
                                : serverTime.ToString();
                            if (!Int64.TryParse(serverTimeText, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedServerTime) ||
                                parsedServerTime <= 0)
                                return Failure("The proxy connected, but Steam returned an unexpected response.");
                        }
                        catch
                        {
                            return Failure("The proxy connected, but Steam returned an unexpected response.");
                        }
                    }

                    string exitIp = null;
                    try
                    {
                        using (HttpRequestMessage exitIpRequest = CreateProxyTestRequest(ExitIpUrl))
                        using (HttpResponseMessage exitIpResponse = await client.SendAsync(
                            exitIpRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeoutSource.Token))
                        {
                            if (exitIpResponse.IsSuccessStatusCode)
                            {
                                string candidate = (await ReadResponseBodyWithLimitAsync(exitIpResponse.Content, timeoutSource.Token)).Trim();
                                if (IPAddress.TryParse(candidate, out IPAddress address) && address.AddressFamily == AddressFamily.InterNetwork)
                                    exitIp = candidate;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SocketException)
                    {
                        // Steam reachability is authoritative; exit-IP display is best effort.
                    }

                    return new ProxyTestResult
                    {
                        Succeeded = true,
                        ExitIp = exitIp,
                        Message = exitIp == null
                            ? "Proxy connected. Steam is reachable."
                            : "Proxy connected. Steam is reachable via " + exitIp + "."
                    };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Failure("Proxy test timed out before Steam's API replied. Check the host, port, and whether the proxy permits api.steampowered.com.");
                }
                catch (InvalidDataException ex)
                {
                    SteamAuthDiagnostics.Log(ex, "The proxy returned an invalid or oversized response during testing.");
                    return Failure("The proxy returned an invalid response. Use a proxy that permits the Steam API endpoint.");
                }
                catch (DecoderFallbackException ex)
                {
                    SteamAuthDiagnostics.Log(ex, "The proxy returned invalid text during testing.");
                    return Failure("The proxy returned an invalid response. Use a proxy that permits the Steam API endpoint.");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    return Failure("Proxy authentication failed. Check the username and password.");
                }
                catch (HttpRequestException ex) when (TryGetProxyTunnelStatusCode(ex, out int statusCode))
                {
                    return Failure("The proxy connected, but rejected Steam's API endpoint (HTTP " + statusCode + "). Use a proxy that permits api.steampowered.com.");
                }
                catch (HttpRequestException)
                {
                    return Failure("Could not connect to Steam through this proxy. Check the proxy type, host, port, and credentials.");
                }
                catch (SocketException)
                {
                    return Failure("Could not resolve or connect to this proxy host.");
                }
            }
        }

        private static ProxyTestResult Failure(string message)
        {
            return new ProxyTestResult { Succeeded = false, Message = message };
        }

        private static HttpRequestMessage CreateProxyTestRequest(string requestUri)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, requestUri)
            {
                // Some proxy gateways do not reliably pass HTTP/2 traffic through a CONNECT
                // tunnel. Steam's endpoint supports HTTP/1.1, which is the most compatible
                // protocol for checking a user-supplied proxy endpoint.
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.ConnectionClose = true;
            return request;
        }

        private static async Task<string> ReadResponseBodyWithLimitAsync(HttpContent content, CancellationToken cancellationToken)
        {
            if (content == null)
                throw new InvalidDataException("The proxy test returned an empty response.");
            if (content.Headers.ContentLength > MaximumProxyTestResponseBytes)
                throw new InvalidDataException("The proxy test returned an oversized response.");

            using (Stream responseStream = await content.ReadAsStreamAsync())
            using (MemoryStream responseBody = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                int totalBytes = 0;
                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    if (bytesRead > MaximumProxyTestResponseBytes - totalBytes)
                        throw new InvalidDataException("The proxy test returned an oversized response.");
                    responseBody.Write(buffer, 0, bytesRead);
                    totalBytes += bytesRead;
                }

                return new UTF8Encoding(false, true).GetString(responseBody.ToArray());
            }
        }

        private static bool TryGetProxyTunnelStatusCode(HttpRequestException exception, out int statusCode)
        {
            statusCode = 0;
            string message = exception?.Message;
            if (String.IsNullOrEmpty(message) ||
                message.IndexOf("proxy tunnel request", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            const string statusMarker = "status code '";
            int statusStart = message.IndexOf(statusMarker, StringComparison.OrdinalIgnoreCase);
            if (statusStart < 0)
                return false;

            statusStart += statusMarker.Length;
            int statusEnd = message.IndexOf('\'', statusStart);
            return statusEnd > statusStart && Int32.TryParse(
                message.Substring(statusStart, statusEnd - statusStart),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out statusCode);
        }
    }
}
