namespace Resurgence.Launcher;

/// <summary>Reads the game's newest <c>*.netlog</c> (the client logs <c>CGameStateRun::OnEnter()</c> when it reaches a hunt or the main page, <c>CGameStatePause::OnEnter()</c> for the pause) to tell a quit from a failure.</summary>
static class GameLog
{
    /// <summary>True: the run reached <c>CGameStateRun</c> or <c>CGameStatePause</c> and closed on <c>WM_CLOSE</c>/<c>WM_DESTROY</c>; false: it did not; null: no log written since <paramref name="since"/>, or it cannot be read.</summary>
    public static bool? QuitCleanly(string folder, DateTimeOffset since)
    {
        try
        {
            var newest = new DirectoryInfo(folder).EnumerateFiles("*.netlog").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null || newest.LastWriteTimeUtc < since.UtcDateTime) return null;
            bool reached = false, closed = false;
            using var r = new StreamReader(new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (r.ReadLine() is { } line)
            {
                reached |= line.Contains("CGameStateRun::OnEnter", StringComparison.Ordinal) || line.Contains("CGameStatePause::OnEnter", StringComparison.Ordinal);
                closed |= line.Contains("WM_CLOSE", StringComparison.Ordinal) || line.Contains("WM_DESTROY", StringComparison.Ordinal);
            }
            return reached && closed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
