using System;
using System.Collections.Generic;
using System.Linq;

namespace Steam_Desktop_Authenticator
{
    public sealed class AccountConfirmationSettings
    {
        public bool AutoConfirmOverrideEnabled { get; set; }
        public bool AutoConfirmTrades { get; set; }
        public bool AutoConfirmMarketTransactions { get; set; }
        public int? TradeConfirmationCheckInterval { get; set; }
    }

    public sealed class ResolvedAccountConfirmationPolicy
    {
        public ResolvedAccountConfirmationPolicy(
            bool overrideEnabled,
            int intervalSeconds,
            bool autoConfirmTrades,
            bool autoConfirmMarketTransactions)
        {
            OverrideEnabled = overrideEnabled;
            IntervalSeconds = intervalSeconds;
            AutoConfirmTrades = autoConfirmTrades;
            AutoConfirmMarketTransactions = autoConfirmMarketTransactions;
        }

        public bool OverrideEnabled { get; }
        public int IntervalSeconds { get; }
        public bool AutoConfirmTrades { get; }
        public bool AutoConfirmMarketTransactions { get; }
        public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);
    }

    public enum AccountScanHealthState
    {
        Idle,
        Ok,
        Warning
    }

    public sealed class AccountScanHealthSnapshot
    {
        public AccountScanHealthSnapshot(
            ulong steamId,
            AccountScanHealthState state,
            int consecutiveFailures,
            DateTime? lastSuccessUtc,
            DateTime? lastErrorUtc,
            string lastErrorSummary,
            DateTime nextCheckUtc)
        {
            SteamId = steamId;
            State = state;
            ConsecutiveFailures = consecutiveFailures;
            LastSuccessUtc = lastSuccessUtc;
            LastErrorUtc = lastErrorUtc;
            LastErrorSummary = lastErrorSummary;
            NextCheckUtc = nextCheckUtc;
        }

        public ulong SteamId { get; }
        public AccountScanHealthState State { get; }
        public int ConsecutiveFailures { get; }
        public DateTime? LastSuccessUtc { get; }
        public DateTime? LastErrorUtc { get; }
        public string LastErrorSummary { get; }
        public DateTime NextCheckUtc { get; }
    }

    public sealed class TradeConfirmationScheduler
    {
        public static readonly TimeSpan MinimumGlobalDispatchSpacing = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan MinimumRateLimitDelay = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan MaximumRateLimitDelay = TimeSpan.FromMinutes(30);
        public const int MaximumPositiveJitterMilliseconds = 1000;

        private sealed class AccountScheduleState
        {
            public ulong SteamId;
            public DateTime NextCheckUtc;
            public DateTime? LastSuccessUtc;
            public DateTime? LastErrorUtc;
            public string LastErrorSummary;
            public int ConsecutiveFailures;
            public AccountScanHealthState HealthState;
        }

        private readonly object syncLock = new object();
        private readonly Func<DateTime> utcNowProvider;
        private readonly Func<int> jitterMillisecondsProvider;
        private readonly Dictionary<ulong, AccountScheduleState> states = new Dictionary<ulong, AccountScheduleState>();
        private DateTime globalRateLimitedUntilUtc = DateTime.MinValue;
        private DateTime lastGlobalDispatchUtc = DateTime.MinValue;

        public TradeConfirmationScheduler(
            Func<DateTime> utcNowProvider = null,
            Func<int> jitterMillisecondsProvider = null)
        {
            this.utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
            this.jitterMillisecondsProvider = jitterMillisecondsProvider ?? (() => Random.Shared.Next(0, MaximumPositiveJitterMilliseconds + 1));
        }

        public DateTime GlobalRateLimitedUntilUtc
        {
            get
            {
                lock (syncLock)
                    return globalRateLimitedUntilUtc;
            }
        }

        public bool TryGetRateLimitRemaining(out TimeSpan remaining)
        {
            lock (syncLock)
            {
                DateTime now = utcNowProvider();
                if (now >= globalRateLimitedUntilUtc)
                {
                    remaining = TimeSpan.Zero;
                    return false;
                }

                remaining = globalRateLimitedUntilUtc - now;
                return true;
            }
        }

        public DateTime ApplyRateLimit(TimeSpan requestedDelay)
        {
            TimeSpan clampedDelay = requestedDelay;
            if (clampedDelay < MinimumRateLimitDelay)
                clampedDelay = MinimumRateLimitDelay;
            if (clampedDelay > MaximumRateLimitDelay)
                clampedDelay = MaximumRateLimitDelay;

            lock (syncLock)
            {
                DateTime proposedUntil = utcNowProvider().Add(clampedDelay);
                if (proposedUntil > globalRateLimitedUntilUtc)
                    globalRateLimitedUntilUtc = proposedUntil;

                foreach (AccountScheduleState state in states.Values)
                {
                    if (state.NextCheckUtc < globalRateLimitedUntilUtc)
                        state.NextCheckUtc = globalRateLimitedUntilUtc;
                }

                return globalRateLimitedUntilUtc;
            }
        }

        public void RecordGlobalDispatch()
        {
            lock (syncLock)
            {
                lastGlobalDispatchUtc = utcNowProvider();
            }
        }

        public TimeSpan GetRemainingGlobalDispatchSpacing()
        {
            lock (syncLock)
            {
                if (lastGlobalDispatchUtc == DateTime.MinValue)
                    return TimeSpan.Zero;

                DateTime allowedAt = lastGlobalDispatchUtc.Add(MinimumGlobalDispatchSpacing);
                DateTime now = utcNowProvider();
                return allowedAt > now ? allowedAt - now : TimeSpan.Zero;
            }
        }

        public void RecordManualRefreshHealth(ulong steamId)
        {
            if (steamId == 0)
                return;

            lock (syncLock)
            {
                DateTime now = utcNowProvider();
                AccountScheduleState state = GetOrCreateStateLocked(steamId, now);
                state.ConsecutiveFailures = 0;
                state.HealthState = AccountScanHealthState.Ok;
                state.LastSuccessUtc = now;
                state.LastErrorSummary = null;
            }
        }

        public bool CanDispatchGloballyNow()
        {
            lock (syncLock)
            {
                DateTime now = utcNowProvider();
                if (now < globalRateLimitedUntilUtc)
                    return false;

                if (lastGlobalDispatchUtc != DateTime.MinValue &&
                    now < lastGlobalDispatchUtc.Add(MinimumGlobalDispatchSpacing))
                {
                    return false;
                }

                return true;
            }
        }

        public void PruneRemovedAccounts(IEnumerable<ulong> activeSteamIds)
        {
            HashSet<ulong> activeSet = new HashSet<ulong>(activeSteamIds ?? Enumerable.Empty<ulong>());
            lock (syncLock)
            {
                foreach (ulong steamId in states.Keys.Where(id => !activeSet.Contains(id)).ToArray())
                    states.Remove(steamId);
            }
        }

        public bool TrySelectNextDueAccount(
            IReadOnlyList<ulong> eligibleSteamIds,
            Func<ulong, bool> isCustomOverride,
            int globalIntervalSeconds,
            out ulong selectedSteamId)
        {
            selectedSteamId = 0;
            if (eligibleSteamIds == null || eligibleSteamIds.Count == 0)
                return false;

            int clampedGlobalInterval = Math.Clamp(globalIntervalSeconds, 3, 3600);
            Func<ulong, bool> overrideResolver = isCustomOverride ?? (_ => false);

            lock (syncLock)
            {
                DateTime now = utcNowProvider();
                if (now < globalRateLimitedUntilUtc)
                    return false;

                if (lastGlobalDispatchUtc != DateTime.MinValue &&
                    now < lastGlobalDispatchUtc.Add(MinimumGlobalDispatchSpacing))
                {
                    return false;
                }

                int inheritedSlotIndex = 0;
                for (int i = 0; i < eligibleSteamIds.Count; i++)
                {
                    ulong steamId = eligibleSteamIds[i];
                    if (steamId == 0)
                        continue;

                    bool custom = overrideResolver(steamId);
                    if (!states.ContainsKey(steamId))
                    {
                        DateTime initialNextUtc = custom
                            ? now
                            : now.AddSeconds((long)inheritedSlotIndex * clampedGlobalInterval);
                        if (initialNextUtc < globalRateLimitedUntilUtc)
                            initialNextUtc = globalRateLimitedUntilUtc;

                        states[steamId] = new AccountScheduleState
                        {
                            SteamId = steamId,
                            NextCheckUtc = initialNextUtc,
                            HealthState = AccountScanHealthState.Idle
                        };
                    }

                    if (!custom)
                        inheritedSlotIndex++;
                }

                AccountScheduleState bestCandidate = null;
                for (int i = 0; i < eligibleSteamIds.Count; i++)
                {
                    ulong steamId = eligibleSteamIds[i];
                    if (steamId == 0 || !states.TryGetValue(steamId, out AccountScheduleState state))
                        continue;

                    if (state.NextCheckUtc > now)
                        continue;

                    if (bestCandidate == null || state.NextCheckUtc < bestCandidate.NextCheckUtc)
                        bestCandidate = state;
                }

                if (bestCandidate == null)
                    return false;

                selectedSteamId = bestCandidate.SteamId;
                return true;
            }
        }

        public void RecordSuccess(ulong steamId, TimeSpan accountInterval)
        {
            if (steamId == 0)
                return;

            TimeSpan safeInterval = accountInterval < TimeSpan.FromSeconds(3)
                ? TimeSpan.FromSeconds(3)
                : accountInterval;

            lock (syncLock)
            {
                DateTime now = utcNowProvider();
                AccountScheduleState state = GetOrCreateStateLocked(steamId, now);
                state.ConsecutiveFailures = 0;
                state.HealthState = AccountScanHealthState.Ok;
                state.LastSuccessUtc = now;
                state.LastErrorSummary = null;

                int jitterMs = Math.Clamp(jitterMillisecondsProvider(), 0, MaximumPositiveJitterMilliseconds);
                DateTime scheduledUtc = now.Add(safeInterval).AddMilliseconds(jitterMs);
                state.NextCheckUtc = MaxUtc(globalRateLimitedUntilUtc, scheduledUtc);
            }
        }

        public void RecordTransientFailure(ulong steamId, TimeSpan accountInterval, string errorSummary)
        {
            if (steamId == 0)
                return;

            TimeSpan safeInterval = accountInterval < TimeSpan.FromSeconds(3)
                ? TimeSpan.FromSeconds(3)
                : accountInterval;

            lock (syncLock)
            {
                DateTime now = utcNowProvider();
                AccountScheduleState state = GetOrCreateStateLocked(steamId, now);
                state.ConsecutiveFailures++;
                state.HealthState = AccountScanHealthState.Warning;
                state.LastErrorUtc = now;
                state.LastErrorSummary = String.IsNullOrWhiteSpace(errorSummary)
                    ? "Last confirmation check did not complete."
                    : errorSummary.Trim();

                double exponentialBackoffSeconds = Math.Min(30.0 * Math.Pow(2.0, Math.Min(state.ConsecutiveFailures - 1, 10)), 900.0);
                double delaySeconds = Math.Max(safeInterval.TotalSeconds, exponentialBackoffSeconds);
                DateTime scheduledUtc = now.AddSeconds(delaySeconds);
                state.NextCheckUtc = MaxUtc(globalRateLimitedUntilUtc, scheduledUtc);
            }
        }

        public void DeferTemporarily(ulong steamId, TimeSpan delay)
        {
            if (steamId == 0)
                return;

            TimeSpan safeDelay = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
            lock (syncLock)
            {
                DateTime now = utcNowProvider();
                AccountScheduleState state = GetOrCreateStateLocked(steamId, now);
                DateTime deferredUtc = now.Add(safeDelay);
                if (state.NextCheckUtc < deferredUtc)
                    state.NextCheckUtc = deferredUtc;
                if (state.NextCheckUtc < globalRateLimitedUntilUtc)
                    state.NextCheckUtc = globalRateLimitedUntilUtc;
            }
        }

        public void OnSettingsChanged(ulong steamId, TimeSpan newInterval)
        {
            if (steamId == 0)
                return;

            TimeSpan safeInterval = newInterval < TimeSpan.FromSeconds(3)
                ? TimeSpan.FromSeconds(3)
                : newInterval;

            lock (syncLock)
            {
                if (!states.TryGetValue(steamId, out AccountScheduleState state))
                    return;

                DateTime now = utcNowProvider();
                DateTime cappedNextUtc = now.Add(safeInterval);
                DateTime clamped = state.NextCheckUtc < cappedNextUtc ? state.NextCheckUtc : cappedNextUtc;
                state.NextCheckUtc = MaxUtc(globalRateLimitedUntilUtc, clamped);
            }
        }

        public void OnGlobalSettingsChanged(Func<ulong, TimeSpan> intervalResolver)
        {
            if (intervalResolver == null)
                return;

            lock (syncLock)
            {
                DateTime now = utcNowProvider();
                foreach (AccountScheduleState state in states.Values)
                {
                    TimeSpan resolved = intervalResolver(state.SteamId);
                    TimeSpan safeInterval = resolved < TimeSpan.FromSeconds(3)
                        ? TimeSpan.FromSeconds(3)
                        : resolved;
                    DateTime cappedNextUtc = now.Add(safeInterval);
                    DateTime clamped = state.NextCheckUtc < cappedNextUtc ? state.NextCheckUtc : cappedNextUtc;
                    state.NextCheckUtc = MaxUtc(globalRateLimitedUntilUtc, clamped);
                }
            }
        }

        public AccountScanHealthSnapshot GetHealthSnapshot(ulong steamId)
        {
            lock (syncLock)
            {
                if (!states.TryGetValue(steamId, out AccountScheduleState state))
                {
                    return new AccountScanHealthSnapshot(
                        steamId,
                        AccountScanHealthState.Idle,
                        0,
                        null,
                        null,
                        null,
                        DateTime.MinValue);
                }

                return new AccountScanHealthSnapshot(
                    state.SteamId,
                    state.HealthState,
                    state.ConsecutiveFailures,
                    state.LastSuccessUtc,
                    state.LastErrorUtc,
                    state.LastErrorSummary,
                    state.NextCheckUtc);
            }
        }

        private AccountScheduleState GetOrCreateStateLocked(ulong steamId, DateTime now)
        {
            if (!states.TryGetValue(steamId, out AccountScheduleState state))
            {
                state = new AccountScheduleState
                {
                    SteamId = steamId,
                    NextCheckUtc = MaxUtc(globalRateLimitedUntilUtc, now),
                    HealthState = AccountScanHealthState.Idle
                };
                states[steamId] = state;
            }
            return state;
        }

        private static DateTime MaxUtc(DateTime left, DateTime right)
        {
            return left >= right ? left : right;
        }
    }
}
