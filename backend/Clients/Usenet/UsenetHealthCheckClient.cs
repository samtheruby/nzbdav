using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// A connection pool dedicated to health checks, separate from the streaming pool.
/// Each provider opens its own HealthCheckConnections (additive to its streaming
/// MaxConnections), so heavy streaming can't starve health-check STATs and vice
/// versa. Only providers with a usable (>= per-check) health-check pool participate.
/// Pool stats are reported on their own websocket topic so the UI can show them
/// separately from the streaming pool.
/// </summary>
public class UsenetHealthCheckClient : WrappingNntpClient
{
    public UsenetHealthCheckClient(ConfigManager configManager, WebsocketManager websocketManager)
        : base(CreateClient(configManager, websocketManager))
    {
        // rebuild the pools whenever the providers (and their health-check counts) change.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;
            ReplaceUnderlyingClient(CreateClient(configManager, websocketManager));
        };
    }

    private static INntpClient CreateClient(ConfigManager configManager, WebsocketManager websocketManager)
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var connectionPoolStats = new ConnectionPoolStats(
            providerConfig,
            websocketManager,
            WebsocketTopic.UsenetHealthCheckConnections,
            p => HealthCheckScheduler.RoundDownToMultipleOfThree(p.HealthCheckConnections));

        // keep the original provider index so the UI maps stats to the right provider card,
        // even though only the health-check-enabled providers get a pool.
        var providerClients = providerConfig.Providers
            .Select((provider, index) => (provider, index))
            .Where(x => x.provider.Type != ProviderType.Disabled
                        && HealthCheckScheduler.RoundDownToMultipleOfThree(x.provider.HealthCheckConnections)
                        >= HealthCheckScheduler.HealthCheckConnectionsPerCheck)
            .Select(x => CreateProviderClient(x.provider, connectionPoolStats.GetOnConnectionPoolChanged(x.index)))
            .ToList();
        return new MultiProviderNntpClient(providerClients);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var maxConnections = HealthCheckScheduler.RoundDownToMultipleOfThree(connectionDetails.HealthCheckConnections);
        var connectionPool = new ConnectionPool<INntpClient>(
            maxConnections,
            ct => UsenetStreamingClient.CreateNewConnection(connectionDetails, ct)
        );
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        onConnectionPoolChanged(connectionPool,
            new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections));

        var circuitBreaker = new ProviderCircuitBreaker(connectionDetails.Host);
        return new MultiConnectionNntpClient(
            connectionPool, connectionDetails.Type, circuitBreaker, connectionDetails.Host);
    }
}
