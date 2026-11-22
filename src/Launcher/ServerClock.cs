using System.Diagnostics;
using System.Net.Http;

namespace Resurgence.Launcher;

/// <summary>
/// How far this PC's clock is from the community server's, from the <c>Date</c> header of a launcher request to the server (the install check's, at the start): <see cref="Observe"/> every answer, read <see cref="Ahead"/>.
/// One instance per launcher (<see cref="LauncherEnv.Clock"/>); the early-exit window uses it, and the clock warning (#648) can too. The server's time is kept with a monotonic stamp and moved on by it, so a player who corrects the clock afterwards is
/// no longer told it is off. ponytail: the header has whole seconds and the request's own time is not taken off, far below <see cref="Tolerance"/>.
/// </summary>
/// <param name="now">This PC's time; a test passes its own.</param>
/// <param name="uptime">A monotonic time (it does not follow the clock being set); a test passes its own.</param>
public sealed class ServerClock(Func<DateTimeOffset>? now = null, Func<TimeSpan>? uptime = null)
{
    /// <summary>A difference up to this is a clock in step (time zones and daylight saving never differ by minutes).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    (DateTimeOffset Server, TimeSpan At)? seen;

    DateTimeOffset Now => now?.Invoke() ?? DateTimeOffset.UtcNow;
    TimeSpan Uptime => uptime?.Invoke() ?? Stopwatch.GetElapsedTime(0);

    /// <summary>This PC's clock minus the server's now (the server's time of the last answer moved on by the time since), however small; null before an answer with a <c>Date</c> header.</summary>
    public TimeSpan? Offset => seen is { } s ? Now - (s.Server + (Uptime - s.At)) : null;

    /// <summary><see cref="Offset"/> when it is more than <see cref="Tolerance"/> (positive: the PC is ahead, negative: behind), else null: in step, or not measured.</summary>
    public TimeSpan? Ahead => Offset is { } o && o.Duration() > Tolerance ? o : null;

    public void Observe(HttpResponseMessage answer)
    {
        if (answer.Headers.Date is { } server) seen = (server, Uptime);
    }
}
