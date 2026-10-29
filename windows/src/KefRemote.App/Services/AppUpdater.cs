using Microsoft.UI.Dispatching;
using Velopack;
using Velopack.Sources;

namespace KefRemote.App;

public enum UpdateStage
{
    /// <summary>This copy can't update itself: a dotnet publish folder, or a build from source.</summary>
    Unavailable,
    Idle,
    Checking,
    UpToDate,
    Downloading,
    /// <summary>Downloaded. Applied on the next start, or now with <see cref="AppUpdater.RestartToUpdate"/>.</summary>
    Ready,
    Failed,
}

/// <summary>
/// Updates from the GitHub releases through Velopack. Both release downloads (Setup.exe and the zip) are Velopack
/// installs and can update themselves; the release assets releases.win-x64.json / releases.win-arm64.json say what
/// the latest version is.
/// </summary>
sealed class AppUpdater
{
    public const string RepositoryUrl = "https://github.com/kimdaehee0824/Remote-for-KEF";

    readonly UpdateManager? manager;
    readonly DispatcherQueue queue;

    public UpdateStage Stage { get; private set; }

    /// <summary>The version found or downloaded, for Ready, Downloading and the notices.</summary>
    public string? NewVersion { get; private set; }

    /// <summary>Download progress, 0–100.</summary>
    public int Progress { get; private set; }

    /// <summary>Stage, version or progress changed. Raised on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>A download finished without the user asking for it (a scheduled check).</summary>
    public event Action<string>? ReadyInBackground;

    public AppUpdater(DispatcherQueue queue, bool enabled)
    {
        this.queue = queue;
        if (enabled)
        {
            try
            {
                var m = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
                if (m.IsInstalled) manager = m;
            }
            catch (Exception e)
            {
                Log.Write("updater unavailable", e);
            }
        }
        if (manager?.UpdatePendingRestart is VelopackAsset pending)
        {
            NewVersion = pending.Version.ToString();
            Stage = UpdateStage.Ready;
        }
        else
        {
            Stage = manager != null ? UpdateStage.Idle : UpdateStage.Unavailable;
        }
    }

    /// <summary>Self-test and demo screens show an installed copy that is up to date.</summary>
    public static AppUpdater ForScreens(DispatcherQueue queue) => new(queue, enabled: false) { Stage = UpdateStage.UpToDate };

    public bool CanCheck => Stage is UpdateStage.Idle or UpdateStage.UpToDate or UpdateStage.Failed;

    /// <summary>Checks the releases and downloads a newer version if there is one. Returns false if the check failed.</summary>
    public async Task<bool> CheckAndDownloadAsync(bool userInitiated)
    {
        if (manager == null || !CanCheck) return Stage != UpdateStage.Failed;
        Set(UpdateStage.Checking);
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update == null)
            {
                Set(UpdateStage.UpToDate);
                return true;
            }
            NewVersion = update.TargetFullRelease.Version.ToString();
            Progress = 0;
            Set(UpdateStage.Downloading);
            await manager.DownloadUpdatesAsync(update, p => queue.TryEnqueue(() =>
            {
                if (Stage != UpdateStage.Downloading || p == Progress) return;
                Progress = p;
                Changed?.Invoke();
            }));
            Log.Write($"update {NewVersion} downloaded");
            Set(UpdateStage.Ready);
            if (!userInitiated) ReadyInBackground?.Invoke(NewVersion);
            return true;
        }
        catch (Exception e)
        {
            Log.Write("update check failed", e);
            Set(UpdateStage.Failed);
            return false;
        }
    }

    /// <summary>
    /// Hands the downloaded version to Velopack's updater, which waits for this process to exit, replaces the app
    /// and starts it again in the tray. The caller exits the app right after.
    /// </summary>
    public bool RestartToUpdate()
    {
        if (manager?.UpdatePendingRestart is not VelopackAsset pending) return false;
        try
        {
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, restartArgs: ["--background"]);
            return true;
        }
        catch (Exception e)
        {
            Log.Write("could not start the update", e);
            Set(UpdateStage.Failed);
            return false;
        }
    }

    void Set(UpdateStage stage)
    {
        Stage = stage;
        Changed?.Invoke();
    }
}
