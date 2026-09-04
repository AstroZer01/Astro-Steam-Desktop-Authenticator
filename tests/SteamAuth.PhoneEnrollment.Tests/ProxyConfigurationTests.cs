using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using SteamAuth;
using SteamKit2;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [CollectionDefinition("Proxy configuration", DisableParallelization = true)]
    public sealed class ProxyConfigurationCollection
    {
    }

    [Collection("Proxy configuration")]
    public sealed class ProxyConfigurationTests
    {
        [Fact]
        public void Payload_NormalizesAValidAuthenticatedProxyAndKeepsSavedPassword()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.ProxyPassword = "saved-secret";
            JObject payload = Payload(true, " [2001:db8::1] ", 8080, " proxy-user ", "keep", null);

            bool valid = ProxyConfiguration.TryFromPayload(payload, manifest, true, out ProxyConfiguration configuration, out string error);

            Assert.True(valid, error);
            Assert.Equal("2001:db8::1", configuration.Host);
            Assert.Equal(8080, configuration.Port);
            Assert.Equal("proxy-user", configuration.Username);
            Assert.Equal("saved-secret", configuration.Password);
            WebProxy proxy = Assert.IsType<WebProxy>(configuration.CreateWebProxy());
            Assert.Equal("http", proxy.Address.Scheme);
            Assert.Contains("2001:db8::1", proxy.Address.Host);
            NetworkCredential credentials = proxy.Credentials.GetCredential(proxy.Address, "Basic");
            Assert.Equal("proxy-user", credentials.UserName);
            Assert.Equal("saved-secret", credentials.Password);
        }

        [Theory]
        [InlineData("https://proxy.example", 8080)]
        [InlineData("proxy.example/path", 8080)]
        [InlineData("proxy.example", 0)]
        [InlineData("proxy.example", 65536)]
        public void Payload_RejectsInvalidEndpoints(string host, int port)
        {
            bool valid = ProxyConfiguration.TryFromPayload(
                Payload(true, host, port, String.Empty, "clear", null),
                Manifest.GenerateNewManifest(false),
                true,
                out _,
                out string error);

            Assert.False(valid);
            Assert.False(String.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void Payload_RequiresAUsernameWhenReplacingWithAPassword()
        {
            bool valid = ProxyConfiguration.TryFromPayload(
                Payload(true, "proxy.example", 3128, String.Empty, "replace", "secret"),
                Manifest.GenerateNewManifest(false),
                true,
                out _,
                out string error);

            Assert.False(valid);
            Assert.Contains("username", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DisabledEmptyProxy_IsAValidDirectConfiguration()
        {
            bool valid = ProxyConfiguration.TryFromPayload(
                Payload(false, String.Empty, 0, String.Empty, "clear", null),
                Manifest.GenerateNewManifest(false),
                false,
                out ProxyConfiguration configuration,
                out string error);

            Assert.True(valid, error);
            Assert.False(configuration.Enabled);
            Assert.Null(configuration.CreateWebProxy());
        }

        [Fact]
        public void DisabledProxy_CanRetainUnusableDetailsWithoutBlockingDirectMode()
        {
            bool valid = ProxyConfiguration.TryFromPayload(
                Payload(false, "https://old-proxy.example/path", 70000, String.Empty, "replace", "old-secret"),
                Manifest.GenerateNewManifest(false),
                false,
                out ProxyConfiguration configuration,
                out string error);

            Assert.True(valid, error);
            Assert.False(configuration.Enabled);
        }

        [Fact]
        public void Payload_UsesTheSelectedHttpsProxyType()
        {
            bool valid = ProxyConfiguration.TryFromPayload(
                Payload(true, "proxy.example", 443, String.Empty, "clear", null, "https"),
                Manifest.GenerateNewManifest(false),
                true,
                out ProxyConfiguration configuration,
                out string error);

            Assert.True(valid, error);
            Assert.Equal("https", configuration.Scheme);
            Assert.Equal(Uri.UriSchemeHttps, configuration.CreateWebProxy().GetProxy(new Uri("https://api.steampowered.com/")).Scheme);
        }

        [Fact]
        public void Payload_RejectsUnsupportedProxyType()
        {
            bool valid = ProxyConfiguration.TryFromPayload(
                Payload(true, "proxy.example", 1080, String.Empty, "clear", null, "socks5"),
                Manifest.GenerateNewManifest(false),
                true,
                out _,
                out string error);

            Assert.False(valid);
            Assert.Contains("HTTP or HTTPS", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SteamKitConfiguration_UsesWebSocketOnly()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(ProxyService.ApplySavedConfiguration(manifest, out string error), error);

            SteamConfiguration configuration = ProxyService.CreateSteamKitConfiguration();

            Assert.Equal(ProtocolTypes.WebSocket, configuration.ProtocolTypes);
        }

        [Fact]
        public async Task DefaultSteamTransport_UsesTheLatestConfiguredProxy()
        {
            RecordingProxy proxy = new RecordingProxy(new Uri("http://127.0.0.1:1"));
            SteamNetworkConfiguration.ConfigureProxy(proxy);
            try
            {
                HttpClientSteamWebTransport transport = new HttpClientSteamWebTransport();
                await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync(
                    HttpMethod.Get,
                    new Uri("https://example.invalid/"),
                    null,
                    null,
                    null,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None));

                Assert.True(proxy.WasUsed);
            }
            finally
            {
                SteamNetworkConfiguration.ConfigureProxy(null);
            }
        }

        private static JObject Payload(bool enabled, string host, int port, string username, string passwordAction, string password, string scheme = "http")
        {
            return new JObject
            {
                ["proxyEnabled"] = enabled,
                ["proxyScheme"] = scheme,
                ["proxyHost"] = host,
                ["proxyPort"] = port,
                ["proxyUsername"] = username,
                ["proxyPasswordAction"] = passwordAction,
                ["proxyPassword"] = password
            };
        }

        private sealed class RecordingProxy : IWebProxy
        {
            private readonly Uri proxyUri;

            public RecordingProxy(Uri proxyUri)
            {
                this.proxyUri = proxyUri;
            }

            public bool WasUsed { get; private set; }
            public ICredentials Credentials { get; set; }

            public Uri GetProxy(Uri destination)
            {
                WasUsed = true;
                return proxyUri;
            }

            public bool IsBypassed(Uri host)
            {
                return false;
            }
        }
    }
}
