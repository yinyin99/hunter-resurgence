using Microsoft.Extensions.DependencyInjection;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The launcher's service provider builds with scope validation, each page's language switcher reloads through that page's NavigationManager, and each page has its own dialog slot.</summary>
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

    // a reload is a new page scope: a dialog open before it must not be open after it
    [Fact]
    public void EachPageStartsWithItsOwnDialogSlot()
    {
        using var services = Resurgence.Launcher.LauncherServices.Build(null);
        using var before = services.CreateScope();
        using var after = services.CreateScope();
        Assert.NotSame(before.ServiceProvider.GetRequiredService<Resurgence.Ui.Modals.ModalService>(), after.ServiceProvider.GetRequiredService<Resurgence.Ui.Modals.ModalService>());
    }
}
