using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Resurgence.Ui;

/// <summary>Date formats of the old interface: English month names in every language (the old French screens show them too).</summary>
public static class Fmt
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>Jul 9th</summary>
    public static string MonthDay(DateTime d) => $"{d.ToString("MMM", English)} {d.Day}{(d.Day % 100 is >= 11 and <= 13 ? "th" : (d.Day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" })}";

    /// <summary>21m 39s, with days and hours in front when needed. The old code zero-pads only the values 1 to 8 (kept).</summary>
    public static string Elapsed(this IStringLocalizer l, int seconds)
    {
        static string Pad(int n) => n is >= 1 and <= 8 ? "0" + n : n.ToString();
        var (d, h, m, s) = (seconds / 86400, seconds / 3600 % 24, seconds / 60 % 60, seconds % 60);
        return Units(l, d, h, Pad(m), Pad(s));
    }

    // 1d 16h 5m 32s with the unit texts of the language; days and hours only when needed
    static string Units(IStringLocalizer l, int d, int h, string m, string s) =>
        (d > 0 ? l["Fmt.Days", d] + " " : "") + (d > 0 || h > 0 ? l["Fmt.Hours", h] + " " : "") + l["Fmt.Minutes", m] + " " + l["Fmt.Seconds", s];

    /// <summary>117.74 Mb: binary units, two decimals for Gb and Mb, none below.</summary>
    public static string Size(this IStringLocalizer l, long bytes) => bytes switch
    {
        >= 1L << 30 => l["Fmt.Gb", ((double)bytes / (1L << 30)).ToString("0.00")],
        >= 1 << 20 => l["Fmt.Mb", ((double)bytes / (1 << 20)).ToString("0.00")],
        >= 1 << 10 => l["Fmt.Kb", bytes >> 10],
        _ => l["Fmt.Bytes", bytes],
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

    /// <summary>A wallet row date: 2026-10-15 12:00.</summary>
    public static string Purchase(DateTime d) => d.ToString("yyyy-MM-dd HH:mm", English);

    /// <summary>The "fromNow" wording of the old pages: a minute ago, an hour ago, 3 days ago.</summary>
    public static string Ago(this IStringLocalizer l, TimeSpan a) => a.TotalSeconds switch
    {
        < 45 => l["Fmt.AgoSeconds"],
        < 90 => l["Fmt.AgoMinute"],
        < 45 * 60 => l["Fmt.AgoMinutes", Math.Round(a.TotalMinutes)],
        < 90 * 60 => l["Fmt.AgoHour"],
        < 22 * 3600 => l["Fmt.AgoHours", Math.Round(a.TotalHours)],
        < 36 * 3600 => l["Fmt.AgoDay"],
        _ => l["Fmt.AgoDays", Math.Round(a.TotalDays)],
    };

    /// <summary>1d 16h 5m 32s: minutes are not padded, seconds have two digits.</summary>
    public static string Duration(this IStringLocalizer l, int seconds) =>
        Units(l, seconds / 86400, seconds / 3600 % 24, (seconds / 60 % 60).ToString(), (seconds % 60).ToString("00"));

    /// <summary>124.53m (408.56ft)</summary>
    public static string Metres(double m) => $"{m.ToString("0.00", English)}m ({(m * 3.28084).ToString("0.00", English)}ft)";

    /// <summary>102.89 km (63.93 miles)</summary>
    public static string Kilometres(this IStringLocalizer l, double km) => l["Fmt.Kilometres", km.ToString("0.00", English), (km * 0.621371192).ToString("0.00", English)];

    /// <summary>79.31%: always with a point, in every language.</summary>
    public static string Pct(double v) => v.ToString("0.00", English) + "%";

    /// <summary>12.3456</summary>
    public static string Score(double v) => v.ToString("0.0000", English);

    /// <summary>A number with a point, in every language.</summary>
    public static string Num(double v, string format) => v.ToString(format, English);
}
