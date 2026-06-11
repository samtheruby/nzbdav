using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public List<ConnectionDetails> Providers { get; set; } = [];

    public int TotalPooledConnections => Math.Max(1, Providers
        .Where(x => x.Type == ProviderType.Pooled)
        .Select(x => x.MaxConnections)
        .Sum());

    public class ConnectionDetails
    {
        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }

        // Dedicated connections this provider opens for health checks, separate from
        // (additive to) MaxConnections used for streaming. Rounded down to a multiple
        // of HealthCheckConnectionsPerCheck. 0 = this provider does no health checks.
        public int HealthCheckConnections { get; set; }
    }
}