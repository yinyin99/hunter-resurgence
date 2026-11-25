using System.Globalization;
using Microsoft.AspNetCore.Components;
using Resurgence.Services;
using Resurgence.Ui;

namespace Resurgence.Launcher;

/// <summary>Saves the choice in launcher.json, makes it the process's default culture and reloads the page, which then renders in it. The game's language follows when the game has the language (a later pick in Game settings stays until the next switch).</summary>
public sealed class LauncherLanguageSwitcher(LauncherEnv env, NavigationManager nav) : ILanguageSwitcher
{
    internal NavigationManager Nav => nav;

    public void Set(string code)
    {
        env.Prefs.Language = code;
        env.Prefs.GameLanguage = GameLanguages.Match(code) ?? env.Prefs.GameLanguage;
        env.Prefs.Save(env.PrefsPath);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(code);
        nav.NavigateTo(nav.Uri, forceLoad: true);
    }
}
