using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Clients.Rclone;

/// <summary>
/// Client for interacting with rclone's remote control (RC) API.
/// See https://rclone.org/rc/ for API documentation.
/// </summary>
public class RcloneClient
{
    private static readonly HttpClient HttpClient = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string? Host { get; private set; }
    private static string? User { get; set; }
    private static string? Pass { get; set; }
    public static bool IsRemoteControlEnabled { get; private set; } = false;

    // When the embedded rclone mount is running, it owns the RC connection
    // (loopback, no auth) and the user's rclone.host/user/pass config is ignored.
    // volatile: written from the mount supervisor, read from the config-change
    // handler that fires on the request thread.
    private static volatile bool _embeddedRcActive = false;

    public static void Initialize(ConfigManager configManager)
    {
        Host = configManager.GetRcloneHost();
        User = configManager.GetRcloneUser();
        Pass = configManager.GetRclonePass();
        IsRemoteControlEnabled = configManager.IsRcloneRemoteControlEnabled();

        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // don't let user config clobber the embedded mount's RC connection.
            if (_embeddedRcActive) return;
            var changedConfig = configEventArgs.ChangedConfig;
            if (changedConfig.TryGetValue("rclone.host", out var host)) Host = host;
            if (changedConfig.TryGetValue("rclone.user", out var user)) User = user;
            if (changedConfig.TryGetValue("rclone.pass", out var pass)) Pass = pass;
            if (changedConfig.ContainsKey("rclone.rc-enabled"))
                IsRemoteControlEnabled = configManager.IsRcloneRemoteControlEnabled();
        };
    }

    /// <summary>
    /// Point the RC client at the embedded mount's loopback RC server. Called by
    /// RcloneMountService when it starts the embedded mount.
    /// </summary>
    public static void UseEmbeddedRemoteControl(string host)
    {
        _embeddedRcActive = true;
        Host = host;
        User = null;
        Pass = null;
        IsRemoteControlEnabled = true;
    }

    /// <summary>
    /// Restore the user-configured RC connection. Called when the embedded mount stops.
    /// </summary>
    public static void RestoreConfiguredRemoteControl(ConfigManager configManager)
    {
        _embeddedRcActive = false;
        Host = configManager.GetRcloneHost();
        User = configManager.GetRcloneUser();
        Pass = configManager.GetRclonePass();
        IsRemoteControlEnabled = configManager.IsRcloneRemoteControlEnabled();
    }

    /// <summary>
    /// Refresh the VFS directory cache for multiple paths in a single request.
    /// </summary>
    /// <param name="paths">The paths to refresh</param>
    /// <param name="recursive">Whether to refresh recursively</param>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<RcloneResponse> RefreshVfsPaths(IEnumerable<string> paths, bool recursive = false)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return new RcloneResponse { Success = true };

        var request = new Dictionary<string, object?>();

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        if (recursive)
            request["recursive"] = true;

        Log.Debug("Rclone vfs/refresh: {0}", paths.ToIndentedJson());
        return await Post<RcloneResponse>("vfs/refresh", request);
    }

    /// <summary>
    /// Forget (clear) VFS directory cache entries for multiple paths in a single request.
    /// </summary>
    /// <param name="paths">The paths to forget</param>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<VfsForgetResponse> ForgetVfsPaths(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return new VfsForgetResponse { Success = true, Forgotten = new List<string>() };

        var request = new Dictionary<string, object?>();

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        Log.Debug("Rclone vfs/forget: {0}", paths.ToIndentedJson());
        return await Post<VfsForgetResponse>("vfs/forget", request);
    }

    /// <summary>
    /// Get VFS statistics including cache information.
    /// </summary>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<VfsStatsResponse> GetVfsStats(string? fs = null)
    {
        var request = fs != null ? new { fs } : null;
        return await Post<VfsStatsResponse>("vfs/stats", request);
    }

    /// <summary>
    /// Get rclone version information.
    /// </summary>
    public static async Task<CoreVersionResponse> GetVersion()
    {
        return await Post<CoreVersionResponse>("core/version", null);
    }

    /// <summary>
    /// Test connectivity - a no-operation call.
    /// </summary>
    public static async Task<RcloneResponse> NoOp()
    {
        return await Post<RcloneResponse>("rc/noop", null);
    }

    /// <summary>
    /// Check if the rclone RC server is reachable and authenticated.
    /// </summary>
    public static async Task<bool> IsAvailable()
    {
        try
        {
            await NoOp();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Test connectivity to a rclone RC server with the given credentials.
    /// </summary>
    public static async Task<RcloneResponse> TestConnection(string host, string? user, string? pass)
    {
        var result = await Post<CoreVersionResponse>(host, user, pass, "core/version", null);
        if (result.Success && string.IsNullOrEmpty(result.Version))
            return new RcloneResponse { Success = false, Error = "Connected but received empty version" };
        return result;
    }

    private static async Task<T> Post<T>
    (
        string host,
        string? user,
        string? pass,
        string endpoint,
        object? body
    ) where T : RcloneResponse, new()
    {
        var url = $"{host}/{endpoint}";
        var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (body != null)
        {
            var jsonBody = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        else
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        AddAuthHeader(request, user, pass);

        try
        {
            using var response = await HttpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Rclone RC request to {Endpoint} failed with status {StatusCode}: {Content}",
                    endpoint, response.StatusCode, content);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new T { Success = false, Error = "Authentication failed" };
                }

                try
                {
                    var errorResponse = JsonSerializer.Deserialize<RcloneErrorResponse>(content, JsonOptions);
                    return new T { Success = false, Error = errorResponse?.Error ?? $"HTTP {response.StatusCode}" };
                }
                catch
                {
                    return new T { Success = false, Error = $"HTTP {response.StatusCode}: {content}" };
                }
            }

            if (string.IsNullOrWhiteSpace(content) || content == "{}")
            {
                return new T { Success = true };
            }

            var result = JsonSerializer.Deserialize<T>(content, JsonOptions) ?? new T();
            result.Success = true;
            return result;
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Rclone RC request to {Endpoint} failed", endpoint);
            return new T { Success = false, Error = ex.Message };
        }
        catch (TaskCanceledException ex)
        {
            Log.Warning(ex, "Rclone RC request to {Endpoint} timed out", endpoint);
            return new T { Success = false, Error = "Request timed out" };
        }
    }

    private static Task<T> Post<T>(string endpoint, object? body) where T : RcloneResponse, new()
        => Post<T>(Host!, User, Pass, endpoint, body);

    private static void AddAuthHeader(HttpRequestMessage request, string? user, string? pass)
    {
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass))
            return;

        var credentials = $"{user ?? ""}:{pass ?? ""}";
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);
    }
}