using System.Globalization;

namespace Resurgence.Launcher;

public partial class App : System.Windows.Application
{
    /// <summary>Language code (also what launcher.json stores) and the culture of its Strings.&lt;culture&gt;.resx.</summary>
    public static readonly (string Code, string Culture)[] Languages =
        [("en", "en"), ("fr", "fr")];

    /// <summary>The language this run uses (fixed at start; a changed choice applies after a restart).</summary>
    public static string Code { get; private set; } = "en";

    public App()
    {
        Code = Pick(Prefs.Load(Prefs.DefaultPath).Language, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        // dates and numbers follow the UI language too
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Languages.First(l => l.Code == Code).Culture);
    }

    /// <summary>The saved choice when it is one of the two, else the Windows display language when it is, else English.</summary>
    public static string Pick(string? saved, string windows) =>
        Languages.Any(l => l.Code == saved) ? saved! : Languages.Any(l => l.Code == windows) ? windows : "en";
}
