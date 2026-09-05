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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RespondAsync_AccessDeniedDoesNotRefreshOrInvalidateSession(bool approve)
        {
            var transport = new ResultTransport(15);
            int saves = 0;
            var service = new LoginApprovalService(_ => { saves++; return true; }, transport);
            SteamGuardAccount account = CreateAccount(transport);
            string accessToken = account.Session.AccessToken;
            string refreshToken = account.Session.RefreshToken;

            LoginApprovalActionResult result = await service.RespondAsync(account, CreateRequest(account),
                approve ? LoginApprovalDecision.ApprovePersistent : LoginApprovalDecision.Deny);

            Assert.False(result.Succeeded);
            Assert.Equal(LoginApprovalErrorKind.Unknown, result.ErrorKind);
            Assert.Equal(new[] { "UpdateAuthSessionWithMobileConfirmation" }, transport.Methods);
            Assert.Equal(0, saves);
            Assert.Equal(accessToken, account.Session.AccessToken);
            Assert.Equal(refreshToken, account.Session.RefreshToken);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FetchPendingRequestsAsync_AccessDeniedRemainsLocalForListAndDetails(bool denyDetails)
        {
            var transport = new ResultTransport(15) { DenyDetails = denyDetails };
            int saves = 0;
            var service = new LoginApprovalService(_ => { saves++; return true; }, transport);
            SteamGuardAccount account = CreateAccount(transport);

            LoginApprovalFetchResult result = await service.FetchPendingRequestsAsync(account);

            Assert.Equal(LoginApprovalErrorKind.Unknown, result.ErrorKind);
            Assert.Empty(result.Requests);
            Assert.Equal(0, saves);
            Assert.Equal(denyDetails
                ? new[] { "GetAuthSessionsForAccount", "GetAuthSessionInfo" }
                : new[] { "GetAuthSessionsForAccount" }, transport.Methods);
        }

        [Theory]
        [InlineData(21)]
        [InlineData(26)]
        public async Task RespondAsync_DefinitiveInvalidSessionStillRefreshesAndReportsRenewal(int steamResult)
        {
            var transport = new ResultTransport(steamResult);
            int saves = 0;
            var service = new LoginApprovalService(_ => { saves++; return true; }, transport);
            SteamGuardAccount account = CreateAccount(transport);

            LoginApprovalActionResult result = await service.RespondAsync(account, CreateRequest(account), LoginApprovalDecision.Deny);

            Assert.False(result.Succeeded);
            Assert.Equal(LoginApprovalErrorKind.Unauthorized, result.ErrorKind);
            Assert.Equal(1, saves);
            Assert.Equal(new[] { "UpdateAuthSessionWithMobileConfirmation", "GenerateAccessTokenForApp",
                "UpdateAuthSessionWithMobileConfirmation" }, transport.Methods);
        }

        [Fact]
        public async Task RespondAsync_AccessDeniedDuringRefreshStillRequiresRenewalAndPreservesTokens()
        {
            var transport = new ResultTransport(1) { RefreshResult = 15 };
            int saves = 0;
            var service = new LoginApprovalService(_ => { saves++; return true; }, transport);
            SteamGuardAccount account = CreateAccount(transport);
            account.Session.AccessToken = "expired-access-token";
            string refreshToken = account.Session.RefreshToken;

            LoginApprovalActionResult result = await service.RespondAsync(account, CreateRequest(account), LoginApprovalDecision.Deny);

            Assert.False(result.Succeeded);
            Assert.Equal(LoginApprovalErrorKind.SessionExpired, result.ErrorKind);
            Assert.Equal(new[] { "GenerateAccessTokenForApp" }, transport.Methods);
            Assert.Equal(0, saves);
            Assert.Equal("expired-access-token", account.Session.AccessToken);
            Assert.Equal(refreshToken, account.Session.RefreshToken);
        }

        private static SteamGuardAccount CreateAccount(IAuthenticatorProtocolTransport transport)
        {
            return new SteamGuardAccount
            {
                SharedSecret = Convert.ToBase64String(new byte[20]),
                Session = new SessionData(transport)
                {
                    SteamID = 76561198000000000UL,
                    AccessToken = CreateUnexpiredToken(),
                    RefreshToken = CreateUnexpiredToken()
                }
            };
        }

        private static PendingLoginRequest CreateRequest(SteamGuardAccount account)
        {
            return new PendingLoginRequest { SteamId = account.Session.SteamID, ClientId = 123, Version = 1 };
        }

        private sealed class ResultTransport : IAuthenticatorProtocolTransport
        {
            private readonly int actionResult;
            public int RefreshResult { get; set; } = 1;
            public bool DenyDetails { get; set; }
            public List<string> Methods { get; } = new List<string>();

            public ResultTransport(int actionResult)
            {
                this.actionResult = actionResult;
            }

            public Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
                string service, string method, TRequest request, string accessToken,
                MessageParser<TResponse> responseParser,
                SteamProtocolRequestMethod requestMethod = SteamProtocolRequestMethod.Post,
                CancellationToken cancellationToken = default)
                where TRequest : class, IMessage<TRequest>
                where TResponse : class, IMessage<TResponse>
            {
                Methods.Add(method);
                int result = actionResult;
                IMessage body = null;
                if (method == "GenerateAccessTokenForApp")
                {
                    result = RefreshResult;
                    body = new CAuthentication_AccessToken_GenerateForApp_Response { AccessToken = CreateUnexpiredToken() };
                }
                else if (method == "GetAuthSessionsForAccount" && DenyDetails)
                {
                    result = 1;
                    body = new CAuthentication_GetAuthSessionsForAccount_Response { ClientIds = { 123UL } };
                }
                return Task.FromResult(new SteamProtocolResponse<TResponse>
                {
                    Result = result,
                    // Error text must not override the endpoint's structured result.
                    ErrorMessage = "401: invalid token; authorization expired",
                    Body = body as TResponse
                });
            }
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
