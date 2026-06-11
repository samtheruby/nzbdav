using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// A connection pool dedicated to health checks, separate from the streaming pool.
/// Each provider opens its own HealthCheckConnections (additive to its streaming
/// MaxConnections), so heavy streaming can't starve health-check STATs and vice
/// versa. Only providers with a usable (>= per-check) health-check pool participate.
/// </summary>
public class UsenetHealthCheckClient : WrappingNntpClient
{
    public UsenetHealthCheckClient(ConfigManager configManager)
        : base(CreateClient(configManager))
    {
        // rebuild the pools whenever the providers (and their health-check counts) change.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;
            ReplaceUnderlyingClient(CreateClient(configManager));
        };
    }

    private static INntpClient CreateClient(ConfigManager configManager)
    {
        var providerClients = configManager.GetUsenetProviderConfig().Providers
            .Where(p => p.Type != ProviderType.Disabled)
            .Where(p => HealthCheckScheduler.RoundDownToMultipleOfThree(p.HealthCheckConnections)
                        >= HealthCheckScheduler.HealthCheckConnectionsPerCheck)
            .Select(CreateProviderClient)
            .ToList();
        return new MultiProviderNntpClient(providerClients);
    }

    private static MultiConnectionNntpClient CreateProviderClient(UsenetProviderConfig.ConnectionDetails connectionDetails)
    {
        var maxConnections = HealthCheckScheduler.RoundDownToMultipleOfThree(connectionDetails.HealthCheckConnections);
        var connectionPool = new ConnectionPool<INntpClient>(
            maxConnections,
            ct => UsenetStreamingClient.CreateNewConnection(connectionDetails, ct)
        );
        var circuitBreaker = new ProviderCircuitBreaker(connectionDetails.Host);
        return new MultiConnectionNntpClient(
            connectionPool, connectionDetails.Type, circuitBreaker, connectionDetails.Host);
    }
}
