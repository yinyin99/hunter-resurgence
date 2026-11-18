using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Launcher;

/// <summary>The launcher's bridge: StartGame, the install check, the folder buttons and the cache buttons are real, the rest is still the mock's.</summary>
/// <param name="appData">%APPDATA%; the game's cache is under its product folder.</param>
/// <param name="pictures">The Pictures folder; the game's screenshots are under its product folder.</param>
/// <param name="clearBrowser">Clears the launcher's own browser cache.</param>
public sealed class LauncherBridge(Func<GameInstall?> install, ILauncherBridge rest, Func<string, bool> isRunning, Func<ProcessStartInfo, bool> start, Func<bool> steamReady, string? steamAppId, Func<bool>? restart = null, TimeSpan? poll = null, TimeSpan? appearWithin = null,
    Func<string, GameProcess?>? find = null, RunHistory? history = null, ProblemReporter? reports = null, Action<string>? openFolder = null, Action<string>? showFile = null, string? appData = null, string? pictures = null, Func<Task>? clearBrowser = null,
    Func<GameInstallInfo>? gameInstall = null, Func<Result>? chooseFolder = null, Action? useAutomatic = null, InstallChecks? checks = null) : ILauncherBridge
{
    /// <param name="cookie">The community server's session; registered only with <c>server_url</c>.</param>
    public LauncherBridge(LauncherEnv env, ILogger<MockLauncherBridge> log, LauncherUpdater? updater = null, ServerCookie? cookie = null) : this(() => env.Install, new MockLauncherBridge(log),
        GameFiles.ClientRunning, Start,
        () => Steam.SignedIn(Steam.ActiveAccount, Steam.StartSilent, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1)), env.Settings.SteamAppId, updater is null ? null : updater.ApplyAndRestart,
        find: GameProcess.Find, history: new RunHistory(RunHistory.PathNextTo(env.PrefsPath)), reports: Reporter(env, cookie), openFolder: LauncherEnv.OpenFolder, showFile: LauncherEnv.ShowFile, appData: GameFiles.AppData,
        pictures: Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), clearBrowser: MainWindow.ClearBrowserCache,
        gameInstall: () => env.GameInstallInfo, chooseFolder: () => env.ChooseGameFolder(env.PickFolder), useAutomatic: env.UseAutomaticGameFolder, checks: env.Checks)
    { }

    static ProblemReporter Reporter(LauncherEnv env, ServerCookie? cookie)
    {
        var history = new RunHistory(RunHistory.PathNextTo(env.PrefsPath));
        // the graphics and options files the Game settings dialog edits (GameSettingsService)
        var settings = new[] { env.Settings.GraphicsFile, env.Settings.OptionsFile }.OfType<string>().Select(f => Path.Combine(GameFiles.AppData, f)).ToList();
        var http = env.Settings.ServerUrl is { } server ? new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = server, Timeout = TimeSpan.FromMinutes(2) } : null;
        // Build runs off the UI thread (ProblemReporter.BuildAsync), so waiting for the checks is fine here
        return new(new Diagnostics(() => env.Install, settings, env.Version, history.Load, installChecks: () => env.Checks.RunAsync(false).GetAwaiter().GetResult()), env.Prefs, env.Save, http, cookie);
    }

    /// <summary>The last game start this watcher saw; null before any.</summary>
    public GameRun? LastRun { get; private set; }

    /// <summary>The client's arguments: the server, a random number with bit 128 clear, and the Steam flag.</summary>
    public static string Arguments(string url, int param) => $"/connect:{url} /param:{param & int.MaxValue & ~128} /steam";

    public bool IsSteam => rest.IsSteam;
    public GameInstallInfo GameInstall => gameInstall?.Invoke() ?? rest.GameInstall;
    bool opened;
    /// <summary>All nine checks; the game-running check only on the first call (the launcher's own start is what runs the game afterwards).</summary>
    public Task<IReadOnlyList<InstallCheck>> CheckInstallAsync()
    {
        if (checks is null) return rest.CheckInstallAsync();
        var opening = !opened;
        opened = true;
        return checks.RunAsync(opening);
    }

    public Result ChooseGameFolder() => chooseFolder?.Invoke() ?? rest.ChooseGameFolder();
    public void UseAutomaticGameFolder() { if (useAutomatic is null) rest.UseAutomaticGameFolder(); else useAutomatic(); }
    /// <summary>
    /// game: the game folder; logs: the client's folder, where the game writes its logs (the exe's own folder); screenshots: <c>Pictures\&lt;product&gt;\Screenshots</c>, or when the game
    /// has made none yet, the nearest folder above it that exists. A folder that does not exist (and no game found) is <c>Settings.NoFolder</c>.
    /// </summary>
    public Result OpenFolder(string kind)
    {
        if (install() is not { } g || openFolder is null || pictures is null) return Result.Fail("Settings.NoFolder");
        var shots = Path.Combine(pictures, Product(g));
        var dir = kind switch
        {
            "game" => g.Folder,
            "logs" => Path.GetDirectoryName(g.ClientExe),
            "screenshots" => new[] { Path.Combine(shots, "Screenshots"), shots, Path.GetDirectoryName(shots) }.FirstOrDefault(Directory.Exists),
            _ => null,
        };
        if (dir is null || !Directory.Exists(dir)) return Result.Fail("Settings.NoFolder");
        openFolder(dir);
        return Result.Success;
    }

    /// <summary>Shows a file in Explorer; <c>Report.ErrShow</c> when it is gone or Windows refuses.</summary>
    public Result ShowFile(string path)
    {
        if (showFile is null) return rest.ShowFile(path);
        if (!File.Exists(path)) return Result.Fail("Report.ErrShow");
        try { showFile(path); return Result.Success; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return Result.Fail("Report.ErrShow"); }
    }

    /// <summary>The game's per-user folder in %APPDATA% and Pictures is named like its exe without the extension.</summary>
    static string Product(GameInstall g) => Path.GetFileNameWithoutExtension(g.ClientExe);

    // the client's browser keeps its profile in cache\ (or cache_local, cache_dev, cache_beta by the server it connects to); in it, the cache folders hold files only the browser can rebuild.
    // Cookies, local storage and settings live in other folders of the profile and stay.
    static readonly string[] CacheRoots = ["cache", "cache_local", "cache_dev", "cache_beta"];
    static readonly string[] CacheFolders = ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache", "GraphiteDawnCache"];

    /// <summary>
    /// game: deletes the cache folders of the game's browser (never cookies, local storage or settings); refused while the game runs (<c>Settings.CacheGameRunning</c>), and
    /// <c>Settings.CacheFailed</c> when a folder could not be deleted (the rest is still cleared). launcher: the launcher's own browser clears its disk and cache storage while running (cookies and
    /// local storage stay); ponytail: not awaited (the bridge is synchronous and runs on the UI thread), so a failure there is not reported.
    /// </summary>
    public Result ClearCache(string kind)
    {
        if (kind == "launcher")
        {
            if (clearBrowser is null) return Result.Fail("Settings.CacheFailed");
            _ = ClearBrowser(clearBrowser);
            return Result.Success;
        }
        if (kind != "game" || appData is null) return Result.Fail("Settings.CacheFailed");
        if (install() is not { } g) return Result.Fail("Settings.NoFolder");
        if (isRunning(g.ClientExe)) return Result.Fail("Settings.CacheGameRunning");
        var failed = false;
        foreach (var root in CacheRoots)
            foreach (var profile in new[] { "", "Default" })
                foreach (var name in CacheFolders)
                {
                    var dir = Path.Combine(appData, Product(g), root, profile, name);
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
                }
        return failed ? Result.Fail("Settings.CacheFailed") : Result.Success;
    }

    static async Task ClearBrowser(Func<Task> clear)
    {
        try { await clear(); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>
    /// Opens an <c>https://</c> or <c>steam://</c> address with Windows (the browser, Steam); any other address is ignored, and so is a failure to open. The text <c>{steam_app_id}</c> in the address is replaced by
    /// <c>steam_app_id</c> of settings.json (an address that names it without one is ignored): the id is configuration, not code.
    /// </summary>
    public void OpenExternal(string url)
    {
        if (openFolder is null) { rest.OpenExternal(url); return; }
        const string Id = "{steam_app_id}";
        if (url.Contains(Id, StringComparison.Ordinal)) url = steamAppId is null ? "" : url.Replace(Id, steamAppId, StringComparison.Ordinal);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "steam")) return;
        try { openFolder(u.AbsoluteUri); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
    public void ToggleDevTools() => rest.ToggleDevTools();
    /// <summary>The launcher has updated itself: apply the downloaded update and start the new one (never while the game runs).</summary>
    public void RestartLauncher() { if (restart?.Invoke() != true) rest.RestartLauncher(); }

    public Result StartGame(string url)
    {
        if (install() is not { } g || !File.Exists(g.ClientExe)) return Result.Fail("Launch.NoGame");
        if (url.Any(c => char.IsWhiteSpace(c) || c == '"')) return Result.Fail("Launch.Failed");
        if (isRunning(g.ClientExe)) return Result.Fail("Launch.AlreadyRunning");
        // version, files and setup block a start with the dashboard's text; ponytail: the first start after a cold cache waits for the server's answer (5 s at most) on this thread
        if (checks?.RunAsync(false, true).GetAwaiter().GetResult().FirstOrDefault(r => r.Level == "block" && r.Id is "version" or "files" or "setup") is { } blocked) return Result.Fail(blocked.Text);
        // the client asks Steam to start it again (running Steam's launch option) unless Steam is up and the app id is given
        if (!steamReady()) return Result.Fail("Launch.NoSteam");
        // the exe's own folder: the client finds its data and writes its logs relative to the working folder
        var info = new ProcessStartInfo(g.ClientExe, Arguments(url, Random.Shared.Next())) { WorkingDirectory = Path.GetDirectoryName(g.ClientExe) };
        if (steamAppId is not null) info.Environment["SteamAppId"] = info.Environment["SteamGameId"] = steamAppId;
        return start(info) ? Result.Success : Result.Fail("Launch.StartFailed");
    }

    /// <summary>The client's process is watched by its exe: Steam may start it a few seconds after <see cref="StartGame"/>, so it is awaited for up
    /// to a minute (false: it never appeared), then until it is gone (true) or the process found has exited. It also records the run (<see cref="LastRun"/>). ponytail: polls every <c>poll</c> (1 s); Steam starts the game, so there is no start to keep a handle from, and the process is looked up by name.</summary>
    public async Task<bool> WhenGameExitsAsync()
    {
        if (install() is not { } g) return false;
        var step = poll ?? TimeSpan.FromSeconds(1);
        for (var waited = TimeSpan.Zero; !isRunning(g.ClientExe) && waited < (appearWithin ?? TimeSpan.FromMinutes(1)); waited += step) await Task.Delay(step);
        if (!isRunning(g.ClientExe)) return false;
        // the run: when it was seen, how long it ran, its exit code and whether it ever showed a window (the process is looked up again while it is not held yet)
        var (start, ran) = (DateTimeOffset.Now, Stopwatch.StartNew());
        GameProcess? game = null;
        var window = false;
        try
        {
            do
            {
                game ??= find?.Invoke(g.ClientExe);
                window |= game?.WindowVisible() == true;
            // the run ends when the exe is gone or the process held has exited (a process that is still listed after its exit, or a second one started at once, must not keep the watch going)
            } while (isRunning(g.ClientExe) && game?.ExitCode() is null && await Delay(step));
            var run = LastRun = new GameRun(start, ran.Elapsed, game?.ExitCode(), window);
            history?.Add(run);
        }
        finally { game?.Dispose(); }
        return true;
    }

    static async Task<bool> Delay(TimeSpan step)
    {
        await Task.Delay(step);
        return true;
    }

    public Task<ProblemReport> BuildReportAsync(string trigger) => reports is null ? rest.BuildReportAsync(trigger) : reports.BuildAsync(trigger);
    public Task<ReportSent> SendReportAsync(ProblemReport report) => reports is null ? rest.SendReportAsync(report) : reports.SendAsync(report);
    public string SaveReport(ProblemReport report) => reports is null ? rest.SaveReport(report) : reports.Save(report);
    public string ReportConsent
    {
        get => reports?.Consent ?? rest.ReportConsent;
        set { if (reports is null) rest.ReportConsent = value; else reports.Consent = value; }
    }

    static bool Start(ProcessStartInfo i)
    {
        try { using var p = Process.Start(i); return p is not null; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
