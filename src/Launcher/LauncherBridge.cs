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
/// <param name="start">Starts the client; null when it does not start.</param>
/// <param name="steam">What a start asks of Steam (docs/launcher.md, "Starting the game").</param>
/// <param name="poll">The step of every wait: for the game to appear, for Steam to sign in, for the client to quit.</param>
/// <param name="steamWait">How long a start waits for a Steam that is installed but has nobody signed in (20 s).</param>
/// <param name="quitWithin">How long a start with Steam is watched for the client to quit at once (5 s).</param>
/// <param name="earlyWithin">A game that ends within this long of its start closed right away (20 s).</param>
/// <param name="runInstaller">Runs one redistributable with its switches and completes when it is done (Windows asks for its rights); null: the mock's.</param>
/// <param name="clock">The PC's clock against the server's; an early exit 0 names the clock only when it is off (<see cref="GameExit.ClockAhead"/>). Null: never.</param>
/// <param name="processes">The client's processes with their main windows; null: none is known to have a window.</param>
/// <param name="bringToFront">Shows a client window over the others.</param>
/// <param name="stopClient">Closes the client and stops it when it stays (<see cref="GameFiles.StopClientAsync"/>); null: nothing is closed.</param>
public sealed class LauncherBridge(Func<GameInstall?> install, ILauncherBridge rest, Func<string, bool> isRunning, Func<ProcessStartInfo, GameProcess?> start, SteamProbes steam, string? steamAppId, Func<bool>? restart = null, TimeSpan? poll = null, TimeSpan? appearWithin = null,
    Func<string, GameProcess?>? find = null, RunHistory? history = null, ProblemReporter? reports = null, Action<string>? openFolder = null, Action<string>? showFile = null, string? appData = null, string? pictures = null, Func<Task>? clearBrowser = null,
    Func<GameInstallInfo>? gameInstall = null, Func<Result>? chooseFolder = null, Action? useAutomatic = null, InstallChecks? checks = null, TimeSpan? steamWait = null, TimeSpan? quitWithin = null,
    Settings? settings = null, ILogger? log = null, TimeSpan? earlyWithin = null, Func<string, string, Task>? runInstaller = null, ServerClock? clock = null,
    Func<string, IReadOnlyList<ClientProcess>>? processes = null, Action<nint>? bringToFront = null, Func<string?, TimeSpan, Task>? stopClient = null) : ILauncherBridge
{
    /// <param name="cookie">The community server's session; registered only with <c>server_url</c>.</param>
    public LauncherBridge(LauncherEnv env, ILogger<MockLauncherBridge> log, ILogger<LauncherBridge> bridgeLog, LauncherUpdater? updater = null, ServerCookie? cookie = null) : this(() => env.Install, new MockLauncherBridge(log),
        GameFiles.ClientRunning, GameProcess.Start, SteamProbes.Real, env.Settings.SteamAppId, updater is null ? null : updater.ApplyAndRestart,
        find: GameProcess.Find, history: new RunHistory(RunHistory.PathNextTo(env.PrefsPath)), reports: Reporter(env, cookie), openFolder: LauncherEnv.OpenFolder, showFile: LauncherEnv.ShowFile, appData: GameFiles.AppData,
        pictures: Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), clearBrowser: MainWindow.ClearBrowserCache,
        gameInstall: () => env.GameInstallInfo, chooseFolder: () => env.ChooseGameFolder(env.PickFolder), useAutomatic: env.UseAutomaticGameFolder, checks: env.Checks, settings: env.Settings, log: bridgeLog, runInstaller: RunInstaller, clock: env.Clock,
        processes: GameFiles.ClientProcesses, bringToFront: GameProcess.BringToFront, stopClient: GameFiles.StopClientAsync)
    { }

    static ProblemReporter Reporter(LauncherEnv env, ServerCookie? cookie)
    {
        var history = new RunHistory(RunHistory.PathNextTo(env.PrefsPath));
        // the graphics and options files the Game settings dialog edits (GameSettingsService)
        var settings = new[] { env.Settings.GraphicsFile, env.Settings.GraphicsFileNoSteam, env.Settings.OptionsFile }.OfType<string>().Select(f => Path.Combine(GameFiles.AppData, f)).ToList();
        var http = env.Settings.ServerUrl is { } server ? new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = server, Timeout = TimeSpan.FromMinutes(2) } : null;
        // Build runs off the UI thread (ProblemReporter.BuildAsync), so waiting for the checks is fine here
        return new(new Diagnostics(() => env.Install, settings, env.Version, history.Load, installChecks: () => env.Checks.RunAsync(false).GetAwaiter().GetResult(), launcherLog: LauncherLog.Default.FilePath), env.Prefs, env.Save, http, cookie);
    }

    /// <summary>The last game start this watcher saw; null before any.</summary>
    public GameRun? LastRun { get; private set; }

    /// <summary>The client's arguments: the server, a random number with bit 128 clear, and <c>/steam</c> when the game starts with Steam.</summary>
    public static string Arguments(string url, int param, bool steam) => $"/connect:{url} /param:{param & int.MaxValue & ~128}{(steam ? " /steam" : "")}";

    public bool IsSteam => rest.IsSteam;
    public GameInstallInfo GameInstall => gameInstall?.Invoke() ?? rest.GameInstall;
    bool opened;
    /// <summary>All ten checks; the game-running check only on the first call (the launcher's own start is what runs the game afterwards).</summary>
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
    /// Opens an <c>https://</c> or <c>steam://</c> address with Windows (the browser, Steam), or exactly <c>windowsdefender://appbrowser</c> (Windows Security's app control page); any other address is ignored, and so is a failure to open. The text <c>{steam_app_id}</c> in the address is replaced by
    /// <c>steam_app_id</c> of settings.json (an address that names it without one is ignored): the id is configuration, not code.
    /// </summary>
    public void OpenExternal(string url)
    {
        if (openFolder is null) { rest.OpenExternal(url); return; }
        const string Id = "{steam_app_id}";
        if (url.Contains(Id, StringComparison.Ordinal)) url = steamAppId is null ? "" : url.Replace(Id, steamAppId, StringComparison.Ordinal);
        Uri? u = null;
        if (url != AppControl && (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme is not ("https" or "steam"))) return;
        try { openFolder(u?.AbsoluteUri ?? url); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    const string AppControl = "windowsdefender://appbrowser";

    /// <summary>
    /// Runs the installers <see cref="Runtimes"/> picks one after the other, off the calling thread (a rejected rights prompt ends the rest). <c>Launch.NoRuntimes</c> when there are none.
    /// </summary>
    public Result RunGameRuntimes()
    {
        if (runInstaller is null) return rest.RunGameRuntimes();
        IReadOnlyList<(string File, string Args)> files;
        try { files = install() is { } g ? Runtimes(g.Folder) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return Result.Fail("Launch.NoRuntimes"); }
        if (files.Count == 0) return Result.Fail("Launch.NoRuntimes");
        // a second press while the installers run starts no second chain
        if (Interlocked.Exchange(ref installing, 1) == 1) return Result.Success;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var (file, args) in files)
                    try { await runInstaller(file, args); }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { log?.LogWarning("A game component was not installed: {Error}", e.Message); return; }
            }
            finally { Interlocked.Exchange(ref installing, 0); }
        });
        return Result.Success;
    }

    int installing;

    /// <summary>
    /// What Steam's first-time setup installs for the 32-bit game, from the first redist folder that has any (<c>_CommonRedist</c> or <c>redist</c> in the game folder, else Steam's shared <c>_CommonRedist</c> beside the
    /// game's library folder), in this order and only the ones present: Visual C++ 2010 and 2005 (<c>vcredist/&lt;year&gt;/vcredist_x86.exe</c>), then DirectX's <c>DirectX/*/DXSETUP.exe</c>. Never the x64, 2012 or 2022 ones
    /// (the game ships the 2015-2022 DLLs). <c>/passive</c> (progress, no questions) is the 2010 installer's documented switch, <c>/silent</c> DXSETUP's; the 2005 installer has no switch that keeps its progress.
    /// </summary>
    internal static IReadOnlyList<(string File, string Args)> Runtimes(string folder)
    {
        foreach (var root in new[] { "_CommonRedist", "redist", Path.Combine("..", "Steamworks Shared", "_CommonRedist") }.Select(r => Path.GetFullPath(Path.Combine(folder, r))).Where(Directory.Exists))
        {
            var found = new List<(string, string)>();
            foreach (var (year, args) in new[] { ("2010", "/passive /norestart"), ("2005", "") })
                if (Path.Combine(root, "vcredist", year, "vcredist_x86.exe") is var vc && File.Exists(vc)) found.Add((vc, args));
            if (Path.Combine(root, "DirectX") is var dx && Directory.Exists(dx))
                found.AddRange(Directory.EnumerateFiles(dx, "DXSETUP.exe", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).Select(f => (f, "/silent")));
            if (found.Count > 0) return found;
        }
        return [];
    }

    static async Task RunInstaller(string path, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
        if (p is not null) await p.WaitForExitAsync();
    }
    public void ToggleDevTools() => rest.ToggleDevTools();
    /// <summary>The launcher has updated itself: apply the downloaded update and start the new one (never while the game runs).</summary>
    public void RestartLauncher() { if (restart?.Invoke() != true) rest.RestartLauncher(); }

    /// <summary>A start link is good for 15 minutes (docs/server.md): the refused start is repeated only within this.</summary>
    static readonly TimeSpan RepeatWithin = TimeSpan.FromMinutes(5);
    sealed record Refusal(string Url, long At);
    Refusal? refused; // the start the running game refused: CloseGameAsync repeats it

    public Result StartGame(string url)
    {
        refused = null;
        if (install() is not { } g || !File.Exists(g.ClientExe)) return Result.Fail("Launch.NoGame");
        if (url.Any(c => char.IsWhiteSpace(c) || c == '"')) return Result.Fail("Launch.Failed");
        if (isRunning(g.ClientExe))
        {
            refused = new(url, Stopwatch.GetTimestamp());
            // a window: show it and start nothing; none: a copy left behind, probably by a crash
            if (processes?.Invoke(g.ClientExe).FirstOrDefault(p => p.Window != 0).Window is nint and not 0 and var window)
            {
                bringToFront?.Invoke(window);
                return Result.Fail("Launch.AlreadyRunning");
            }
            return Result.Fail("Launch.LeftOver");
        }
        // version, files and setup block a start with the dashboard's text; ponytail: the first start after a cold cache waits for the server's answer (5 s at most) on this thread
        if (checks?.RunAsync(false, true).GetAwaiter().GetResult().FirstOrDefault(r => r.Level == "block" && r.Id is "version" or "files" or "setup") is { } blocked) return Result.Fail(blocked.Text);
        var param = Random.Shared.Next();
        // this start has no mode yet: the last one's must not show while it waits for Steam or if it fails
        lastStartMode = null;
        StartStateChanged?.Invoke();
        var withSteam = SteamUp();
        CarryGraphics(withSteam);
        var game = Launch(g, url, param, withSteam);
        if (game is null) return Result.Fail("Launch.StartFailed");
        // the client quit at once because Steam is not there: once more without Steam, and never again
        if (withSteam && QuitEarly(game.Game) == SteamMissing)
        {
            log?.LogInformation("The game quit with {Code} after {Seconds} s when started with Steam; starting it again without Steam", SteamMissing, (int)game.Ran.Elapsed.TotalSeconds);
            game.Game.Dispose();
            CarryGraphics(false);
            game = Launch(g, url, param, false) is { } retry ? retry with { Retried = true } : null;
            if (game is null) return Result.Fail("Launch.StartFailed");
            withSteam = false;
        }
        // the process of the last start is the one watched (a process the watch never took is let go)
        Interlocked.Exchange(ref started, game)?.Game.Dispose();
        // the mode is told only now, once the game has really started: a start that failed says nothing of Steam
        lastStartMode = withSteam ? "steam" : "nosteam";
        StartStateChanged?.Invoke();
        return Result.Success;
    }

    public async Task<Result> CloseGameAsync(bool restart)
    {
        if (install() is not { } g) return Result.Success;
        if (stopClient is not null) await stopClient(g.ClientExe, TimeSpan.FromSeconds(10));
        if (isRunning(g.ClientExe)) return Result.Fail("Launch.CloseFailed");
        var was = Interlocked.Exchange(ref refused, null);
        if (!restart) return Result.Success;
        // an old link is not repeated (the game would crash on it): the player starts again from the page
        if (was is not { } r || Stopwatch.GetElapsedTime(r.At) > RepeatWithin) return Result.Fail("Launch.Failed");
        // off the UI thread: a start may wait for Steam
        return await Task.Run(() => StartGame(r.Url));
    }

    /// <summary>The graphics settings follow the start's mode (<see cref="GameFiles.CarryGraphics"/>); a failure is logged and never blocks the start.</summary>
    void CarryGraphics(bool withSteam)
    {
        if (settings is null || appData is null) return;
        if (GameFiles.CarryGraphics(settings, appData, withSteam) is { } error) log?.LogWarning("The graphics settings could not be copied for the start: {Error}", error);
    }

    /// <summary>
    /// Completes when the process the last <see cref="StartGame"/> started (the retry after a 16010) has ended, and records the run (<see cref="LastRun"/>): its mode, exit code and whether a window showed. Without such a
    /// process (the game was started by something else) the client is looked up by its exe: awaited for up to a minute (null: it never appeared), then watched until it is gone. Early: it ended within <c>earlyWithin</c> (20 s)
    /// of its start, with a known exit code. ponytail: polls every <c>poll</c> (1 s), so <see cref="GameExit.Seconds"/> may be a poll late.
    /// </summary>
    public async Task<GameExit?> WhenGameExitsAsync()
    {
        if (install() is not { } g) return null;
        var step = poll ?? TimeSpan.FromSeconds(1);
        var held = Interlocked.Exchange(ref started, null);
        if (held is null)
        {
            for (var waited = TimeSpan.Zero; !isRunning(g.ClientExe) && waited < (appearWithin ?? TimeSpan.FromMinutes(1)); waited += step) await Task.Delay(step);
            if (!isRunning(g.ClientExe)) return null;
        }
        // the run: when it started (seen, without a held process), how long it ran, its exit code and whether it ever showed a window (without a held process it is looked up again while it is not found yet)
        var (at, ran, mode, retried) = held is null ? (DateTimeOffset.Now, Stopwatch.StartNew(), lastStartMode, false) : (held.At, held.Ran, held.Mode, held.Retried);
        var game = held?.Game;
        var window = false;
        try
        {
            do
            {
                game ??= find?.Invoke(g.ClientExe);
                window |= game?.WindowVisible() == true;
            // the run ends when the process held has exited; without one, when the exe is gone or the process found has exited (a process that is still listed after its exit, or a second one started at once, must not keep the watch going)
            } while ((held is not null || isRunning(g.ClientExe)) && game?.ExitCode() is null && await Delay(step));
            var (code, elapsed) = (game?.ExitCode(), ran.Elapsed);
            var run = LastRun = new GameRun(at, elapsed, code, window, mode);
            history?.Add(run);
            log?.LogInformation("Game exited: code {Code}, {Seconds} s, window seen {Window}", code?.ToString() ?? "unknown", (int)elapsed.TotalSeconds, window);
            var early = code is not null && elapsed < (earlyWithin ?? TimeSpan.FromSeconds(20));
            return new GameExit(code ?? 0, (int)elapsed.TotalSeconds, mode ?? "", early, early ? Cause(code!.Value, retried) : null, early && code == 0 ? clock?.Ahead : null);
        }
        finally { game?.Dispose(); }
    }

    /// <summary>The cause of a game that closed right away: steam (16010 of the no-Steam retry after a start with Steam that quit with 16010 too: both were tried), blocked (app control), runtime (a DLL not found), dll (a function a game file imports is missing: a file replaced), closed (exit 0), crash (anything else).</summary>
    internal static string Cause(int code, bool retried) => code switch
    {
        SteamMissing when retried => "steam",
        Blocked => "blocked",
        DllNotFound => "runtime",
        EntryPointNotFound => "dll",
        0 => "closed",
        _ => "crash",
    };

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

    // ponytail: one start at a time (the UI disables its button while one runs)
    readonly ManualResetEventSlim skipSteam = new();
    volatile bool waitingForSteam;
    string? lastStartMode;
    /// <summary>The process of the last start, kept for the watch; the watch takes it.</summary>
    Started? started;

    /// <param name="Retried">a start with Steam quit with 16010 and this process is the start without Steam that followed it</param>
    sealed record Started(GameProcess Game, string Mode, DateTimeOffset At, Stopwatch Ran, bool Retried = false);

    public bool WaitingForSteam => waitingForSteam;
    public string? LastStartMode => lastStartMode;
    public event Action? StartStateChanged;
    public void StartWithoutSteam() { if (waitingForSteam) skipSteam.Set(); }

    /// <summary>The exit code of a client that quit with Steam missing.</summary>
    const int SteamMissing = 16010;
    // as the signed exit codes of Process: 3236495362 (blocked by Windows app control), 3221225781 (DLL not found) and 3221225785 (STATUS_ENTRYPOINT_NOT_FOUND)
    internal const int Blocked = unchecked((int)3236495362u), DllNotFound = unchecked((int)3221225781u), EntryPointNotFound = unchecked((int)3221225785u);

    /// <summary>
    /// True: start with <c>/steam</c>. Steam not installed: false at once. Signed in: true. Installed with nobody signed in: Steam is started minimized and waited for (<see cref="WaitingForSteam"/>) at most
    /// <c>steamWait</c> (20 s) or until <see cref="StartWithoutSteam"/>; false when it is late, so a Steam that updates itself does not hold the start, and the next start tries it again. Only the player's press
    /// of Start without Steam leaves Steam closed: the Steam this start launched, still without anybody signed in, is asked to exit (<see cref="SteamProbes.StopStarted"/>; never one that ran before). When the 20 s
    /// run out Steam is left alone, the player may be typing a sign-in code.
    /// </summary>
    bool SteamUp()
    {
        if (!steam.Installed()) return false;
        if (steam.SignedIn()) return true;
        if (!steam.Start()) return false;
        skipSteam.Reset();
        waitingForSteam = true;
        StartStateChanged?.Invoke();
        try
        {
            var step = poll ?? TimeSpan.FromSeconds(1);
            for (var waited = TimeSpan.Zero; waited < (steamWait ?? TimeSpan.FromSeconds(20)); waited += step)
            {
                if (skipSteam.Wait(step)) return Abandon();
                if (steam.SignedIn()) return true;
            }
            return false;
        }
        finally
        {
            waitingForSteam = false;
            StartStateChanged?.Invoke();
        }
    }

    bool Abandon()
    {
        if (!steam.SignedIn()) steam.StopStarted?.Invoke();
        return false;
    }

    /// <summary>Starts the client in the exe's own folder (it finds its data and writes its logs relative to the working folder); null when it does not start.</summary>
    Started? Launch(GameInstall g, string url, int param, bool withSteam)
    {
        var info = new ProcessStartInfo(g.ClientExe, Arguments(url, param, withSteam)) { WorkingDirectory = Path.GetDirectoryName(g.ClientExe) };
        // with the app id set the client does not ask Steam to start it again (that would run Steam's launch option), in either mode
        if (steamAppId is not null) info.Environment["SteamAppId"] = info.Environment["SteamGameId"] = steamAppId;
        var mode = withSteam ? "steam" : "nosteam";
        var game = start(info);
        if (game is null) log?.LogWarning("The game did not start, mode {Mode}", mode);
        else log?.LogInformation("Game process started, mode {Mode}", mode);
        return game is null ? null : new(game, mode, DateTimeOffset.Now, Stopwatch.StartNew());
    }

    /// <summary>Watches a client started with Steam for <c>quitWithin</c> (5 s) or until its window shows: its exit code when it quit, else null.</summary>
    int? QuitEarly(GameProcess game)
    {
        var step = poll ?? TimeSpan.FromSeconds(1);
        for (var waited = TimeSpan.Zero; waited < (quitWithin ?? TimeSpan.FromSeconds(5)); waited += step)
        {
            if (game.ExitCode() is { } code) return code;
            if (game.WindowVisible()) return null;
            Thread.Sleep(step);
        }
        return game.ExitCode();
    }
}
