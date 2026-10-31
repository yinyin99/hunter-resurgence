using Microsoft.Extensions.DependencyInjection;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The launcher's service provider builds with scope validation, and each page's language switcher reloads through that page's NavigationManager.</summary>
public class LauncherServicesTests
{
    // Set is never called: it writes the real launcher.json and changes the culture of the process
    [Fact]
    public void TheLanguageSwitcherReloadsThroughItsOwnPagesNavigationManager()
    {
        using var services = Resurgence.Launcher.LauncherServices.Build(null);
        using var page = services.CreateScope();
        var switcher = Assert.IsType<Resurgence.Launcher.LauncherLanguageSwitcher>(page.ServiceProvider.GetRequiredService<Resurgence.Ui.ILanguageSwitcher>());
        Assert.Same(page.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(), switcher.Nav);
    }
}
