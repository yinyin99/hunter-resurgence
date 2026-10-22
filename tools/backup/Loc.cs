using System.Globalization;
using System.Resources;

namespace Backup;

/// <summary>The app's UI language: Strings.resx (English, the fallback) plus one Strings.&lt;lang&gt;.resx per language. Logs and manifest.json stay English and do not come through here.</summary>
static class Loc
{
    /// <summary>Code (also the file content), the culture of its resx, the name in its own language.</summary>
    public static readonly (string Code, string Culture, string Name)[] Languages =
    [
        ("en", "en", "English"), ("fr", "fr", "Français"), ("es", "es", "Español"), ("pt", "pt-BR", "Português"), ("de", "de", "Deutsch"), ("ru", "ru", "Русский"),
    ];

    public static readonly ResourceManager Rm = new("Backup.Strings", typeof(Loc).Assembly);
    static CultureInfo culture = CultureInfo.GetCultureInfo("en");

    public static string Code { get; private set; } = "en";

    /// <summary>The saved choice in <paramref name="path"/>, else the Windows display language when it is one of the six, else English.</summary>
    public static void Init(string path)
    {
        var saved = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        Set(Languages.Any(l => l.Code == saved) ? saved : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    }

    /// <summary>An unknown code means English. Safe from any thread: no thread culture is touched.</summary>
    public static void Set(string code)
    {
        var l = Languages.FirstOrDefault(l => l.Code == code, Languages[0]);
        (Code, culture) = (l.Code, CultureInfo.GetCultureInfo(l.Culture));
    }

    public static string S(string key) => Rm.GetString(key, culture) ?? throw new KeyNotFoundException("no string " + key);

    public static string F(string key, params object[] args) => string.Format(culture, S(key), args);
}
