using SteamAuth.Protocol;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamAuth
{
    /// <summary>
    /// Aligns the system clock with Steam's typed two-factor time service.
    /// </summary>
    public class TimeAligner
    {
        private static bool _aligned = false;
        private static int _timeDifference = 0;
        private static DateTimeOffset _lastAlignmentFailureUtc = DateTimeOffset.MinValue;
        private static readonly SemaphoreSlim _alignmentLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan AlignmentRetryBackoff = TimeSpan.FromSeconds(30);

        public static long GetSteamTime()
        {
            if (!_aligned && CanAttemptAlignment())
                AlignTime();

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _timeDifference;
        }

        public static async Task<long> GetSteamTimeAsync(CancellationToken cancellationToken = default)
        {
            if (!_aligned && CanAttemptAlignment())
                await AlignTimeAsync(cancellationToken);

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _timeDifference;
        }

        public static void AlignTime()
        {
            AlignTimeAsync().GetAwaiter().GetResult();
        }

        public static async Task AlignTimeAsync(CancellationToken cancellationToken = default)
        {
            await _alignmentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_aligned || !CanAttemptAlignment())
                    return;

                long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SteamProtobufAuthenticatorTransport transport = new SteamProtobufAuthenticatorTransport();
                SteamProtocolResponse<CTwoFactor_Time_Response> response = await transport.SendAsync(
                    "ITwoFactorService",
                    "QueryTime",
                    new CTwoFactor_Time_Request { SenderTime = (ulong)currentTime },
                    null,
                    CTwoFactor_Time_Response.Parser,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (response == null || response.Result != 1 || response.Body == null || response.Body.ServerTime == 0)
                {
                    RecordAlignmentFailure();
                    return;
                }

                _timeDifference = (int)((long)response.Body.ServerTime - currentTime);
                _aligned = true;
                _lastAlignmentFailureUtc = DateTimeOffset.MinValue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Code generation can safely use the local clock if Steam's time endpoint is unavailable.
                SteamAuthDiagnostics.Log(ex, "Steam time alignment failed; local time will be used until the retry backoff expires.");
                RecordAlignmentFailure();
            }
            finally
            {
                _alignmentLock.Release();
            }
        }

        private static bool CanAttemptAlignment()
        {
            return DateTimeOffset.UtcNow - _lastAlignmentFailureUtc >= AlignmentRetryBackoff;
        }

        private static void RecordAlignmentFailure()
        {
            _lastAlignmentFailureUtc = DateTimeOffset.UtcNow;
        }
    }
}
