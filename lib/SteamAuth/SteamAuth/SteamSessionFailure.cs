using System;

namespace SteamAuth
{
    /// <summary>
    /// Describes failures returned while using or renewing a saved Steam session.
    /// </summary>
    public enum SteamSessionFailureKind
    {
        Other,
        InvalidSession,
        RateLimited,
        Transient
    }

    /// <summary>
    /// Preserves Steam's EResult so callers can distinguish revoked sessions from
    /// temporary service failures without relying on localized error text.
    /// </summary>
    public sealed class SteamSessionException : Exception
    {
        public SteamSessionException(SteamSessionFailureKind kind, string message, int result = 0)
            : base(message)
        {
            Kind = kind;
            Result = result;
        }

        public SteamSessionException(SteamSessionFailureKind kind, string message, int result, Exception innerException)
            : base(message, innerException)
        {
            Kind = kind;
            Result = result;
        }

        public SteamSessionFailureKind Kind { get; }

        public int Result { get; }
    }

    public static class SteamSessionFailureClassifier
    {
        public static bool IsInvalidSessionResult(int result)
        {
            switch (result)
            {
                // Invalid password, not logged on, revoked,
                // logon replaced, account disabled/denied/locked/deleted, and
                // invalid cached credentials all make the saved session unusable.
                case 5:
                case 21:
                case 26:
                case 34:
                case 43:
                case 63:
                case 73:
                case 114:
                case 126:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsRateLimitedResult(int result)
        {
            return result == 84 || result == 87;
        }

        public static bool IsTransientResult(int result)
        {
            // Fail, busy, timeout, service unavailable, try another CM, and
            // remote-call failure are service/transport conditions, not proof
            // that the saved account session was revoked.
            switch (result)
            {
                case 2:
                case 10:
                case 16:
                case 20:
                case 48:
                case 55:
                    return true;
                default:
                    return false;
            }
        }

        public static SteamSessionFailureKind ClassifyResult(int result, bool expiredResultInvalidatesSession = false)
        {
            if (IsInvalidSessionResult(result) || (expiredResultInvalidatesSession && result == 27))
                return SteamSessionFailureKind.InvalidSession;
            if (IsRateLimitedResult(result))
                return SteamSessionFailureKind.RateLimited;
            if (IsTransientResult(result))
                return SteamSessionFailureKind.Transient;
            return SteamSessionFailureKind.Other;
        }

        public static SteamSessionFailureKind ClassifyRefreshResult(int result)
        {
            // AccessDenied rejects the saved credential only at the token-refresh
            // endpoint. Action endpoints can deny an operation on a valid session.
            return result == 15
                ? SteamSessionFailureKind.InvalidSession
                : ClassifyResult(result, expiredResultInvalidatesSession: true);
        }
    }
}
