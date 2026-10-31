using System.Globalization;

namespace Resurgence.Launcher;

public partial class App : System.Windows.Application
{
    public App()
    {
        // dates and numbers follow the UI language too. Only the process defaults are set, so that a language chosen later
        // (LauncherLanguageSwitcher) shows after the reload: assigning CurrentCulture/CurrentUICulture here would pin this thread
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.GetCultureInfo(Pick(Prefs.Load(Prefs.DefaultPath).Language, CultureInfo.CurrentUICulture.Name));
    }

    /// <summary>The saved choice when it is one of the languages, else the closest to the Windows display language (de-AT: German), else English.</summary>
    public static string Pick(string? saved, string windows) =>
        (Resurgence.Ui.Languages.Find(saved) ?? Resurgence.Ui.Languages.Match(windows))?.Code ?? "en";
}
