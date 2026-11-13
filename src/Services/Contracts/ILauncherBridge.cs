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
}
