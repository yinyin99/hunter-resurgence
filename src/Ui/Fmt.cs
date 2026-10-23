using System.Globalization;

namespace Resurgence.Ui;

/// <summary>Date formats of the old interface: English month names in every language (the old French screens show them too).</summary>
public static class Fmt
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>Jul 9th</summary>
    public static string MonthDay(DateTime d) => $"{d.ToString("MMM", English)} {d.Day}{(d.Day % 100 is >= 11 and <= 13 ? "th" : (d.Day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" })}";

    /// <summary>21m 39s, with days and hours in front when needed. The old code zero-pads only the values 1 to 8 (kept).</summary>
    public static string Elapsed(int seconds)
    {
        static string Pad(int n) => n is >= 1 and <= 8 ? "0" + n : n.ToString();
        var (d, h, m, s) = (seconds / 86400, seconds / 3600 % 24, seconds / 60 % 60, seconds % 60);
        return (d > 0 ? $"{d}d " : "") + (d > 0 || h > 0 ? $"{h}h " : "") + $"{Pad(m)}m {Pad(s)}s";
    }

    /// <summary>117.74 Mb: binary units, two decimals for Gb and Mb, none below.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{(double)bytes / (1L << 30):0.00} Gb",
        >= 1 << 20 => $"{(double)bytes / (1 << 20):0.00} Mb",
        >= 1 << 10 => $"{bytes >> 10} Kb",
        _ => $"{bytes} bytes",
    };

    /// <summary>Oct 21st 07:00 UTC</summary>
    public static string Utc(DateTime d) => $"{MonthDay(d)} {d:HH:mm} UTC";

    /// <summary>Oct 21st 2026, 07:00 UTC</summary>
    public static string UtcLong(DateTime d) => $"{MonthDay(d)} {d.Year}, {d:HH:mm} UTC";

    /// <summary>A leaderboard value: scores with 4 decimals, ranges as 508.8m (1669.3ft), the other boards whole numbers.</summary>
    public static string Board(string kind, double v) => kind switch
    {
        "range" => $"{v.ToString("0.0", English)}m ({(v * 3.28084).ToString("0.0", English)}ft)",
        "hunterscore" or "lanebandit" or "bustthrough" => v.ToString("0", English),
        _ => v.ToString("0.0000", English),
    };

    /// <summary>2026-08-19 15:26:35</summary>
    public static string DateTime(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", English);

    /// <summary>A wallet row date: 2021-02-21 16:16.</summary>
    public static string Purchase(DateTime d) => d.ToString("yyyy-MM-dd HH:mm", English);

    /// <summary>The "fromNow" wording of the old pages: a minute ago, an hour ago, 3 days ago.</summary>
    public static string Ago(TimeSpan a) => a.TotalSeconds switch
    {
        < 45 => "a few seconds ago",
        < 90 => "a minute ago",
        < 45 * 60 => $"{Math.Round(a.TotalMinutes)} minutes ago",
        < 90 * 60 => "an hour ago",
        < 22 * 3600 => $"{Math.Round(a.TotalHours)} hours ago",
        < 36 * 3600 => "a day ago",
        _ => $"{Math.Round(a.TotalDays)} days ago",
    };

    /// <summary>1d 16h 5m 32s: minutes are not padded, seconds have two digits.</summary>
    public static string Duration(int seconds)
    {
        var (d, h, m, s) = (seconds / 86400, seconds / 3600 % 24, seconds / 60 % 60, seconds % 60);
        return (d > 0 ? $"{d}d " : "") + (d > 0 || h > 0 ? $"{h}h " : "") + $"{m}m {s:00}s";
    }

    /// <summary>124.53m (408.56ft)</summary>
    public static string Metres(double m) => $"{m.ToString("0.00", English)}m ({(m * 3.28084).ToString("0.00", English)}ft)";

    /// <summary>102.89 km (63.93 miles)</summary>
    public static string Kilometres(double km) => $"{km.ToString("0.00", English)} km ({(km * 0.621371192).ToString("0.00", English)} miles)";

    /// <summary>79.31%: always with a point, in every language.</summary>
    public static string Pct(double v) => v.ToString("0.00", English) + "%";

    /// <summary>12.3456</summary>
    public static string Score(double v) => v.ToString("0.0000", English);
}
