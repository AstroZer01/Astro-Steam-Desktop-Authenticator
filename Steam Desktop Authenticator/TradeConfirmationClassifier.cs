using System;
using Newtonsoft.Json;
using SteamAuth;

namespace Steam_Desktop_Authenticator
{
    internal sealed class SafeTradeConfirmationTabJsonConverter : JsonConverter<string>
    {
        public override string ReadJson(
            JsonReader reader,
            Type objectType,
            string existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            if (reader == null)
                return TradeConfirmationClassifier.CategoryAll;

            if (reader.TokenType == JsonToken.String)
                return TradeConfirmationClassifier.NormalizeSelectableTab((string)reader.Value);

            if (reader.TokenType == JsonToken.Null)
                return TradeConfirmationClassifier.CategoryAll;

            reader.Skip();
            return TradeConfirmationClassifier.CategoryAll;
        }

        public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
        {
            writer.WriteValue(TradeConfirmationClassifier.NormalizeSelectableTab(value));
        }
    }

    internal static class TradeConfirmationClassifier
    {
        public const string CategoryAll = "all";
        public const string CategoryTrades = "trades";
        public const string CategoryMarket = "market";
        public const string CategoryOther = "other";

        public static bool IsSelectableTab(string tabName)
        {
            return String.Equals(tabName, CategoryAll, StringComparison.Ordinal) ||
                String.Equals(tabName, CategoryTrades, StringComparison.Ordinal) ||
                String.Equals(tabName, CategoryMarket, StringComparison.Ordinal);
        }

        public static string NormalizeSelectableTab(string tabName)
        {
            string normalized = tabName?.Trim().ToLowerInvariant();
            return IsSelectableTab(normalized) ? normalized : CategoryAll;
        }

        public static bool IsTradeConfirmation(Confirmation.EMobileConfirmationType confType)
        {
            return confType == Confirmation.EMobileConfirmationType.Trade;
        }

        public static bool IsMarketConfirmation(Confirmation.EMobileConfirmationType confType)
        {
            return confType == Confirmation.EMobileConfirmationType.MarketListing ||
                confType == Confirmation.EMobileConfirmationType.MarketPurchase;
        }

        public static string GetCategory(Confirmation.EMobileConfirmationType confType)
        {
            if (IsTradeConfirmation(confType))
                return CategoryTrades;

            if (IsMarketConfirmation(confType))
                return CategoryMarket;

            return CategoryOther;
        }

        public static string GetDisplayLabel(Confirmation confirmation)
        {
            if (confirmation == null)
                return "Confirmation";

            if (IsTradeConfirmation(confirmation.ConfType))
                return "Trade";

            if (IsMarketConfirmation(confirmation.ConfType))
                return "Market";

            string typeName = confirmation.TypeName?.Trim();
            return String.IsNullOrEmpty(typeName) ? "Confirmation" : typeName;
        }

        public static string GetNotificationTitle(Confirmation.EMobileConfirmationType confType)
        {
            if (IsMarketConfirmation(confType))
                return "Market confirmation needed";

            if (IsTradeConfirmation(confType))
                return "Trade confirmation needed";

            return "Steam confirmation needed";
        }

        public static string GetNotificationTab(Confirmation.EMobileConfirmationType confType)
        {
            string category = GetCategory(confType);
            return category == CategoryTrades || category == CategoryMarket
                ? category
                : CategoryAll;
        }

        public static object CreateWebPayload(string id, string accountName, Confirmation confirmation)
        {
            Confirmation.EMobileConfirmationType confType = confirmation?.ConfType ?? Confirmation.EMobileConfirmationType.Invalid;
            return new
            {
                Id = id,
                AccountName = accountName,
                Headline = confirmation?.Headline,
                Creator = confirmation?.Creator.ToString() ?? "0",
                Icon = confirmation?.Icon,
                Summary = confirmation?.Summary,
                Type = confType.ToString(),
                Category = GetCategory(confType),
                TypeLabel = GetDisplayLabel(confirmation)
            };
        }
    }
}
