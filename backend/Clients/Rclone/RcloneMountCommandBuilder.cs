namespace NzbWebDAV.Clients.Rclone;

/// <summary>
/// Inputs for building an embedded `rclone mount` command. Secrets (the WebDAV
/// username and password) are intentionally NOT included here — they are passed
/// to the rclone child process via environment variables by RcloneMountService so
/// they never appear in the process argument list.
/// </summary>
public class RcloneMountOptions
{
    /// <summary>Filesystem path to mount onto (e.g. /mnt/nzbdav).</summary>
    public required string MountDir { get; init; }

    /// <summary>URL of the WebDAV server to mount (e.g. http://localhost:8080).</summary>
    public required string WebdavUrl { get; init; }

    /// <summary>Owner uid for mounted files (PUID).</summary>
    public required int Uid { get; init; }

    /// <summary>Owner gid for mounted files (PGID).</summary>
    public required int Gid { get; init; }

    public string VfsCacheMode { get; init; } = "full";
    public string VfsCacheMaxSize { get; init; } = "20G";
    public string VfsCacheMaxAge { get; init; } = "24h";
    public string BufferSize { get; init; } = "0M";
    public string VfsReadAhead { get; init; } = "512M";
    public string DirCacheTime { get; init; } = "20s";
    public string LogLevel { get; init; } = "NOTICE";

    /// <summary>Loopback address rclone's RC API binds to, for cache-refresh notifications.</summary>
    public string RcAddr { get; init; } = "127.0.0.1:5572";

    /// <summary>Optional advanced flags appended verbatim (whitespace-separated).</summary>
    public string? ExtraFlags { get; init; }
}

/// <summary>
/// Builds the argument list for the embedded `rclone mount` command from
/// configuration. Pure function (no side effects) so it is trivially testable.
/// Consumed by RcloneMountService.
/// </summary>
public static class RcloneMountCommandBuilder
{
    public static List<string> Build(RcloneMountOptions options)
    {
        var args = new List<string>
        {
            "mount",
            // on-the-fly webdav remote; url/credentials supplied via flags + env.
            ":webdav:",
            options.MountDir,
            $"--webdav-url={options.WebdavUrl}",
            "--webdav-vendor=other",
            $"--uid={options.Uid}",
            $"--gid={options.Gid}",

            // Required for correctness — not user-configurable:
            // translate *.rclonelink files into real symlinks (needs rclone >= 1.70.3).
            "--links",
            // reuse the auth session instead of re-authenticating every request.
            "--use-cookies",
            // let other containers (Plex/Radarr) see the mount.
            "--allow-other",

            // Streaming-tuned VFS options (user-configurable):
            $"--vfs-cache-mode={options.VfsCacheMode}",
            $"--vfs-cache-max-size={options.VfsCacheMaxSize}",
            $"--vfs-cache-max-age={options.VfsCacheMaxAge}",
            $"--buffer-size={options.BufferSize}",
            $"--vfs-read-ahead={options.VfsReadAhead}",
            $"--dir-cache-time={options.DirCacheTime}",
            $"--log-level={options.LogLevel}",

            // RC API on loopback so nzbdav can push vfs cache refreshes.
            "--rc",
            $"--rc-addr={options.RcAddr}",
            "--rc-no-auth",
        };

        // Append advanced flags last so they can override managed defaults.
        if (!string.IsNullOrWhiteSpace(options.ExtraFlags))
        {
            var extra = options.ExtraFlags
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            args.AddRange(extra);
        }

        return args;
    }
}
