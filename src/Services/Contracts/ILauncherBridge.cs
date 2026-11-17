namespace Resurgence.Services;

/// <summary>What only the launcher window can do; the website gets a stub that logs.</summary>
public interface ILauncherBridge
{
    bool IsSteam { get; }
    /// <param name="kind">game, screenshots or logs</param>
    void OpenFolder(string kind);
    /// <param name="kind">launcher or game</param>
    void ClearCache(string kind);
    void OpenExternal(string url);
    /// <summary>Starts the game client; a failure is a <see cref="Result"/> whose Error is a text key.</summary>
    Result StartGame(string url);
    /// <summary>Completes when the game started by <see cref="StartGame"/> has closed: true, the "Done hunting for now?" window opens; false when the game never appeared.</summary>
    Task<bool> WhenGameExitsAsync();
    /// <summary>Ctrl+D in the launcher window.</summary>
    void ToggleDevTools();
    /// <summary>The launcher has updated itself: start the new one.</summary>
    void RestartLauncher();
    /// <summary>The last game start the launcher watched; null before any.</summary>
    GameRun? LastRun { get; }
    /// <summary>Builds the problem report zip (never throws; a missing file is listed as missing).</summary>
    Task<ProblemReport> BuildReportAsync(string trigger);
    /// <summary>Sends a built report to the community server.</summary>
    Task<ReportSent> SendReportAsync(ProblemReport report);
    /// <summary>Copies the zip to the user's Downloads folder; returns the full path.</summary>
    string SaveReport(ProblemReport report);
    /// <summary>"unset" (never asked), "auto" (send after a failed start), "ask" (offer only). Stored in the launcher's prefs.</summary>
    string ReportConsent { get; set; }
}
