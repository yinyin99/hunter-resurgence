using Microsoft.Extensions.DependencyInjection;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The launcher's service provider builds with scope validation, each page's language switcher reloads through that page's NavigationManager, and each page has its own dialog slot.</summary>
public class LauncherServicesTests
{
    // Set is never called: it writes the real launcher.json and changes the culture of the process
    [Fact]
    public void TheLanguageSwitcherReloadsThroughItsOwnPagesNavigationManager()
    {
        using var services = Resurgence.Launcher.LauncherServices.Build(null, null);
        using var page = services.CreateScope();
        var switcher = Assert.IsType<Resurgence.Launcher.LauncherLanguageSwitcher>(page.ServiceProvider.GetRequiredService<Resurgence.Ui.ILanguageSwitcher>());
        Assert.Same(page.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(), switcher.Nav);
    }

    // a reload is a new page scope: a dialog open before it must not be open after it
    [Fact]
    public void EachPageStartsWithItsOwnDialogSlot()
    {
        using var services = Resurgence.Launcher.LauncherServices.Build(null, null);
        using var before = services.CreateScope();
        using var after = services.CreateScope();
        Assert.NotSame(before.ServiceProvider.GetRequiredService<Resurgence.Ui.Modals.ModalService>(), after.ServiceProvider.GetRequiredService<Resurgence.Ui.Modals.ModalService>());
    }

    // server_url: the website's services with one cookie and live mode; without it the mocks
    [Fact]
    public void ServerUrlSelectsTheWebsitesServicesAndNoServerUrlTheMocks()
    {
        using var mock = Resurgence.Launcher.LauncherServices.Build(null, null);
        using var live = Resurgence.Launcher.LauncherServices.Build(null, new Uri("https://server.example"));
        Assert.IsType<Resurgence.Services.Mocks.MockStoreService>(mock.GetRequiredService<Resurgence.Services.IStoreService>());
        Assert.False(mock.GetRequiredService<Resurgence.Ui.HostMode>().Live);
        Assert.IsType<Resurgence.Services.ServerStoreService>(live.GetRequiredService<Resurgence.Services.IStoreService>());
        Assert.IsType<Resurgence.Services.ServerProfileService>(live.GetRequiredService<Resurgence.Services.IProfileService>());
        Assert.IsType<Resurgence.Services.ServerFriendsService>(live.GetRequiredService<Resurgence.Services.IFriendsService>());
        Assert.IsType<Resurgence.Services.ServerMessagesService>(live.GetRequiredService<Resurgence.Services.IMessagesService>());
        Assert.IsType<Resurgence.Services.ServerWalletService>(live.GetRequiredService<Resurgence.Services.IWalletService>());
        Assert.IsType<Resurgence.Services.ServerMissionsService>(live.GetRequiredService<Resurgence.Services.IMissionsService>());
        Assert.IsType<Resurgence.Services.ServerLeaderboardsService>(live.GetRequiredService<Resurgence.Services.ILeaderboardsService>());
        Assert.IsType<Resurgence.Services.ServerRestoreService>(live.GetRequiredService<Resurgence.Services.IRestoreService>());
        Assert.True(live.GetRequiredService<Resurgence.Ui.HostMode>().Live);
        Assert.Equal(Resurgence.Services.PatchState.Ready, live.GetRequiredService<Resurgence.Services.IPatchService>().StatusAsync().Result.State);
        Assert.Same(live.GetRequiredService<Resurgence.Services.ServerCookie>(), live.GetRequiredService<Resurgence.Services.ServerCookie>());
    }

    [Theory]
    [InlineData("/competitions", true)]
    [InlineData("/bank", true)]
    [InlineData("/settings", true)]
    [InlineData("/game-management", true)]
    [InlineData("/feed", false)]
    public void LiveLauncherHidesTheMockOnlyPages(string path, bool hidden) => Assert.Equal(hidden, Resurgence.Ui.LauncherGuard.MockOnly(path));
}
