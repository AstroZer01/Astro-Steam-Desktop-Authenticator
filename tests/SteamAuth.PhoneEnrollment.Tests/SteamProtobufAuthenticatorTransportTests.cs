using Google.Protobuf;
using SteamAuth;
using SteamAuth.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class SteamProtobufAuthenticatorTransportTests
    {
        [Fact]
        public void Constructor_RejectsTimeoutsBeyondCancellationTimerLimit()
        {
            using (HttpClient client = new HttpClient(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK))))
            {
                ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new SteamProtobufAuthenticatorTransport(client, TimeSpan.FromMilliseconds((double)Int32.MaxValue + 1)));

                Assert.Equal("requestTimeout", exception.ParamName);
            }
        }

        [Fact]
        public async Task Post_UsesTypedEndpointAndSendsAccessTokenInTheFormBody()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ByteString.Empty.ToByteArray()),
                Headers = { { "X-eresult", "1" } }
            });
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                await transport.SendAsync(
                    "IPhoneService",
                    "AccountPhoneStatus",
                    new CPhone_AccountPhoneStatus_Request(),
                    "access token",
                    CPhone_AccountPhoneStatus_Response.Parser);
            }

            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal("/IPhoneService/AccountPhoneStatus/v1", handler.Uri.AbsolutePath);
            Assert.DoesNotContain("access_token", handler.Uri.Query);
            Assert.DoesNotContain("input_protobuf_encoded", handler.Uri.Query);
            Assert.Contains("input_protobuf_encoded=", handler.Body);
            Assert.Contains("access_token=access+token", handler.Body);
        }

        [Fact]
        public async Task Post_WithAccessTokenSendsTheTokenInTheFormBody()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ByteString.Empty.ToByteArray()),
                Headers = { { "X-eresult", "1" } }
            });
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                await transport.SendAsync(
                    "IAuthenticationService",
                    "GetAuthSessionsForAccount",
                    new CAuthentication_GetAuthSessionsForAccount_Request(),
                    "access token",
                    CAuthentication_GetAuthSessionsForAccount_Response.Parser,
                    SteamProtocolRequestMethod.Post);
            }

            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal("/IAuthenticationService/GetAuthSessionsForAccount/v1", handler.Uri.AbsolutePath);
            Assert.DoesNotContain("access_token", handler.Uri.Query);
            Assert.DoesNotContain("input_protobuf_encoded", handler.Uri.Query);
            Assert.Contains("access_token=access+token", handler.Body);
            Assert.Contains("input_protobuf_encoded=", handler.Body);
        }

        [Fact]
        public async Task Get_WithAccessTokenIsRejectedBeforeSendingTheTokenInAUrl()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(
                    "IAuthenticationService",
                    "GetAuthSessionsForAccount",
                    new CAuthentication_GetAuthSessionsForAccount_Request(),
                    "access token",
                    CAuthentication_GetAuthSessionsForAccount_Response.Parser,
                    SteamProtocolRequestMethod.Get));

                Assert.Equal("requestMethod", exception.ParamName);
            }

            Assert.Null(handler.Method);
        }

        [Fact]
        public async Task Get_WithoutAccessTokenUsesTypedQueryParametersWithoutARequestBody()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ByteString.Empty.ToByteArray()),
                Headers = { { "X-eresult", "1" } }
            });
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                await transport.SendAsync(
                    "IAuthenticationService",
                    "GetAuthSessionsForAccount",
                    new CAuthentication_GetAuthSessionsForAccount_Request(),
                    null,
                    CAuthentication_GetAuthSessionsForAccount_Response.Parser,
                    SteamProtocolRequestMethod.Get);
            }

            Assert.Equal(HttpMethod.Get, handler.Method);
            Assert.Equal("/IAuthenticationService/GetAuthSessionsForAccount/v1", handler.Uri.AbsolutePath);
            Assert.DoesNotContain("access_token", handler.Uri.Query);
            Assert.Contains("input_protobuf_encoded=", handler.Uri.Query);
            Assert.Null(handler.Body);
        }

        [Fact]
        public async Task UnsuccessfulHttpResponse_DoesNotTryToParseItsBody()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("this is not protobuf"),
                Headers = { { "X-eresult", "15" }, { "X-error_message", "access denied" } }
            });
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                SteamProtocolResponse<CPhone_AccountPhoneStatus_Response> response = await transport.SendAsync(
                    "IPhoneService",
                    "AccountPhoneStatus",
                    new CPhone_AccountPhoneStatus_Request(),
                    "access-token",
                    CPhone_AccountPhoneStatus_Response.Parser);

                Assert.Equal(15, response.Result);
                Assert.Equal("access denied", response.ErrorMessage);
                Assert.Null(response.Body);
            }
        }

        [Fact]
        public async Task SuccessfulResponse_WithInvalidProtobufIsRejected()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0xff }),
                Headers = { { "X-eresult", "1" } }
            });
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                await Assert.ThrowsAsync<InvalidDataException>(() => transport.SendAsync(
                    "IPhoneService",
                    "AccountPhoneStatus",
                    new CPhone_AccountPhoneStatus_Request(),
                    "access-token",
                    CPhone_AccountPhoneStatus_Response.Parser));
            }
        }

        [Fact]
        public async Task SuccessfulResponse_OverTheBodyLimitIsRejected()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]),
                Headers = { { "X-eresult", "1" } }
            });
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                await Assert.ThrowsAsync<InvalidDataException>(() => transport.SendAsync(
                    "IPhoneService",
                    "AccountPhoneStatus",
                    new CPhone_AccountPhoneStatus_Request(),
                    "access-token",
                    CPhone_AccountPhoneStatus_Response.Parser));
            }
        }

        [Fact]
        public async Task RequestRouteSegmentsMustNotContainPathSyntax()
        {
            using (HttpClient client = new HttpClient(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK))))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(
                    "IPhoneService/evil",
                    "AccountPhoneStatus",
                    new CPhone_AccountPhoneStatus_Request(),
                    null,
                    CPhone_AccountPhoneStatus_Response.Parser));

                Assert.Equal("service", exception.ParamName);
            }
        }

        [Fact]
        public async Task Post_CancelsAStalledRequestAtTheConfiguredDeadline()
        {
            using (HttpClient client = new HttpClient(new BlockingHandler()))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client, TimeSpan.FromMilliseconds(50));

                await Assert.ThrowsAsync<TimeoutException>(() => transport.SendAsync(
                    "IPhoneService",
                    "AccountPhoneStatus",
                    new CPhone_AccountPhoneStatus_Request(),
                    "access-token",
                    CPhone_AccountPhoneStatus_Response.Parser));
            }
        }

        [Fact]
        public async Task Http429_MapsToSteamRateLimitResult()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            });
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                SteamProtocolResponse<CPhone_AccountPhoneStatus_Response> response = await transport.SendAsync(
                    "IPhoneService",
                    "AccountPhoneStatus",
                    new CPhone_AccountPhoneStatus_Request(),
                    "access-token",
                    CPhone_AccountPhoneStatus_Response.Parser);

                Assert.Equal(84, response.Result);
                Assert.Contains("rate limiting", response.ErrorMessage);
                Assert.Null(response.Body);
            }
        }

        [Fact]
        public async Task HttpFailure_ExposesResponseHeadersOnTheException()
        {
            RecordingHandler handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            });
            handler.Response.Headers.TryAddWithoutValidation("Retry-After", "60");
            using (HttpClient client = new HttpClient(handler))
            {
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport(client);
                SteamWebRequestException exception = await Assert.ThrowsAsync<SteamWebRequestException>(() => transport.SendAsync(
                    "IPhoneService",
                    "AccountPhoneStatus",
                    new CPhone_AccountPhoneStatus_Request(),
                    "access-token",
                    CPhone_AccountPhoneStatus_Response.Parser));

                Assert.Equal("60", exception.Headers["Retry-After"]);
            }
        }

        [Fact]
        public void SteamWebRequestException_ExposesStatusCodeThroughHttpRequestException()
        {
            HttpRequestException exception = new SteamWebRequestException(
                "Rate limited",
                HttpStatusCode.TooManyRequests,
                new WebHeaderCollection());

            Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly HttpResponseMessage response;

            public RecordingHandler(HttpResponseMessage response)
            {
                this.response = response;
            }

            public HttpResponseMessage Response => response;

            public HttpMethod Method { get; private set; }
            public Uri Uri { get; private set; }
            public string Body { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Method = request.Method;
                Uri = request.RequestUri;
                Body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                return response;
            }
        }

        private sealed class BlockingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The request should have been canceled.");
            }
        }
    }
}
