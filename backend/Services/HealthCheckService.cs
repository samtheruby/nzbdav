using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// This service monitors for health checks
/// </summary>
public class HealthCheckService : BackgroundService
{
    private readonly ConfigManager _configManager;
    private readonly INntpClient _streamingClient;
    private readonly INntpClient _healthCheckClient;
    private readonly WebsocketManager _websocketManager;

    private static readonly HashSet<string> _missingSegmentIds = [];

    private CancellationTokenSource _rescheduleCts = new();

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient streamingClient,
        UsenetHealthCheckClient healthCheckClient,
        WebsocketManager websocketManager
    )
    {
        _configManager = configManager;
        _streamingClient = streamingClient;
        _healthCheckClient = healthCheckClient;
        _websocketManager = websocketManager;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // when usenet host changes, clear the missing segments cache
            if (configEventArgs.ChangedConfig.ContainsKey("usenet.host"))
                lock (_missingSegmentIds) _missingSegmentIds.Clear();

            // when the health-check schedule changes, wake the loop to recompute it
            if (configEventArgs.ChangedConfig.ContainsKey("repair.healthcheck.schedule-enabled") ||
                configEventArgs.ChangedConfig.ContainsKey("repair.healthcheck.schedule-time"))
            {
                var old = Interlocked.Exchange(ref _rescheduleCts, new CancellationTokenSource());
                old.Cancel();
                old.Dispose();
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // if the repair-job is disabled, then don't do anything
                if (!_configManager.IsRepairJobEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // a config change to the schedule cancels this token so the loop recomputes
                using var cts = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken, _rescheduleCts.Token);

                var execution = ResolveExecution();

                if (_configManager.IsHealthCheckScheduleEnabled())
                {
                    // scheduled: wait for the daily run time, then drain the due queue once.
                    await RunScheduledDrain(execution, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    // continuous: process a batch of due items, or idle if the queue is empty.
                    if (!await ProcessDueBatch(execution, cts.Token).ConfigureAwait(false))
                        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (OperationCanceledException)
            {
                // the schedule config changed — loop and recompute the next run.
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error performing background health checks: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Picks the client, per-check connection count, and max concurrency for this pass.
    /// When a dedicated health-check pool is configured, checks run in parallel against
    /// it (HealthCheckConnectionsPerCheck connections each). Otherwise we fall back to
    /// the shared streaming pool, one check at a time, as before.
    /// </summary>
    private HealthCheckExecution ResolveExecution()
    {
        var maxConcurrent = HealthCheckScheduler.ComputeMaxConcurrentChecks(_configManager.GetUsenetProviderConfig());
        if (maxConcurrent >= 1)
            return new HealthCheckExecution(_healthCheckClient, HealthCheckScheduler.HealthCheckConnectionsPerCheck, maxConcurrent);

        var streamingConcurrency = _configManager.GetUsenetProviderConfig().TotalPooledConnections;
        return new HealthCheckExecution(_streamingClient, streamingConcurrency, 1);
    }

    private readonly record struct HealthCheckExecution(INntpClient Client, int PerCheckConcurrency, int MaxConcurrentChecks);

    /// <summary>
    /// Waits until the configured daily run time, then drains every currently-due
    /// item before returning. The backoff schedule still decides which items are
    /// due; this only confines when the worker is active (e.g. to off-hours).
    /// </summary>
    private async Task RunScheduledDrain(HealthCheckExecution execution, CancellationToken ct)
    {
        var now = DateTime.Now;
        var nextRun = HealthCheckScheduler.ComputeNextScheduledRun(now, _configManager.HealthCheckSchedule());

        Log.Information("HealthCheckScheduler: next run scheduled at {NextRun}", nextRun);
        await Task.Delay(nextRun - now, ct).ConfigureAwait(false);

        Log.Information("HealthCheckScheduler: draining due health checks");
        while (await ProcessDueBatch(execution, ct).ConfigureAwait(false))
        {
            // keep going until no item is currently due
        }
    }

    /// <summary>
    /// Health-checks up to MaxConcurrentChecks of the most-due items in parallel,
    /// returning true if at least one was processed. A distinct batch of item ids is
    /// selected up front so parallel workers never pick the same item. Items that have
    /// failed too many times are skipped — they're marked failed and no longer retried.
    /// </summary>
    private async Task<bool> ProcessDueBatch(HealthCheckExecution execution, CancellationToken ct)
    {
        var maxFailures = _configManager.GetHealthCheckMaxFailures();

        List<Guid> dueItemIds;
        await using (var dbContext = new DavDatabaseContext())
        {
            var dbClient = new DavDatabaseClient(dbContext);
            var currentDateTime = DateTimeOffset.UtcNow;
            dueItemIds = await GetHealthCheckQueueItems(dbClient)
                .Where(x => x.HealthCheckFailureCount < maxFailures)
                .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime)
                .Take(execution.MaxConcurrentChecks)
                .Select(x => x.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        if (dueItemIds.Count == 0) return false;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = execution.MaxConcurrentChecks,
            CancellationToken = ct
        };
        await Parallel.ForEachAsync(dueItemIds, options, async (id, token) =>
            await ProcessItem(execution, id, maxFailures, token).ConfigureAwait(false)
        ).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Loads and health-checks a single item using its own database context, so checks
    /// running in parallel don't share an EF context (which isn't thread-safe).
    /// </summary>
    private async Task ProcessItem(HealthCheckExecution execution, Guid id, int maxFailures, CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (davItem == null) return;

        await PerformHealthCheck(execution.Client, davItem, dbClient, execution.PerCheckConcurrency, maxFailures, ct)
            .ConfigureAwait(false);
    }

    public static IOrderedQueryable<DavItem> GetHealthCheckQueueItems(DavDatabaseClient dbClient)
    {
        return GetHealthCheckQueueItemsQuery(dbClient)
            .OrderBy(x => x.NextHealthCheck)
            .ThenByDescending(x => x.ReleaseDate)
            .ThenBy(x => x.Id);
    }

    public static IQueryable<DavItem> GetHealthCheckQueueItemsQuery(DavDatabaseClient dbClient)
    {
        return dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x => x.HistoryItemId == null);
    }

    private async Task PerformHealthCheck
    (
        INntpClient client,
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        int maxFailures,
        CancellationToken ct
    )
    {
        try
        {
            // update the release date, if null
            var segments = await GetAllSegments(davItem, dbClient, ct).ConfigureAwait(false);
            if (davItem.ReleaseDate == null) await UpdateReleaseDate(client, davItem, segments, ct).ConfigureAwait(false);


            // setup progress tracking
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
            progressHook.ProgressChanged += (_, progress) =>
            {
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            };

            // perform health check
            var progress = progressHook.ToPercentage(segments.Count);
            await client.CheckAllSegmentsAsync(segments, concurrency, progress, ct).ConfigureAwait(false);
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            // update the database
            var now = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = now;
            davItem.NextHealthCheck = HealthCheckScheduler.ComputeTieredNextCheck(
                davItem.ReleaseDate, now, _configManager.GetHealthCheckBackoffTiers());
            davItem.HealthCheckFailureCount = 0;
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Healthy,
                RepairStatus = HealthCheckResult.RepairAction.None,
                Message = "File is healthy."
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            if (FilenameUtil.IsImportantFileType(davItem.Name))
                lock (_missingSegmentIds)
                    _missingSegmentIds.Add(e.SegmentId);

            // when usenet article is missing, perform repairs
            await Repair(davItem, dbClient, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown or reschedule — let the outer loop decide what to do.
            throw;
        }
        catch (Exception e)
        {
            // transient failure (timeout, streaming contention, circuit-breaker trip).
            // Do NOT condemn the file. Advance NextHealthCheck by a short retry interval
            // and move on, so one flaky item can't re-select forever and stall the queue.
            // After too many consecutive failures, mark it failed and stop retrying.
            var now = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = now;
            davItem.NextHealthCheck = HealthCheckScheduler.ComputeRetryNextCheck(now);
            davItem.HealthCheckFailureCount++;
            var terminal = HealthCheckScheduler.IsTerminalFailure(davItem.HealthCheckFailureCount, maxFailures);
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = now,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = terminal
                    ? $"Health check failed {davItem.HealthCheckFailureCount} times, marked failed and no longer retried: {e.Message}"
                    : $"Transient error during health check, will retry: {e.Message}"
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task UpdateReleaseDate(INntpClient client, DavItem davItem, List<string> segments, CancellationToken ct)
    {
        var firstSegmentId = StringUtil.EmptyToNull(segments.FirstOrDefault());
        if (firstSegmentId == null) return;
        var articleHeadersResponse = await client.HeadAsync(firstSegmentId, ct).ConfigureAwait(false);
        var articleHeaders = articleHeadersResponse.ArticleHeaders!;
        davItem.ReleaseDate = articleHeaders.Date;
    }

    private async Task<List<string>> GetAllSegments(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
            return nzbFile?.SegmentIds?.ToList() ?? [];
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
            return rarFile?.RarParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var multipartFile = await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            return multipartFile?.Metadata?.FileParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        return [];
    }

    private async Task Repair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        try
        {
            // if the file pattern has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blocklistedFiles = _configManager.GetBlocklistedFiles();
            if (BlocklistedFilePostProcessor.MatchesAnyPattern(davItem.Name, blocklistedFiles))
            {
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        "Filename pattern is marked in settings as an ignored (unwanted) file.",
                        "Deleted file."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is unlinked/orphaned,
            // then we can simply delete it.
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            if (symlinkOrStrmPath == null)
            {
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        "Could not find corresponding symlink or strm-file within Library Dir.",
                        "Deleted file."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var linkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";
            foreach (var arrClient in _configManager.GetArrConfig().GetArrClients())
            {
                var rootFolders = await arrClient.GetRootFolders().ConfigureAwait(false);
                if (!rootFolders.Any(x => symlinkOrStrmPath.StartsWith(x.Path!))) continue;

                // if we found a corresponding arr instance,
                // then remove and search.
                if (await arrClient.RemoveAndSearch(symlinkOrStrmPath).ConfigureAwait(false))
                {
                    dbClient.Ctx.Items.Remove(davItem);
                    dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                    {
                        Id = Guid.NewGuid(),
                        DavItemId = davItem.Id,
                        Path = davItem.Path,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Result = HealthCheckResult.HealthResult.Unhealthy,
                        RepairStatus = HealthCheckResult.RepairAction.Repaired,
                        Message = string.Join(" ", [
                            "File had missing articles.",
                            $"Corresponding {linkType} found within Library Dir.",
                            "Triggered new Arr search."
                        ])
                    }));
                    await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                    return;
                }

                // if we could not find corresponding media-item to remove-and-search
                // within the found arr instance, then break out of this loop so that
                // we can fall back to the behavior below of deleting both the link-file
                // and the dav-item.
                break;
            }

            // if we could not find a corresponding arr instance
            // then we can delete both the item and the link-file.
            await Task.Run(() => File.Delete(symlinkOrStrmPath)).ConfigureAwait(false);
            dbClient.Ctx.Items.Remove(davItem);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.Deleted,
                Message = string.Join(" ", [
                    "File had missing articles.",
                    $"Corresponding {linkType} found within Library Dir.",
                    "Could not find corresponding Radarr/Sonarr media-item to trigger a new search.",
                    $"Deleted the webdav-file and {linkType}."
                ])
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy, and check again in a day.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}"
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private HealthCheckResult SendStatus(HealthCheckResult result)
    {
        _ = _websocketManager.SendMessage
        (
            WebsocketTopic.HealthItemStatus,
            $"{result.DavItemId}|{(int)result.Result}|{(int)result.RepairStatus}"
        );
        return result;
    }

    public static void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds)
                if (_missingSegmentIds.Contains(segmentId))
                    throw new UsenetArticleNotFoundException(segmentId);
        }
    }
}