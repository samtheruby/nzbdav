using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

/// <summary>
/// Repoints organized-library symlinks from an old rclone mount path to the current
/// (e.g. embedded) mount path, so the old rclone mount can be safely deleted.
///
/// Each imported library symlink targets an absolute path like
/// {mountDir}/.ids/&lt;prefix&gt;/&lt;guid&gt;. When the mount path changes, those targets
/// break. This task finds every library symlink pointing under the old mount path and
/// rewrites it to the same dav-item under the new mount path. It is a no-op on a fresh
/// install (no matching symlinks) and when the old and new paths are identical.
/// </summary>
public class RepointSymlinksTask(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    string oldMountDir
) : BaseTask
{
    protected override async Task ExecuteInternal()
    {
        try
        {
            await RepointAll().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to repoint library symlinks to the new mount path.");
        }
    }

    private async Task RepointAll()
    {
        var libraryRoot = configManager.GetLibraryDir();
        if (string.IsNullOrEmpty(libraryRoot))
        {
            Report("Failed: Library Directory is not configured (set it on the Repairs tab).");
            return;
        }

        var newMountDir = configManager.GetRcloneMountDir();
        var oldDir = oldMountDir.TrimEnd('/');
        if (oldDir == newMountDir)
        {
            Report($"Done!\nThe old and new mount paths are identical ({newMountDir}); nothing to repoint.");
            return;
        }

        // Guard: don't repoint symlinks to a target that isn't actually mounted. An active
        // nzbdav mount always exposes the .ids folder; if it's missing, the embedded mount
        // isn't up yet and migrating now would create broken links.
        if (!Directory.Exists(Path.Combine(newMountDir, ".ids")))
        {
            Report($"Failed: '{newMountDir}' doesn't look like an active nzbdav mount (no .ids folder). " +
                   "Enable and verify the embedded mount before running the migration.");
            return;
        }

        var repointed = 0;
        var alreadyCorrect = 0;
        var errors = 0;
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));

        ReportProgress("Scanning library...", repointed, alreadyCorrect, errors);

        foreach (var info in SymlinkAndStrmUtil.GetAllSymlinksAndStrms(libraryRoot))
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (info is not SymlinkAndStrmUtil.SymlinkInfo symlink) continue;

            var davItemId = TryGetDavItemId(symlink.TargetPath, oldDir);
            if (davItemId == null) continue;

            var newTarget = DatabaseStoreSymlinkFile.GetTargetPath(davItemId.Value, newMountDir);
            if (newTarget == symlink.TargetPath)
            {
                alreadyCorrect++;
                continue;
            }

            try
            {
                await Task.Run(() =>
                {
                    // atomic replace: a crash between Delete and Create would lose the
                    // symlink entirely, so stage the new link beside the old then rename.
                    var tempPath = symlink.SymlinkPath + ".rpsl-tmp";
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    File.CreateSymbolicLink(tempPath, newTarget);
                    File.Move(tempPath, symlink.SymlinkPath, overwrite: true);
                }).ConfigureAwait(false);
                repointed++;
            }
            catch (Exception e)
            {
                errors++;
                Log.Warning(e, "Failed to repoint symlink {Path}", symlink.SymlinkPath);
            }

            debounce(() => ReportProgress("Repointing library symlinks...", repointed, alreadyCorrect, errors));
        }

        ReportProgress("Done!", repointed, alreadyCorrect, errors);
    }

    /// <summary>
    /// Returns the dav-item id a symlink points to, but only if it targets the given
    /// old mount path's .ids tree. Returns null for unrelated symlinks.
    /// </summary>
    private static Guid? TryGetDavItemId(string targetPath, string oldMountDir)
    {
        // require the separator so "/mnt/nzbdav" doesn't also match "/mnt/nzbdav-backup",
        // and so "/.idsfoo" can't satisfy the .ids guard.
        var prefix = oldMountDir + "/.ids/";
        if (!targetPath.StartsWith(prefix)) return null;
        var guid = Path.GetFileNameWithoutExtension(targetPath);
        return Guid.TryParse(guid, out var id) ? id : null;
    }

    private void Report(string message)
    {
        _ = websocketManager.SendMessage(WebsocketTopic.RepointSymlinksTaskProgress, message);
    }

    private void ReportProgress(string message, int repointed, int alreadyCorrect, int errors)
    {
        Report($"{message}\nRepointed: {repointed} | Already correct: {alreadyCorrect} | Errors: {errors}");
    }
}
