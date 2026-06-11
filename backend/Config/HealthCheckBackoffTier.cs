namespace NzbWebDAV.Config;

/// <summary>
/// One band of the health-check backoff schedule: files whose release age is
/// less than <see cref="MaxAgeDays"/> are re-checked every <see cref="IntervalDays"/>.
/// A null <see cref="MaxAgeDays"/> is the catch-all "and older" band and must be last.
/// Serialized to/from the <c>repair.healthcheck.backoff-tiers</c> config value as JSON.
/// </summary>
public class HealthCheckBackoffTier
{
    public int? MaxAgeDays { get; set; }
    public int IntervalDays { get; set; }

    /// <summary>
    /// Default cadence: daily for the first 2 weeks, every 2 days to a month,
    /// weekly to a year, then every 3 weeks.
    /// </summary>
    public static List<HealthCheckBackoffTier> Defaults =>
    [
        new() { MaxAgeDays = 14, IntervalDays = 1 },
        new() { MaxAgeDays = 30, IntervalDays = 2 },
        new() { MaxAgeDays = 365, IntervalDays = 7 },
        new() { MaxAgeDays = null, IntervalDays = 21 },
    ];
}
