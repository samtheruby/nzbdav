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
    /// Next check time after a successful (healthy) check.
    /// A null release date resolves to a bounded retry rather than null — a null
    /// here previously made NextHealthCheck null, which re-selected the same item
    /// every tick (tight loop).
    /// </summary>
    public static DateTimeOffset ComputeHealthyNextCheck(DateTimeOffset? releaseDate, DateTimeOffset now)
    {
        if (releaseDate is null) return now + RetryInterval;

        // geometric backoff: interval == current file age, doubles each pass.
        // (replaced by the tiered scheme in Module 2.)
        return releaseDate.Value + 2 * (now - releaseDate.Value);
    }

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
}
