using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

/// <param name="mock">Development states of the "Game folder" window: "folder:two" (a second install seen), "folder:manual" (chosen by the player), "folder:gone" (the chosen folder lost the game), "folder:none" (no game; a start says so),
/// "folder:refuse" (the picker refuses the folder). "licence:owned", "licence:not_owned" and "licence:unknown" (no game, and the Steam licence the server reports for the account), "licence:nosteam" (no game, no Steam). States of the game check (docs/website.md#the-game-check): "check:old", "check:updating", "check:newer", "check:files", "check:path", "check:steam",
/// "check:setup", "check:windows", "check:disk", "check:running", "check:dlls" (one problem each) and "check:many" (an old game, a path and a disk warning). Opening Steam "fixes" the problem, as the player would in Steam.
/// Steam states of a start (docs/website.md#starting-without-steam): "steam:none" (Steam is not installed: the start goes on without it at once) and "steam:wait" (Steam is not signed in: the start waits for it 20 seconds or until "Start without Steam"); "check:steam" is the first one.</param>
public sealed class MockLauncherBridge(ILogger<MockLauncherBridge> log, string? mock = null) : ILauncherBridge
{
    const string Steam = @"C:\Program Files (x86)\Steam\steamapps\common\Hunter", Chosen = @"D:\Games\Hunter";

    string? check = mock switch { { } m when m.StartsWith("check:") => m[6..], "steam:wait" => "steam", "steam:none" => "steam-none", _ => null };
    readonly ManualResetEventSlim skipWait = new();

    public Task<IReadOnlyList<InstallCheck>> CheckInstallAsync() => Task.FromResult(Checks());

    IReadOnlyList<InstallCheck> Checks()
    {
        InstallCheck Ok(string id) => new(id, "ok", "Check.Ok");
        InstallCheck Problem(string id, string level, string text, string? fix = null, params (string, string)[] args) => new(id, level, text, args.ToDictionary(a => a.Item1, a => a.Item2), fix);
        var steam = Problem("steam", "warn", "Check.Steam.Fallback", "open-steam"); // a warning: the game starts without Steam
        var noSteam = Problem("steam", "warn", "Check.Steam.NotInstalled"); // not installed: nothing to open
        var old = Problem("version", "block", "Check.Version.Old", "open-steam");
        var path = Problem("path", "warn", "Check.Path.NonAscii", "choose-folder", ("path", @"C:\Users\Zoë\Games\Hunter"));
        var disk = Problem("disk", "warn", "Check.Disk.Low", null, ("drive", "C:"), ("free", "600 MB"));
        IReadOnlyList<InstallCheck> all =
        [
            GameInstall.Folder is null ? Problem("game", "block", "Check.Game.NotFound", "choose-folder") : Ok("game"),
            check switch
            {
                "old" or "many" => old,
                "updating" => Problem("version", "block", "Check.Version.Updating", "open-steam"),
                "newer" => Problem("version", "warn", "Check.Version.Newer"),
                _ => Ok("version"),
            },
            check == "files" ? Problem("files", "block", "Check.Files.Missing", "verify-files", ("file", "pc3.arc")) : Ok("files"),
            check == "exe" ? Problem("exe", "block", "Check.Exe.Other", "open-steam", ("version", "1.0.0.0")) : Ok("exe"),
            check is "path" or "many" ? path : Ok("path"),
            check == "steam" ? steam : check == "steam-none" ? noSteam : Ok("steam"),
            check == "setup" ? Problem("setup", "block", "Kit.NoHosts", "setup") : Ok("setup"),
            check == "windows" ? Problem("windows", "warn", "Check.Windows.Blocked", null, ("file", "crashreport.dll")) : Ok("windows"),
            check is "disk" or "many" ? disk : Ok("disk"),
            check == "display" ? Problem("display", "warn", "Check.Display.Mode", "fix-display", ("mode", "1366x768 60 Hz")) : check == "virtual" ? Problem("display", "warn", "Check.Display.Virtual", null, ("adapter", "Remote Display Adapter")) : Ok("display"),
            check == "running" ? Problem("running", "block", "Check.Running", "close-game") : Ok("running"),
            check == "dlls" ? Problem("dlls", "warn", "Check.Dlls.Extra", "open-folder", ("files", "d3d9.dll")) : Ok("dlls"),
        ];
        return all;
    }

    public bool IsSteam => true;
    bool installed;
    public void DetectGame() { if (installed) GameInstall = new(Steam, "steam-library", []); }
    public bool SteamInstalled => mock is not ("steam:none" or "licence:nosteam");
    public GameInstallInfo GameInstall { get; private set; } = mock switch
    {
        "folder:two" => new(Steam, "steam-library", [@"E:\SteamLibrary\steamapps\common\Hunter"]),
        "folder:manual" => new(Chosen, "manual", []),
        "folder:gone" => new(Steam, "steam-library", [], @"D:\Games\Old"),
        "folder:none" or "licence:owned" or "licence:not_owned" or "licence:unknown" or "licence:nosteam" => new(null, null, []),
        _ => new(Steam, "steam-library", []),
    };
    public Result ChooseGameFolder()
    {
        if (mock == "folder:refuse") return Result.Fail("Settings.FolderNoGame");
        GameInstall = new(Chosen, "manual", []);
        return Result.Success;
    }
    public void UseAutomaticGameFolder() => GameInstall = new(Steam, "steam-library", []);
    public Result OpenFolder(string kind) { log.LogInformation("open folder {Kind}", kind); return Result.Success; }
    public Result ShowFile(string path) { log.LogInformation("show file {Path}", path); return Result.Success; }
    public Result ClearCache(string kind) { log.LogInformation("clear {Kind} cache", kind); return Result.Success; }
    public void OpenExternal(string url)
    {
        log.LogInformation("open {Url}", url);
        if (url.StartsWith("steam://")) check = null; // the player fixes it in Steam
        if (url.StartsWith("steam://install/") && mock == "licence:owned") installed = true; // Steam installs the game; the next look for it finds it
    }
    public Result StartGame(string url)
    {
        log.LogInformation("start game {Url}", url);
        // as the launcher: the game and the running game keep their older keys, version, files and setup say the check's text
        if (Checks().FirstOrDefault(c => c.Level == "block") is { } blocked)
            return Result.Fail(blocked.Id switch { "game" => "Launch.NoGame", "running" => "Launch.AlreadyRunning", _ => blocked.Text });
        LastStartMode = null; // as the launcher: this start has no mode yet
        StartStateChanged?.Invoke();
        if (mock == "steam:wait" && check == "steam")
        {
            skipWait.Reset();
            WaitingForSteam = true;
            StartStateChanged?.Invoke();
            skipWait.Wait(TimeSpan.FromSeconds(20)); // the launcher's longest wait
            WaitingForSteam = false;
        }
        LastStartMode = check is "steam" or "steam-none" ? "nosteam" : "steam";
        StartStateChanged?.Invoke();
        return Result.Success;
    }
    public Task<Result> CloseGameAsync()
    {
        log.LogInformation("close game");
        if (check == "running") check = null;
        return Task.FromResult(Result.Success);
    }
    public bool WaitingForSteam { get; private set; }
    public void StartWithoutSteam() => skipWait.Set();
    public event Action? StartStateChanged;
    public string? LastStartMode { get; private set; }
    /// <summary>No game runs here: after the old five seconds a normal hunt; "exit:steam|blocked|runtime|noruntime|dll|closed|clock|display|crash" ends the start within seconds for that cause instead ("noruntime": the runtimes button fails, as with no runtimes in the game folder; "display": a closed-right-away exit after a start where the display check warned).</summary>
    public async Task<GameExit?> WhenGameExitsAsync()
    {
        var cause = mock is { } m && m.StartsWith("exit:") ? m[5..] : null;
        if (cause is null)
        {
            await Task.Delay(5000);
            LastRun = new(DateTimeOffset.Now.AddSeconds(-5), TimeSpan.FromSeconds(5), 0, true);
            return new(0, 5, LastStartMode ?? "steam", false, null);
        }
        await Task.Delay(1500);
        var code = cause switch { "steam" => 16010, "blocked" => unchecked((int)3236495362), "runtime" or "noruntime" => unchecked((int)3221225781), "closed" or "clock" or "display" => 0, "dll" => unchecked((int)3221225785), _ => unchecked((int)3221226505) };
        LastRun = new(DateTimeOffset.Now.AddSeconds(-4), TimeSpan.FromSeconds(4), code, cause != "steam", LastStartMode ?? "steam");
        return new(code, 4, LastStartMode ?? "steam", true, cause == "noruntime" ? "runtime" : cause is "clock" or "display" ? "closed" : cause, cause == "clock" ? TimeSpan.FromHours(3) : null,
            cause == "display" ? new InstallCheck("display", "warn", "Check.Display.Mode", new Dictionary<string, string> { ["mode"] = "1366x768 60 Hz" }, "fix-display") : null);
    }
    public Result RunGameRuntimes() { log.LogInformation("run game runtimes"); return mock == "exit:noruntime" ? Result.Fail("Launch.NoRuntimes") : Result.Success; }
    public void ToggleDevTools() => log.LogInformation("toggle dev tools");
    public void RestartLauncher() => log.LogInformation("restart launcher");
    public Result FixDisplay() { log.LogInformation("fix display"); check = null; return Result.Success; }

    // problem reports: nothing is collected here; a start that "ran" five seconds counts as a short one
    public GameRun? LastRun { get; private set; }
    public string ReportConsent { get; set; } = "unset";
    public Task<ProblemReport> BuildReportAsync(string trigger) =>
        Task.FromResult(new ProblemReport(Path.Combine(Path.GetTempPath(), "problem-report.zip"), ["debug.log", "settings.json", "events.txt", "starts.json", "system.txt"], ["cef.log"], 48_213, trigger));
    public Task<ReportSent> SendReportAsync(ProblemReport report) { log.LogInformation("send report {Trigger}", report.Trigger); return Task.FromResult(new ReportSent("p1-0a1b2c3d", null)); }
    public string SaveReport(ProblemReport report) => @"C:\Users\Hunter\Downloads\problem-report.zip";
}
