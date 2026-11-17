using Microsoft.Extensions.Logging;

namespace Resurgence.Services.Mocks;

public sealed class MockLauncherBridge(ILogger<MockLauncherBridge> log) : ILauncherBridge
{
    public bool IsSteam => true;
    public Result OpenFolder(string kind) { log.LogInformation("open folder {Kind}", kind); return Result.Success; }
    public Result ClearCache(string kind) { log.LogInformation("clear {Kind} cache", kind); return Result.Success; }
    public void OpenExternal(string url) => log.LogInformation("open {Url}", url);
    public Result StartGame(string url) { log.LogInformation("start game {Url}", url); return Result.Success; }
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
