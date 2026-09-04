using SteamAuth;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class SteamWebTransportTests
    {
        [Fact]
        public async Task CrossHostRedirect_SendsOnlyCookiesScopedToTheRedirectTarget()
        {
            Uri source = new Uri("https://source.example/start");
            Uri target = new Uri("https://target.example/complete");
            CookieContainer cookies = new CookieContainer();
            cookies.SetCookies(source, "source_cookie=source; Path=/");
            cookies.SetCookies(target, "target_cookie=target; Path=/");
            RedirectHandler handler = new RedirectHandler(index => index == 0
                ? Redirect(target, "source_redirect=added; Path=/")
                : Success());

            using (HttpClient client = new HttpClient(handler))
            {
                HttpClientSteamWebTransport transport = new HttpClientSteamWebTransport(client);
                await transport.SendAsync(HttpMethod.Get, source, cookies, null, RequestHeaders(), TimeSpan.FromSeconds(5), CancellationToken.None);
            }

            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains("source_cookie=source", handler.Requests[0].CookieHeader);
            Assert.Contains("target_cookie=target", handler.Requests[1].CookieHeader);
            Assert.DoesNotContain("source_cookie", handler.Requests[1].CookieHeader);
            Assert.DoesNotContain("source_redirect", handler.Requests[1].CookieHeader);
            Assert.Equal(SteamWeb.MOBILE_APP_USER_AGENT, handler.Requests[1].UserAgent);
        }

        [Fact]
        public async Task Redirect_SetCookieIsIncludedOnTheNextSameHostRequest()
        {
            Uri source = new Uri("https://source.example/start");
            Uri target = new Uri("https://source.example/complete");
            CookieContainer cookies = new CookieContainer();
            cookies.SetCookies(source, "initial=present; Path=/");
            RedirectHandler handler = new RedirectHandler(index => index == 0
                ? Redirect(target, "redirect_cookie=added; Path=/")
                : Success());

            using (HttpClient client = new HttpClient(handler))
            {
                HttpClientSteamWebTransport transport = new HttpClientSteamWebTransport(client);
                await transport.SendAsync(HttpMethod.Get, source, cookies, null, RequestHeaders(), TimeSpan.FromSeconds(5), CancellationToken.None);
            }

            Assert.Contains("initial=present", handler.Requests[1].CookieHeader);
            Assert.Contains("redirect_cookie=added", handler.Requests[1].CookieHeader);
            Assert.Equal("test-agent", handler.Requests[1].UserAgent);
        }

        [Fact]
        public async Task SendAsync_RejectsNonHttpUris()
        {
            using (HttpClient client = new HttpClient(new RedirectHandler(index => Success())))
            {
                HttpClientSteamWebTransport transport = new HttpClientSteamWebTransport(client);

                await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(
                    HttpMethod.Get,
                    new Uri("ftp://example.test/resource"),
                    null,
                    null,
                    RequestHeaders(),
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None));
            }
        }

        [Fact]
        public async Task SendAsync_DisposesContentWhenValidationFails()
        {
            using (HttpClient client = new HttpClient(new RedirectHandler(index => Success())))
            {
                HttpClientSteamWebTransport transport = new HttpClientSteamWebTransport(client);
                TrackingContent invalidUriContent = new TrackingContent();
                await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(
                    HttpMethod.Post,
                    new Uri("ftp://example.test/resource"),
                    null,
                    invalidUriContent,
                    RequestHeaders(),
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None));
                Assert.True(invalidUriContent.Disposed);

                TrackingContent invalidTimeoutContent = new TrackingContent();
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.SendAsync(
                    HttpMethod.Post,
                    new Uri("https://example.test/resource"),
                    null,
                    invalidTimeoutContent,
                    RequestHeaders(),
                    TimeSpan.FromSeconds(-2),
                    CancellationToken.None));
                Assert.True(invalidTimeoutContent.Disposed);
            }
        }

        private static IReadOnlyDictionary<string, string> RequestHeaders()
        {
            return new Dictionary<string, string> { ["User-Agent"] = "test-agent" };
        }

        private static HttpResponseMessage Redirect(Uri location, string setCookie)
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = location;
            response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
            return response;
        }

        private static HttpResponseMessage Success()
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }

        private sealed class RedirectHandler : HttpMessageHandler
        {
            private readonly Func<int, HttpResponseMessage> responseFactory;

            public RedirectHandler(Func<int, HttpResponseMessage> responseFactory)
            {
                this.responseFactory = responseFactory;
            }

            public List<RequestRecord> Requests { get; } = new List<RequestRecord>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string cookieHeader = request.Headers.TryGetValues("Cookie", out IEnumerable<string> values)
                    ? String.Join("; ", values)
                    : String.Empty;
                Requests.Add(new RequestRecord(request.RequestUri, cookieHeader, request.Headers.UserAgent.ToString()));
                return Task.FromResult(responseFactory(Requests.Count - 1));
            }
        }

        private sealed class RequestRecord
        {
            public RequestRecord(Uri uri, string cookieHeader, string userAgent)
            {
                Uri = uri;
                CookieHeader = cookieHeader;
                UserAgent = userAgent;
            }

            public Uri Uri { get; }
            public string CookieHeader { get; }
            public string UserAgent { get; }
        }

        private sealed class TrackingContent : ByteArrayContent
        {
            public TrackingContent()
                : base(Array.Empty<byte>())
            {
            }

            public bool Disposed { get; private set; }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
