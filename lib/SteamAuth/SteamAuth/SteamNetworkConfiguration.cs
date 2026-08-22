using System;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace SteamAuth
{
    /// <summary>
    /// Owns the HTTP client used by the default Steam transports. Reconfiguring the
    /// proxy swaps clients atomically while allowing requests on the retired client
    /// to finish before it is disposed.
    /// </summary>
    public static class SteamNetworkConfiguration
    {
        private static ClientGeneration current = new ClientGeneration(CreateHttpClient(null));

        public static void ConfigureProxy(IWebProxy proxy)
        {
            ReplaceClient(CreateHttpClient(proxy));
        }

        public static void BlockSteamTraffic()
        {
            ReplaceClient(new HttpClient(new BlockedSteamHttpHandler())
            {
                Timeout = Timeout.InfiniteTimeSpan
            });
        }

        public static HttpClient CreateHttpClient(IWebProxy proxy)
        {
            return CreateHttpClient(proxy, false);
        }

        public static HttpClient CreateHttpClient(IWebProxy proxy, bool allowAutoRedirect)
        {
            return CreateHttpClient(proxy, allowAutoRedirect, true);
        }

        public static HttpClient CreateHttpClient(IWebProxy proxy, bool allowAutoRedirect, bool includeMobileUserAgent)
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = allowAutoRedirect,
                UseProxy = proxy != null,
                Proxy = proxy
            };
            HttpClient client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            if (includeMobileUserAgent)
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SteamWeb.MOBILE_APP_USER_AGENT);
            return client;
        }

        internal static HttpClientLease AcquireHttpClient()
        {
            while (true)
            {
                ClientGeneration generation = Volatile.Read(ref current);
                if (generation.TryAcquire())
                    return new HttpClientLease(generation);
            }
        }

        private static void ReplaceClient(HttpClient client)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));

            ClientGeneration replacement = new ClientGeneration(client);
            ClientGeneration previous = Interlocked.Exchange(ref current, replacement);
            previous.Release();
        }

        internal sealed class HttpClientLease : IDisposable
        {
            private ClientGeneration generation;

            internal HttpClientLease(ClientGeneration generation)
            {
                this.generation = generation;
                Client = generation.Client;
            }

            public HttpClient Client { get; }

            public void Dispose()
            {
                ClientGeneration released = Interlocked.Exchange(ref generation, null);
                released?.Release();
            }
        }

        internal sealed class ClientGeneration
        {
            private int references = 1;

            internal ClientGeneration(HttpClient client)
            {
                Client = client;
            }

            internal HttpClient Client { get; }

            internal bool TryAcquire()
            {
                int observed = Volatile.Read(ref references);
                while (observed > 0)
                {
                    int exchanged = Interlocked.CompareExchange(ref references, observed + 1, observed);
                    if (exchanged == observed)
                        return true;
                    observed = exchanged;
                }
                return false;
            }

            internal void Release()
            {
                if (Interlocked.Decrement(ref references) == 0)
                    Client.Dispose();
            }
        }

        private sealed class BlockedSteamHttpHandler : HttpMessageHandler
        {
            protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return System.Threading.Tasks.Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Steam networking is blocked because the saved proxy configuration is invalid."));
            }
        }
    }
}
