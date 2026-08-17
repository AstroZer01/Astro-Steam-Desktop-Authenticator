using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SteamAuth
{
    /// <summary>
    /// A typed Steam Web API response. Steam reports the operation result in headers,
    /// while the response payload is protobuf encoded.
    /// </summary>
    public sealed class SteamProtocolResponse<TResponse>
        where TResponse : class, IMessage<TResponse>
    {
        public int Result { get; set; }
        public string ErrorMessage { get; set; }
        public TResponse Body { get; set; }
    }

    /// <summary>
    /// Mockable boundary for the authenticated protobuf Steam Web API.
    /// </summary>
    public interface IAuthenticatorProtocolTransport
    {
        Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
            string service,
            string method,
            TRequest request,
            string accessToken,
            MessageParser<TResponse> responseParser,
            SteamProtocolRequestMethod requestMethod = SteamProtocolRequestMethod.Post,
            CancellationToken cancellationToken = default)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>;
    }

    public enum SteamProtocolRequestMethod
    {
        Get,
        Post
    }

    /// <summary>
    /// Sends Steam's current typed/protobuf Web API requests.
    /// </summary>
    public sealed class SteamProtobufAuthenticatorTransport : IAuthenticatorProtocolTransport
    {
        public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
        private static readonly HttpClient sharedHttpClient = CreateHttpClient();
        private readonly HttpClient httpClient;
        private readonly TimeSpan requestTimeout;

        public SteamProtobufAuthenticatorTransport()
            : this(sharedHttpClient, DefaultRequestTimeout)
        {
        }

        /// <summary>
        /// Creates a transport using the supplied client. This is primarily useful for
        /// deterministic protocol tests; ownership of the client remains with the caller.
        /// </summary>
        public SteamProtobufAuthenticatorTransport(HttpClient httpClient)
            : this(httpClient, DefaultRequestTimeout)
        {
        }

        public SteamProtobufAuthenticatorTransport(HttpClient httpClient, TimeSpan requestTimeout)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (requestTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(requestTimeout));
            this.requestTimeout = requestTimeout;
        }

        public async Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
            string service,
            string method,
            TRequest request,
            string accessToken,
            MessageParser<TResponse> responseParser,
            SteamProtocolRequestMethod requestMethod = SteamProtocolRequestMethod.Post,
            CancellationToken cancellationToken = default)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            if (String.IsNullOrWhiteSpace(service))
                throw new ArgumentException("A Steam service is required.", nameof(service));
            if (String.IsNullOrWhiteSpace(method))
                throw new ArgumentException("A Steam method is required.", nameof(method));
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (responseParser == null)
                throw new ArgumentNullException(nameof(responseParser));

            string url = "https://api.steampowered.com/" + service + "/" + method + "/v1";
            string encodedRequest = Convert.ToBase64String(request.ToByteArray());
            if (requestMethod == SteamProtocolRequestMethod.Get && !String.IsNullOrWhiteSpace(accessToken))
            {
                throw new ArgumentException(
                    "Authenticated Steam protocol requests must use POST so the access token is not placed in the URL.",
                    nameof(requestMethod));
            }

            bool useGet = requestMethod == SteamProtocolRequestMethod.Get;
            using (HttpRequestMessage httpRequest = new HttpRequestMessage(useGet ? HttpMethod.Get : HttpMethod.Post, url))
            {
                if (useGet)
                {
                    var query = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("input_protobuf_encoded", encodedRequest)
                    };
                    httpRequest.RequestUri = new Uri(url + "?" + String.Join("&", query.ConvertAll(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value))));
                }
                else
                {
                    List<KeyValuePair<string, string>> formValues = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("input_protobuf_encoded", encodedRequest)
                    };
                    if (!String.IsNullOrWhiteSpace(accessToken))
                        formValues.Add(new KeyValuePair<string, string>("access_token", accessToken));
                    httpRequest.Content = new FormUrlEncodedContent(formValues);
                }

                using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutSource.CancelAfter(requestTimeout);
                    HttpResponseMessage response;
                    try
                    {
                        response = await httpClient.SendAsync(httpRequest, timeoutSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("The Steam request timed out.");
                    }

                    using (response)
                    {
                    int result = GetHeaderResult(response);
                    string errorMessage = GetHeaderValue(response, "X-error_message");

                    if (!response.IsSuccessStatusCode)
                    {
                        if (result == 0 && (int)response.StatusCode == 429)
                        {
                            return new SteamProtocolResponse<TResponse>
                            {
                                Result = 84,
                                ErrorMessage = "Steam is rate limiting requests. Wait a while before trying again.",
                                Body = null
                            };
                        }
                        if (result == 0)
                        {
                            string detail = String.IsNullOrWhiteSpace(errorMessage)
                                ? "Steam returned HTTP " + (int)response.StatusCode + " (" + response.ReasonPhrase + ")."
                                : errorMessage;
                            throw new SteamWebRequestException(detail, response.StatusCode, CopyHeaders(response));
                        }

                        return new SteamProtocolResponse<TResponse>
                        {
                            Result = result,
                            ErrorMessage = errorMessage,
                            Body = null
                        };
                    }

                    byte[] responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    TResponse body = responseBytes.Length == 0 ? responseParser.ParseFrom(ByteString.Empty) : responseParser.ParseFrom(responseBytes);
                    return new SteamProtocolResponse<TResponse>
                    {
                        Result = result,
                        ErrorMessage = errorMessage,
                        Body = body
                    };
                    }
                }
            }
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient(new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false
            });
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SteamWeb.MOBILE_APP_USER_AGENT);
            return client;
        }

        private static int GetHeaderResult(HttpResponseMessage response)
        {
            string value = GetHeaderValue(response, "X-eresult");
            return Int32.TryParse(value, out int result)
                ? result
                : response.IsSuccessStatusCode ? 1 : 0;
        }

        private static string GetHeaderValue(HttpResponseMessage response, string name)
        {
            return response.Headers.TryGetValues(name, out IEnumerable<string> values)
                ? String.Join(", ", values)
                : null;
        }

        private static WebHeaderCollection CopyHeaders(HttpResponseMessage response)
        {
            WebHeaderCollection headers = new WebHeaderCollection();
            AddHeaders(headers, response.Headers);
            if (response.Content != null)
                AddHeaders(headers, response.Content.Headers);
            return headers;
        }

        private static void AddHeaders(WebHeaderCollection destination, IEnumerable<KeyValuePair<string, IEnumerable<string>>> source)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in source)
            {
                foreach (string value in header.Value)
                {
                    try
                    {
                        destination.Add(header.Key, value);
                    }
                    catch (ArgumentException)
                    {
                        // WebHeaderCollection rejects restricted headers on some targets.
                    }
                }
            }
        }
    }
}
