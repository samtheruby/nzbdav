using NzbWebDAV.Clients.Rclone;

namespace NzbWebDAV.Tests;

public class RcloneMountCommandBuilderTests
{
    private static RcloneMountOptions Opts() => new()
    {
        MountDir = "/mnt/nzbdav",
        Uid = 1000,
        Gid = 1000,
    };

    [Fact]
    public void Build_UsesStableNamedRemote_NotConnectionString()
    {
        // A named remote keeps rclone's VFS cache namespace stable across restarts.
        // A connection string (":webdav:") makes rclone hash the full config — including
        // the non-deterministically-obscured password — into the cache name, so every
        // restart mints a fresh, never-pruned cache namespace.
        var args = RcloneMountCommandBuilder.Build(Opts());

        Assert.Contains($"{RcloneMountCommandBuilder.RemoteName}:", args);
        Assert.DoesNotContain(args, a => a.StartsWith(":webdav"));
    }

    [Fact]
    public void Build_KeepsWebdavConfigAndSecretsOffTheArgList()
    {
        // WebDAV url/vendor/credentials move into the named-remote env config; nothing
        // WebDAV-specific should leak onto the process argument list.
        var args = RcloneMountCommandBuilder.Build(Opts());

        Assert.DoesNotContain(args, a => a.StartsWith("--webdav-url"));
        Assert.DoesNotContain(args, a => a.StartsWith("--webdav-vendor"));
    }

    [Fact]
    public void BuildRemoteEnvironment_DefinesNamedWebdavRemote()
    {
        var env = RcloneMountCommandBuilder.BuildRemoteEnvironment(
            "http://127.0.0.1:8080", "admin", "OBSCURED_PASS");

        Assert.Equal("webdav", env["RCLONE_CONFIG_NZBDAV_TYPE"]);
        Assert.Equal("http://127.0.0.1:8080", env["RCLONE_CONFIG_NZBDAV_URL"]);
        Assert.Equal("other", env["RCLONE_CONFIG_NZBDAV_VENDOR"]);
        Assert.Equal("admin", env["RCLONE_CONFIG_NZBDAV_USER"]);
        Assert.Equal("OBSCURED_PASS", env["RCLONE_CONFIG_NZBDAV_PASS"]);
    }
}
