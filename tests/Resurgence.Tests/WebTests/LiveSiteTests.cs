using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Components;
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
    public void TheBundlesOwnColumnIsNotShownLive(bool live, int columns)
    {
        Live(live);
        var cut = Render<BundleDetail>(p => p.Add(b => b.Id, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.item")));
        Assert.Equal(columns, cut.FindAll("th.own").Count);
        Assert.Equal(live ? 2 : 4, cut.FindAll("article.item thead th").Count);  // no own limit either: the server sends none
    }

    // the store buys from the server: the buy buttons of the list and the bundle page are there on the live site too
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheBuyButtonsShowLive(bool live)
    {
        Live(live);
        var buy = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Resurgence.Ui.Strings>>()["Store.Buy"].Value;
        var list = Render<Store>();
        list.WaitForAssertion(() => Assert.Contains(list.FindAll(".card button:not([disabled])"), b => b.TextContent.Trim() == buy));
        var bundle = Render<BundleDetail>(p => p.Add(b => b.Id, 1));
        bundle.WaitForAssertion(() => Assert.Single(bundle.FindAll("#buy-bundle")));
    }

    [Fact]
    public void ARealCategoryNamesItsFirstSubCategoryNotItself()
    {
        const string reply = """{"categories": [{"key": "0", "name": "Weapons", "children": [{"key": "0", "name": "Rifles"}, {"key": "1", "name": "Handguns"}]}], "items": [], "bundles": []}""";
        Services.AddSingleton<IStoreService>(new ServerStoreService(new HttpClient(new Answer(HttpStatusCode.OK, reply)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new ServerCache<ServerStore>(), NullLogger<ServerStoreService>.Instance));

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

    /// <summary>Answers the skills, statistics and dogs paths with theirs and every other with the profile header.</summary>
    sealed class Server(string header, string skills, string statistics = "{}", string dogs = "{}") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.AbsolutePath switch { var p when p.EndsWith("/skills") => skills, var p when p.EndsWith("/statistics") => statistics, var p when p.EndsWith("/dogs") => dogs, _ => header }, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public void ARealPlayersProfileIsTheServersHeaderWithTheTabsOfAServerSource()
    {
        const string header = """{"handle":"Fox","hunterscore":5,"rank":3,"membership":"Hunter","avatar":"https://web.test/media/avatar/256/1000000.png"}""";
        const string skills = """{"species":[{"name":"Red Fox","spotting":2.5,"tracking":1.007}],"weapons":[]}""";
        var real = new ServerProfileService(new HttpClient(new Server(header, skills)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance);
        Services.AddSingleton<IProfileService>(real);
        Live(true);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Fox"));
        cut.WaitForAssertion(() => Assert.Equal("Fox", cut.Find("h1").TextContent));
        Assert.Equal("Hunter", cut.Find("p.sub span").TextContent);
        Assert.Equal("avatar-1000000", cut.Find(".polaroid .asset-slot").GetAttribute("aria-label"));
        Assert.Equal(["/profile/Fox/skills", "/profile/Fox/statistics", "/profile/Fox/trophies", "/profile/Fox/dogs", "/profile/Fox/friends"], cut.FindAll("ul.tabs a").Select(a => a.GetAttribute("href")));  // ServerProfileService.Tabs: the tabs with a server source
        cut.WaitForAssertion(() => Assert.Equal("Red Fox", cut.Find("section.card h4").TextContent));
        Assert.Single(cut.FindAll("button.request"));  // the page's session is signed in: the friend request is the server's
    }

    [Fact]
    public void TheStatisticsTabShowsTheServersLifetimeStatisticsOnly()
    {
        const string header = """{"handle":"Fox","hunterscore":5,"rank":3,"membership":"Hunter","avatar":null}""";
        const string statistics = """
            {"weapons":[{"weapon":"Fox Rifle","ammo":"Fox Bullet","shots":4,"accuracy":75.0,"kills":1,"killsPerHit":33.3,"longest":123.0}],
             "animals":[{"name":"Fox Deer","spotted":3,"tracked":2,"harvested":1}],"collectables":[],"distanceKm":5.0,"huntSeconds":7200}
            """;
        Services.AddSingleton<IProfileService>(new ServerProfileService(new HttpClient(new Server(header, "{}", statistics)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance));
        Live(true);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Fox").Add(h => h.Tab, "statistics"));
        cut.WaitForAssertion(() => Assert.Contains("Fox Rifle", cut.Markup));
        Assert.Contains("Fox Deer", cut.Markup);
        Assert.Equal(["/profile/Fox/statistics/lifetime"], cut.FindAll("ul.pills a").Select(a => a.GetAttribute("href")));  // the mock's pills are hidden
    }

    [Fact]
    public void TheDogsTabShowsTheServersDogs()
    {
        const string header = """{"handle":"Fox","hunterscore":5,"rank":3}""";
        const string dogs = """{"dogs":[{"name":"Rex","type":"Test Hound","level":2,"percent":25,"skills":[{"title":"Loyalty One","level":1}],"trophies":["Ribbon"]}]}""";
        Services.AddSingleton<IProfileService>(new ServerProfileService(new HttpClient(new Server(header, "{}", dogs: dogs)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance));
        Live(true);
        var cut = Render<DogsTab>(p => p.Add(d => d.Handle, "Fox"));
        cut.WaitForAssertion(() => Assert.Equal("Rex", cut.Find("ul.dogs li > div:last-child > b").TextContent));
        Assert.Contains("Loyalty One", cut.Find("ul.dogs li em").TextContent);
        Assert.Contains("Ribbon", cut.Markup);
    }

    /// <summary>The profile of <see cref="Inner"/> with only <see cref="Tabs"/> shown.</summary>
    public class TabsOf : System.Reflection.DispatchProxy
    {
        public IProfileService Inner = new MockProfileService();
        public IReadOnlySet<string>? Tabs;

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            return method.Name == nameof(IProfileService.ProfileAsync) ? ((Task<Profile?>)result!).ContinueWith(t => t.Result! with { Tabs = Tabs }) : result;
        }
    }

    // each tab is hidden on its own until it has a server source; an address of a hidden tab opens the first shown one
    [Theory]
    [InlineData(null)]
    [InlineData("ranks")]
    [InlineData("expedition")]
    public void OnlyTheTabsWithASourceAreShown(string? asked)
    {
        var proxy = System.Reflection.DispatchProxy.Create<IProfileService, TabsOf>();
        ((TabsOf)(object)proxy).Tabs = new HashSet<string> { "statistics" };
        Services.AddSingleton(proxy);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Hunter05").Add(h => h.Tab, asked));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("ul.tabs")));
        Assert.Equal(["/profile/Hunter05/statistics"], cut.FindAll("ul.tabs a").Select(a => a.GetAttribute("href")));
        Assert.Single(cut.FindAll("ul.tabs li.on"));
    }

    [Fact]
    public void AFictionalHuntersProfileKeepsItsTabs()
    {
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Hunter05"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("ul.tabs")));
    }

    // the community server has no seasons, species or antler boards: the live site offers the three it has, and another board's address goes to the hunterscore board
    [Theory]
    [InlineData("score", "/leaderboards/hunterscore")]
    [InlineData("halloffame/3", "/leaderboards/hunterscore")]
    [InlineData("antlers/single", "/leaderboards/hunterscore")]
    [InlineData("bustthrough", "/")]
    public void TheLiveBoardsAreTheServersThree(string path, string landed)
    {
        Services.AddSingleton<ILeaderboardsService>(new ServerLeaderboardsService(
            new HttpClient(new Answer(HttpStatusCode.OK, """{"page": 1, "pages": 1, "total": 0, "rows": []}""")) { BaseAddress = new Uri("http://server.test") }, new ServerCookie(), NullLogger<ServerLeaderboardsService>.Instance));
        Live(true);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = Render<Leaderboards>(p => p.Add(l => l.Path, path));

        Assert.Equal(landed, "/" + nav.ToBaseRelativePath(nav.Uri));
        Assert.Equal(["/leaderboards/hunterscore", "/leaderboards/lanebandit", "/leaderboards/bustthrough"], cut.FindAll(".tabs a").Select(a => a.GetAttribute("href")));
    }

    // the live help keeps its support tab: the other tabs are the mock's (or explain a page the live site lacks), and their addresses go to /help
    [Theory]
    [InlineData("guides", null, "/help")]
    [InlineData("tutorials", null, "/help")]
    [InlineData(null, 2, "/help")]
    [InlineData("community", null, "/help")]
    [InlineData("day-mission", null, "/help")]
    [InlineData(null, null, "/")]
    public void TheLiveHelpIsItsSupportTab(string? tab, int? section, string landed)
    {
        Live(true);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = Render<Help>(p => p.Add(h => h.Tab, tab).Add(h => h.Section, section));

        Assert.Equal(landed, "/" + nav.ToBaseRelativePath(nav.Uri));
        Assert.Equal(["/help"], cut.FindAll(".tabs a").Select(a => a.GetAttribute("href")));
        Assert.Empty(cut.FindAll("a.banner"));
    }

    [Fact]
    public void TheMockHelpKeepsItsTabsAndBanner()
    {
        Live(false);

        var cut = Render<Help>();

        Assert.Equal(5, cut.FindAll(".tabs a").Count);
        Assert.Single(cut.FindAll("a.banner"));
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

    // the friend requests are the server's: a signed-in player can ask any other hunter, the live site too
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHunterOffersAFriendRequestLiveToo(bool live)
    {
        Live(live);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Hunter05"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("ul.tabs")));
        Assert.Single(cut.FindAll("button.request"));
    }

    /// <summary>Answers by path: the friends page of a hunter, the signed-in player's friends, and a change of them (kept in <see cref="Posts"/>).</summary>
    sealed class FriendsServer : HttpMessageHandler
    {
        public readonly List<string> Posts = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post) Posts.Add(path + "?" + await request.Content!.ReadAsStringAsync(ct));
            var body = path switch
            {
                "/hunters/Fox/friends" => """{"page":1,"pages":2,"total":21,"rows":[{"handle":"Birch","hunterscore":120},{"handle":"Elm","hunterscore":80}]}""",
                "/hunters/Fox" => """{"handle":"Fox","hunterscore":5,"rank":3}""",
                "/account/friends" => """{"friends":[{"handle":"Birch","online":true},{"handle":"Elm","online":false}],"requests":[{"handle":"Oak","online":false},{"handle":"Ash","online":false}],"pending":[{"handle":"Fox","online":false}]}""",
                _ => """{"status":"ok"}""",
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    ServerFriendsService FriendsOf(FriendsServer server) =>
        new(new HttpClient(server) { BaseAddress = new Uri("http://server.test") }, new ServerCookie { Value = "abc" }, NullLogger<ServerFriendsService>.Instance);

    [Fact]
    public void TheFriendsTabShowsTheServersFriendsWithoutMessageButtons()
    {
        var server = new FriendsServer();
        Services.AddSingleton<IProfileService>(new ServerProfileService(new HttpClient(server) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance));
        Services.AddSingleton<IFriendsService>(FriendsOf(server));
        Live(true);
        var cut = Render<FriendsTab>(p => p.Add(t => t.Handle, "Fox").Add(t => t.Own, true));
        cut.WaitForAssertion(() => Assert.Equal(["Birch", "Elm"], cut.FindAll("ul.list a.handle").Select(a => a.TextContent)));
        Assert.Empty(cut.FindAll("a[href^='/messages']"));  // the messages are the mock's
        Assert.Equal(2, cut.FindAll("button.btn-inverse").Count);  // the remove buttons of the own list
        Assert.Equal("/profile/Fox/friends/2", cut.Find("p.pager a").GetAttribute("href"));  // 21 friends: a second page
    }

    [Fact]
    public void TheStatusBarListsTheServersFriendsAndCountsItsRequests()
    {
        var server = new FriendsServer();
        Services.AddSingleton<IFriendsService>(FriendsOf(server));
        Live(true);
        var cut = Render<StatusBar>(p => p.Add(b => b.Session, new Session("Fox", 5)));
        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("button.friends .count").TextContent));
        cut.Find("button.friends").Click();
        cut.WaitForAssertion(() => Assert.Equal(["Oak", "Birch", "Elm"], cut.FindAll("#friends .handle").Select(e => e.TextContent)));
        Assert.Empty(cut.FindAll("#friends a[href^='/new-message']"));
        // accepting asks the server; declining removes
        cut.Find("#friends .answer .btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Single(server.Posts));
        cut.Find("#friends .answer .btn-inverse").Click();
        cut.WaitForAssertion(() => Assert.Equal(["/account/friends/add?handle=Oak", "/account/friends/remove?handle=Oak"], server.Posts));
    }

    // the community server has no Discord widget: a signed-in player sees it on the launcher and the development website only
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void OnlyTheMockFeedHasTheDiscordWidget(bool live, int widgets)
    {
        Live(live);
        var cut = Render<Feed>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".news")));
        Assert.Equal(widgets, cut.FindAll("section.discord").Count);
    }
}
