using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class TradeConfirmationClassificationTests
    {
        [Fact]
        public void ConfirmationsResponse_DeserializesTradeMarketListingMarketPurchaseAndUnknownTypes()
        {
            const string json = @"{
                ""success"": true,
                ""conf"": [
                    { ""id"": ""101"", ""nonce"": ""201"", ""creator_id"": ""301"", ""type"": 2, ""type_name"": ""Trade"", ""headline"": ""Trade with Alice"", ""summary"": [""You give 1 item""] },
                    { ""id"": ""102"", ""nonce"": ""202"", ""creator_id"": ""302"", ""type"": 3, ""type_name"": ""Market Listing"", ""headline"": ""Sell - Sticker"", ""summary"": [""$1.25""] },
                    { ""id"": ""103"", ""nonce"": ""203"", ""creator_id"": ""303"", ""type"": 12, ""type_name"": ""Market Purchase"", ""headline"": ""Buy Order - Crate"", ""summary"": [""$2.50""] },
                    { ""id"": ""104"", ""nonce"": ""204"", ""creator_id"": ""304"", ""type"": 9, ""type_name"": ""API Key"", ""headline"": ""Create API Key"", ""summary"": [""Web API key""] },
                    { ""id"": ""105"", ""nonce"": ""205"", ""creator_id"": ""305"", ""type"": 999, ""type_name"": ""Future Action"", ""headline"": ""Future"", ""summary"": [""Review""] }
                ]
            }";

            ConfirmationsResponse response = JsonConvert.DeserializeObject<ConfirmationsResponse>(json);

            Assert.NotNull(response);
            Assert.True(response.Success);
            Assert.NotNull(response.Confirmations);
            Assert.Equal(5, response.Confirmations.Length);

            Assert.Equal(Confirmation.EMobileConfirmationType.Trade, response.Confirmations[0].ConfType);
            Assert.Equal("Trade", response.Confirmations[0].TypeName);

            Assert.Equal(Confirmation.EMobileConfirmationType.MarketListing, response.Confirmations[1].ConfType);
            Assert.Equal("Market Listing", response.Confirmations[1].TypeName);

            Assert.Equal(Confirmation.EMobileConfirmationType.MarketPurchase, response.Confirmations[2].ConfType);
            Assert.Equal("Market Purchase", response.Confirmations[2].TypeName);

            Assert.Equal(Confirmation.EMobileConfirmationType.RegisterApiKey, response.Confirmations[3].ConfType);
            Assert.Equal("API Key", response.Confirmations[3].TypeName);

            Assert.Equal(Confirmation.EMobileConfirmationType.Unknown, response.Confirmations[4].ConfType);
            Assert.Equal("Future Action", response.Confirmations[4].TypeName);
        }

        [Theory]
        [InlineData(Confirmation.EMobileConfirmationType.Trade, "trades")]
        [InlineData(Confirmation.EMobileConfirmationType.MarketListing, "market")]
        [InlineData(Confirmation.EMobileConfirmationType.MarketPurchase, "market")]
        [InlineData(Confirmation.EMobileConfirmationType.Invalid, "other")]
        [InlineData(Confirmation.EMobileConfirmationType.Test, "other")]
        [InlineData(Confirmation.EMobileConfirmationType.FeatureOptOut, "other")]
        [InlineData(Confirmation.EMobileConfirmationType.PhoneNumberChange, "other")]
        [InlineData(Confirmation.EMobileConfirmationType.AccountRecovery, "other")]
        [InlineData(Confirmation.EMobileConfirmationType.RegisterApiKey, "other")]
        [InlineData(Confirmation.EMobileConfirmationType.JoinFamilyGroup, "other")]
        [InlineData(Confirmation.EMobileConfirmationType.RequestRefund, "other")]
        [InlineData(Confirmation.EMobileConfirmationType.Unknown, "other")]
        [InlineData((Confirmation.EMobileConfirmationType)999, "other")]
        public void GetCategory_MapsConfirmationTypesToExpectedTabs(
            Confirmation.EMobileConfirmationType confType,
            string expectedCategory)
        {
            Assert.Equal(expectedCategory, TradeConfirmationClassifier.GetCategory(confType));
        }

        [Fact]
        public void GetDisplayLabel_UsesFriendlyLabelForTradeMarketAndOtherTypes()
        {
            Assert.Equal("Trade", TradeConfirmationClassifier.GetDisplayLabel(new Confirmation { ConfType = Confirmation.EMobileConfirmationType.Trade }));
            Assert.Equal("Market", TradeConfirmationClassifier.GetDisplayLabel(new Confirmation { ConfType = Confirmation.EMobileConfirmationType.MarketListing }));
            Assert.Equal("Purchase", TradeConfirmationClassifier.GetDisplayLabel(new Confirmation { ConfType = Confirmation.EMobileConfirmationType.MarketPurchase }));
            Assert.Equal("API Key", TradeConfirmationClassifier.GetDisplayLabel(new Confirmation
            {
                ConfType = Confirmation.EMobileConfirmationType.RegisterApiKey,
                TypeName = "  API Key  "
            }));
            Assert.Equal("Account Recovery", TradeConfirmationClassifier.GetDisplayLabel(new Confirmation
            {
                ConfType = Confirmation.EMobileConfirmationType.AccountRecovery,
                TypeName = "   "
            }));
            Assert.Equal("Future Action", TradeConfirmationClassifier.GetDisplayLabel(new Confirmation
            {
                ConfType = Confirmation.EMobileConfirmationType.Unknown,
                TypeName = "  Future Action  "
            }));
            Assert.Equal("Confirmation", TradeConfirmationClassifier.GetDisplayLabel(new Confirmation
            {
                ConfType = Confirmation.EMobileConfirmationType.Unknown,
                TypeName = "   "
            }));
            Assert.Equal("Confirmation", TradeConfirmationClassifier.GetDisplayLabel(null));
        }

        [Theory]
        [InlineData(Confirmation.EMobileConfirmationType.Trade, "Trade confirmation needed", "trades")]
        [InlineData(Confirmation.EMobileConfirmationType.MarketListing, "Market confirmation needed", "market")]
        [InlineData(Confirmation.EMobileConfirmationType.MarketPurchase, "Purchase confirmation needed", "market")]
        [InlineData(Confirmation.EMobileConfirmationType.AccountRecovery, "Account recovery confirmation needed", "all")]
        [InlineData(Confirmation.EMobileConfirmationType.RegisterApiKey, "API key confirmation needed", "all")]
        [InlineData(Confirmation.EMobileConfirmationType.Unknown, "Steam confirmation needed", "all")]
        [InlineData((Confirmation.EMobileConfirmationType)999, "Steam confirmation needed", "all")]
        public void NotificationMetadata_MatchesConfirmationCategory(
            Confirmation.EMobileConfirmationType confType,
            string expectedTitle,
            string expectedTab)
        {
            Assert.Equal(expectedTitle, TradeConfirmationClassifier.GetNotificationTitle(confType));
            Assert.Equal(expectedTab, TradeConfirmationClassifier.GetNotificationTab(confType));
        }

        [Fact]
        public void CreateWebPayload_IncludesCategoryAndTypeLabelAlongWithExistingFields()
        {
            Confirmation confirmation = new Confirmation
            {
                ID = 103,
                Key = 203,
                Creator = 303,
                ConfType = Confirmation.EMobileConfirmationType.MarketPurchase,
                TypeName = "Market Purchase",
                Headline = "Buy Order - Weapon Case",
                Icon = "https://community.cloudflare.steamstatic.com/icon.png",
                Summary = new List<string> { "Buy 1x Weapon Case for $2.50" }
            };

            object payload = TradeConfirmationClassifier.CreateWebPayload("76561198000000001:103", "main_account", confirmation);
            JObject serialized = JObject.FromObject(payload);

            Assert.Equal("76561198000000001:103", serialized.Value<string>("Id"));
            Assert.Equal("main_account", serialized.Value<string>("AccountName"));
            Assert.Equal("Buy Order - Weapon Case", serialized.Value<string>("Headline"));
            Assert.Equal("Steam purchase", serialized.Value<string>("DefaultHeadline"));
            Assert.Equal("303", serialized.Value<string>("Creator"));
            Assert.Equal("https://community.cloudflare.steamstatic.com/icon.png", serialized.Value<string>("Icon"));
            Assert.Equal(new[] { "Buy 1x Weapon Case for $2.50" }, serialized["Summary"].ToObject<string[]>());
            Assert.Equal("MarketPurchase", serialized.Value<string>("Type"));
            Assert.Equal("market", serialized.Value<string>("Category"));
            Assert.Equal("Purchase", serialized.Value<string>("TypeLabel"));
            Assert.True(serialized.Value<bool>("IsSecuritySensitive"));
            Assert.Equal("Confirm Purchase", serialized.Value<string>("AcceptLabel"));
            Assert.Equal("Cancel", serialized.Value<string>("CancelLabel"));
        }

        [Theory]
        [InlineData(Confirmation.EMobileConfirmationType.MarketListing, true, false, true)]
        [InlineData(Confirmation.EMobileConfirmationType.MarketPurchase, true, false, false)]
        [InlineData(Confirmation.EMobileConfirmationType.Trade, false, true, true)]
        [InlineData(Confirmation.EMobileConfirmationType.MarketPurchase, false, true, false)]
        public void ShouldAutoConfirmTrade_DoesNotAutoConfirmMarketPurchases(
            Confirmation.EMobileConfirmationType confType,
            bool autoConfirmMarket,
            bool autoConfirmTrades,
            bool expectedAutoConfirm)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.AutoConfirmMarketTransactions = autoConfirmMarket;
            manifest.AutoConfirmTrades = autoConfirmTrades;

            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                MethodInfo method = typeof(MainForm).GetMethod("ShouldAutoConfirmTrade", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);

                bool result = (bool)method.Invoke(form, new object[] { new Confirmation { ConfType = confType } });
                Assert.Equal(expectedAutoConfirm, result);
            }
        }

        [Theory]
        [InlineData("all", true, "all")]
        [InlineData("trades", true, "trades")]
        [InlineData("market", true, "market")]
        [InlineData("Market", false, "market")]
        [InlineData(" trades ", false, "trades")]
        [InlineData("other", false, "all")]
        [InlineData("", false, "all")]
        [InlineData(null, false, "all")]
        public void SelectableTabHelpers_ValidateAndNormalizeTabs(string input, bool expectedSelectable, string expectedNormalized)
        {
            Assert.Equal(expectedSelectable, TradeConfirmationClassifier.IsSelectableTab(input));
            Assert.Equal(expectedNormalized, TradeConfirmationClassifier.NormalizeSelectableTab(input));
        }
    }

    [Collection("Manifest storage")]
    public sealed class TradeConfirmationTabPersistenceTests : IDisposable
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-confirmation-tab-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public TradeConfirmationTabPersistenceTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        [Fact]
        public void NewAndLegacyManifests_DefaultSelectedConfirmationTabToAll()
        {
            Manifest fresh = Manifest.GenerateNewManifest(false);
            Assert.Equal(TradeConfirmationClassifier.CategoryAll, fresh.TradeConfirmationSelectedTab);

            string maDir = Path.Combine(dataDirectory, "maFiles");
            Directory.CreateDirectory(maDir);
            File.WriteAllText(
                Path.Combine(maDir, "manifest.json"),
                new JObject { ["encrypted"] = false, ["entries"] = new JArray() }.ToString(Formatting.None));

            Manifest loadedLegacy = Manifest.GetManifest(true);
            Assert.Equal(TradeConfirmationClassifier.CategoryAll, loadedLegacy.TradeConfirmationSelectedTab);
        }

        [Theory]
        [InlineData("\"unexpected-value\"")]
        [InlineData("[]")]
        [InlineData("[\"market\"]")]
        [InlineData("{}")]
        [InlineData("{\"tab\":\"market\"}")]
        [InlineData("123")]
        [InlineData("true")]
        [InlineData("null")]
        public void MalformedOrInvalidPersistedConfirmationTab_NormalizesToAllWithoutCorruptingManifest(string rawJsonValue)
        {
            string maDir = Path.Combine(dataDirectory, "maFiles");
            Directory.CreateDirectory(maDir);
            string manifestJson = "{\"encrypted\":false,\"entries\":[],\"minimize_to_tray\":false,\"auto_confirm_trades\":true,\"trade_confirmation_selected_tab\":" + rawJsonValue + "}";
            File.WriteAllText(Path.Combine(maDir, "manifest.json"), manifestJson);

            Manifest loaded = Manifest.GetManifest(true);
            Assert.Equal(TradeConfirmationClassifier.CategoryAll, loaded.TradeConfirmationSelectedTab);
            Assert.False(loaded.MinimizeToTray);
            Assert.True(loaded.AutoConfirmTrades);
        }

        [Fact]
        public void SelectedConfirmationTab_PersistsAcrossReloadsSettingsSavesAccountSavesAndResetToAll()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveWithResult().Succeeded);

            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                Assert.True(form.PersistTradeConfirmationSelectedTab(TradeConfirmationClassifier.CategoryMarket));
                Assert.Equal(TradeConfirmationClassifier.CategoryMarket, manifest.TradeConfirmationSelectedTab);

                Assert.False(form.PersistTradeConfirmationSelectedTab("other"));
                Assert.Equal(TradeConfirmationClassifier.CategoryMarket, manifest.TradeConfirmationSelectedTab);
            }

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal(TradeConfirmationClassifier.CategoryMarket, reloaded.TradeConfirmationSelectedTab);

            StorageResult unrelatedSave = reloaded.SaveSettingsWithResult(staged => staged.MinimizeToTray = !staged.MinimizeToTray);
            Assert.True(unrelatedSave.Succeeded);
            Assert.Equal(TradeConfirmationClassifier.CategoryMarket, Manifest.GetManifest(true).TradeConfirmationSelectedTab);

            SteamGuardAccount account = new SteamGuardAccount
            {
                AccountName = "tab_persist_user",
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:00000000-0000-0000-0000-000000000001",
                Session = new SessionData
                {
                    SteamID = 76561198000000123UL,
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token"
                }
            };
            Assert.True(reloaded.SaveAccount(account, false).Succeeded);
            Assert.Equal(TradeConfirmationClassifier.CategoryMarket, Manifest.GetManifest(true).TradeConfirmationSelectedTab);

            Manifest reloadedAfterAccount = Manifest.GetManifest(true);
            using (MainForm form = new MainForm(reloadedAfterAccount, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                Assert.True(form.PersistTradeConfirmationSelectedTab(TradeConfirmationClassifier.CategoryTrades));
                Assert.Equal(TradeConfirmationClassifier.CategoryTrades, Manifest.GetManifest(true).TradeConfirmationSelectedTab);

                Assert.True(form.PersistTradeConfirmationSelectedTab(TradeConfirmationClassifier.CategoryAll));
                Assert.Equal(TradeConfirmationClassifier.CategoryAll, Manifest.GetManifest(true).TradeConfirmationSelectedTab);
            }
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }
    }
}
