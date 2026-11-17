using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

/// <param name="mock">Development states of the "Game folder" window: "folder:two" (a second install seen), "folder:manual" (chosen by the player), "folder:gone" (the chosen folder lost the game), "folder:none" (no game; a start says so),
/// "folder:refuse" (the picker refuses the folder). States of the game check (docs/website.md#the-game-check): "check:old", "check:updating", "check:newer", "check:files", "check:path", "check:steam",
/// "check:setup", "check:windows", "check:disk", "check:running" (one problem each) and "check:many" (an old game, a path and a disk warning). Opening Steam "fixes" the problem, as the player would in Steam.</param>
public sealed class MockLauncherBridge(ILogger<MockLauncherBridge> log, string? mock = null) : ILauncherBridge
{
    const string Steam = @"C:\Program Files (x86)\Steam\steamapps\common\Hunter", Chosen = @"D:\Games\Hunter";

    string? check = mock is { } m && m.StartsWith("check:") ? m[6..] : null;

    public Task<IReadOnlyList<InstallCheck>> CheckInstallAsync() => Task.FromResult(Checks());

    IReadOnlyList<InstallCheck> Checks()
    {
        InstallCheck Ok(string id) => new(id, "ok", "Check.Ok");
        InstallCheck Problem(string id, string level, string text, string? fix = null, params (string, string)[] args) => new(id, level, text, args.ToDictionary(a => a.Item1, a => a.Item2), fix);
        var steam = Problem("steam", "block", "Check.Steam.SignedOut", "open-steam");
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
            check is "path" or "many" ? path : Ok("path"),
            check == "steam" ? steam : Ok("steam"),
            check == "setup" ? Problem("setup", "block", "Kit.NoHosts", "setup") : Ok("setup"),
            check == "windows" ? Problem("windows", "warn", "Check.Windows.Blocked", null, ("file", "crashreport.dll")) : Ok("windows"),
            check is "disk" or "many" ? disk : Ok("disk"),
            check == "running" ? Problem("running", "block", "Check.Running", "close-game") : Ok("running"),
        ];
        return all;
    }

    public bool IsSteam => true;
    public GameInstallInfo GameInstall { get; private set; } = mock switch
    {
        "folder:two" => new(Steam, "steam-library", [@"E:\SteamLibrary\steamapps\common\Hunter"]),
        "folder:manual" => new(Chosen, "manual", []),
        "folder:gone" => new(Steam, "steam-library", [], @"D:\Games\Old"),
        "folder:none" => new(null, null, []),
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
    public Result ClearCache(string kind) { log.LogInformation("clear {Kind} cache", kind); return Result.Success; }
    public void OpenExternal(string url)
    {
        log.LogInformation("open {Url}", url);
        if (url.StartsWith("steam://")) check = null; // the player fixes it in Steam
    }
    public Result StartGame(string url)
    {
        log.LogInformation("start game {Url}", url);
        // as the launcher: the game, Steam and the running game keep their older keys, version, files and setup say the check's text
        return Checks().FirstOrDefault(c => c.Level == "block") is { } blocked ? Result.Fail(blocked.Id switch { "game" => "Launch.NoGame", "steam" => "Launch.NoSteam", "running" => "Launch.AlreadyRunning", _ => blocked.Text }) : Result.Success;
    }
    /// <summary>No game runs here: the old five seconds.</summary>
    public async Task<bool> WhenGameExitsAsync() { await Task.Delay(5000); LastRun = new(DateTimeOffset.Now.AddSeconds(-5), TimeSpan.FromSeconds(5), 0, true); return true; }
    public void ToggleDevTools() => log.LogInformation("toggle dev tools");
    public void RestartLauncher() => log.LogInformation("restart launcher");

    // problem reports: nothing is collected here; a start that "ran" five seconds counts as a short one
    public GameRun? LastRun { get; private set; }
    public string ReportConsent { get; set; } = "unset";
    public Task<ProblemReport> BuildReportAsync(string trigger) =>
        Task.FromResult(new ProblemReport(Path.Combine(Path.GetTempPath(), "problem-report.zip"), ["debug.log", "settings.json", "events.txt", "starts.json", "system.txt"], ["cef.log"], 48_213, trigger));
    public Task<ReportSent> SendReportAsync(ProblemReport report) { log.LogInformation("send report {Trigger}", report.Trigger); return Task.FromResult(new ReportSent("p1-0a1b2c3d", null)); }
    public string SaveReport(ProblemReport report) => @"C:\Users\Hunter\Downloads\problem-report.zip";
}
