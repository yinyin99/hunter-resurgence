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
    void StartGame(string url);
    /// <summary>Ctrl+D in the launcher window.</summary>
    void ToggleDevTools();
    /// <summary>The launcher has updated itself: start the new one.</summary>
    void RestartLauncher();
}
