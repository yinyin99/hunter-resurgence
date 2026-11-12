using Resurgence.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Resurgence.Launcher;

/// <summary>
/// The launcher's own update, shown on the existing patch screens: check at start, download (Downloading, Kind Launcher), then the "launcher updated" notice;
/// its OK button restarts into the new version (<see cref="ApplyAndRestart"/>). Nothing happens while the game runs, and any failure leaves the installed version.
/// </summary>
public sealed class LauncherUpdater(UpdateManager manager, Func<bool> gameRunning) : IPatchService
{
    const string Repo = "https://github.com/yinyin99/hunter-resurgence";

    volatile PatchStatus status = PatchStatus.Idle;
    UpdateInfo? downloaded;

    /// <summary>The updater of an installed launcher, with the releases of this repository as its feed; null when the launcher was not installed by Velopack (a dev run, the tests).</summary>
    public static LauncherUpdater? Create() =>
        VelopackLocator.IsCurrentSet && new UpdateManager(new GithubSource(Repo, null, false)) is { IsInstalled: true } m
            ? new(m, () => GameFiles.ClientRunning(Settings.Load().ClientExe)) : null;

    /// <summary>The update is found, downloaded and waiting for the restart.</summary>
    public bool Pending => downloaded is not null;

    public Task<PatchStatus> StatusAsync() => Task.FromResult(Pending && gameRunning() ? PatchStatus.Idle : status);

    /// <summary>Check and download once. False when there was no update, the game runs, or something failed (the installed version stays).</summary>
    public async Task<bool> CheckAsync()
    {
        try
        {
            if (!manager.IsInstalled || gameRunning()) return false;
            if (await manager.CheckForUpdatesAsync() is not { } update) return false;
            var asset = update.TargetFullRelease;
            status = new(PatchState.Downloading, DownloadKind.Launcher, 0, asset.Version.ToString(), asset.Size);
            await manager.DownloadUpdatesAsync(update, p => status = status with { Progress = p / 100.0 });
            downloaded = update;
            status = new(PatchState.Ready, Notice: LauncherNotice.LauncherReady);
            return true;
        }
        // a feed or download that fails in any way (network, checksum, a locked package folder) must not stop the launcher
        catch (Exception)
        {
            status = PatchStatus.Idle;
            return false;
        }
    }

    /// <summary>Applies the downloaded update and starts the new launcher; false (nothing done) when none is waiting or the game runs.</summary>
    public bool ApplyAndRestart()
    {
        if (downloaded is null || gameRunning()) return false;
        manager.ApplyUpdatesAndRestart(downloaded.TargetFullRelease, Environment.GetCommandLineArgs()[1..]); // the restart keeps the arguments (Steam's %command%)
        return true;
    }
}
