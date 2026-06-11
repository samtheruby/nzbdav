using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests;

public class HealthCheckBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private static double IntervalDays(DateTimeOffset? releaseDate)
    {
        var next = HealthCheckScheduler.ComputeTieredNextCheck(releaseDate, Now, HealthCheckBackoffTier.Defaults);
        return (next - Now).TotalDays;
    }

    [Theory]
    [InlineData(0, 1)]    // brand new → daily
    [InlineData(5, 1)]    // < 2 weeks → daily
    [InlineData(13, 1)]
    [InlineData(14, 2)]   // 2 weeks .. 1 month → every 2 days
    [InlineData(20, 2)]
    [InlineData(29, 2)]
    [InlineData(30, 7)]   // 1 month .. 1 year → weekly
    [InlineData(100, 7)]
    [InlineData(364, 7)]
    [InlineData(365, 21)] // 1 year+ → every 3 weeks
    [InlineData(1000, 21)]
    public void TieredNextCheck_PicksIntervalForReleaseAge(int ageDays, int expectedIntervalDays)
    {
        var releaseDate = Now - TimeSpan.FromDays(ageDays);
        Assert.Equal(expectedIntervalDays, IntervalDays(releaseDate));
    }

    [Fact]
    public void TieredNextCheck_NullReleaseDate_FallsBackToBoundedRetry()
    {
        // preserves the null-release-date guard: never returns null / now.
        var next = HealthCheckScheduler.ComputeTieredNextCheck(null, Now, HealthCheckBackoffTier.Defaults);
        Assert.Equal(Now + HealthCheckScheduler.RetryInterval, next);
    }

    [Fact]
    public void TieredNextCheck_EmptyTiers_FallsBackToOneDay()
    {
        var releaseDate = Now - TimeSpan.FromDays(10);
        var next = HealthCheckScheduler.ComputeTieredNextCheck(releaseDate, Now, []);
        Assert.True(next > Now);
        Assert.Equal(1, (next - Now).TotalDays);
    }

    [Fact]
    public void TieredNextCheck_NonPositiveInterval_IsClampedToAtLeastOneDay()
    {
        // a misconfigured 0-day interval must not produce a tight loop.
        var tiers = new List<HealthCheckBackoffTier> { new() { MaxAgeDays = null, IntervalDays = 0 } };
        var next = HealthCheckScheduler.ComputeTieredNextCheck(Now - TimeSpan.FromDays(1), Now, tiers);
        Assert.True((next - Now).TotalDays >= 1);
    }
}
