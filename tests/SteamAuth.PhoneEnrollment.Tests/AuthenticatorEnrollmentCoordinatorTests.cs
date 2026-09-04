using Google.Protobuf;
using SteamAuth;
using SteamAuth.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public class AuthenticatorEnrollmentCoordinatorTests
    {
        [Fact]
        public async Task AddPhoneNumber_ReturnsPhoneAdded_WhenPhoneIsAlreadyAttached()
        {
            FakeTransport transport = new FakeTransport(Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = true }));

            Assert.Equal(AuthenticatorLinker.PhoneLinkResult.PhoneAdded, await CreateLinker(transport).AddPhoneNumber());
            Assert.Equal(new[] { "AccountPhoneStatus" }, transport.Methods);
        }

        [Theory]
        [InlineData(1, AuthenticatorLinker.ConfirmationCodeType.SMS)]
        [InlineData(3, AuthenticatorLinker.ConfirmationCodeType.Email)]
        public async Task AddAuthenticator_IdentifiesTheConfirmationChannelSteamSelects(int confirmType, AuthenticatorLinker.ConfirmationCodeType expected)
        {
            AuthenticatorLinker linker = CreateLinker(new FakeTransport(AwaitingFinalization(confirmType)));

            Assert.Equal(AuthenticatorLinker.LinkResult.AwaitingFinalization, await linker.AddAuthenticator());
            Assert.Equal(expected, linker.FinalizationConfirmationType);
        }

        [Fact]
        public async Task StartAsync_DoesNotCheckPhone_WhenSteamAllowsAuthenticatorWithoutIt()
        {
            FakeTransport transport = new FakeTransport(AwaitingFinalization(3));
            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(CreateLinker(transport), new FakeInteraction()).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.AwaitingFinalization, outcome.Result);
            Assert.Equal(new[] { "AddAuthenticator" }, transport.Methods);
        }

        [Fact]
        public async Task StartAsync_RetriesInvalidSmsThenAddsAuthenticatorOnceAfterVerification()
        {
            FakeTransport transport = new FakeTransport(
                MustProvidePhoneNumber(),
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                Response(22, new CPhone_SetAccountPhoneNumber_Response { ConfirmationEmailAddress = "m***@example.com" }),
                Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = true }),
                Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = false }),
                Response(1, new CPhone_SendPhoneVerificationCode_Response()),
                Response(89, new CPhone_VerifyAccountPhoneWithCode_Response()),
                Response(1, new CPhone_VerifyAccountPhoneWithCode_Response()),
                AwaitingFinalization());
            FakeInteraction interaction = new FakeInteraction(new PhoneEnrollmentDetails("+15551234567", "US"), "wrong", "right");

            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(CreateLinker(transport), interaction).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.AwaitingFinalization, outcome.Result);
            Assert.Equal(new[] { false, true }, interaction.InvalidCodeFlags);
            Assert.Equal(2, transport.Methods.Count(method => method == "AddAuthenticator"));
            Assert.Equal(2, transport.Methods.Count(method => method == "VerifyAccountPhoneWithCode"));
            Assert.Equal(1, transport.Methods.Count(method => method == "SendPhoneVerificationCode"));
            int verifiedAt = transport.Methods.FindLastIndex(method => method == "VerifyAccountPhoneWithCode");
            Assert.Equal("AddAuthenticator", transport.Methods[verifiedAt + 1]);
        }

        [Fact]
        public async Task AddPhoneNumber_PreservesSmsCodeWhenSteamReturnsARetryableFailure()
        {
            AuthenticatorLinker linker = CreateLinker(new FakeTransport(
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                Response(22, new CPhone_SetAccountPhoneNumber_Response { ConfirmationEmailAddress = "m***@example.com" }),
                Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = false }),
                Response(1, new CPhone_SendPhoneVerificationCode_Response()),
                Response(15, new CPhone_VerifyAccountPhoneWithCode_Response(), "Temporary Steam error")));
            linker.PhoneNumber = "+15551234567";
            linker.PhoneCountryCode = "US";

            Assert.Equal(AuthenticatorLinker.PhoneLinkResult.MustConfirmEmail, await linker.AddPhoneNumber());
            Assert.Equal(AuthenticatorLinker.PhoneLinkResult.MustConfirmSMS, await linker.AddPhoneNumber());
            linker.PhoneSMSCode = "123456";

            Assert.Equal(AuthenticatorLinker.PhoneLinkResult.FailureAddingPhone, await linker.AddPhoneNumber());
            Assert.Equal("123456", linker.PhoneSMSCode);
        }

        [Fact]
        public async Task StartAsync_ContinuesWithoutPhone_WhenSteamAcceptsTheRetry()
        {
            FakeTransport transport = new FakeTransport(
                MustProvidePhoneNumber(),
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                AwaitingFinalization(3));

            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(
                CreateLinker(transport), new FakeInteraction(new PhoneEnrollmentDetails(null, null, true))).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.AwaitingFinalization, outcome.Result);
            Assert.Equal(2, transport.Methods.Count(method => method == "AddAuthenticator"));
            Assert.DoesNotContain("SetAccountPhoneNumber", transport.Methods);
            Assert.DoesNotContain("SendPhoneVerificationCode", transport.Methods);
        }

        [Fact]
        public async Task StartAsync_OnlyOffersPhoneSetup_WhenNoPhoneRetryIsSpecificallyRejected()
        {
            FakeTransport transport = new FakeTransport(
                MustProvidePhoneNumber(),
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                MustProvidePhoneNumber());
            FakeInteraction interaction = new FakeInteraction(new PhoneEnrollmentDetails(null, null, true));

            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(CreateLinker(transport), interaction).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.Canceled, outcome.Result);
            Assert.Equal(1, interaction.PhoneRequiredConfirmationCount);
        }

        [Fact]
        public async Task StartAsync_ReturnsSpecificRateLimitMessage()
        {
            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(
                CreateLinker(new FakeTransport(Response(84, new CTwoFactor_AddAuthenticator_Response()))), new FakeInteraction()).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.Failed, outcome.Result);
            Assert.Contains("rate limiting", outcome.ErrorMessage);
        }

        [Fact]
        public async Task StartAsync_ReturnsSteamPhoneFailureDetails()
        {
            FakeTransport transport = new FakeTransport(
                MustProvidePhoneNumber(),
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                Response(15, new CPhone_SetAccountPhoneNumber_Response(), "Phone activity limit reached"));
            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(
                CreateLinker(transport), new FakeInteraction(new PhoneEnrollmentDetails("+15551234567", "US"))).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.Failed, outcome.Result);
            Assert.Contains("denied", outcome.ErrorMessage);
        }

        [Fact]
        public async Task StartAsync_DoesNotSpendSmsRetryBudgetWhileWaitingForEmailConfirmation()
        {
            List<object> outcomes = new List<object>
            {
                MustProvidePhoneNumber(),
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                Response(22, new CPhone_SetAccountPhoneNumber_Response { ConfirmationEmailAddress = "m***@example.com" })
            };
            for (int i = 0; i < 10; i++)
            {
                outcomes.Add(Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = true }));
            }
            outcomes.Add(Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = false }));
            outcomes.Add(Response(1, new CPhone_SendPhoneVerificationCode_Response()));

            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(
                CreateLinker(new FakeTransport(outcomes.ToArray())), new FakeInteraction(new PhoneEnrollmentDetails("+15551234567", "US"))).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.Canceled, outcome.Result);
        }

        [Fact]
        public async Task StartAsync_AllowsTheSixtiethPhoneEnrollmentStatusCheck()
        {
            List<object> outcomes = new List<object>
            {
                MustProvidePhoneNumber(),
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                Response(22, new CPhone_SetAccountPhoneNumber_Response { ConfirmationEmailAddress = "m***@example.com" })
            };
            for (int i = 0; i < 57; i++)
            {
                outcomes.Add(Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = true }));
            }
            outcomes.Add(Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = false }));
            outcomes.Add(Response(1, new CPhone_SendPhoneVerificationCode_Response()));

            FakeTransport transport = new FakeTransport(outcomes.ToArray());
            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(
                CreateLinker(transport), new FakeInteraction(new PhoneEnrollmentDetails("+15551234567", "US"))).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.Canceled, outcome.Result);
            Assert.Equal(58, transport.Methods.Count(method => method == "IsAccountWaitingForEmailConfirmation"));
            Assert.Equal(1, transport.Methods.Count(method => method == "SendPhoneVerificationCode"));
        }

        [Fact]
        public async Task StartAsync_FailsOnTheSixtyFirstPhoneEnrollmentStatusCheck()
        {
            List<object> outcomes = new List<object>
            {
                MustProvidePhoneNumber(),
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                Response(22, new CPhone_SetAccountPhoneNumber_Response { ConfirmationEmailAddress = "m***@example.com" })
            };
            for (int i = 0; i < 58; i++)
            {
                outcomes.Add(Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = true }));
            }

            FakeTransport transport = new FakeTransport(outcomes.ToArray());
            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(
                CreateLinker(transport), new FakeInteraction(new PhoneEnrollmentDetails("+15551234567", "US"))).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.Failed, outcome.Result);
            Assert.Contains("status checks", outcome.ErrorMessage);
            Assert.Equal(58, transport.Methods.Count(method => method == "IsAccountWaitingForEmailConfirmation"));
        }

        [Fact]
        public async Task StartAsync_RetryWithoutPhoneSharesThePhoneEnrollmentStatusLimit()
        {
            List<object> outcomes = new List<object>
            {
                MustProvidePhoneNumber(),
                Response(1, new CPhone_AccountPhoneStatus_Response { HasPhone = false }),
                MustProvidePhoneNumber(),
                Response(22, new CPhone_SetAccountPhoneNumber_Response { ConfirmationEmailAddress = "m***@example.com" })
            };
            for (int i = 0; i < 57; i++)
            {
                outcomes.Add(Response(1, new CPhone_IsAccountWaitingForEmailConfirmation_Response { AwaitingEmailConfirmation = true }));
            }

            FakeInteraction interaction = new FakeInteraction(new[]
            {
                new PhoneEnrollmentDetails(null, null, true),
                new PhoneEnrollmentDetails("+15551234567", "US")
            })
            {
                ConfirmPhoneRequiredResult = true
            };
            FakeTransport transport = new FakeTransport(outcomes.ToArray());
            AuthenticatorEnrollmentOutcome outcome = await new AuthenticatorEnrollmentCoordinator(
                CreateLinker(transport), interaction).StartAsync();

            Assert.Equal(AuthenticatorEnrollmentResult.Failed, outcome.Result);
            Assert.Contains("status checks", outcome.ErrorMessage);
            Assert.Equal(1, interaction.PhoneRequiredConfirmationCount);
            Assert.Equal(57, transport.Methods.Count(method => method == "IsAccountWaitingForEmailConfirmation"));
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_VerifiesStatusBeforeReportingSuccess()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(1, new CTwoFactor_FinalizeAddAuthenticator_Response { Success = true, WantMore = false }),
                Response(1, new CTwoFactor_Status_Response { State = 1 }));
            AuthenticatorLinker linker = CreateLinker(transport);
            await linker.AddAuthenticator();

            AuthenticatorLinker.FinalizeResult result = await linker.FinalizeAddAuthenticator("email-code");

            Assert.Equal(AuthenticatorLinker.FinalizeResult.Success, result);
            Assert.True(linker.LinkedAccount.FullyEnrolled);
            Assert.Equal(new[] { "AddAuthenticator", "FinalizeAddAuthenticator", "QueryStatus" }, transport.Methods);
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_DoesNotReportSuccess_WhenSteamStatusIsNotActive()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(1, new CTwoFactor_FinalizeAddAuthenticator_Response { Success = true, WantMore = false }),
                Response(1, new CTwoFactor_Status_Response { State = 0 }));
            AuthenticatorLinker linker = CreateLinker(transport);
            await linker.AddAuthenticator();

            AuthenticatorLinker.FinalizeResult result = await linker.FinalizeAddAuthenticator("email-code");

            Assert.Equal(AuthenticatorLinker.FinalizeResult.NotFinalized, result);
            Assert.False(linker.LinkedAccount.FullyEnrolled);
            Assert.Contains("not confirmed", linker.LastErrorMessage);
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_PersistsAnAcceptedFinalizationWhenStatusCannotBeVerified()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(1, new CTwoFactor_FinalizeAddAuthenticator_Response { Success = true, WantMore = false }),
                Response(15, (CTwoFactor_Status_Response)null, "Temporary Steam error"));
            AuthenticatorLinker linker = CreateLinker(transport);
            await linker.AddAuthenticator();

            Assert.Equal(AuthenticatorLinker.FinalizeResult.FinalizedStatusUnverified, await linker.FinalizeAddAuthenticator("email-code"));
            Assert.True(linker.Finalized);
            Assert.True(linker.LinkedAccount.FullyEnrolled);
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_ReturnsRateLimitResult()
        {
            FakeTransport transport = new FakeTransport(AwaitingFinalization(), Response(84, new CTwoFactor_FinalizeAddAuthenticator_Response()));
            AuthenticatorLinker linker = CreateLinker(transport);
            await linker.AddAuthenticator();

            Assert.Equal(AuthenticatorLinker.FinalizeResult.RateLimited, await linker.FinalizeAddAuthenticator("email-code"));
            Assert.Contains("rate limiting", linker.LastErrorMessage);
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_UsesSteamServerTimeWhenSteamRequestsAnotherCode()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(1, new CTwoFactor_FinalizeAddAuthenticator_Response { WantMore = true, ServerTime = 1700000030 }),
                Response(1, new CTwoFactor_FinalizeAddAuthenticator_Response { Success = true }),
                Response(1, new CTwoFactor_Status_Response { State = 1 }));
            List<TimeSpan> retryDelays = new List<TimeSpan>();
            AuthenticatorLinker linker = new AuthenticatorLinker(
                new SessionData { SteamID = 76561198000000000, AccessToken = "test-access-token" },
                transport,
                () => 1700000000,
                delay =>
                {
                    retryDelays.Add(delay);
                    return Task.CompletedTask;
                });
            await linker.AddAuthenticator();

            Assert.Equal(AuthenticatorLinker.FinalizeResult.Success, await linker.FinalizeAddAuthenticator("email-code"));

            CTwoFactor_FinalizeAddAuthenticator_Request[] requests = transport.Requests
                .OfType<CTwoFactor_FinalizeAddAuthenticator_Request>()
                .ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal((ulong)1700000000, requests[0].AuthenticatorTime);
            Assert.Equal((ulong)1700000040, requests[1].AuthenticatorTime);
            Assert.All(requests, request => Assert.Equal("email-code", request.ActivationCode));
            Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(retryDelays));
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_CancelsBeforeWaitingForTheNextCode()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(1, new CTwoFactor_FinalizeAddAuthenticator_Response { WantMore = true, ServerTime = 1700000030 }));
            AuthenticatorLinker linker = CreateLinker(transport);
            await linker.AddAuthenticator();
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => linker.FinalizeAddAuthenticator("email-code", cancellation.Token));
            }
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_RejectsAnOutOfRangeServerTime()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(1, new CTwoFactor_FinalizeAddAuthenticator_Response { WantMore = true, ServerTime = UInt64.MaxValue }));
            AuthenticatorLinker linker = CreateLinker(transport);
            await linker.AddAuthenticator();

            Assert.Equal(AuthenticatorLinker.FinalizeResult.GeneralFailure, await linker.FinalizeAddAuthenticator("email-code"));
            Assert.Contains("current time", linker.LastErrorMessage);
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_MapsSteamActivationCodeMismatchHeaderToRetryableResult()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(89, new CTwoFactor_FinalizeAddAuthenticator_Response()));
            AuthenticatorLinker linker = CreateLinker(transport);
            await linker.AddAuthenticator();

            Assert.Equal(AuthenticatorLinker.FinalizeResult.BadConfirmationCode, await linker.FinalizeAddAuthenticator("email-code"));
            Assert.Contains("did not accept", linker.LastErrorMessage);
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_MapsActivationCodeMismatchWithoutAResponseBody()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(89, (CTwoFactor_FinalizeAddAuthenticator_Response)null));
            AuthenticatorLinker linker = CreateLinker(transport);
            await linker.AddAuthenticator();

            Assert.Equal(AuthenticatorLinker.FinalizeResult.BadConfirmationCode, await linker.FinalizeAddAuthenticator("email-code"));
            Assert.Contains("did not accept", linker.LastErrorMessage);
        }

        [Fact]
        public async Task AddAuthenticator_ReturnsFailure_WhenTransportReturnsNoResponse()
        {
            AuthenticatorLinker linker = CreateLinker(new FakeTransport(NullResponse.Instance));

            Assert.Equal(AuthenticatorLinker.LinkResult.GeneralFailure, await linker.AddAuthenticator());
            Assert.Contains("did not return a response", linker.LastErrorMessage);
        }

        [Fact]
        public async Task AddAuthenticator_RejectsIncompleteAuthenticatorData()
        {
            AuthenticatorLinker linker = CreateLinker(new FakeTransport(Response(1, new CTwoFactor_AddAuthenticator_Response
            {
                Status = 1,
                RevocationCode = "R12345"
            })));

            Assert.Equal(AuthenticatorLinker.LinkResult.GeneralFailure, await linker.AddAuthenticator());
            Assert.Contains("incomplete", linker.LastErrorMessage);
            Assert.Null(linker.LinkedAccount);
        }

        [Fact]
        public async Task DeactivateAuthenticator_RejectsMissingRecoveryCodeBeforeCallingSteam()
        {
            SteamGuardAccount account = new SteamGuardAccount
            {
                Session = new SessionData { AccessToken = "access-token" }
            };

            Assert.False(await account.DeactivateAuthenticator());
            Assert.Contains("recovery code", account.LastAuthenticatorOperationError);
        }

        [Fact]
        public async Task DeactivateAuthenticator_RejectsUnsupportedSchemeBeforeCallingSteam()
        {
            SteamGuardAccount account = new SteamGuardAccount
            {
                RevocationCode = "R12345",
                Session = new SessionData { AccessToken = "access-token" }
            };

            Assert.False(await account.DeactivateAuthenticator(3));
            Assert.Contains("removal method", account.LastAuthenticatorOperationError);
        }

        [Fact]
        public async Task RefreshAccessToken_SendsTheCurrentAccessTokenToSteam()
        {
            FakeTransport transport = new FakeTransport(Response(1, new CAuthentication_AccessToken_GenerateForApp_Response
            {
                AccessToken = "new-access-token"
            }));
            SessionData session = new SessionData(transport)
            {
                SteamID = 76561198000000000,
                AccessToken = "current-access-token",
                RefreshToken = CreateUnexpiredToken()
            };

            await session.RefreshAccessToken();

            Assert.Equal("current-access-token", transport.AccessTokens.Single());
            Assert.Equal("new-access-token", session.AccessToken);
        }

        [Theory]
        [InlineData("not-a-token")]
        [InlineData("a.b.c")]
        [InlineData("a.!!!!.c")]
        public void SessionData_TreatsMalformedAccessTokensAsExpired(string token)
        {
            SessionData session = new SessionData
            {
                AccessToken = token
            };

            Assert.True(session.IsAccessTokenExpired());
        }

        [Fact]
        public async Task FinalizeAddAuthenticator_CancelsAnInjectedCodeWait()
        {
            FakeTransport transport = new FakeTransport(
                AwaitingFinalization(),
                Response(1, new CTwoFactor_FinalizeAddAuthenticator_Response { WantMore = true, ServerTime = 1700000030 }));
            TaskCompletionSource<bool> waitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AuthenticatorLinker linker = new AuthenticatorLinker(
                new SessionData { SteamID = 76561198000000000, AccessToken = "test-access-token" },
                transport,
                () => 1700000000,
                (delay, cancellationToken) =>
                {
                    waitStarted.TrySetResult(true);
                    return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                });
            await linker.AddAuthenticator();

            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                Task finalization = linker.FinalizeAddAuthenticator("email-code", cancellation.Token);
                await waitStarted.Task;
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalization);
            }
        }

        private static AuthenticatorLinker CreateLinker(IAuthenticatorProtocolTransport transport)
        {
            return new AuthenticatorLinker(new SessionData
            {
                SteamID = 76561198000000000,
                AccessToken = "test-access-token"
            }, transport, () => 1700000000, _ => Task.CompletedTask);
        }

        private static ProtocolOutcome AwaitingFinalization(int confirmType = 0)
        {
            return Response(1, new CTwoFactor_AddAuthenticator_Response
            {
                Status = 1,
                SharedSecret = ByteString.CopyFrom(new byte[20]),
                IdentitySecret = ByteString.CopyFrom(new byte[20]),
                RevocationCode = "R12345",
                ConfirmType = confirmType
            });
        }

        private static ProtocolOutcome MustProvidePhoneNumber()
        {
            return Response(1, new CTwoFactor_AddAuthenticator_Response { Status = 2 });
        }

        private static ProtocolOutcome Response<TResponse>(int result, TResponse body, string errorMessage = null)
            where TResponse : class, IMessage<TResponse>
        {
            return new ProtocolOutcome(result, body, errorMessage);
        }

        private static string CreateUnexpiredToken()
        {
            string header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{}"));
            string payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"exp\":4102444800}"));
            return header.TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." + payload.TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        }

        private sealed class ProtocolOutcome
        {
            public ProtocolOutcome(int result, IMessage body, string errorMessage)
            {
                Result = result;
                Body = body;
                ErrorMessage = errorMessage;
            }

            public int Result { get; }
            public IMessage Body { get; }
            public string ErrorMessage { get; }
        }

        private sealed class NullResponse
        {
            public static readonly NullResponse Instance = new NullResponse();

            private NullResponse()
            {
            }
        }

        private sealed class FakeTransport : IAuthenticatorProtocolTransport
        {
            private readonly Queue<object> outcomes;

            public FakeTransport(params object[] outcomes)
            {
                this.outcomes = new Queue<object>(outcomes);
            }

            public List<string> Methods { get; } = new List<string>();
            public List<object> Requests { get; } = new List<object>();
            public List<string> AccessTokens { get; } = new List<string>();

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
                Requests.Add(request);
                AccessTokens.Add(accessToken);
                if (outcomes.Count == 0)
                    return Task.FromException<SteamProtocolResponse<TResponse>>(new InvalidOperationException("Unexpected Steam request: " + method));

                object outcome = outcomes.Dequeue();
                if (outcome is Exception exception)
                    return Task.FromException<SteamProtocolResponse<TResponse>>(exception);
                if (outcome is NullResponse)
                    return Task.FromResult<SteamProtocolResponse<TResponse>>(null);

                ProtocolOutcome response = (ProtocolOutcome)outcome;
                return Task.FromResult(new SteamProtocolResponse<TResponse>
                {
                    Result = response.Result,
                    ErrorMessage = response.ErrorMessage,
                    Body = (TResponse)response.Body
                });
            }
        }

        private sealed class FakeInteraction : IPhoneEnrollmentInteraction
        {
            private readonly Queue<PhoneEnrollmentDetails> phoneNumbers;
            private readonly Queue<string> smsCodes;

            public FakeInteraction(PhoneEnrollmentDetails phoneNumber = null, params string[] smsCodes)
                : this(phoneNumber == null ? Enumerable.Empty<PhoneEnrollmentDetails>() : new[] { phoneNumber }, smsCodes)
            {
            }

            public FakeInteraction(IEnumerable<PhoneEnrollmentDetails> phoneNumbers, params string[] smsCodes)
            {
                this.phoneNumbers = new Queue<PhoneEnrollmentDetails>(phoneNumbers);
                this.smsCodes = new Queue<string>(smsCodes);
            }

            public int PhoneRequiredConfirmationCount { get; private set; }
            public bool ConfirmPhoneRequiredResult { get; set; }
            public List<bool> InvalidCodeFlags { get; } = new List<bool>();

            public PhoneEnrollmentDetails RequestPhoneNumber() => phoneNumbers.Count == 0 ? null : phoneNumbers.Dequeue();
            public bool ConfirmEmail(string confirmationEmailAddress) => true;
            public bool ConfirmPhoneRequired()
            {
                PhoneRequiredConfirmationCount++;
                return ConfirmPhoneRequiredResult;
            }
            public string RequestSmsCode(bool previousCodeWasInvalid)
            {
                InvalidCodeFlags.Add(previousCodeWasInvalid);
                return smsCodes.Count == 0 ? null : smsCodes.Dequeue();
            }
        }
    }
}
