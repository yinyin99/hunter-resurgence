using System.Globalization;
using System.Resources;

namespace Play;

/// <summary>The console texts: Strings.resx (English, the fallback) plus one Strings.&lt;culture&gt;.resx per language, picked by the Windows display language.</summary>
static class Loc
{
    /// <summary>The cultures that have a Strings.&lt;culture&gt;.resx besides English.</summary>
    public static readonly string[] Cultures = ["fr", "es", "pt-BR", "de", "ru"];

    public static readonly ResourceManager Rm = new("Play.Strings", typeof(Loc).Assembly);

    static readonly CultureInfo culture = CultureInfo.GetCultureInfo(
        Cultures.FirstOrDefault(c => c.StartsWith(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase)) ?? "en");

    public static string S(string key) => Rm.GetString(key, culture) ?? throw new KeyNotFoundException("no string " + key);

    public static string F(string key, params object[] args) => string.Format(culture, S(key), args);
}
