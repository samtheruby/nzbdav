using Serilog;

namespace NzbWebDAV.Clients.Rclone;

/// <summary>
/// Removes stale rclone VFS cache namespaces left over from previous mounts.
///
/// rclone stores each remote's VFS cache under &lt;cache-dir&gt;/vfs/&lt;remote&gt;/, with
/// parallel metadata under &lt;cache-dir&gt;/vfsMeta/&lt;remote&gt;/. Before the mount was
/// switched to a stable named remote (see <see cref="RcloneMountCommandBuilder.RemoteName"/>),
/// every restart created a fresh namespace and the orphaned ones were never pruned —
/// --vfs-cache-max-size only bounds the active namespace. Purging on start reclaims
/// that historic leakage and is defense in depth against any future namespace churn.
/// </summary>
public static class RcloneVfsCacheCleaner
{
    // rclone keeps cache data and its metadata in sibling directories.
    private static readonly string[] CacheRoots = ["vfs", "vfsMeta"];

    /// <summary>
    /// Pure selector: the namespaces that are not the active one and may be deleted.
    /// </summary>
    public static IReadOnlyList<string> SelectStaleNamespaces(IEnumerable<string> existing, string active) =>
        existing.Where(name => !string.Equals(name, active, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Deletes every VFS cache namespace under <paramref name="cacheDir"/> except
    /// <paramref name="activeRemote"/>. Best-effort: a missing cache dir and per-namespace
    /// delete failures are logged and skipped, never thrown.
    /// </summary>
    public static void PurgeStaleNamespaces(string cacheDir, string activeRemote, Action<string>? log = null)
    {
        foreach (var root in CacheRoots)
        {
            var rootDir = Path.Combine(cacheDir, root);
            if (!Directory.Exists(rootDir))
                continue;

            var existing = Directory.GetDirectories(rootDir).Select(Path.GetFileName).OfType<string>();
            foreach (var stale in SelectStaleNamespaces(existing, activeRemote))
            {
                var staleDir = Path.Combine(rootDir, stale);
                try
                {
                    Directory.Delete(staleDir, recursive: true);
                    log?.Invoke($"Purged stale rclone VFS cache namespace {staleDir}");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not purge stale rclone VFS cache namespace {Dir}", staleDir);
                }
            }
        }
    }
}
