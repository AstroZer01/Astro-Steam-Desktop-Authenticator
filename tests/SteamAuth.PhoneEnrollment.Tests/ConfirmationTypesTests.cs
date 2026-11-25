using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class ConfirmationTypesTests
    {
        [Theory]
        [InlineData(0, Confirmation.EMobileConfirmationType.Invalid)]
        [InlineData(1, Confirmation.EMobileConfirmationType.Test)]
        [InlineData(2, Confirmation.EMobileConfirmationType.Trade)]
        [InlineData(3, Confirmation.EMobileConfirmationType.MarketListing)]
        [InlineData(4, Confirmation.EMobileConfirmationType.FeatureOptOut)]
        [InlineData(5, Confirmation.EMobileConfirmationType.PhoneNumberChange)]
        [InlineData(6, Confirmation.EMobileConfirmationType.AccountRecovery)]
        [InlineData(7, Confirmation.EMobileConfirmationType.BuildChangeRequest)]
        [InlineData(8, Confirmation.EMobileConfirmationType.AddUser)]
        [InlineData(9, Confirmation.EMobileConfirmationType.RegisterApiKey)]
        [InlineData(10, Confirmation.EMobileConfirmationType.InviteToFamilyGroup)]
        [InlineData(11, Confirmation.EMobileConfirmationType.JoinFamilyGroup)]
        [InlineData(12, Confirmation.EMobileConfirmationType.MarketPurchase)]
        [InlineData(13, Confirmation.EMobileConfirmationType.RequestRefund)]
        public void Converter_DeserializesAllSteamKitConfirmationTypeIntegers(
            int rawValue,
            Confirmation.EMobileConfirmationType expected)
        {
            string json = "{\"id\":\"1\",\"type\":" + rawValue + ",\"headline\":\"Test\"}";
            Confirmation confirmation = JsonConvert.DeserializeObject<Confirmation>(json);

            Assert.NotNull(confirmation);
            Assert.Equal(expected, confirmation.ConfType);
            Assert.Equal("Test", confirmation.Headline);
        }

        [Fact]
        public void Converter_OmittedTypePreservesDefaultInvalid()
        {
            Confirmation confirmation = JsonConvert.DeserializeObject<Confirmation>("{\"id\":\"1\",\"headline\":\"No Type\"}");
            Assert.NotNull(confirmation);
            Assert.Equal(Confirmation.EMobileConfirmationType.Invalid, confirmation.ConfType);
        }

        [Theory]
        [InlineData("{\"id\":\"1\",\"type\":-1,\"headline\":\"neg\"}")]
        [InlineData("{\"id\":\"1\",\"type\":99999999999999999999999999999999,\"headline\":\"bigint\"}")]
        [InlineData("{\"id\":\"1\",\"type\":-99999999999999999999999999999999,\"headline\":\"negbigint\"}")]
        [InlineData("{\"id\":\"1\",\"type\":2147483648,\"headline\":\"int64\"}")]
        [InlineData("{\"id\":\"1\",\"type\":\"Trade,MarketListing\",\"headline\":\"comma\"}")]
        [InlineData("{\"id\":\"1\",\"type\":\"Unknown\",\"headline\":\"unknown-name\"}")]
        [InlineData("{\"id\":\"1\",\"type\":\"-1\",\"headline\":\"neg-string\"}")]
        [InlineData("{\"id\":\"1\",\"type\":{\"nested\":{\"a\":[1,2,3]}},\"headline\":\"object\"}")]
        [InlineData("{\"id\":\"1\",\"type\":[1,2,{\"x\":3}],\"headline\":\"array\"}")]
        [InlineData("{\"id\":\"1\",\"type\":null,\"headline\":\"null\"}")]
        [InlineData("{\"id\":\"1\",\"type\":true,\"headline\":\"bool\"}")]
        [InlineData("{\"id\":\"1\",\"type\":2.5,\"headline\":\"float\"}")]
        public void Converter_ToleratesMalformedOutOfRangeBigIntegerAndNestedTokensAsUnknown(string json)
        {
            Confirmation confirmation = JsonConvert.DeserializeObject<Confirmation>(json);

            Assert.NotNull(confirmation);
            Assert.Equal(Confirmation.EMobileConfirmationType.Unknown, confirmation.ConfType);
            Assert.False(String.IsNullOrEmpty(confirmation.Headline));
        }

        [Theory]
        [InlineData("\"Trade\"", Confirmation.EMobileConfirmationType.Trade)]
        [InlineData("\"marketlisting\"", Confirmation.EMobileConfirmationType.MarketListing)]
        [InlineData("\"12\"", Confirmation.EMobileConfirmationType.MarketPurchase)]
        [InlineData("\"RequestRefund\"", Confirmation.EMobileConfirmationType.RequestRefund)]
        public void Converter_AcceptsValidStringNamesAndNumericStrings(
            string rawTypeJson,
            Confirmation.EMobileConfirmationType expected)
        {
            string json = "{\"id\":\"1\",\"type\":" + rawTypeJson + "}";
            Confirmation confirmation = JsonConvert.DeserializeObject<Confirmation>(json);

            Assert.NotNull(confirmation);
            Assert.Equal(expected, confirmation.ConfType);
        }

        [Theory]
        [InlineData("https://avatars.steamstatic.com/abc.jpg", true)]
        [InlineData("https://community.cloudflare.steamstatic.com/public/images/icon.png", true)]
        [InlineData("https://steamcommunity.com/economy/image/xyz", true)]
        [InlineData("http://avatars.steamstatic.com/abc.jpg", false)]
        [InlineData("https://avatars.steamstatic.com:8443/abc.jpg", false)]
        [InlineData("https://user:pass@avatars.steamstatic.com/abc.jpg", false)]
        [InlineData("https://evil.example.com/icon.png", false)]
        [InlineData("https://avatars.steamstatic.com.evil.com/icon.png", false)]
        [InlineData("javascript:alert(1)", false)]
        [InlineData("data:image/svg+xml,<svg/>", false)]
        public void SanitizeIconUrl_EnforcesHttpsSteamCdnAllowlist(string url, bool expectedAllowed)
        {
            string result = ConfirmationTypePresentation.SanitizeIconUrl(url);
            if (expectedAllowed)
            {
                Assert.NotNull(result);
                Assert.StartsWith("https://", result, StringComparison.Ordinal);
            }
            else
            {
                Assert.Null(result);
            }
        }

        [Fact]
        public void SanitizeButtonLabel_UsesFallbackForSecuritySensitiveAndClampsNonSensitive()
        {
            Assert.Equal("Approve API Key", ConfirmationTypePresentation.SanitizeButtonLabel("Click here to win free skins", "Approve API Key", true));

            string longSteamLabel = "  Accept\r\n\t" + new string('A', 60);
            string sanitized = ConfirmationTypePresentation.SanitizeButtonLabel(longSteamLabel, "Accept", false);
            Assert.True(sanitized.Length <= 40);
            Assert.DoesNotContain("\r", sanitized);
            Assert.DoesNotContain("\n", sanitized);
            Assert.DoesNotContain("\t", sanitized);
        }

        [Theory]
        [InlineData(Confirmation.EMobileConfirmationType.Unknown, false)]
        [InlineData(Confirmation.EMobileConfirmationType.Invalid, false)]
        [InlineData(Confirmation.EMobileConfirmationType.Test, false)]
        [InlineData(Confirmation.EMobileConfirmationType.Trade, true)]
        [InlineData(Confirmation.EMobileConfirmationType.MarketListing, true)]
        [InlineData(Confirmation.EMobileConfirmationType.FeatureOptOut, false)]
        [InlineData(Confirmation.EMobileConfirmationType.PhoneNumberChange, false)]
        [InlineData(Confirmation.EMobileConfirmationType.AccountRecovery, false)]
        [InlineData(Confirmation.EMobileConfirmationType.BuildChangeRequest, false)]
        [InlineData(Confirmation.EMobileConfirmationType.AddUser, false)]
        [InlineData(Confirmation.EMobileConfirmationType.RegisterApiKey, false)]
        [InlineData(Confirmation.EMobileConfirmationType.InviteToFamilyGroup, false)]
        [InlineData(Confirmation.EMobileConfirmationType.JoinFamilyGroup, false)]
        [InlineData(Confirmation.EMobileConfirmationType.MarketPurchase, false)]
        [InlineData(Confirmation.EMobileConfirmationType.RequestRefund, false)]
        public void ShouldAutoConfirmTrade_AllowsOnlyTradeAndMarketListing(
            Confirmation.EMobileConfirmationType confType,
            bool expectedAllowed)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.AutoConfirmTrades = true;
            manifest.AutoConfirmMarketTransactions = true;

            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                MethodInfo method = typeof(MainForm).GetMethod("ShouldAutoConfirmTrade", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);

                bool result = (bool)method.Invoke(form, new object[] { new Confirmation { ConfType = confType } });
                Assert.Equal(expectedAllowed, result);
            }
        }

        [Fact]
        public void CreateWebPayload_DropsUntrustedIconAndMarksSecuritySensitiveConfirmations()
        {
            Confirmation apiKeyConf = new Confirmation
            {
                ID = 500,
                ConfType = Confirmation.EMobileConfirmationType.RegisterApiKey,
                Icon = "https://attacker.invalid/phish.png",
                Accept = "Allow Everything",
                Cancel = "Nope"
            };

            JObject payload = JObject.FromObject(TradeConfirmationClassifier.CreateWebPayload("1:500", "acct", apiKeyConf));
            Assert.Null(payload.Value<string>("Icon"));
            Assert.True(payload.Value<bool>("IsSecuritySensitive"));
            Assert.Equal("API Key", payload.Value<string>("TypeLabel"));
            Assert.Equal("Steam Web API key registration", payload.Value<string>("DefaultHeadline"));
            Assert.Equal("Approve API Key", payload.Value<string>("AcceptLabel"));
            Assert.Equal("Cancel", payload.Value<string>("CancelLabel"));
        }
    }
}
