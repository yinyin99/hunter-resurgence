using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Components;
using Resurgence.Ui.Pages;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

/// <summary>The "online now" pill: long text in the public hero, short text in the menu row, gone when the server gives no counts.</summary>
public sealed class OnlineNowTests : UiContext
{
    sealed class Counts(OnlineCounts? first) : IOnlineService
    {
        public OnlineCounts? Value { get; set; } = first;
        public Task<OnlineCounts?> NowAsync() => Task.FromResult(Value);
    }

    Counts Serve(OnlineCounts? counts)
    {
        var fake = new Counts(counts);
        Services.AddSingleton<IOnlineService>(fake);
        return fake;
    }

    [Theory]
    [InlineData(1, 37, 4, "37 hunters online · 4 multiplayer games")]
    [InlineData(1, 1, 1, "1 hunter online · 1 multiplayer game")]
    [InlineData(4, 37, 4, "37 players · 4 games")]
    [InlineData(4, 1, 1, "1 player · 1 game")]
    public void TheTextIsLongInTheHeroShortInTheMenuRowAndSingularForOne(int place, int players, int games, string text)
    {
        Serve(new(players, games));
        var cut = Render<OnlineNow>(p => p.Add(x => x.Place, place));
        Assert.Equal(text, cut.Find(".online").TextContent);
        Assert.Equal([players.ToString(), games.ToString()], cut.FindAll(".online strong").Select(s => s.TextContent));
    }

    [Fact]
    public void WithoutCountsThereIsNoPill()
    {
        Serve(null);
        Assert.Empty(Render<OnlineNow>(p => p.Add(x => x.Place, 4)).FindAll(".online"));
    }

    [Fact]
    public void TheCountsAreAskedAgainAndAFailedAnswerHidesThePill()
    {
        var fake = Serve(new(37, 4));
        var cut = Render<OnlineNow>(p => p.Add(x => x.Place, 4).Add(x => x.Every, TimeSpan.FromMilliseconds(50)));
        Assert.Contains("37 players", cut.Find(".online").TextContent);

        fake.Value = new(38, 5);
        cut.WaitForAssertion(() => Assert.Contains("38 players · 5 games", cut.Find(".online").TextContent));

        fake.Value = null;
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".online")));
    }

    [Fact]
    public async Task ThePublicHeroAndTheMenuRowEachHaveOne()
    {
        Serve(new(37, 4));
        await Services.GetRequiredService<ISessionService>().SignOutAsync();
        var feed = Render<Feed>();
        feed.WaitForAssertion(() => Assert.Single(feed.FindAll(".hero .online.p1")));
        Assert.Single(Render<MenuBar>().FindAll("nav.menu .online.p4"));
    }

    [Fact]
    public async Task ThePublicHeroHasNoPillOverAFriendsInvitation()
    {
        Serve(new(37, 4));
        await Services.GetRequiredService<ISessionService>().SignOutAsync();
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo("/feed?ref=Friend");
        var feed = Render<Feed>();
        feed.WaitForAssertion(() => Assert.Contains("Friend", feed.Find(".hero .referrer").TextContent));
        Assert.Empty(feed.FindAll(".hero .online"));
    }
}
