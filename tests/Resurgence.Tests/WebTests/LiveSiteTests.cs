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

    /// <summary>Answers the skills, statistics, dogs, ranks, achievements, hunts, score and best paths with theirs and every other with the profile header.</summary>
    sealed class Server(string header, string skills, string statistics = "{}", string dogs = "{}", string ranks = "{}", string achievements = "{}", string hunts = "{}", string hunt = "{}", string score = "{}", string best = "{}") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.AbsolutePath switch { var p when p.EndsWith("/skills") => skills, var p when p.EndsWith("/statistics") => statistics, var p when p.EndsWith("/dogs") => dogs, var p when p.EndsWith("/ranks") => ranks, var p when p.EndsWith("/achievements") => achievements, var p when p.EndsWith("/best") => best, var p when p.EndsWith("/hunts") => hunts, var p when p.Contains("/hunts/") => hunt, var p when p.Contains("/score/") => score, _ => header }, System.Text.Encoding.UTF8, "application/json") });
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
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Fox").Add(h => h.Tab, "skills"));
        cut.WaitForAssertion(() => Assert.Equal("Fox", cut.Find("h1").TextContent));
        Assert.Equal("Hunter", cut.Find("p.sub span").TextContent);
        Assert.Equal("avatar-1000000", cut.Find(".polaroid .asset-slot").GetAttribute("aria-label"));
        Assert.Equal(["/profile/Fox/ranks", "/profile/Fox/achievements", "/profile/Fox/skills", "/profile/Fox/statistics", "/profile/Fox/trophies", "/profile/Fox/dogs", "/profile/Fox/friends"], cut.FindAll("ul.tabs a").Select(a => a.GetAttribute("href")));  // ServerProfileService.Tabs: the tabs with a server source
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
        Assert.Equal(["/profile/Fox/statistics/lifetime", "/profile/Fox/statistics/history", "/profile/Fox/statistics/best"], cut.FindAll("ul.pills a").Select(a => a.GetAttribute("href")));  // the mock's last hunt is hidden
    }

    [Fact]
    public void TheBestPillShowsTheServersPersonalBestsWithoutSeasonsOrSheetLinks()
    {
        const string header = """{"handle":"Fox","hunterscore":5,"rank":3,"membership":"Hunter","avatar":null}""";
        const string best = """{"rows":[{"species":"Fox Deer","score":82.5588,"range":74.77,"weapon":"Fox Rifle","ts":1790887016,"kill":9}]}""";
        Services.AddSingleton<IProfileService>(new ServerProfileService(new HttpClient(new Server(header, "{}", best: best)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance));
        Live(true);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Fox").Add(h => h.Tab, "statistics").Add(h => h.Sub, "best"));
        cut.WaitForAssertion(() => Assert.Contains("Fox Rifle", cut.Markup));
        Assert.Equal(["Fox Deer", "Fox Rifle", "2026-10-01 20:36"], cut.FindAll("table.hist tbody td").Take(3).Select(td => td.TextContent));
        Assert.Empty(cut.FindAll("select#season"));
        Assert.Empty(cut.FindAll("table.hist a"));
    }

    const string Hunter = """{"handle":"Fox","hunterscore":5,"rank":3}""";

    ServerProfileService Real(Server server) => new(new HttpClient(server) { BaseAddress = new Uri("http://server.test") },
        new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance);

    [Fact]
    public void TheHistoryTabListsTheServersHuntsAndLinksEachToItsPage()
    {
        const string hunts = """{"page":1,"pages":1,"total":2,"rows":[{"id":12,"start":1700000100,"seconds":3600,"reserve":"Fox Reserve","lodge":"#101","collectables":0,"kills":2},{"id":11,"start":1699990000,"seconds":0,"reserve":"Fox Reserve","lodge":"","collectables":1,"kills":0}]}""";
        Services.AddSingleton<IProfileService>(Real(new Server(Hunter, "{}", hunts: hunts)));
        Live(true);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Fox").Add(h => h.Tab, "statistics").Add(h => h.Sub, "history"));
        cut.WaitForAssertion(() => Assert.Equal(["/profile/Fox/expedition/12", "/profile/Fox/expedition/11"], cut.FindAll("table.hist a").Select(a => a.GetAttribute("href"))));
        Assert.Equal("2023-11-14 22:15", cut.Find("table.hist a").TextContent);
        Assert.Contains("Fox Reserve", cut.Markup);
    }

    [Fact]
    public void TheHuntPageShowsTheServersHuntAndLinksOnlyTheKillsWithASheet()
    {
        const string hunt = """
            {"id":12,"reserve":"Fox Reserve","lodge":"#101","start":1700000100,"end":1700003700,"weapons":[{"weapon":"Fox Rifle","ammo":"Fox Bullet","shots":1,"accuracy":100.0,"kills":1,"killsPerHit":100.0,"longest":82.0}],
             "animals":[{"name":"Fox Deer","spotted":0,"tracked":0,"harvested":2}],"collectables":[],"achievements":[],"distanceKm":4.0,"seconds":3600,"antlers":[],
             "kills":[{"id":5,"species":"Fox Deer","weapon":"Fox Rifle","ammo":"Fox Bullet","distanceM":82.0,"score":120.5,"weightKg":64.35,"when":1700000500,"sheet":true},
                      {"id":777,"species":"Restored Deer","weapon":"Fox Rifle","ammo":"Fox Bullet","distanceM":60.0,"score":99.0,"weightKg":50.0,"when":1700000600,"sheet":false}]}
            """;
        Services.AddSingleton<IProfileService>(Real(new Server(Hunter, "{}", hunt: hunt)));
        Live(true);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Fox").Add(h => h.Tab, "expedition").Add(h => h.Sub, "12"));
        cut.WaitForAssertion(() => Assert.Contains("Fox Reserve", cut.Markup));
        Assert.Contains("Fox Rifle", cut.Markup);
        Assert.Equal(["/profile/Fox/score/5"], cut.FindAll("table.kills a").Select(a => a.GetAttribute("href")));  // the restored kill has no sheet yet
        Assert.Contains("Restored Deer", cut.Find("table.kills").TextContent);
    }

    [Fact]
    public void TheScoreSheetPageShowsTheServersSheet()
    {
        const string score = """
            {"kind":"antler","species":"Fox Deer","facts":[],"rows":[{"label":"1. Length of Main Beam","a":40.0,"b":40.0,"c":80.0},{"label":"5. Inside Span of Main Beams","a":null,"b":null,"c":45.0}],"abnormal":[],"sums":[],
             "weapon":"Fox Rifle","scope":true,"ammo":"Fox Bullet","distanceM":82.0,"state":"Idle","bodyPart":"Neck","stance":"Prone","platform":"None","weightKg":64.35,"type":"Common (M)","woundTime":4.5,"integrity":97,
             "shotLocation":"Fox Reserve (-9593, 8198)","shots":1,"when":1700000500,"expeditionId":12,"score":120.5,"harvestValue":70.25}
            """;
        Services.AddSingleton<IProfileService>(Real(new Server(Hunter, "{}", score: score)));
        Live(true);
        var cut = Render<HunterProfile>(p => p.Add(h => h.Handle, "Fox").Add(h => h.Tab, "score").Add(h => h.Sub, "5"));
        cut.WaitForAssertion(() => Assert.Contains("1. Length of Main Beam", cut.Markup));
        Assert.Contains("Fox Reserve (-9593, 8198)", cut.Markup);
        Assert.Equal("/profile/Fox/expedition/12", cut.Find("div.info a").GetAttribute("href"));
        Assert.Contains("Fox Rifle", cut.Find("table.shot").TextContent);
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

    const string Ranks = """
        {"cap":1000,"ranks":[{"id":2,"category":"animals","name":"Red Fox","value":1500},{"id":1,"category":"animals","name":"Alpine Ibex","value":7},
                             {"id":41,"category":"weapons","name":"Rifle","value":3},{"id":38,"category":"collectables","name":"Mushrooms","value":0}],
         "milestones":[]}
        """;

    ServerProfileService RealRanks() => new(new HttpClient(new Server("""{"handle":"Fox","hunterscore":5,"rank":3}""", "{}", ranks: Ranks)) { BaseAddress = new Uri("http://server.test") },
        new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance);

    [Fact]
    public void TheRanksTabShowsTheServersCountsByAnimalName()
    {
        Services.AddSingleton<IProfileService>(RealRanks());
        Live(true);
        var cut = Render<RanksTab>(p => p.Add(r => r.Handle, "Fox").Add(r => r.Sub, "animals"));
        cut.WaitForAssertion(() => Assert.Equal(["Alpine Ibex", "Red Fox"], cut.FindAll("div.what h4").Select(h => h.TextContent)));
        Assert.Equal(["7", "1000"], cut.FindAll("div.what .bar b").Select(b => b.TextContent));  // a count counts to the cap
        Assert.Equal(["width:0%", "width:100%"], cut.FindAll("div.what .bar i").Select(i => i.GetAttribute("style")));
    }

    [Fact]
    public void TheRanksOverviewAddsTheRowsUpToTheCap()
    {
        Services.AddSingleton<IProfileService>(RealRanks());
        Live(true);
        var cut = Render<RanksTab>(p => p.Add(r => r.Handle, "Fox"));
        // hunterscore 1000 + 7 + 3 + 0 of 4 rows; animals 1007 of 2000 (a row counts 1000 at most), weapons 3 of 1000; collectables none
        cut.WaitForAssertion(() => Assert.Equal(["1010 / 4000 (25%)", "1007 / 2000 (50%)", "3 / 1000 (0%)"], cut.FindAll("div.ov .bar b").Select(b => b.TextContent.Split(": ")[1])));
    }


    [Fact]
    public void TheAchievementsTabShowsTheServersOverviewOnly()
    {
        const string header = """{"handle":"Fox","hunterscore":5,"rank":3}""";
        const string achievements = """{"categories":[{"name":"Animals","level":1,"done":4,"total":5}],"unlocked":4,"recent":[{"title":"25 Mule Deer","description":"25 Mule Deer harvested.","date":"2023-11-15"}]}""";
        Services.AddSingleton<IProfileService>(new ServerProfileService(new HttpClient(new Server(header, "{}", achievements: achievements)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie(), new MockSessionService(() => null), new MockProfileService(), NullLogger<ServerProfileService>.Instance));
        Live(true);
        var cut = Render<AchievementsTab>(p => p.Add(a => a.Handle, "Fox").Add(a => a.Sub, "challenges"));  // a mock page: the overview shows
        cut.WaitForAssertion(() => Assert.Equal("25 Mule Deer", cut.Find("div.item .text b").TextContent));
        Assert.Contains("2023-11-15", cut.Find("div.item time").TextContent);
        Assert.Contains("4", cut.Find("p.line").TextContent);
        Assert.Single(cut.FindAll("ul.pills a"));
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
