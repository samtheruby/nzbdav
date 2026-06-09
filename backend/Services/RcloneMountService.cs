using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Optionally runs rclone as a child process inside the nzbdav container, mounting
/// the local WebDAV server onto the filesystem. This lets single-`docker run`
/// platforms avoid a separate rclone sidecar container.
///
/// Opt-in via the WebUI toggle (rclone.embedded-mount-enabled) or the RCLONE_MOUNT
/// env var. The mount authenticates with the plaintext WEBDAV_PASSWORD env var
/// (obscured and passed to rclone via environment, never on the command line).
///
/// Requires the container to be started with FUSE privileges:
///   --cap-add SYS_ADMIN --device /dev/fuse --security-opt apparmor:unconfined
/// and, for other containers to see the mount, a shared bind mount (e.g. /mnt:rshared).
/// </summary>
public class RcloneMountService(ConfigManager configManager, RcloneMountStatus status) : BackgroundService
{
    private const string RcloneBinary = "rclone";
    private const string RcAddr = "127.0.0.1:5572";
    private const string EmbeddedRcHost = "http://127.0.0.1:5572";

    // The local WebDAV server the mount points at (the backend's Kestrel port).
    private static readonly string WebdavUrl =
        EnvironmentUtil.GetEnvironmentVariable("MOUNT_WEBDAV_URL") ?? "http://127.0.0.1:8080";

    // rclone's VFS cache. Lives under /config (writable by PUID:PGID) because rclone's
    // default ($HOME/.cache/rclone) fails — the app user has no home directory.
    private static readonly string CacheDir =
        Path.Combine(EnvironmentUtil.GetEnvironmentVariable("CONFIG_PATH") ?? "/config", "rclone-cache");

    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    // Config keys that require the mount to be restarted when they change.
    private static readonly HashSet<string> RemountKeys =
    [
        "rclone.embedded-mount-enabled",
        "rclone.mount-dir",
        "rclone.vfs-cache-mode",
        "rclone.vfs-cache-max-size",
        "rclone.vfs-cache-max-age",
        "rclone.buffer-size",
        "rclone.vfs-read-ahead",
        "rclone.dir-cache-time",
        "rclone.log-level",
        "rclone.extra-flags",
        "webdav.user",
    ];

    private Process? _mountProcess;
    private string? _mountDir;
    private volatile bool _restartRequested;
    private bool _warnedMissingPassword;
    private DateTime? _startedAtUtc;
    private string? _lastError;

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs args)
    {
        if (args.ChangedConfig.Keys.Any(RemountKeys.Contains))
            _restartRequested = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        configManager.OnConfigChanged += OnConfigChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error(ex, "Embedded rclone mount supervisor error");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            configManager.OnConfigChanged -= OnConfigChanged;
            StopMount();
        }
    }

    /// <summary>
    /// Bring the running state in line with config: start, stop, or restart the mount.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        var enabled = configManager.IsRcloneEmbeddedMountEnabled();
        var running = _mountProcess is { HasExited: false };

        // config changed — restart so new flags take effect.
        if (_restartRequested)
        {
            _restartRequested = false;
            if (running)
            {
                Log.Information("Rclone mount config changed; restarting mount");
                StopMount();
                running = false;
            }
        }

        if (enabled && !running)
        {
            // log if the process died unexpectedly while still enabled.
            if (_mountProcess is { HasExited: true } dead)
            {
                Log.Warning("Rclone mount exited (code {Code}); restarting", dead.ExitCode);
                _lastError = $"rclone exited with code {dead.ExitCode}";
                StopMount();
            }

            await StartMountAsync(stoppingToken).ConfigureAwait(false);
        }
        else if (!enabled && running)
        {
            Log.Information("Embedded rclone mount disabled; unmounting");
            _lastError = null;
            StopMount();
        }

        PublishStatus(enabled);
    }

    private void PublishStatus(bool enabled)
    {
        var liveProcess = _mountProcess is { HasExited: false } ? _mountProcess : null;
        status.Set(new RcloneMountStatusSnapshot
        {
            Enabled = enabled,
            Running = liveProcess != null,
            Pid = liveProcess?.Id,
            StartedAtUtc = liveProcess != null ? _startedAtUtc : null,
            LastError = _lastError,
        });
    }

    private async Task StartMountAsync(CancellationToken stoppingToken)
    {
        var password = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            // throttle: the reconcile loop runs every few seconds.
            if (!_warnedMissingPassword)
            {
                Log.Error(
                    "Embedded rclone mount is enabled but the WEBDAV_PASSWORD env var is not set. " +
                    "Set WEBDAV_PASSWORD to the plaintext WebDAV password so the mount can authenticate.");
                _warnedMissingPassword = true;
            }

            _lastError = "WEBDAV_PASSWORD env var is not set";
            return;
        }

        _warnedMissingPassword = false;

        // The local WebDAV may still be starting on first boot. Waiting here (and
        // retrying next reconcile) avoids launching rclone before it can connect,
        // which would otherwise produce a burst of failed-mount cycles. Leaving early
        // without an error keeps the status as "Starting…".
        if (!await IsWebdavReadyAsync(stoppingToken).ConfigureAwait(false))
            return;

        var mountDir = configManager.GetRcloneMountDir();
        var user = configManager.GetWebdavUser() ?? "admin";
        var (uid, gid) = GetUidGid();

        // Clear any stale FUSE mount left behind by an unclean shutdown (crash, OOM,
        // docker kill). Without this, rclone fails to mount over a "transport endpoint
        // not connected" path. No-op when nothing is mounted.
        TryUnmount(mountDir);

        try
        {
            Directory.CreateDirectory(mountDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not create rclone mount directory {Dir}", mountDir);
            _lastError = $"Could not create mount directory: {ex.Message}";
            return;
        }

        // best-effort: rclone would otherwise try (and fail) to create this itself.
        try { Directory.CreateDirectory(CacheDir); }
        catch (Exception ex) { Log.Warning(ex, "Could not create rclone cache dir {Dir}", CacheDir); }

        string obscuredPass;
        try
        {
            obscuredPass = await ObscureAsync(password, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to obscure WebDAV password for rclone");
            _lastError = $"Failed to obscure WebDAV password: {ex.Message}";
            return;
        }

        var options = new RcloneMountOptions
        {
            MountDir = mountDir,
            WebdavUrl = WebdavUrl,
            Uid = uid,
            Gid = gid,
            VfsCacheMode = configManager.GetRcloneVfsCacheMode(),
            VfsCacheMaxSize = configManager.GetRcloneVfsCacheMaxSize(),
            VfsCacheMaxAge = configManager.GetRcloneVfsCacheMaxAge(),
            BufferSize = configManager.GetRcloneBufferSize(),
            VfsReadAhead = configManager.GetRcloneVfsReadAhead(),
            DirCacheTime = configManager.GetRcloneDirCacheTime(),
            LogLevel = configManager.GetRcloneLogLevel(),
            RcAddr = RcAddr,
            ExtraFlags = configManager.GetRcloneExtraFlags(),
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = RcloneBinary,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in RcloneMountCommandBuilder.Build(options))
            startInfo.ArgumentList.Add(arg);

        // secrets via environment only — never in the argument list.
        startInfo.Environment["RCLONE_WEBDAV_USER"] = user;
        startInfo.Environment["RCLONE_WEBDAV_PASS"] = obscuredPass;
        // --cache-dir via env (RCLONE_<FLAG>); rclone's default $HOME/.cache/rclone
        // fails because the app user has no home directory.
        startInfo.Environment["RCLONE_CACHE_DIR"] = CacheDir;

        try
        {
            _mountProcess = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Process.Start returned null");
            _mountDir = mountDir;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start embedded rclone mount. Is the rclone binary present " +
                          "and the container started with FUSE privileges (--cap-add SYS_ADMIN, " +
                          "--device /dev/fuse)?");
            _lastError = $"Failed to start rclone: {ex.Message}";
            return;
        }

        _startedAtUtc = DateTime.UtcNow;
        _lastError = null;
        Log.Information("Started embedded rclone mount: {Url} -> {Dir} (pid {Pid})",
            WebdavUrl, mountDir, _mountProcess.Id);
        RcloneClient.UseEmbeddedRemoteControl(EmbeddedRcHost);
    }

    private void StopMount()
    {
        var process = _mountProcess;
        var mountDir = _mountDir;
        _mountProcess = null;
        _mountDir = null;
        _startedAtUtc = null;

        RcloneClient.RestoreConfiguredRemoteControl(configManager);

        if (process is { HasExited: false })
        {
            try
            {
                // SIGTERM first so rclone can unmount cleanly; SIGKILL leaves a
                // "transport endpoint not connected" stale mount that the next
                // start has to clean up via fusermount3 -uz.
                TrySigterm(process.Id);
                if (!process.WaitForExit(milliseconds: 5_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(milliseconds: 5_000);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error stopping rclone mount process");
            }
        }

        process?.Dispose();

        // best-effort lazy unmount to clear any stale FUSE mount.
        if (mountDir != null)
            TryUnmount(mountDir);
    }

    private static void TrySigterm(int pid)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "kill",
                ArgumentList = { "-TERM", pid.ToString() },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(milliseconds: 1_000);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "kill -TERM {Pid} failed", pid);
        }
    }

    private static void TryUnmount(string mountDir)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "fusermount3",
                ArgumentList = { "-uz", mountDir },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(milliseconds: 5_000);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "fusermount3 -uz {Dir} failed (mount may already be gone)", mountDir);
        }
    }

    private static async Task<string> ObscureAsync(string plaintext, CancellationToken stoppingToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = RcloneBinary,
            ArgumentList = { "obscure", plaintext },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start `rclone obscure`");

        var output = await process.StandardOutput.ReadToEndAsync(stoppingToken).ConfigureAwait(false);
        await process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(stoppingToken).ConfigureAwait(false);
            throw new InvalidOperationException($"`rclone obscure` exited {process.ExitCode}: {error}");
        }

        return output.Trim();
    }

    private static async Task<bool> IsWebdavReadyAsync(CancellationToken stoppingToken)
    {
        try
        {
            var url = $"{WebdavUrl.TrimEnd('/')}/health";
            using var response = await HealthClient.GetAsync(url, stoppingToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static (int uid, int gid) GetUidGid()
    {
        var uid = int.TryParse(EnvironmentUtil.GetEnvironmentVariable("PUID"), out var u) ? u : 1000;
        var gid = int.TryParse(EnvironmentUtil.GetEnvironmentVariable("PGID"), out var g) ? g : 1000;
        return (uid, gid);
    }
}
