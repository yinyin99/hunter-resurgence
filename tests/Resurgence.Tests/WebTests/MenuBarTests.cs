using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Resurgence.Ui;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

public sealed class MenuBarTests : UiContext
{
    IReadOnlyList<string> ActiveItems(string path, bool launcher)
    {
        Services.GetRequiredService<HostMode>().Launcher = launcher;
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo(path);
        var cut = Render<MenuBar>();
        return [.. cut.FindAll("ul.items a.active").Select(a => a.TextContent)];
    }

    [Fact]
    public void TheMissionsItemIsMarkedOnAPathThatContainsItsRoute()
    {
        var missions = ActiveItems("/missions/daily", false);
        Assert.Equal([Services.GetRequiredService<IStringLocalizer<Strings>>()["Menu.Missions"].Value], missions);
    }

    [Fact]
    public void AnItemIsMarkedWhenItsRouteIsAnywhereInThePath()
    {
        var items = ActiveItems("/help/missions", false);
        var l = Services.GetRequiredService<IStringLocalizer<Strings>>();
        Assert.Equal([l["Menu.Missions"].Value, l["Menu.Help"].Value], items);
    }

    [Fact]
    public void InTheLauncherTheRootMarksTheNewsItem()
    {
        var items = ActiveItems("/", true);
        Assert.Equal([Services.GetRequiredService<IStringLocalizer<Strings>>()["Menu.News"].Value], items);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void TheBankLinkShowsOnlyOffTheLiveSite(bool live, int links)
    {
        Services.GetRequiredService<HostMode>().Live = live;
        var cut = Render<MenuBar>();
        Assert.Equal(links, cut.FindAll("a[href='/bank/ems']").Count);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]  // the live site has no competitions
    public void TheCompetitionsItemShowsOnlyOffTheLiveSite(bool live, int items)
    {
        Services.GetRequiredService<HostMode>().Live = live;
        var cut = Render<MenuBar>();
        Assert.Equal(items, cut.FindAll("a[href='/competitions']").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheMissionsItemOpensTheDailyPage(bool live)
    {
        Services.GetRequiredService<HostMode>().Live = live;
        var cut = Render<MenuBar>();
        Assert.Equal("/missions/daily", cut.Find("ul.items a.active, ul.items a[href^='/missions']").GetAttribute("href"));
    }
}
