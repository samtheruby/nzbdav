using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests;

public class HealthCheckConnectionsTests
{
    [Fact]
    public void ConnectionsPerCheck_IsThree()
    {
        Assert.Equal(3, HealthCheckScheduler.HealthCheckConnectionsPerCheck);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 3)]
    [InlineData(9, 9)]
    [InlineData(10, 9)]   // 10 → 9 (nearest lower multiple of 3)
    [InlineData(11, 9)]
    [InlineData(12, 12)]
    [InlineData(-5, 0)]   // never negative
    public void RoundDownToMultipleOfThree(int input, int expected)
    {
        Assert.Equal(expected, HealthCheckScheduler.RoundDownToMultipleOfThree(input));
    }

    [Theory]
    [InlineData(0, 0)]    // unset → no threading (fallback handled by caller)
    [InlineData(3, 1)]
    [InlineData(9, 3)]
    [InlineData(10, 3)]   // 10 connections → 9 usable → 3 concurrent checks
    [InlineData(30, 10)]
    public void MaxConcurrentChecks_DrivenByPrimaryProvider(int hcConnections, int expected)
    {
        var config = ConfigWith(
            Provider(ProviderType.Pooled, hcConnections));
        Assert.Equal(expected, HealthCheckScheduler.ComputeMaxConcurrentChecks(config));
    }

    [Fact]
    public void MaxConcurrentChecks_UsesProviderWithMostConnections()
    {
        var config = ConfigWith(
            Provider(ProviderType.Pooled, 9),
            Provider(ProviderType.BackupAndStats, 6));
        Assert.Equal(3, HealthCheckScheduler.ComputeMaxConcurrentChecks(config));
    }

    [Fact]
    public void MaxConcurrentChecks_IgnoresDisabledProviders()
    {
        var config = ConfigWith(
            Provider(ProviderType.Disabled, 30),
            Provider(ProviderType.Pooled, 3));
        Assert.Equal(1, HealthCheckScheduler.ComputeMaxConcurrentChecks(config));
    }

    [Fact]
    public void MaxConcurrentChecks_NoProviders_IsZero()
    {
        Assert.Equal(0, HealthCheckScheduler.ComputeMaxConcurrentChecks(new UsenetProviderConfig()));
    }

    private static UsenetProviderConfig ConfigWith(params UsenetProviderConfig.ConnectionDetails[] providers)
        => new() { Providers = providers.ToList() };

    private static UsenetProviderConfig.ConnectionDetails Provider(ProviderType type, int healthCheckConnections)
        => new()
        {
            Type = type,
            Host = "host",
            Port = 563,
            UseSsl = true,
            User = "u",
            Pass = "p",
            MaxConnections = 10,
            HealthCheckConnections = healthCheckConnections,
        };
}
