using Steam_Desktop_Authenticator;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class HttpTransportDefaultsTests
    {
        [Fact]
        public void SteamHttpHandler_EnablesDecompressionAndFiniteConnectionLifetimeWithoutProxy()
        {
            using (HttpMessageHandler handler = SteamNetworkConfiguration.CreateHttpMessageHandler(null, allowAutoRedirect: false))
            {
                SocketsHttpHandler socketsHandler = Assert.IsType<SocketsHttpHandler>(handler);
                Assert.False(socketsHandler.UseCookies);
                Assert.False(socketsHandler.AllowAutoRedirect);
                Assert.False(socketsHandler.UseProxy);
                Assert.Null(socketsHandler.Proxy);
                Assert.Equal(DecompressionMethods.All, socketsHandler.AutomaticDecompression);
                Assert.Equal(SteamNetworkConfiguration.DefaultPooledConnectionLifetime, socketsHandler.PooledConnectionLifetime);
                Assert.True(socketsHandler.PooledConnectionLifetime > TimeSpan.Zero);
                Assert.NotEqual(Timeout.InfiniteTimeSpan, socketsHandler.PooledConnectionLifetime);
                Assert.True(socketsHandler.PooledConnectionLifetime <= TimeSpan.FromMinutes(15));
            }
        }

        [Fact]
        public void SteamHttpHandler_PreservesProxyAndRedirectSettings()
        {
            WebProxy proxy = new WebProxy(new Uri("http://127.0.0.1:8080"));
            using (HttpMessageHandler handler = SteamNetworkConfiguration.CreateHttpMessageHandler(proxy, allowAutoRedirect: true))
            {
                SocketsHttpHandler socketsHandler = Assert.IsType<SocketsHttpHandler>(handler);
                Assert.False(socketsHandler.UseCookies);
                Assert.True(socketsHandler.AllowAutoRedirect);
                Assert.True(socketsHandler.UseProxy);
                Assert.Same(proxy, socketsHandler.Proxy);
                Assert.Equal(DecompressionMethods.All, socketsHandler.AutomaticDecompression);
                Assert.Equal(SteamNetworkConfiguration.DefaultPooledConnectionLifetime, socketsHandler.PooledConnectionLifetime);
            }
        }

        [Fact]
        public void SteamHttpClient_PreservesInfiniteTimeoutAndOptionalMobileUserAgent()
        {
            using (HttpClient withUserAgent = SteamNetworkConfiguration.CreateHttpClient(null, allowAutoRedirect: false, includeMobileUserAgent: true))
            using (HttpClient withoutUserAgent = SteamNetworkConfiguration.CreateHttpClient(null, allowAutoRedirect: true, includeMobileUserAgent: false))
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, withUserAgent.Timeout);
                Assert.Equal(SteamWeb.MOBILE_APP_USER_AGENT, withUserAgent.DefaultRequestHeaders.UserAgent.ToString());

                Assert.Equal(Timeout.InfiniteTimeSpan, withoutUserAgent.Timeout);
                Assert.Empty(withoutUserAgent.DefaultRequestHeaders.UserAgent);
            }
        }

        [Fact]
        public void UpdateHttpHandler_EnablesDecompressionAndFiniteConnectionLifetime()
        {
            using (SocketsHttpHandler handler = MainForm.CreateDefaultUpdateHttpHandler())
            {
                Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
                Assert.Equal(SteamNetworkConfiguration.DefaultPooledConnectionLifetime, handler.PooledConnectionLifetime);
                Assert.True(handler.PooledConnectionLifetime > TimeSpan.Zero);
                Assert.NotEqual(Timeout.InfiniteTimeSpan, handler.PooledConnectionLifetime);
                Assert.True(handler.PooledConnectionLifetime <= TimeSpan.FromMinutes(15));
            }
        }
    }
}
