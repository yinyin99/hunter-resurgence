using System.Globalization;

namespace Resurgence.Ui;

/// <summary>Date formats of the old interface: English month names in every language (the old French screens show them too).</summary>
public static class Fmt
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>Jul 9th</summary>
    public static string MonthDay(DateTime d) => $"{d.ToString("MMM", English)} {d.Day}{(d.Day % 100 is >= 11 and <= 13 ? "th" : (d.Day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" })}";

    /// <summary>2026-08-19 15:26:35</summary>
    public static string DateTime(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", English);
}
