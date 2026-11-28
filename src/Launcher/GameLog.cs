namespace Resurgence.Launcher;

/// <summary>Reads the game's newest <c>*.netlog</c> to tell a quit from a failure. The client logs <c>CGameStateRun::OnEnter()</c> when a hunt starts (it has no state for the main menu) and <c>CGameStatePause::OnEnter()</c> for the pause.</summary>
static class GameLog
{
    /// <summary>The line the game's own Quit writes (the <c>exit_game</c> event, sent by the quit button and the in-game menu); it is not a window message, so a quit from the menu has no <c>WM_CLOSE</c>.</summary>
    const string ExitEvent = "CGame::m_ExitGameEvent";

    /// <summary>Lines that say the connection ended the session; a close or exit event after one is the player leaving a dead game, not a quit.</summary>
    static readonly string[] Dropped = ["CGameStateLostConnectionTo", "LOST CONNECTION DEBUG OnHostLostConnection", "LOST CONNECTION DEBUG OnKickPlayer", "Incompatible game versions"];

    /// <summary>True: the run reached <c>CGameStateRun</c> or <c>CGameStatePause</c> and then closed on <c>WM_CLOSE</c>/<c>WM_DESTROY</c>, or ended on <c>CGame::m_ExitGameEvent</c>, with none of <see cref="Dropped"/> before the closing line; false: it did not; null: no folder, no log written since <paramref name="since"/>, or it cannot be read.</summary>
    public static bool? QuitCleanly(string folder, DateTimeOffset since)
    {
        try
        {
            var newest = new DirectoryInfo(folder).EnumerateFiles("*.netlog").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null || newest.LastWriteTimeUtc < since.UtcDateTime) return null;
            bool reached = false, closed = false, dropped = false;
            using var r = new StreamReader(new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (r.ReadLine() is { } line)
            {
                reached |= line.Contains("CGameStateRun::OnEnter", StringComparison.Ordinal) || line.Contains("CGameStatePause::OnEnter", StringComparison.Ordinal);
                closed |= (line.Contains("WM_CLOSE", StringComparison.Ordinal) || line.Contains("WM_DESTROY", StringComparison.Ordinal) || line.Contains(ExitEvent, StringComparison.Ordinal)) && !dropped;
                dropped |= Array.Exists(Dropped, d => line.Contains(d, StringComparison.Ordinal));
            }
            return reached && closed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; } // an empty, missing or invalid folder: unknown, never a throw into the exit path
    }
}
