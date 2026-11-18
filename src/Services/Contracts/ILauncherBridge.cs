namespace Resurgence.Services;

/// <summary>What only the launcher window can do; the website gets a stub that logs.</summary>
public interface ILauncherBridge
{
    bool IsSteam { get; }
    /// <summary>Opens a folder in Explorer; a failure is a <see cref="Result"/> whose Error is a text key (<c>Settings.NoFolder</c>).</summary>
    /// <param name="kind">game, screenshots or logs</param>
    Result OpenFolder(string kind);
    /// <summary>Shows a file in Explorer, selected in its folder (the saved problem report); a failure is a <see cref="Result"/> whose Error is a text key (<c>Report.ErrShow</c>), also when the file is gone.</summary>
    Result ShowFile(string path);
    /// <summary>The game install the launcher uses now, how it was found, and the other installs it saw; Folder null when none.</summary>
    GameInstallInfo GameInstall { get; }
    /// <summary>Opens a folder picker; the chosen folder must hold the game's exe (the folder itself or its install root); saved as the manual folder. Error: the Strings key <c>Settings.FolderNoGame</c> when the exe isn't there; a cancelled picker is not an error: Ok, nothing changes.</summary>
    Result ChooseGameFolder();
    /// <summary>Forgets the manual folder; detection goes back to Steam.</summary>
    void UseAutomaticGameFolder();
    /// <summary>Clears a cache; a failure is a <see cref="Result"/> whose Error is a text key (<c>Settings.NoFolder</c>, <c>Settings.CacheGameRunning</c>, <c>Settings.CacheFailed</c>).</summary>
    /// <param name="kind">launcher or game</param>
    Result ClearCache(string kind);
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
    /// <summary>Runs every install check (#631) and returns one result per check, in the order of the Id list; never throws.</summary>
    Task<IReadOnlyList<InstallCheck>> CheckInstallAsync();
}
