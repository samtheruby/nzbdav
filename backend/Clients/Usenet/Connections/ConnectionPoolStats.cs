using System.Text;
using NzbWebDAV.Config;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionPoolStats
{
    private readonly int[] _live;
    private readonly int[] _idle;
    private readonly bool[] _countsTowardTotal;
    private readonly int _max;
    private readonly WebsocketManager _websocketManager;
    private readonly WebsocketTopic _topic;

    /// <param name="topic">Which websocket topic to broadcast this pool's stats on.</param>
    /// <param name="maxConnectionsSelector">
    /// Per-provider capacity for this pool kind (streaming vs health-check). A provider
    /// contributes to the reported total only when its value is greater than 0.
    /// </param>
    public ConnectionPoolStats(
        UsenetProviderConfig providerConfig,
        WebsocketManager websocketManager,
        WebsocketTopic topic,
        Func<UsenetProviderConfig.ConnectionDetails, int> maxConnectionsSelector)
    {
        var count = providerConfig.Providers.Count;
        _live = new int[count];
        _idle = new int[count];
        _countsTowardTotal = providerConfig.Providers.Select(p => maxConnectionsSelector(p) > 0).ToArray();
        _max = providerConfig.Providers.Select(maxConnectionsSelector).Sum();

        _websocketManager = websocketManager;
        _topic = topic;
    }

    public EventHandler<ConnectionPoolChangedEventArgs> GetOnConnectionPoolChanged(int providerIndex)
    {
        return OnEvent;

        void OnEvent(object? _, ConnectionPoolChangedEventArgs args)
        {
            string message;
            lock (this)
            {
                _live[providerIndex] = args.Live;
                _idle[providerIndex] = args.Idle;

                var totalLive = 0;
                var totalIdle = 0;
                for (var i = 0; i < _live.Length; i++)
                {
                    if (!_countsTowardTotal[i]) continue;
                    totalLive += _live[i];
                    totalIdle += _idle[i];
                }

                // A full snapshot of every provider goes out on each change, because the
                // websocket only replays the last message per topic to new subscribers —
                // a per-provider message would leave all but one provider blank on load.
                message = BuildMessage(totalLive, _max, totalIdle, _live, _idle);
            }

            _websocketManager.SendMessage(_topic, message);
        }
    }

    /// <summary>
    /// Format: <c>totalLive|totalMax|totalIdle|index:live:idle;index:live:idle;...</c>
    /// </summary>
    public static string BuildMessage(int totalLive, int totalMax, int totalIdle, int[] live, int[] idle)
    {
        var snapshot = new StringBuilder();
        for (var i = 0; i < live.Length; i++)
        {
            if (i > 0) snapshot.Append(';');
            snapshot.Append(i).Append(':').Append(live[i]).Append(':').Append(idle[i]);
        }

        return $"{totalLive}|{totalMax}|{totalIdle}|{snapshot}";
    }

    public sealed class ConnectionPoolChangedEventArgs(int live, int idle, int max) : EventArgs
    {
        public int Live { get; } = live;
        public int Idle { get; } = idle;
        public int Max { get; } = max;
        public int Active => Live - Idle;
    }
}
