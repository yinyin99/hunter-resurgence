using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Pages;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

/// <summary>HostMode.Live (the website with a community server): what the signed-in player owns or has joined, and the account's own pages, are not shown.</summary>
public sealed class LiveSiteTests : UiContext
{
    sealed class Answer(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    void Live(bool live) => Services.GetRequiredService<HostMode>().Live = live;

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void TheEnrolledListAndTheActionsAreTheMocksPlayersNotLiveData(bool live, int shown)
    {
        Live(live);
        var cut = Render<Competitions>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("table.comps.all tbody tr")));
        Assert.Equal(shown, cut.FindAll("h4.enrolled").Count);
        Assert.Equal(shown > 0, cut.FindAll("td.action").Count > 0);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void TheBundlesOwnColumnIsNotShownLive(bool live, int columns)
    {
        Live(live);
        var cut = Render<BundleDetail>(p => p.Add(b => b.Id, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.item")));
        Assert.Equal(columns, cut.FindAll("th.own").Count);
        Assert.Equal(live ? 2 : 4, cut.FindAll("article.item thead th").Count);  // no own limit either: the server sends none
    }

    [Fact]
    public void ARealCategoryNamesItsFirstSubCategoryNotItself()
    {
        const string reply = """{"categories": [{"key": "0", "name": "Weapons", "children": [{"key": "0", "name": "Rifles"}, {"key": "1", "name": "Handguns"}]}], "items": [], "bundles": []}""";
        Services.AddSingleton<IStoreService>(new ServerStoreService(new HttpClient(new Answer(HttpStatusCode.OK, reply)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new StoreCache(), NullLogger<ServerStoreService>.Instance));

        var cut = Render<Resurgence.Ui.Components.StoreFrame>(p => p.Add(f => f.Active, "0"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("nav.menu a.sub")));
        Assert.Equal(["Rifles", "Handguns"], cut.FindAll("nav.menu a.sub").Select(a => a.TextContent.Trim()));
        Assert.Equal("Weapons", cut.Find("nav.menu a.top.open").TextContent.Trim());  // the top-level label is the translated one
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void TheSettingsMenuKeepsRestoreAndLanguageOnly(bool live, int accountLinks)
    {
        Live(live);
        var cut = Render<SettingsMenu>();
        Assert.Equal(accountLinks, cut.FindAll("a[href='/settings'], a[href='/game-management']").Count);
        Assert.Single(cut.FindAll("a[href='/restore']"));
    }

    [Theory]
    [InlineData(false, "/settings")]
    [InlineData(true, "/restore")]
    public void TheSettingsHeadingOpensRestoreLive(bool live, string href)
    {
        Live(live);
        var cut = Render<StatusBar>(p => p.Add(b => b.Session, new Session("Fox", 5)));
        Assert.Equal(href, cut.Find("div.settings > a").GetAttribute("href"));
    }

    [Fact]
    public void ARealPlayersProfileIsTheHeaderOnly()
    {
        var real = new ServerProfileService(new HttpClient(new Answer(HttpStatusCode.OK, """{"handle":"Fox","hunterscore":5,"rank":3}""")) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance);
        Services.AddSingleton<IProfileService>(real);
        Live(true);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Fox"));
        cut.WaitForAssertion(() => Assert.Equal("Fox", cut.Find("h1").TextContent));
        Assert.Empty(cut.FindAll("ul.tabs"));
        Assert.Empty(cut.FindAll("button.request"));
    }

    [Fact]
    public void AFictionalHuntersProfileKeepsItsTabs()
    {
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Hunter05"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("ul.tabs")));
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 3)]
    public void ACompetitionShowsNoPositionAttemptsOrJoinLive(bool live, int stats)
    {
        Live(live);
        var cut = Render<CompetitionView>(p => p.Add(c => c.Id, 180100));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("dl.stats")));
        Assert.Equal(stats, cut.FindAll("dl.stats > div").Count);
        Assert.Equal(live ? 0 : 1, cut.FindAll("button.leave").Count);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void ABoardWithoutARankSaysNothingAboutItLive(bool live, int blocks)
    {
        Services.AddSingleton<ILeaderboardsService>(new ServerLeaderboardsService(
            new HttpClient(new Answer(HttpStatusCode.OK, "{}")) { BaseAddress = new Uri("http://server.test") }, new ServerCookie { Value = "abc" }, NullLogger<ServerLeaderboardsService>.Instance));
        Live(live);
        var cut = Render<Leaderboards>(p => p.Add(l => l.Path, "lanebandit"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("table.board")));
        Assert.Equal(blocks, cut.FindAll("div.mine").Count);
        Assert.Equal(blocks, cut.FindAll("p.norank").Count);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void AFictionalHunterOffersNoFriendRequestLive(bool live, int buttons)
    {
        Live(live);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Hunter05"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("ul.tabs")));
        Assert.Equal(buttons, cut.FindAll("button.request").Count);
    }
}
