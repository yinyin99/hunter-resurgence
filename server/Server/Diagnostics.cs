// What the operator looks at when a player says the game fails: the last 30 days of one account (docs/server.md#crash-reports).
using System.Globalization;

namespace Resurgence.Server;

/// <summary>NoDisconnect: hunts that ended without the game's disconnect call (no heartbeat for a while, or a new start replaced them);
/// Short: closed hunts under 60 s; Event30: how many times the game said the connection to the host failed.</summary>
public sealed record Diagnostics(List<CrashReports.Row> Crashes, List<Diagnostics.Hunt> NoDisconnect, List<Diagnostics.Hunt> Short, long Event30)
{
    public const int Days = 30, ShortSeconds = 60, MaxHunts = 100;

    public sealed record Hunt(long Id, long Start, long Seconds);

    public static Diagnostics Of(Store store, long account, long now)
    {
        var since = now - Days * 86400L;
        using var con = store.Open(account);
        List<Hunt> Hunts(string where) =>
            [.. con.Rows("SELECT id, start_ts, end_ts - start_ts FROM expeditions WHERE account_id = $a AND end_ts IS NOT NULL AND start_ts >= ? AND " + where
                         + " ORDER BY start_ts DESC LIMIT ?", since, MaxHunts)
                .Select(r => new Hunt((long)r[0], (long)Convert.ToDouble(r[1]), (long)Convert.ToDouble(r[2])))];
        return new(CrashReports.Recent(store, account, since), Hunts("end_ts = last_seen"), Hunts($"end_ts - start_ts < {ShortSeconds}"),
                   (long)con.Scalar("SELECT COUNT(*) FROM events WHERE account_id = $a AND event = 30 AND ts >= ?", since)!);
    }

    static string At(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).ToString("u", CultureInfo.InvariantCulture);

    /// <summary>The text of the `review diagnostics` command.</summary>
    public IEnumerable<string> Lines()
    {
        yield return $"crash reports, last {Days} days: {Crashes.Count}";
        foreach (var c in Crashes)
            yield return $"  {At(c.Ts)}\t{c.Kind}\t{c.Module ?? "-"}\t{string.Join(" < ", c.Frames.Take(3))}\t{c.Message}";
        yield return $"hunts that ended without a disconnect: {NoDisconnect.Count}";
        foreach (var h in NoDisconnect)
            yield return $"  hunt {h.Id}\t{At(h.Start)}\t{h.Seconds} s";
        yield return $"hunts under {ShortSeconds} s: {Short.Count}";
        foreach (var h in Short)
            yield return $"  hunt {h.Id}\t{At(h.Start)}\t{h.Seconds} s";
        yield return $"event 30 (the connection to the host failed): {Event30}";
    }
}
