using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests;

public class HealthCheckSchedulerTests
{
    [Fact]
    public void HealthyNextCheck_NullReleaseDate_ReturnsBoundedRetry()
    {
        // regression: a null release date used to compute NextHealthCheck = null,
        // which re-selected the same item every 5s tick (tight loop). It must now
        // resolve to a bounded, future time instead.
        var now = DateTimeOffset.UtcNow;

        var next = HealthCheckScheduler.ComputeHealthyNextCheck(null, now);

        Assert.Equal(now + HealthCheckScheduler.RetryInterval, next);
        Assert.True(next > now);
    }

    [Fact]
    public void HealthyNextCheck_WithReleaseDate_BacksOffIntoFuture()
    {
        var releaseDate = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
        var now = DateTimeOffset.UtcNow;

        var next = HealthCheckScheduler.ComputeHealthyNextCheck(releaseDate, now);

        Assert.True(next > now);
    }

    [Fact]
    public void RetryNextCheck_AdvancesByRetryInterval()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(now + HealthCheckScheduler.RetryInterval, HealthCheckScheduler.ComputeRetryNextCheck(now));
    }

    [Fact]
    public void IsTransientFailure_ArticleNotFound_IsFalse()
    {
        // a genuine missing article is authoritative — repair, do not retry.
        Assert.False(HealthCheckScheduler.IsTransientFailure(new UsenetArticleNotFoundException("seg-id")));
    }

    [Fact]
    public void IsTransientFailure_Cancellation_IsFalse()
    {
        // cancellation is shutdown/reschedule, handled by the outer loop.
        Assert.False(HealthCheckScheduler.IsTransientFailure(new OperationCanceledException()));
    }

    [Fact]
    public void IsTransientFailure_GenericError_IsTrue()
    {
        // timeouts, circuit-breaker trips, STAT contention → retry later, do not condemn.
        Assert.True(HealthCheckScheduler.IsTransientFailure(new TimeoutException("STAT timed out")));
    }
}
