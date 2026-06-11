using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests;

public class HealthCheckSchedulerTests
{
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

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    public void IsTerminalFailure_TrueOnceCountReachesMax(int failureCount, int maxFailures, bool expected)
    {
        // after maxFailures consecutive transient errors the item is marked failed
        // and stops being retried, instead of looping forever.
        Assert.Equal(expected, HealthCheckScheduler.IsTerminalFailure(failureCount, maxFailures));
    }
}
