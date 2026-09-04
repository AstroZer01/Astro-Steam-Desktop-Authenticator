using Google.Protobuf;
using Steam_Desktop_Authenticator;
using SteamAuth;
using SteamAuth.Protocol;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class LoginApprovalServiceTests
    {
        [Fact]
        public async Task RespondAsync_DoesNotTreatHttp403AsSessionRevocation()
        {
            var transport = new ThrowingTransport(new SteamWebRequestException(
                "Steam returned HTTP 403.",
                HttpStatusCode.Forbidden,
                new WebHeaderCollection()));
            var service = new LoginApprovalService(_ => true, transport);
            var account = new SteamGuardAccount
            {
                SharedSecret = Convert.ToBase64String(new byte[20]),
                Session = new SessionData
                {
                    SteamID = 76561198000000000UL,
                    AccessToken = CreateUnexpiredToken(),
                    RefreshToken = CreateUnexpiredToken()
                }
            };
            var request = new PendingLoginRequest
            {
                SteamId = account.Session.SteamID,
                ClientId = 123,
                Version = 1
            };

            LoginApprovalActionResult result = await service.RespondAsync(
                account,
                request,
                LoginApprovalDecision.Deny);

            Assert.False(result.Succeeded);
            Assert.Equal(LoginApprovalErrorKind.Unknown, result.ErrorKind);
            Assert.Single(transport.Methods);
        }

        private static string CreateUnexpiredToken()
        {
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":4102444800}"))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return "header." + payload + ".signature";
        }

        private sealed class ThrowingTransport : IAuthenticatorProtocolTransport
        {
            private readonly Exception exception;

            public ThrowingTransport(Exception exception)
            {
                this.exception = exception;
            }

            public List<string> Methods { get; } = new List<string>();

            public Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
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
                Methods.Add(method);
                return Task.FromException<SteamProtocolResponse<TResponse>>(exception);
            }
        }
    }
}
