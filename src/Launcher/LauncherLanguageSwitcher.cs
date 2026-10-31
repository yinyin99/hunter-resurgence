using System.Globalization;
using Microsoft.AspNetCore.Components;
using Resurgence.Ui;

namespace Resurgence.Launcher;

/// <summary>Saves the choice in launcher.json, makes it the process's default culture and reloads the page, which then renders in it.</summary>
public sealed class LauncherLanguageSwitcher(LauncherEnv env, NavigationManager nav) : ILanguageSwitcher
{
    internal NavigationManager Nav => nav;

    public void Set(string code)
    {
        env.Prefs.Language = code;
        env.Prefs.Save(Prefs.DefaultPath);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(code);
        nav.NavigateTo(nav.Uri, forceLoad: true);
    }
}
