using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamAuth.Protocol;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class SessionDataTokenExpirationTests
    {
        [Fact]
        public void RepeatedExpiryChecks_ParseUnchangedAccessAndRefreshTokensOnceEach()
        {
            long farFuture = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            SessionData session = new SessionData
            {
                AccessToken = CreateJwt("{\"exp\":" + farFuture + "}"),
                RefreshToken = CreateJwt("{\"exp\":" + farFuture + "}")
            };

            Assert.False(session.IsAccessTokenExpired());
            Assert.Equal(1, session.TokenExpirationParseCount);

            for (int i = 0; i < 10; i++)
                Assert.False(session.IsAccessTokenExpired());
            Assert.Equal(1, session.TokenExpirationParseCount);

            Assert.False(session.IsRefreshTokenExpired());
            Assert.Equal(2, session.TokenExpirationParseCount);

            for (int i = 0; i < 10; i++)
            {
                Assert.False(session.IsRefreshTokenExpired());
                Assert.False(session.IsRefreshTokenAboutToExpire());
                Assert.False(session.IsAccessTokenExpired());
            }

            Assert.Equal(2, session.TokenExpirationParseCount);
        }

        [Fact]
        public void ReassigningEqualStringInstance_RetainsCachedExpiration()
        {
            long farFuture = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeSeconds();
            string token = CreateJwt("{\"exp\":" + farFuture + "}");
            SessionData session = new SessionData { AccessToken = token };

            Assert.False(session.IsAccessTokenExpired());
            Assert.Equal(1, session.TokenExpirationParseCount);

            session.AccessToken = new string(token.ToCharArray());
            Assert.False(session.IsAccessTokenExpired());
            Assert.Equal(1, session.TokenExpirationParseCount);
        }

        [Fact]
        public void UpdatingTokens_ReEvaluatesExpirationAcrossTransitions()
        {
            long past = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            long soon = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds();
            long farFuture = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();

            SessionData session = new SessionData
            {
                AccessToken = CreateJwt("{\"exp\":" + farFuture + "}"),
                RefreshToken = CreateJwt("{\"exp\":" + farFuture + "}")
            };

            Assert.False(session.IsAccessTokenExpired());
            Assert.False(session.IsRefreshTokenExpired());
            Assert.False(session.IsRefreshTokenAboutToExpire());
            Assert.Equal(2, session.TokenExpirationParseCount);

            session.AccessToken = CreateJwt("{\"exp\":" + past + "}");
            Assert.True(session.IsAccessTokenExpired());
            Assert.Equal(3, session.TokenExpirationParseCount);

            session.RefreshToken = CreateJwt("{\"exp\":" + soon + "}");
            Assert.False(session.IsRefreshTokenExpired());
            Assert.True(session.IsRefreshTokenAboutToExpire());
            Assert.Equal(4, session.TokenExpirationParseCount);

            session.RefreshToken = CreateJwt("{\"exp\":" + past + "}");
            Assert.True(session.IsRefreshTokenExpired());
            Assert.True(session.IsRefreshTokenAboutToExpire());
            Assert.Equal(5, session.TokenExpirationParseCount);

            session.AccessToken = null;
            Assert.True(session.IsAccessTokenExpired());
            Assert.Equal(5, session.TokenExpirationParseCount);
        }

        [Theory]
        [MemberData(nameof(MalformedTokens))]
        public void MalformedTokens_RemainExpiredAndCacheBoundedInvalidTokens(string malformedToken, long expectedParses)
        {
            SessionData session = new SessionData
            {
                AccessToken = malformedToken,
                RefreshToken = malformedToken
            };

            Assert.True(session.IsAccessTokenExpired());
            Assert.True(session.IsAccessTokenExpired());
            Assert.True(session.IsRefreshTokenExpired());
            Assert.True(session.IsRefreshTokenAboutToExpire());
            Assert.Equal(expectedParses, session.TokenExpirationParseCount);
        }

        public static IEnumerable<object[]> MalformedTokens()
        {
            yield return new object[] { null, 0L };
            yield return new object[] { String.Empty, 0L };
            yield return new object[] { "   ", 0L };
            yield return new object[] { new string('a', 16 * 1024 + 1), 0L };
            yield return new object[] { "not-a-jwt", 2L };
            yield return new object[] { "a.b", 2L };
            yield return new object[] { "a.b.c.d", 2L };
            yield return new object[] { "a. .c", 2L };
            yield return new object[] { "a.invalid*chars.c", 2L };
            yield return new object[] { "a.abcde.c", 2L }; // base64 length % 4 == 1
            yield return new object[] { "a." + ToBase64Url(new byte[] { 0xFF, 0xFE }) + ".c", 2L }; // invalid UTF-8
            yield return new object[] { CreateJwt("{ invalid json"), 2L };
            yield return new object[] { CreateJwt("{}"), 2L };
            yield return new object[] { CreateJwt("{\"exp\":\"4102444800\"}"), 2L };
            yield return new object[] { CreateJwt("{\"exp\":0}"), 2L };
            yield return new object[] { CreateJwt("{\"exp\":-10}"), 2L };
        }

        [Fact]
        public async Task RefreshAccessToken_UpdatesCachedExpirationForNewTokens()
        {
            long past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            long farFuture = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            string newAccess = CreateJwt("{\"exp\":" + farFuture + "}");
            string newRefresh = CreateJwt("{\"exp\":" + farFuture + "}");
            StaticRefreshTransport transport = new StaticRefreshTransport(newAccess, newRefresh);

            SessionData session = new SessionData(transport)
            {
                SteamID = 76561198000000001UL,
                AccessToken = CreateJwt("{\"exp\":" + past + "}"),
                RefreshToken = CreateJwt("{\"exp\":" + DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds() + "}")
            };

            Assert.True(session.IsAccessTokenExpired());
            Assert.True(session.IsRefreshTokenAboutToExpire());

            await session.RefreshAccessToken(allowRenewal: true);

            Assert.Equal(newAccess, session.AccessToken);
            Assert.Equal(newRefresh, session.RefreshToken);
            Assert.False(session.IsAccessTokenExpired());
            Assert.False(session.IsRefreshTokenAboutToExpire());
        }

        [Fact]
        public void JsonSerialization_DoesNotLeakInternalCacheState()
        {
            long farFuture = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            SessionData session = new SessionData
            {
                SteamID = 76561198000000001UL,
                AccessToken = CreateJwt("{\"exp\":" + farFuture + "}"),
                RefreshToken = CreateJwt("{\"exp\":" + farFuture + "}"),
                SessionID = "session-123"
            };

            Assert.False(session.IsAccessTokenExpired());
            Assert.False(session.IsRefreshTokenExpired());

            string json = JsonConvert.SerializeObject(session);
            JObject parsed = JObject.Parse(json);
            Assert.Equal(4, parsed.Count);
            Assert.NotNull(parsed["SteamID"]);
            Assert.NotNull(parsed["AccessToken"]);
            Assert.NotNull(parsed["RefreshToken"]);
            Assert.NotNull(parsed["SessionID"]);

            SessionData deserialized = JsonConvert.DeserializeObject<SessionData>(json);
            Assert.False(deserialized.IsAccessTokenExpired());
            Assert.False(deserialized.IsRefreshTokenExpired());
        }

        [Fact]
        public async Task ClearingTokenWhileReaderIsPausedBeforeCacheStore_DoesNotRetainClearedCredential()
        {
            long farFuture = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            string initialAccessToken = CreateJwt("{\"exp\":" + farFuture + "}");
            string initialRefreshToken = CreateJwt("{\"exp\":" + farFuture + "}");

            using (ManualResetEventSlim accessReaderPaused = new ManualResetEventSlim(false))
            using (ManualResetEventSlim releaseAccessReader = new ManualResetEventSlim(false))
            {
                SessionData session = new SessionData
                {
                    AccessToken = initialAccessToken,
                    RefreshToken = initialRefreshToken,
                    BeforeTokenExpirationCacheStore = _ =>
                    {
                        accessReaderPaused.Set();
                        releaseAccessReader.Wait(TimeSpan.FromSeconds(5));
                    }
                };

                Task<bool> accessReader = Task.Run(() => session.IsAccessTokenExpired());
                Assert.True(accessReaderPaused.Wait(TimeSpan.FromSeconds(5)));

                session.AccessToken = null;
                releaseAccessReader.Set();

                Assert.False(await accessReader);
                Assert.Null(session.AccessToken);
                Assert.False(session.HasCachedAccessTokenExpiration);
                Assert.True(session.IsAccessTokenExpired());
            }

            using (ManualResetEventSlim refreshReaderPaused = new ManualResetEventSlim(false))
            using (ManualResetEventSlim releaseRefreshReader = new ManualResetEventSlim(false))
            {
                SessionData session = new SessionData
                {
                    RefreshToken = initialRefreshToken,
                    BeforeTokenExpirationCacheStore = _ =>
                    {
                        refreshReaderPaused.Set();
                        releaseRefreshReader.Wait(TimeSpan.FromSeconds(5));
                    }
                };

                Task<bool> refreshReader = Task.Run(() => session.IsRefreshTokenExpired());
                Assert.True(refreshReaderPaused.Wait(TimeSpan.FromSeconds(5)));

                session.RefreshToken = null;
                releaseRefreshReader.Set();

                Assert.False(await refreshReader);
                Assert.Null(session.RefreshToken);
                Assert.False(session.HasCachedRefreshTokenExpiration);
                Assert.True(session.IsRefreshTokenExpired());
            }
        }

        [Fact]
        public async Task ReplacingTokenWhileReaderIsPausedBeforeCacheStore_DoesNotOverwriteReplacement()
        {
            long past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            long farFuture = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            string validToken = CreateJwt("{\"exp\":" + farFuture + "}");
            string expiredToken = CreateJwt("{\"exp\":" + past + "}");

            using (ManualResetEventSlim readerPaused = new ManualResetEventSlim(false))
            using (ManualResetEventSlim releaseReader = new ManualResetEventSlim(false))
            {
                SessionData session = new SessionData
                {
                    AccessToken = validToken,
                    BeforeTokenExpirationCacheStore = token =>
                    {
                        if (String.Equals(token, validToken, StringComparison.Ordinal))
                        {
                            readerPaused.Set();
                            releaseReader.Wait(TimeSpan.FromSeconds(5));
                        }
                    }
                };

                Task<bool> reader = Task.Run(() => session.IsAccessTokenExpired());
                Assert.True(readerPaused.Wait(TimeSpan.FromSeconds(5)));

                session.AccessToken = expiredToken;
                releaseReader.Set();

                Assert.False(await reader);
                Assert.Equal(expiredToken, session.AccessToken);
                Assert.False(session.HasCachedAccessTokenExpiration);
                Assert.True(session.IsAccessTokenExpired());
                Assert.True(session.HasCachedAccessTokenExpiration);
            }
        }

        [Fact]
        public async Task ConcurrentReadsAndUpdates_RemainConsistent()
        {
            long past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            long farFuture = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            string expiredToken = CreateJwt("{\"exp\":" + past + "}");
            string validToken = CreateJwt("{\"exp\":" + farFuture + "}");
            SessionData session = new SessionData
            {
                AccessToken = validToken,
                RefreshToken = validToken
            };

            Task writer = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    session.AccessToken = (i % 2 == 0) ? expiredToken : validToken;
                    session.RefreshToken = (i % 2 == 0) ? validToken : expiredToken;
                }
                session.AccessToken = validToken;
                session.RefreshToken = expiredToken;
            });

            Task[] readers = new Task[4];
            for (int i = 0; i < readers.Length; i++)
            {
                readers[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 200; j++)
                    {
                        _ = session.IsAccessTokenExpired();
                        _ = session.IsRefreshTokenExpired();
                        _ = session.IsRefreshTokenAboutToExpire();
                    }
                });
            }

            await Task.WhenAll(writer, Task.WhenAll(readers));

            Assert.False(session.IsAccessTokenExpired());
            Assert.True(session.IsRefreshTokenExpired());
            Assert.True(session.IsRefreshTokenAboutToExpire());
        }

        private static string CreateJwt(string payloadJson)
        {
            return "header." + ToBase64Url(Encoding.UTF8.GetBytes(payloadJson)) + ".signature";
        }

        private static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private sealed class StaticRefreshTransport : IAuthenticatorProtocolTransport
        {
            private readonly string accessToken;
            private readonly string refreshToken;

            public StaticRefreshTransport(string accessToken, string refreshToken)
            {
                this.accessToken = accessToken;
                this.refreshToken = refreshToken;
            }

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
                IMessage body = new CAuthentication_AccessToken_GenerateForApp_Response
                {
                    AccessToken = this.accessToken,
                    RefreshToken = this.refreshToken
                };
                return Task.FromResult(new SteamProtocolResponse<TResponse>
                {
                    Result = 1,
                    Body = body as TResponse
                });
            }
        }
    }
}
