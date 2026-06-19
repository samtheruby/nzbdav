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
    /// <summary>
    /// Name of the WebDAV remote the mount points at. Using a *named* remote (rather
    /// than an on-the-fly ":webdav:" connection string) keeps rclone's VFS cache
    /// namespace stable across restarts: rclone keys the cache dir on the remote name
    /// for named remotes, but on a hash of the full config — including the
    /// non-deterministically-obscured password — for connection strings. A connection
    /// string therefore mints a brand-new cache namespace on every restart, and the
    /// --vfs-cache-max-size cap (which is per-namespace) never prunes the orphans.
    /// </summary>
    public const string RemoteName = "nzbdav";

    // rclone reads remote config from RCLONE_CONFIG_<NAME>_<KEY>, with the name
    // upper-cased. Keep this in sync with RemoteName.
    private const string EnvPrefix = "RCLONE_CONFIG_NZBDAV_";

    public static List<string> Build(RcloneMountOptions options)
    {
        var args = new List<string>
        {
            "mount",
            // Stable named remote (defined via BuildRemoteEnvironment); url/credentials
            // are supplied through its env config, never on the argument list.
            $"{RemoteName}:",
            options.MountDir,
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

    /// <summary>
    /// Environment variables that define the named WebDAV remote (<see cref="RemoteName"/>)
    /// rclone mounts. Secrets stay in the environment, never on the argument list. See
    /// <see cref="RemoteName"/> for why a named remote (not a connection string) is used.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildRemoteEnvironment(
        string webdavUrl, string user, string obscuredPassword) => new Dictionary<string, string>
    {
        [EnvPrefix + "TYPE"] = "webdav",
        [EnvPrefix + "URL"] = webdavUrl,
        [EnvPrefix + "VENDOR"] = "other",
        [EnvPrefix + "USER"] = user,
        [EnvPrefix + "PASS"] = obscuredPassword,
    };
}
