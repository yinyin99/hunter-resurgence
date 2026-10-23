using System.Globalization;

namespace Resurgence.Launcher;

public partial class App : System.Windows.Application
{
    /// <summary>The language this run uses (fixed at start; a changed choice applies after a restart).</summary>
    public static string Code { get; private set; } = "en";

    public App()
    {
        Code = Pick(Prefs.Load(Prefs.DefaultPath).Language, CultureInfo.CurrentUICulture.Name);
        // dates and numbers follow the UI language too
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Code);
    }

    /// <summary>The saved choice when it is one of the languages, else the closest to the Windows display language (de-AT: German), else English.</summary>
    public static string Pick(string? saved, string windows) =>
        (Resurgence.Ui.Languages.Find(saved) ?? Resurgence.Ui.Languages.Match(windows))?.Code ?? "en";
}
