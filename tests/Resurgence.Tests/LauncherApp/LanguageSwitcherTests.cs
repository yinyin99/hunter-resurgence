using System.Globalization;
using Microsoft.AspNetCore.Components;
using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

/// <summary>WP19: choosing a language saves it to the given launcher.json, becomes the process default and reloads the page. A real LauncherEnv, like LauncherServicesTests.</summary>
[Collection("ProcessState")]
public class LanguageSwitcherTests
{
    sealed class FakeNav : NavigationManager
    {
        public List<(string Uri, bool Force)> Navigations { get; } = [];

        public FakeNav() => Initialize("http://x/", "http://x/p");

        protected override void NavigateToCore(string uri, NavigationOptions options) => Navigations.Add((uri, options.ForceLoad));
    }

    [Fact]
    public void SetSavesTheChoiceSetsTheDefaultCulturesAndReloadsTheCurrentPage()
    {
        var (culture, ui) = (CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.json");
        try
        {
            var nav = new FakeNav();
            new LauncherLanguageSwitcher(new LauncherEnv(path), nav).Set("fr");

            Assert.Equal("fr", Prefs.Load(path).Language);
            Assert.Equal(("fr", "fr"), (CultureInfo.DefaultThreadCurrentCulture!.Name, CultureInfo.DefaultThreadCurrentUICulture!.Name));
            string? seen = null;
            var t = new Thread(() => seen = CultureInfo.CurrentUICulture.Name); // what Routes.razor sends on a reload
            t.Start();
            t.Join();
            Assert.Equal("fr", seen);
            Assert.Equal([("http://x/p", true)], nav.Navigations);
        }
        finally { (CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture) = (culture, ui); }
    }
}
