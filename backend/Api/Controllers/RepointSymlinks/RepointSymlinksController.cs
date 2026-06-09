using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Api.Controllers.RepointSymlinks;

[ApiController]
[Route("api/repoint-symlinks")]
public class RepointSymlinksController(
    ConfigManager configManager,
    WebsocketManager websocketManager
) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        var oldMountDir = HttpContext.GetRequestParam("oldMountDir")?.Trim();
        if (string.IsNullOrEmpty(oldMountDir))
            throw new BadHttpRequestException("oldMountDir is required");
        if (!oldMountDir.StartsWith('/') || oldMountDir.TrimEnd('/').Length < 2)
            throw new BadHttpRequestException("oldMountDir must be an absolute, non-root path");

        // Fire-and-forget: a large library walk can take minutes, longer than a
        // typical reverse proxy's HTTP timeout. Progress is streamed over the
        // websocket, so the request only needs to acknowledge the kick-off.
        var task = new RepointSymlinksTask(configManager, websocketManager, oldMountDir);
        _ = task.Execute().ContinueWith(
            t => Log.Error(t.Exception, "RepointSymlinksTask failed"),
            TaskContinuationOptions.OnlyOnFaulted);
        return Task.FromResult<IActionResult>(Accepted());
    }
}
