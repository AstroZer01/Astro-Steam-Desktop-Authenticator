using Steam_Desktop_Authenticator;
using System;
using System.Reflection;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class SteamSessionFailureTests
    {
        [Fact]
        public void AccessDenied_IsCredentialInvalidOnlyDuringRefresh()
        {
            Assert.False(SteamSessionFailureClassifier.IsInvalidSessionResult(15));
            Assert.Equal(SteamSessionFailureKind.Other, SteamSessionFailureClassifier.ClassifyResult(15));
            // Removal treats Expired as session-invalid, but AccessDenied is still local.
            Assert.Equal(SteamSessionFailureKind.Other,
                SteamSessionFailureClassifier.ClassifyResult(15, expiredResultInvalidatesSession: true));
            Assert.Equal(SteamSessionFailureKind.InvalidSession,
                SteamSessionFailureClassifier.ClassifyRefreshResult(15));
        }

        [Theory]
        [InlineData(5)]
        [InlineData(21)]
        [InlineData(26)]
        [InlineData(34)]
        [InlineData(43)]
        [InlineData(63)]
        [InlineData(73)]
        [InlineData(114)]
        [InlineData(126)]
        public void DefinitiveSessionResults_RemainInvalidForActionsAndRefresh(int result)
        {
            Assert.True(SteamSessionFailureClassifier.IsInvalidSessionResult(result));
            Assert.Equal(SteamSessionFailureKind.InvalidSession, SteamSessionFailureClassifier.ClassifyResult(result));
            Assert.Equal(SteamSessionFailureKind.InvalidSession, SteamSessionFailureClassifier.ClassifyRefreshResult(result));
        }

        [Theory]
        [InlineData("IsDefinitiveSessionFailure", false)]
        [InlineData("IsDefinitiveSessionFailure", true)]
        [InlineData("IsTradeAuthenticationFailure", false)]
        [InlineData("IsTradeAuthenticationFailure", true)]
        public void MainForm_UsesStructuredContextInsteadOfActionErrorText(string methodName, bool wrapped)
        {
            MethodInfo method = typeof(MainForm).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            const string message = "401: invalid token; authorization expired";
            Exception actionFailure = new SteamSessionException(SteamSessionFailureClassifier.ClassifyResult(15), message, 15);
            Exception refreshFailure = new SteamSessionException(SteamSessionFailureClassifier.ClassifyRefreshResult(15), message, 15);
            if (wrapped)
            {
                actionFailure = new Exception(message, actionFailure);
                refreshFailure = new Exception(message, refreshFailure);
            }

            Assert.False((bool)method.Invoke(null, new object[] { actionFailure }));
            Assert.True((bool)method.Invoke(null, new object[] { refreshFailure }));
        }

        [Fact]
        public void TradeConfirmationTokenFailureIsDeferredOnlyWhenRetryRemains()
        {
            MethodInfo method = typeof(MainForm).GetMethod("ShouldDeferTradeConfirmationFailure", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            Exception staleAccessToken = new SteamGuardAccount.WGTokenInvalidException();
            Exception wrappedStaleAccessToken = new Exception("confirmation request failed", staleAccessToken);
            Exception rejectedRefreshToken = new SteamSessionException(SteamSessionFailureKind.InvalidSession, "refresh token rejected");

            Assert.True((bool)method.Invoke(null, new object[] { staleAccessToken, true }));
            Assert.True((bool)method.Invoke(null, new object[] { wrappedStaleAccessToken, true }));
            Assert.False((bool)method.Invoke(null, new object[] { staleAccessToken, false }));
            Assert.False((bool)method.Invoke(null, new object[] { rejectedRefreshToken, true }));
        }

        [Fact]
        public void MainForm_RejectsAccountObjectsFromEarlierReload()
        {
            MethodInfo method = typeof(MainForm).GetMethod("IsCurrentAccountGeneration", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            SteamGuardAccount staleAccount = new SteamGuardAccount { AccountName = "stale" };
            SteamGuardAccount currentAccount = new SteamGuardAccount { AccountName = "current" };

            Assert.True((bool)method.Invoke(null, new object[] { staleAccount, new[] { staleAccount } }));
            Assert.False((bool)method.Invoke(null, new object[] { staleAccount, new[] { currentAccount } }));
        }
    }
}
