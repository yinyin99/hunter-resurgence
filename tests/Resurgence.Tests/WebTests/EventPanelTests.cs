using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui.Components;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class EventPanelTests : UiContext
{
    static async Task<MockSeasonalEventsService> Service(string? mock, bool play)
    {
        var service = new MockSeasonalEventsService(mock);
        await service.SetAsync(play);
        return service;
    }

    [Theory]
    [InlineData("events:running", true, true)]
    [InlineData("events:running", false, false)]  // the player opted out
    [InlineData(null, true, false)]               // no event runs
    public async Task ThePanelShowsOnlyWhileAnEventRunsForAPlayerWhoPlaysEvents(string? mock, bool play, bool shown)
    {
        Services.AddSingleton<ISeasonalEventsService>(await Service(mock, play));

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".dash")));
        Assert.Equal(shown, cut.FindAll("#event-title").Count == 1);
    }

    [Fact]
    public async Task ThePanelListsTheCommunityMissionsTheContributorsThePersonalMissionsAndTheItems()
    {
        Services.AddSingleton<ISeasonalEventsService>(await Service("events:running", true));

        var cut = Render<EventPanel>();

        cut.WaitForAssertion(() => Assert.Equal("Halloween 2026", cut.Find("#event-title").TextContent));
        Assert.Contains("2026-10-22", cut.Find(".dates").TextContent);
        Assert.Equal(["12/40", "100/100"], cut.FindAll(".community .numbers").Select(n => n.TextContent));
        Assert.Equal("30", cut.FindAll(".community .bar")[0].Children[0].GetAttribute("style")!.Replace("width:", "").Replace("%", "").Replace(";", ""));
        Assert.Contains("500 gm$, 10 em$", cut.FindAll(".community .pay")[0].TextContent);
        Assert.Equal(["MockHunter", "OtherHunter"], cut.FindAll(".contributors a").Select(a => a.TextContent));
        Assert.Contains("1 of 2", cut.Find(".personal .numbers").TextContent);
        Assert.Equal("/store/item/962", cut.Find(".items a").GetAttribute("href"));
    }
}
