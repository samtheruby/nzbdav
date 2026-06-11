using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;

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
