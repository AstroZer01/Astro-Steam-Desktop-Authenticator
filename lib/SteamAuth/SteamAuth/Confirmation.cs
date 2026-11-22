using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Newtonsoft.Json;

namespace SteamAuth
{
    public sealed class MobileConfirmationTypeConverter : JsonConverter<Confirmation.EMobileConfirmationType>
    {
        public override Confirmation.EMobileConfirmationType ReadJson(
            JsonReader reader,
            Type objectType,
            Confirmation.EMobileConfirmationType existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            if (reader == null)
                return Confirmation.EMobileConfirmationType.Unknown;

            switch (reader.TokenType)
            {
                case JsonToken.Integer:
                    return ParseIntegerToken(reader.Value);

                case JsonToken.String:
                    return ParseStringToken(reader.Value as string);

                case JsonToken.StartObject:
                case JsonToken.StartArray:
                    reader.Skip();
                    return Confirmation.EMobileConfirmationType.Unknown;

                default:
                    return Confirmation.EMobileConfirmationType.Unknown;
            }
        }

        public override void WriteJson(
            JsonWriter writer,
            Confirmation.EMobileConfirmationType value,
            JsonSerializer serializer)
        {
            writer.WriteValue((int)value);
        }

        private static Confirmation.EMobileConfirmationType ParseIntegerToken(object value)
        {
            if (value is long longValue)
                return FromInt64(longValue);

            if (value is int intValue)
                return FromInt32(intValue);

            if (value is BigInteger bigInteger)
            {
                if (bigInteger < int.MinValue || bigInteger > int.MaxValue)
                    return Confirmation.EMobileConfirmationType.Unknown;

                return FromInt32((int)bigInteger);
            }

            if (value is ulong ulongValue)
            {
                if (ulongValue > int.MaxValue)
                    return Confirmation.EMobileConfirmationType.Unknown;

                return FromInt32((int)ulongValue);
            }

            if (value is short shortValue)
                return FromInt32(shortValue);

            if (value is byte byteValue)
                return FromInt32(byteValue);

            return Confirmation.EMobileConfirmationType.Unknown;
        }

        private static Confirmation.EMobileConfirmationType ParseStringToken(string rawValue)
        {
            if (String.IsNullOrWhiteSpace(rawValue) || rawValue.IndexOf(',') >= 0)
                return Confirmation.EMobileConfirmationType.Unknown;

            string trimmed = rawValue.Trim();
            if (Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericValue))
                return FromInt32(numericValue);

            if (String.Equals(trimmed, nameof(Confirmation.EMobileConfirmationType.Unknown), StringComparison.OrdinalIgnoreCase))
                return Confirmation.EMobileConfirmationType.Unknown;

            if (Enum.TryParse(trimmed, true, out Confirmation.EMobileConfirmationType parsed) &&
                parsed != Confirmation.EMobileConfirmationType.Unknown &&
                Enum.IsDefined(typeof(Confirmation.EMobileConfirmationType), parsed))
            {
                return parsed;
            }

            return Confirmation.EMobileConfirmationType.Unknown;
        }

        private static Confirmation.EMobileConfirmationType FromInt64(long value)
        {
            if (value < int.MinValue || value > int.MaxValue)
                return Confirmation.EMobileConfirmationType.Unknown;

            return FromInt32((int)value);
        }

        private static Confirmation.EMobileConfirmationType FromInt32(int value)
        {
            if (value == (int)Confirmation.EMobileConfirmationType.Unknown)
                return Confirmation.EMobileConfirmationType.Unknown;

            return Enum.IsDefined(typeof(Confirmation.EMobileConfirmationType), value)
                ? (Confirmation.EMobileConfirmationType)value
                : Confirmation.EMobileConfirmationType.Unknown;
        }
    }

    public class Confirmation
    {
        [JsonProperty(PropertyName = "id")]
        public ulong ID { get; set; }

        [JsonProperty(PropertyName = "nonce")]
        public ulong Key { get; set; }

        [JsonProperty(PropertyName = "creator_id")]
        public ulong Creator { get; set; }

        [JsonProperty(PropertyName = "headline")]
        public string Headline { get; set; }

        [JsonProperty(PropertyName = "summary")]
        public List<String> Summary { get; set; }

        [JsonProperty(PropertyName = "accept")]
        public string Accept { get; set; }

        [JsonProperty(PropertyName = "cancel")]
        public string Cancel { get; set; }

        [JsonProperty(PropertyName = "icon")]
        public string Icon { get; set; }

        [JsonProperty(PropertyName = "type")]
        [JsonConverter(typeof(MobileConfirmationTypeConverter))]
        public EMobileConfirmationType ConfType { get; set; } = EMobileConfirmationType.Invalid;

        [JsonProperty(PropertyName = "type_name")]
        public string TypeName { get; set; }

        public enum EMobileConfirmationType
        {
            Unknown = -1,
            Invalid = 0,
            Test = 1,
            Trade = 2,
            MarketListing = 3,
            FeatureOptOut = 4,
            PhoneNumberChange = 5,
            AccountRecovery = 6,
            BuildChangeRequest = 7,
            AddUser = 8,
            RegisterApiKey = 9,
            InviteToFamilyGroup = 10,
            JoinFamilyGroup = 11,
            MarketPurchase = 12,
            RequestRefund = 13
        }
    }

    public class ConfirmationsResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("needauth")]
        public bool NeedAuthentication { get; set; }

        [JsonProperty("conf")]
        public Confirmation[] Confirmations { get; set; }
    }
}
