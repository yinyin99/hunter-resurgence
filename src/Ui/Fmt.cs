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

    /// <summary>2026-08-19 15:26:35</summary>
    public static string DateTime(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", English);
}
