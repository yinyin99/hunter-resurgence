using System.Net.Http;

namespace Resurgence.Launcher;

/// <summary>
/// How far this PC's clock is from the community server's, from the <c>Date</c> header of a launcher request to the server (the install check's, at the start): <see cref="Observe"/> every answer, read <see cref="Ahead"/>.
/// One instance per launcher (<see cref="LauncherEnv.Clock"/>); the early-exit window uses it, and so will the clock warning (#648). ponytail: the header has whole seconds and the request's own time is not taken off, far below <see cref="Tolerance"/>.
/// </summary>
/// <param name="now">This PC's time; a test passes its own.</param>
public sealed class ServerClock(Func<DateTimeOffset>? now = null)
{
    /// <summary>A difference up to this is a clock in step (time zones and daylight saving never differ by minutes).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>This PC's clock minus the server's at the last answer, however small; null before an answer with a <c>Date</c> header.</summary>
    public TimeSpan? Offset { get; private set; }

    /// <summary><see cref="Offset"/> when it is more than <see cref="Tolerance"/> (positive: the PC is ahead, negative: behind), else null: in step, or not measured.</summary>
    public TimeSpan? Ahead => Offset is { } o && o.Duration() > Tolerance ? o : null;

    public void Observe(HttpResponseMessage answer)
    {
        if (answer.Headers.Date is { } server) Offset = (now?.Invoke() ?? DateTimeOffset.UtcNow) - server;
    }
}
