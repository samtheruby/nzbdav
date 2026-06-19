using NzbWebDAV.Clients.Rclone;

namespace NzbWebDAV.Tests;

public class RcloneVfsCacheCleanerTests
{
    [Fact]
    public void SelectStaleNamespaces_ReturnsEveryNamespaceExceptTheActiveOne()
    {
        var existing = new[] { "nzbdav", ":webdav{aaa}", ":webdav{bbb}" };

        var stale = RcloneVfsCacheCleaner.SelectStaleNamespaces(existing, "nzbdav");

        Assert.Equal(new[] { ":webdav{aaa}", ":webdav{bbb}" }, stale);
    }

    [Fact]
    public void SelectStaleNamespaces_WhenActiveAbsent_ReturnsAll()
    {
        var existing = new[] { ":webdav{aaa}", ":webdav{bbb}" };

        var stale = RcloneVfsCacheCleaner.SelectStaleNamespaces(existing, "nzbdav");

        Assert.Equal(existing, stale);
    }

    [Fact]
    public void PurgeStaleNamespaces_DeletesOrphansAndKeepsActiveUnderVfsAndVfsMeta()
    {
        var cacheDir = Directory.CreateTempSubdirectory("rclonecache").FullName;
        try
        {
            foreach (var sub in new[] { "vfs", "vfsMeta" })
            {
                Directory.CreateDirectory(Path.Combine(cacheDir, sub, "nzbdav"));
                Directory.CreateDirectory(Path.Combine(cacheDir, sub, ":webdav{aaa}"));
                Directory.CreateDirectory(Path.Combine(cacheDir, sub, ":webdav{bbb}"));
            }
            // a non-empty orphan must still be removed (recursive delete).
            File.WriteAllText(Path.Combine(cacheDir, "vfs", ":webdav{aaa}", "chunk.1"), "data");

            RcloneVfsCacheCleaner.PurgeStaleNamespaces(cacheDir, "nzbdav");

            Assert.True(Directory.Exists(Path.Combine(cacheDir, "vfs", "nzbdav")));
            Assert.True(Directory.Exists(Path.Combine(cacheDir, "vfsMeta", "nzbdav")));
            Assert.False(Directory.Exists(Path.Combine(cacheDir, "vfs", ":webdav{aaa}")));
            Assert.False(Directory.Exists(Path.Combine(cacheDir, "vfs", ":webdav{bbb}")));
            Assert.False(Directory.Exists(Path.Combine(cacheDir, "vfsMeta", ":webdav{aaa}")));
            Assert.False(Directory.Exists(Path.Combine(cacheDir, "vfsMeta", ":webdav{bbb}")));
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void PurgeStaleNamespaces_WhenCacheDirMissing_DoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rclone-cache-" + Guid.NewGuid().ToString("N"));

        RcloneVfsCacheCleaner.PurgeStaleNamespaces(missing, "nzbdav");
    }
}
