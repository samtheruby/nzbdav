using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Api.Controllers.GetRcloneMountStatus;

[ApiController]
[Route("api/rclone-mount-status")]
public class GetRcloneMountStatusController(RcloneMountStatus status, ConfigManager configManager) : BaseApiController
{
    private async Task<GetRcloneMountStatusResponse> GetStatus()
    {
        var snapshot = status.Current;
        var response = new GetRcloneMountStatusResponse
        {
            Status = true,
            Enabled = snapshot.Enabled,
            Running = snapshot.Running,
            Pid = snapshot.Pid,
            StartedAtUtc = snapshot.StartedAtUtc,
            LastError = snapshot.LastError,
            CacheMaxSize = configManager.GetRcloneVfsCacheMaxSize(),
        };

        // Best-effort live enrichment from the embedded RC server. If it's not
        // reachable, the badge still shows the running state without these extras.
        if (snapshot.Running && RcloneClient.IsRemoteControlEnabled)
        {
            try
            {
                var version = await RcloneClient.GetVersion().ConfigureAwait(false);
                if (version.Success) response.RcloneVersion = version.Version;

                var vfs = await RcloneClient.GetVfsStats().ConfigureAwait(false);
                if (vfs.Success && vfs.DiskCache != null)
                    response.CacheBytesUsed = vfs.DiskCache.BytesUsed;
            }
            catch (Exception ex)
            {
                // live details are optional, but a silent swallow hides real RC issues.
                Log.Debug(ex, "Rclone RC status enrichment failed");
            }
        }

        return response;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        return Ok(await GetStatus().ConfigureAwait(false));
    }
}
