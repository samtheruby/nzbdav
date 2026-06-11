using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;

namespace NzbWebDAV.Services;

/// <summary>
/// Pure scheduling/classification helpers for the background health-check loop.
/// Kept free of database/usenet dependencies so the queue-advancement behavior
/// can be unit-tested directly.
/// </summary>
public static class HealthCheckScheduler
{
    /// <summary>
    /// Cadence used when a check can't complete normally: transient (non-NotFound)
    /// failures, and items whose release date can't yet be resolved. Bounded and
    /// short so the item is retried soon without tight-looping the whole queue.
    /// </summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Connections one health check uses to STAT a file's segments concurrently.
    /// The dedicated health-check connection count is rounded down to a multiple of
    /// this so every check gets a full set; max concurrent checks = connections / 3.
    /// </summary>
    public const int HealthCheckConnectionsPerCheck = 3;

    /// <summary>
    /// Rounds a connection count down to a whole multiple of
    /// <see cref="HealthCheckConnectionsPerCheck"/> (never negative), so the pool
    /// divides evenly into checks with no unusable remainder.
    /// </summary>
    public static int RoundDownToMultipleOfThree(int value)
        => value < 0 ? 0 : value - (value % HealthCheckConnectionsPerCheck);

    /// <summary>
    /// How many health checks may run concurrently, driven by the provider with the
    /// most dedicated health-check connections (the de-facto primary). Returns 0 when
    /// no provider has a usable health-check pool — callers fall back to the shared
    /// streaming pool with a single check at a time.
    /// </summary>
    public static int ComputeMaxConcurrentChecks(UsenetProviderConfig providerConfig)
    {
        var maxConnections = providerConfig.Providers
            .Where(p => p.Type != ProviderType.Disabled)
            .Select(p => RoundDownToMultipleOfThree(p.HealthCheckConnections))
            .DefaultIfEmpty(0)
            .Max();
        return maxConnections / HealthCheckConnectionsPerCheck;
    }

    /// <summary>
    /// Next check time after a successful (healthy) check, using the tiered backoff
    /// schedule: older releases are checked less often.
    /// A null release date resolves to a bounded retry rather than null — a null
    /// here previously made NextHealthCheck null, which re-selected the same item
    /// every tick (tight loop).
    /// </summary>
    public static DateTimeOffset ComputeTieredNextCheck
    (
        DateTimeOffset? releaseDate,
        DateTimeOffset now,
        IReadOnlyList<HealthCheckBackoffTier> tiers
    )
    {
        if (releaseDate is null) return now + RetryInterval;
        if (tiers is null || tiers.Count == 0) return now + TimeSpan.FromDays(1);

        var ageDays = (now - releaseDate.Value).TotalDays;
        foreach (var tier in tiers)
            if (tier.MaxAgeDays is null || ageDays < tier.MaxAgeDays.Value)
                return now + ClampInterval(tier.IntervalDays);

        // no catch-all tier matched — fall back to the last (oldest) band.
        return now + ClampInterval(tiers[^1].IntervalDays);
    }

    // a non-positive interval would re-select the item immediately (tight loop);
    // clamp to at least one day.
    private static TimeSpan ClampInterval(int intervalDays) => TimeSpan.FromDays(Math.Max(1, intervalDays));

    /// <summary>
    /// Next check time after a transient failure (timeout, contention, circuit-breaker).
    /// </summary>
    public static DateTimeOffset ComputeRetryNextCheck(DateTimeOffset now) => now + RetryInterval;

    /// <summary>
    /// Whether a failure is transient and should NOT condemn the file (retry later).
    /// A missing article is authoritative (handled by repair); cancellation is
    /// shutdown/reschedule (handled by the outer loop).
    /// </summary>
    public static bool IsTransientFailure(Exception e)
        => e is not OperationCanceledException && e is not UsenetArticleNotFoundException;

    /// <summary>
    /// Whether an item has failed transiently enough consecutive times to be
    /// marked failed and stop being retried, instead of looping forever.
    /// </summary>
    public static bool IsTerminalFailure(int failureCount, int maxFailures) => failureCount >= maxFailures;

    /// <summary>
    /// The next occurrence of the daily scheduled run time. If the time has already
    /// passed today (or is exactly now), schedules for tomorrow to avoid double-running.
    /// </summary>
    public static DateTime ComputeNextScheduledRun(DateTime now, TimeSpan scheduleTime)
    {
        var todayRun = now.Date + scheduleTime;
        return todayRun > now ? todayRun : todayRun.AddDays(1);
    }
}
