using System;
using System.Collections.Generic;
using System.Text;
using SteamAuth;

namespace Steam_Desktop_Authenticator
{
    public sealed class ConfirmationTypeMetadata
    {
        public ConfirmationTypeMetadata(
            string badgeLabel,
            string defaultHeadline,
            string notificationTitle,
            bool isSecuritySensitive,
            string defaultAcceptLabel = "Accept",
            string defaultCancelLabel = "Cancel")
        {
            BadgeLabel = badgeLabel;
            DefaultHeadline = defaultHeadline;
            NotificationTitle = notificationTitle;
            IsSecuritySensitive = isSecuritySensitive;
            DefaultAcceptLabel = defaultAcceptLabel;
            DefaultCancelLabel = defaultCancelLabel;
        }

        public string BadgeLabel { get; }
        public string DefaultHeadline { get; }
        public string NotificationTitle { get; }
        public bool IsSecuritySensitive { get; }
        public string DefaultAcceptLabel { get; }
        public string DefaultCancelLabel { get; }
    }

    public static class ConfirmationTypePresentation
    {
        private const int MaxButtonLabelLength = 40;
        private const int MaxBadgeLabelLength = 40;

        private static readonly HashSet<string> AllowedIconHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "avatars.steamstatic.com",
            "avatars.akamai.steamstatic.com",
            "avatars.fastly.steamstatic.com",
            "avatars.cloudflare.steamstatic.com",
            "community.steamstatic.com",
            "community.akamai.steamstatic.com",
            "community.fastly.steamstatic.com",
            "community.cloudflare.steamstatic.com",
            "steamcommunity-a.akamaihd.net",
            "cdn.akamai.steamstatic.com",
            "cdn.cloudflare.steamstatic.com",
            "shared.fastly.steamstatic.com",
            "shared.cloudflare.steamstatic.com",
            "steamcommunity.com"
        };

        private static readonly ConfirmationTypeMetadata TradeMetadata = new ConfirmationTypeMetadata(
            "Trade",
            "Steam trade",
            "Trade confirmation needed",
            isSecuritySensitive: false,
            defaultAcceptLabel: "Accept",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata MarketListingMetadata = new ConfirmationTypeMetadata(
            "Market",
            "Market listing",
            "Market confirmation needed",
            isSecuritySensitive: false,
            defaultAcceptLabel: "Accept",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata MarketPurchaseMetadata = new ConfirmationTypeMetadata(
            "Purchase",
            "Steam purchase",
            "Purchase confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Confirm Purchase",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata RegisterApiKeyMetadata = new ConfirmationTypeMetadata(
            "API Key",
            "Steam Web API key registration",
            "API key confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve API Key",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata InviteToFamilyGroupMetadata = new ConfirmationTypeMetadata(
            "Steam Family",
            "Steam Family invitation",
            "Steam Family confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve Invite",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata JoinFamilyGroupMetadata = new ConfirmationTypeMetadata(
            "Steam Family",
            "Join Steam Family",
            "Steam Family confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve Join",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata RequestRefundMetadata = new ConfirmationTypeMetadata(
            "Refund",
            "Steam refund request",
            "Refund confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve Refund",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata BuildChangeRequestMetadata = new ConfirmationTypeMetadata(
            "Build Change",
            "Steamworks build change",
            "Steamworks confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve Build",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata AddUserMetadata = new ConfirmationTypeMetadata(
            "Add User",
            "Steamworks user addition",
            "Steamworks confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve User",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata PhoneNumberChangeMetadata = new ConfirmationTypeMetadata(
            "Phone Change",
            "Phone number change",
            "Phone change confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve Change",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata AccountRecoveryMetadata = new ConfirmationTypeMetadata(
            "Account Recovery",
            "Account recovery",
            "Account recovery confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve Recovery",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata FeatureOptOutMetadata = new ConfirmationTypeMetadata(
            "Feature Opt-Out",
            "Steam feature opt-out",
            "Steam confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata TestMetadata = new ConfirmationTypeMetadata(
            "Test",
            "Test confirmation",
            "Steam confirmation needed",
            isSecuritySensitive: false,
            defaultAcceptLabel: "Accept",
            defaultCancelLabel: "Cancel");

        private static readonly ConfirmationTypeMetadata FallbackMetadata = new ConfirmationTypeMetadata(
            "Confirmation",
            "Steam confirmation",
            "Steam confirmation needed",
            isSecuritySensitive: true,
            defaultAcceptLabel: "Approve",
            defaultCancelLabel: "Cancel");

        public static ConfirmationTypeMetadata Get(Confirmation.EMobileConfirmationType confType)
        {
            switch (confType)
            {
                case Confirmation.EMobileConfirmationType.Trade:
                    return TradeMetadata;
                case Confirmation.EMobileConfirmationType.MarketListing:
                    return MarketListingMetadata;
                case Confirmation.EMobileConfirmationType.MarketPurchase:
                    return MarketPurchaseMetadata;
                case Confirmation.EMobileConfirmationType.RegisterApiKey:
                    return RegisterApiKeyMetadata;
                case Confirmation.EMobileConfirmationType.InviteToFamilyGroup:
                    return InviteToFamilyGroupMetadata;
                case Confirmation.EMobileConfirmationType.JoinFamilyGroup:
                    return JoinFamilyGroupMetadata;
                case Confirmation.EMobileConfirmationType.RequestRefund:
                    return RequestRefundMetadata;
                case Confirmation.EMobileConfirmationType.BuildChangeRequest:
                    return BuildChangeRequestMetadata;
                case Confirmation.EMobileConfirmationType.AddUser:
                    return AddUserMetadata;
                case Confirmation.EMobileConfirmationType.PhoneNumberChange:
                    return PhoneNumberChangeMetadata;
                case Confirmation.EMobileConfirmationType.AccountRecovery:
                    return AccountRecoveryMetadata;
                case Confirmation.EMobileConfirmationType.FeatureOptOut:
                    return FeatureOptOutMetadata;
                case Confirmation.EMobileConfirmationType.Test:
                    return TestMetadata;
                case Confirmation.EMobileConfirmationType.Invalid:
                case Confirmation.EMobileConfirmationType.Unknown:
                default:
                    return FallbackMetadata;
            }
        }

        public static string SanitizeButtonLabel(string steamLabel, string fallback, bool isSecuritySensitive)
        {
            string safeFallback = SanitizeShortText(fallback, MaxButtonLabelLength);
            if (String.IsNullOrEmpty(safeFallback))
                safeFallback = "Confirm";

            if (isSecuritySensitive)
                return safeFallback;

            string sanitized = SanitizeShortText(steamLabel, MaxButtonLabelLength);
            return String.IsNullOrEmpty(sanitized) ? safeFallback : sanitized;
        }

        public static string SanitizeBadgeLabel(string steamTypeName, string fallback)
        {
            string sanitized = SanitizeShortText(steamTypeName, MaxBadgeLabelLength);
            if (!String.IsNullOrEmpty(sanitized))
                return sanitized;

            string safeFallback = SanitizeShortText(fallback, MaxBadgeLabelLength);
            return String.IsNullOrEmpty(safeFallback) ? "Confirmation" : safeFallback;
        }

        public static string SanitizeIconUrl(string iconUrl)
        {
            if (String.IsNullOrWhiteSpace(iconUrl))
                return null;

            string trimmed = iconUrl.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri))
                return null;

            if (!String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!uri.IsDefaultPort || !String.IsNullOrEmpty(uri.UserInfo))
                return null;

            if (!AllowedIconHosts.Contains(uri.Host))
                return null;

            return uri.AbsoluteUri;
        }

        private static string SanitizeShortText(string text, int maxLength)
        {
            if (String.IsNullOrWhiteSpace(text))
                return null;

            StringBuilder builder = new StringBuilder(Math.Min(text.Length, maxLength));
            bool previousWasSpace = false;
            foreach (char ch in text)
            {
                if (Char.IsControl(ch))
                    continue;

                if (Char.IsWhiteSpace(ch))
                {
                    if (builder.Length > 0 && !previousWasSpace)
                    {
                        if (builder.Length >= maxLength)
                            break;
                        builder.Append(' ');
                        previousWasSpace = true;
                    }
                    continue;
                }

                if (builder.Length >= maxLength)
                    break;

                builder.Append(ch);
                previousWasSpace = false;
            }

            return builder.ToString().Trim();
        }
    }
}
