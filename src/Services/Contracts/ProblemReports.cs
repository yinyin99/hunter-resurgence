namespace Resurgence.Services;

/// <summary>One game start the launcher watched.</summary>
/// <param name="WindowSeen">A visible top-level window of the game was seen while it ran.</param>
/// <param name="QuitCleanly">The game's log shows it reached a hunt or the main page and closed on WM_CLOSE; null when the log could not be read (or the run was not short).</param>
/// <param name="Mode">"steam" or "nosteam": how the launcher started it; null when it did not say (older records).</param>
public sealed record GameRun(DateTimeOffset Start, TimeSpan Ran, int? ExitCode, bool WindowSeen, string? Mode = null, bool? QuitCleanly = null)
{
    /// <summary>short, exitcode or nowindow when this run looks failed; null for a normal run.</summary>
    public string? Trigger => !WindowSeen ? "nowindow" : ExitCode is { } c && c != 0 ? "exitcode" : Ran < TimeSpan.FromSeconds(60) && QuitCleanly != true ? "short" : null;
}

/// <summary>A built problem report: the zip at <paramref name="Path"/>, the entries in it, what could not be read, its size and why it was made (manual, short, exitcode or nowindow).</summary>
public sealed record ProblemReport(string Path, IReadOnlyList<string> Files, IReadOnlyList<string> Missing, long Bytes, string Trigger);

/// <param name="Error">A Strings text key; null when sent.</param>
public sealed record ReportSent(string? Reference, string? Error);
