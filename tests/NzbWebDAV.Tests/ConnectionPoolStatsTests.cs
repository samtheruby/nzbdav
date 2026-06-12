using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests;

public class ConnectionPoolStatsTests
{
    [Fact]
    public void BuildMessage_EmitsTotalsThenPerProviderSnapshot()
    {
        // totalLive|totalMax|totalIdle|index:live:idle;...
        var message = ConnectionPoolStats.BuildMessage(8, 20, 3, [5, 3], [2, 1]);
        Assert.Equal("8|20|3|0:5:2;1:3:1", message);
    }

    [Fact]
    public void BuildMessage_IncludesEveryProviderEvenWhenIdle()
    {
        // a full snapshot is required so new websocket subscribers see all providers,
        // not just the last one to change.
        var message = ConnectionPoolStats.BuildMessage(0, 0, 0, [0, 0, 0], [0, 0, 0]);
        Assert.Equal("0|0|0|0:0:0;1:0:0;2:0:0", message);
    }
}
