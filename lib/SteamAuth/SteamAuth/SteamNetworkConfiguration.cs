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
        public static readonly TimeSpan DefaultPooledConnectionLifetime = TimeSpan.FromMinutes(2);
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
            HttpMessageHandler handler = CreateHttpMessageHandler(proxy, allowAutoRedirect);
            HttpClient client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            if (includeMobileUserAgent)
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SteamWeb.MOBILE_APP_USER_AGENT);
            return client;
        }

        internal static HttpMessageHandler CreateHttpMessageHandler(IWebProxy proxy, bool allowAutoRedirect)
        {
#if NET5_0_OR_GREATER
            return new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = allowAutoRedirect,
                UseProxy = proxy != null,
                Proxy = proxy,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = DefaultPooledConnectionLifetime
            };
#else
            HttpMessageHandler socketsHandler = TryCreateSocketsHttpHandler(proxy, allowAutoRedirect);
            if (socketsHandler != null)
                return socketsHandler;

            return new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = allowAutoRedirect,
                UseProxy = proxy != null,
                Proxy = proxy,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
#endif
        }

#if !NET5_0_OR_GREATER
        private static HttpMessageHandler TryCreateSocketsHttpHandler(IWebProxy proxy, bool allowAutoRedirect)
        {
            Type handlerType = typeof(HttpClientHandler).Assembly.GetType("System.Net.Http.SocketsHttpHandler", false);
            if (handlerType == null)
                return null;

            HttpMessageHandler handler = null;
            try
            {
                handler = Activator.CreateInstance(handlerType) as HttpMessageHandler;
                if (handler == null)
                    return null;

                DecompressionMethods decompression = Enum.TryParse("All", out DecompressionMethods allDecompression)
                    ? allDecompression
                    : DecompressionMethods.GZip | DecompressionMethods.Deflate;

                if (!TrySetProperty(handlerType, handler, "UseCookies", false) ||
                    !TrySetProperty(handlerType, handler, "AllowAutoRedirect", allowAutoRedirect) ||
                    !TrySetProperty(handlerType, handler, "UseProxy", proxy != null) ||
                    !TrySetProperty(handlerType, handler, "Proxy", proxy) ||
                    !TrySetProperty(handlerType, handler, "AutomaticDecompression", decompression) ||
                    !TrySetProperty(handlerType, handler, "PooledConnectionLifetime", DefaultPooledConnectionLifetime))
                {
                    handler.Dispose();
                    return null;
                }

                return handler;
            }
            catch
            {
                handler?.Dispose();
                return null;
            }
        }

        private static bool TrySetProperty(Type targetType, object instance, string propertyName, object value)
        {
            System.Reflection.PropertyInfo property = targetType.GetProperty(propertyName);
            if (property == null || !property.CanWrite)
                return false;

            property.SetValue(instance, value);
            return true;
        }
#endif

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
