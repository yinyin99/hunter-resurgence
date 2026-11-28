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
    /// <summary>Steam is installed on this PC: the "Install with Steam" button then opens Steam's install dialog, else it sends the player to Steam's download page.</summary>
    bool SteamInstalled => true;
    /// <summary>Looks for the game again, so that <see cref="GameInstall"/> and the install check see an install made meanwhile (the player installed it with Steam).</summary>
    void DetectGame() { }
    /// <summary>Clears a cache; a failure is a <see cref="Result"/> whose Error is a text key (<c>Settings.NoFolder</c>, <c>Settings.CacheGameRunning</c>, <c>Settings.CacheFailed</c>).</summary>
    /// <param name="kind">launcher or game</param>
    Result ClearCache(string kind);
    void OpenExternal(string url);
    /// <summary>Starts the game client; a failure is a <see cref="Result"/> whose Error is a text key.</summary>
    Result StartGame(string url);
    /// <summary>Closes the running game like its window's × and stops it when it does not (also an error-reporting window that holds it). Starts nothing: the player starts again from the page. Fails with <c>Launch.CloseFailed</c> when the game is still there.</summary>
    Task<Result> CloseGameAsync();
    /// <summary>Completes when the game the launcher started LAST has closed (after a 16010 fallback: the retry's process, never an exited one). Null when the game never appeared.</summary>
    Task<GameExit?> WhenGameExitsAsync();
    /// <summary>Installs what Steam's first-time setup would, from the redistributables found in the game folder (<c>_CommonRedist</c> or the install's redist folder) or Steam's shared ones: Visual C++ 2010 and 2005 (x86) and DirectX June 2010, one after the other; Windows asks for the rights of each. A failure is a <see cref="Result"/> whose Error is a text key (<c>Launch.NoRuntimes</c> when there are none).</summary>
    Result RunGameRuntimes();
    /// <summary>Ctrl+D in the launcher window.</summary>
    void ToggleDevTools();
    /// <summary>The launcher has updated itself: start the new one.</summary>
    void RestartLauncher();
    /// <summary>The last game start the launcher watched; null before any.</summary>
    GameRun? LastRun { get; }
    /// <summary>True while a start waits for Steam to sign in (at most 20 s); the UI shows "Starting Steam…" and a Start without Steam button.</summary>
    bool WaitingForSteam { get; }
    /// <summary>Ends the wait at once: this start goes on without Steam.</summary>
    void StartWithoutSteam();
    /// <summary>Raised when <see cref="WaitingForSteam"/> or <see cref="LastStartMode"/> changes (any thread).</summary>
    event Action? StartStateChanged;
    /// <summary>"steam" or "nosteam" for the last start, null before any. A start that quit with 16010 (Steam missing) and was tried again reads "nosteam".</summary>
    string? LastStartMode { get; }
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
    /// <summary>The fix of the <c>display</c> check (<c>fix-display</c>): writes the display's current mode into the graphics file that is full screen at a mode the display does not offer. A failure is a <see cref="Result"/> whose Error is a text key (<c>GameSettings.*</c>).</summary>
    Result FixDisplay();
}
