using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "unknown";

    private readonly Dictionary<string, string> _config = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    private string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue == null ? default : JsonSerializer.Deserialize<T>(rawValue);
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        mountDir = mountDir.TrimEnd('/');
        // Reject relative or root paths: relative would resolve against the backend's
        // CWD (surprising), and "/" as a mount point would mean rclone takes over the
        // container's root. Fall back to the default rather than fail loudly — this
        // is read from many callers, not just the mount service.
        if (mountDir.Length < 2 || mountDir[0] != '/') return "/mnt/nzbdav";
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("api.categories"))
                    ?? EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    // PasswordHasher salts every call, so re-hashing on each WebDAV request would
    // also bypass PasswordUtil.Verify's cache (its key includes the hash). The env
    // var is process-static, so hash once and reuse.
    private static readonly Lazy<string?> EnvPasswordHash = new(() =>
    {
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        return pass != null ? PasswordUtil.Hash(pass) : null;
    });

    public string? GetWebdavPasswordHash()
    {
        // In embedded-mount mode, the WEBDAV_PASSWORD env var is the source of truth
        // for the WebDAV password (the UI field is disabled). Prefer it, but fall
        // through to the stored hash if it isn't set so existing users aren't locked
        // out the instant they flip the toggle without setting the env var.
        if (IsRcloneEmbeddedMountEnabled() && EnvPasswordHash.Value is { } embeddedHash)
            return embeddedHash;

        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        return hashedPass ?? EnvPasswordHash.Value;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxDownloadConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.max-download-connections"))
            ?? Math.Min(GetUsenetProviderConfig().TotalPooledConnections, 15).ToString()
        );
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.article-buffer-size"))
            ?? "40"
        );
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-priority"));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue("api.ensure-article-existence-categories");
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.preview-par2-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ignore-history-limit"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.enable"));
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public int GetHealthCheckMaxFailures()
    {
        // after this many consecutive transient (non-NotFound) failures, an item is
        // marked failed and stops being retried instead of looping forever.
        var defaultValue = 5;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.healthcheck.max-failures"));
        if (configValue == null || !int.TryParse(configValue, out var maxFailures) || maxFailures < 1)
            return defaultValue;
        return maxFailures;
    }

    public List<HealthCheckBackoffTier> GetHealthCheckBackoffTiers()
    {
        var configured = GetConfigValue<List<HealthCheckBackoffTier>>("repair.healthcheck.backoff-tiers");
        return configured is { Count: > 0 } ? configured : HealthCheckBackoffTier.Defaults;
    }

    public bool IsHealthCheckScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.healthcheck.schedule-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan HealthCheckSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.healthcheck.schedule-time"));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? defaultValue;
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue("api.download-file-blocklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue("api.import-strategy") ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("rclone.rc-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue("rclone.host");
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue("rclone.user");
    }

    public string? GetRclonePass()
    {
        return GetConfigValue("rclone.pass");
    }

    public bool IsRcloneEmbeddedMountEnabled()
    {
        // The WebUI toggle is the source of truth once set; the RCLONE_MOUNT env
        // var is a first-boot default for single-container deployments.
        var configValue = StringUtil.EmptyToNull(GetConfigValue("rclone.embedded-mount-enabled"));
        if (configValue != null) return bool.Parse(configValue);
        return EnvironmentUtil.IsVariableTrue("RCLONE_MOUNT");
    }

    public string GetRcloneVfsCacheMode()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.vfs-cache-mode")) ?? "full";
    }

    public string GetRcloneVfsCacheMaxSize()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.vfs-cache-max-size")) ?? "20G";
    }

    public string GetRcloneVfsCacheMaxAge()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.vfs-cache-max-age")) ?? "24h";
    }

    public string GetRcloneBufferSize()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.buffer-size")) ?? "0M";
    }

    public string GetRcloneVfsReadAhead()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.vfs-read-ahead")) ?? "512M";
    }

    public string GetRcloneDirCacheTime()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.dir-cache-time")) ?? "20s";
    }

    public string GetRcloneLogLevel()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.log-level")) ?? "NOTICE";
    }

    public string? GetRcloneExtraFlags()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.extra-flags"));
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("db.is-startup-vacuum-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsNzbBackupEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-location"));
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-time"));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}