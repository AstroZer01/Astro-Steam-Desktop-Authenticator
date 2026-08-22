using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamAuth
{
    public class SteamWebResponse
    {
        public string Body { get; set; }
        public WebHeaderCollection Headers { get; set; }
    }

    public sealed class SteamWebRequestException : HttpRequestException
    {
#if NET5_0_OR_GREATER
        public SteamWebRequestException(string message, HttpStatusCode statusCode, WebHeaderCollection headers)
            : base(message, null, statusCode)
        {
            Headers = headers;
        }
#else
        public SteamWebRequestException(string message, HttpStatusCode statusCode, WebHeaderCollection headers)
            : base(message)
        {
            StatusCode = statusCode;
            Headers = headers;
        }

        // HttpRequestException did not expose StatusCode on netstandard2.0.
        public HttpStatusCode StatusCode { get; }
#endif
        public WebHeaderCollection Headers { get; }
    }

    public interface ISteamWebTransport
    {
        Task<SteamWebResponse> SendAsync(HttpMethod method, Uri uri, CookieContainer cookies, HttpContent content,
            IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken,
            bool followRedirects = true);
    }

    public sealed class HttpClientSteamWebTransport : ISteamWebTransport
    {
        private const int MaximumRedirects = 5;
        private readonly HttpClient httpClient;

        public HttpClientSteamWebTransport()
        {
        }

        public HttpClientSteamWebTransport(HttpClient httpClient)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<SteamWebResponse> SendAsync(HttpMethod method, Uri uri, CookieContainer cookies, HttpContent content,
            IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken,
            bool followRedirects = true)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                content?.Dispose();
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
            if (!IsHttpUri(uri))
            {
                content?.Dispose();
                throw new ArgumentException("A valid Steam URL is required.", nameof(uri));
            }

            byte[] requestBody;
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> contentHeaders;
            try
            {
                requestBody = content == null ? null : await content.ReadAsByteArrayAsync().ConfigureAwait(false);
                contentHeaders = content == null
                    ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
                    : content.Headers.ToArray();
            }
            finally
            {
                content?.Dispose();
            }

            using (SteamNetworkConfiguration.HttpClientLease clientLease = httpClient == null
                ? SteamNetworkConfiguration.AcquireHttpClient()
                : null)
            using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                HttpClient requestClient = httpClient ?? clientLease.Client;
                timeoutSource.CancelAfter(timeout);
                Uri requestUri = uri;
                HttpMethod requestMethod = method;
                for (int redirectCount = 0; ; redirectCount++)
                {
                    HttpResponseMessage response;
                    using (HttpRequestMessage request = CreateRequest(
                        requestMethod,
                        requestUri,
                        cookies,
                        requestMethod == HttpMethod.Get ? null : requestBody,
                        contentHeaders,
                        headers,
                        IsSameOrigin(uri, requestUri)))
                    {
                        try
                        {
                            response = await requestClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException("The Steam request timed out.");
                        }
                    }

                    using (response)
                    {
                        StoreResponseCookies(response, requestUri, cookies);
                        if (IsRedirect(response.StatusCode) && !followRedirects)
                        {
                            return new SteamWebResponse
                            {
                                Body = response.Content == null ? String.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                                Headers = CopyHeaders(response)
                            };
                        }

                        if (IsRedirect(response.StatusCode) && response.Headers.Location != null)
                        {
                            if (redirectCount >= MaximumRedirects)
                                throw new HttpRequestException("Steam redirected the request too many times.");
                            if (!Uri.TryCreate(requestUri, response.Headers.Location, out Uri redirectedUri) || !IsHttpUri(redirectedUri))
                                throw new HttpRequestException("Steam returned an invalid redirect location.");
                            if (requestUri.Scheme == Uri.UriSchemeHttps && redirectedUri.Scheme != Uri.UriSchemeHttps)
                                throw new HttpRequestException("Steam redirected the request to an insecure location.");

                            bool preservesMethod = (int)response.StatusCode == 307 || (int)response.StatusCode == 308;
                            if (!preservesMethod && requestMethod != HttpMethod.Get)
                                requestMethod = HttpMethod.Get;
                            requestUri = redirectedUri;
                            continue;
                        }

                        if (!response.IsSuccessStatusCode)
                            throw new SteamWebRequestException(
                                "Steam returned HTTP " + (int)response.StatusCode + " (" + response.ReasonPhrase + ").",
                                response.StatusCode,
                                CopyHeaders(response));

                        return new SteamWebResponse
                        {
                            Body = response.Content == null ? String.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                            Headers = CopyHeaders(response)
                        };
                    }
                }
            }
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, CookieContainer cookies, byte[] requestBody,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> contentHeaders, IReadOnlyDictionary<string, string> headers, bool includeCallerHeaders)
        {
            HttpRequestMessage request = new HttpRequestMessage(method, uri);
            if (requestBody != null)
            {
                ByteArrayContent body = new ByteArrayContent(requestBody);
                foreach (KeyValuePair<string, IEnumerable<string>> header in contentHeaders)
                    body.Headers.TryAddWithoutValidation(header.Key, header.Value);
                request.Content = body;
            }

            string cookieHeader = cookies?.GetCookieHeader(uri);
            if (!String.IsNullOrWhiteSpace(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            if (includeCallerHeaders && headers != null)
            {
                foreach (KeyValuePair<string, string> header in headers)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            else
            {
                // Redirects must keep Steam's required mobile identity without
                // forwarding caller-supplied headers to another origin.
                request.Headers.TryAddWithoutValidation("User-Agent", SteamWeb.MOBILE_APP_USER_AGENT);
            }
            return request;
        }

        private static bool IsHttpUri(Uri uri)
        {
            return uri != null &&
                (String.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSameOrigin(Uri first, Uri second)
        {
            return first != null && second != null &&
                String.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
                first.Port == second.Port;
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            int status = (int)statusCode;
            return status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
        }

        private static void StoreResponseCookies(HttpResponseMessage response, Uri uri, CookieContainer cookies)
        {
            if (cookies == null || !response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> cookieValues))
                return;
            foreach (string cookie in cookieValues)
            {
                try { cookies.SetCookies(uri, cookie); }
                catch (CookieException) { }
            }
        }

        private static WebHeaderCollection CopyHeaders(HttpResponseMessage response)
        {
            WebHeaderCollection headers = new WebHeaderCollection();
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> contentHeaders = response.Content == null
                ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
                : response.Content.Headers;
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(contentHeaders))
            {
                foreach (string value in header.Value)
                {
                    try
                    {
                        headers.Add(header.Key, value);
                    }
                    catch (ArgumentException)
                    {
                        // WebHeaderCollection rejects restricted response headers on some targets.
                    }
                }
            }
            return headers;
        }
    }

    public static class SteamWeb
    {
        public const string MOBILE_APP_USER_AGENT = "Dalvik/2.1.0 (Linux; U; Android 9; Valve Steam App Version/3)";
        public static readonly TimeSpan GetTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(20);

        // The production transport is shared; tests can inject a deterministic replacement.
        public static ISteamWebTransport Transport { get; set; } = new HttpClientSteamWebTransport();

        public static async Task<string> GETRequest(string url, CookieContainer cookies, CancellationToken cancellationToken = default)
        {
            SteamWebResponse response = await GETRequestWithHeaders(url, cookies, cancellationToken).ConfigureAwait(false);
            return response.Body;
        }

        public static Task<SteamWebResponse> GETRequestWithHeaders(string url, CookieContainer cookies, CancellationToken cancellationToken = default)
        {
            return SendAsync(HttpMethod.Get, url, cookies, null, null, GetTimeout, cancellationToken);
        }

        public static async Task<string> POSTRequest(string url, CookieContainer cookies, NameValueCollection body, string headerToReturn = null, CancellationToken cancellationToken = default)
        {
            SteamWebResponse response = await POSTRequestWithHeaders(
                url,
                cookies,
                body,
                null,
                cancellationToken,
                followRedirects: String.IsNullOrEmpty(headerToReturn)).ConfigureAwait(false);
            return !String.IsNullOrEmpty(headerToReturn) ? response.Headers?[headerToReturn] : response.Body;
        }

        public static Task<SteamWebResponse> POSTRequestWithHeaders(string url, CookieContainer cookies, NameValueCollection body,
            IReadOnlyDictionary<string, string> headers = null, CancellationToken cancellationToken = default,
            bool followRedirects = true)
        {
            NameValueCollection values = body ?? new NameValueCollection();
            IEnumerable<KeyValuePair<string, string>> formValues = values.AllKeys.Where(key => key != null)
                .SelectMany(key => (values.GetValues(key) ?? Array.Empty<string>()).Select(value => new KeyValuePair<string, string>(key, value ?? String.Empty)));
            return SendAsync(HttpMethod.Post, url, cookies, new FormUrlEncodedContent(formValues), headers, PostTimeout, cancellationToken, followRedirects);
        }

        private static Task<SteamWebResponse> SendAsync(HttpMethod method, string url, CookieContainer cookies, HttpContent content,
            IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken,
            bool followRedirects = true)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException("A valid Steam URL is required.", nameof(url));
            Dictionary<string, string> requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = MOBILE_APP_USER_AGENT
            };
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> header in headers)
                    requestHeaders[header.Key] = header.Value;
            }
            return Transport.SendAsync(method, uri, cookies, content, requestHeaders, timeout, cancellationToken, followRedirects);
        }
    }
}
