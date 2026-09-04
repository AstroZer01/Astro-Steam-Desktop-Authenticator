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
        private static int _aligned = 0;
        private static int _timeDifference = 0;
        private static long _lastAlignmentFailureUtcTicks = DateTime.MinValue.Ticks;
        private static int _backgroundAlignmentScheduled = 0;
        private static readonly SemaphoreSlim _alignmentLock = new SemaphoreSlim(1, 1);
        private static readonly object _backgroundAlignmentLock = new object();
        private static Task _backgroundAlignmentTask = Task.CompletedTask;
        private static readonly TimeSpan AlignmentRetryBackoff = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FirstAlignmentWait = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxAllowedTimeDifference = TimeSpan.FromDays(1);

        public static long GetSteamTime()
        {
            if (Volatile.Read(ref _aligned) == 0 && CanAttemptAlignment())
            {
                bool startedAlignment;
                Task alignmentTask = StartBackgroundAlignment(out startedAlignment);
                if (startedAlignment)
                {
                    try
                    {
                        alignmentTask.Wait(FirstAlignmentWait);
                    }
                    catch (AggregateException ex)
                    {
                        SteamAuthDiagnostics.Log(ex.Flatten(), "Steam time alignment failed during the initial bounded wait.");
                    }
                }
            }

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Volatile.Read(ref _timeDifference);
        }

        public static async Task<long> GetSteamTimeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _aligned) == 0 && CanAttemptAlignment())
                await AlignTimeAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Volatile.Read(ref _timeDifference);
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
                if (Volatile.Read(ref _aligned) != 0 || !CanAttemptAlignment())
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

                if (response.Body.ServerTime > (ulong)Int64.MaxValue)
                {
                    RecordAlignmentFailure();
                    return;
                }

                long difference = (long)response.Body.ServerTime - currentTime;
                if (difference < -MaxAllowedTimeDifference.TotalSeconds ||
                    difference > MaxAllowedTimeDifference.TotalSeconds)
                {
                    RecordAlignmentFailure();
                    return;
                }

                Volatile.Write(ref _timeDifference, (int)difference);
                Interlocked.Exchange(ref _lastAlignmentFailureUtcTicks, DateTime.MinValue.Ticks);
                Volatile.Write(ref _aligned, 1);
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
            long lastFailureTicks = Interlocked.Read(ref _lastAlignmentFailureUtcTicks);
            return DateTime.UtcNow.Ticks - lastFailureTicks >= AlignmentRetryBackoff.Ticks;
        }

        private static void RecordAlignmentFailure()
        {
            Interlocked.Exchange(ref _lastAlignmentFailureUtcTicks, DateTime.UtcNow.Ticks);
        }

        private static Task StartBackgroundAlignment(out bool startedAlignment)
        {
            lock (_backgroundAlignmentLock)
            {
                if (Volatile.Read(ref _backgroundAlignmentScheduled) != 0)
                {
                    startedAlignment = false;
                    return _backgroundAlignmentTask;
                }

                Volatile.Write(ref _backgroundAlignmentScheduled, 1);
                _backgroundAlignmentTask = AlignInBackgroundAsync();
                startedAlignment = true;
                return _backgroundAlignmentTask;
            }
        }

        private static async Task AlignInBackgroundAsync()
        {
            try
            {
                await AlignTimeAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _backgroundAlignmentScheduled, 0);
            }
        }
    }
}
