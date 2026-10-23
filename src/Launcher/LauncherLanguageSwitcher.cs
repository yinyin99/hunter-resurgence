using System.Globalization;
using Microsoft.AspNetCore.Components;
using Resurgence.Ui;

namespace Resurgence.Launcher;

/// <summary>Saves the choice in launcher.json, applies the culture to this process and reloads the page.</summary>
public sealed class LauncherLanguageSwitcher(LauncherEnv env, NavigationManager nav) : ILanguageSwitcher
{
    public void Set(string code)
    {
        env.Prefs.Language = code;
        env.Prefs.Save(Prefs.DefaultPath);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(App.Languages.First(l => l.Code == code).Culture);
        nav.NavigateTo(nav.Uri, forceLoad: true);
    }
}
