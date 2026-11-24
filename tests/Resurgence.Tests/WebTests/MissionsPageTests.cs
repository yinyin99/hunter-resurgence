using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class MissionsPageTests : UiContext
{
    [Fact]
    public void ClickingAPackExpandsIt()
    {
        var cut = Render<Missions>();
        var pack = cut.Find("button.row.pack");
        Assert.Equal("false", pack.GetAttribute("aria-expanded"));

        pack.Click();

        Assert.Equal("true", cut.Find("button.row.pack").GetAttribute("aria-expanded"));
        Assert.NotEmpty(cut.FindAll("li[data-pack] .more ul.rows li"));
    }

    [Fact]
    public void ADeepLinkOpensItsPackAndScrollsToIt()
    {
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("scrollToPack", 3);

        var cut = Render<Missions>(p => p.Add(m => m.Group, 3));

        cut.WaitForAssertion(() => shell.VerifyInvoke("scrollToPack"));
        Assert.Equal("true", cut.Find("li[data-pack='3'] button.row.pack").GetAttribute("aria-expanded"));
        Assert.Equal("false", cut.Find("li[data-pack='1'] button.row.pack").GetAttribute("aria-expanded"));
    }

    sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    // made-up: the active mission names a reserve and a species and no weapon, the available one a weapon only
    const string Server = """
        {"missions": [
          {"id": 1, "title": "Test One", "desc": "", "state": "active", "objectives": [], "reward": "", "reserve": "Test Reserve", "species": "Test Deer", "weapon": ""},
          {"id": 2, "title": "Test Two", "desc": "", "state": "available", "objectives": [], "reward": "", "reserve": "", "species": "", "weapon": "Test Rifle"},
          {"id": 3, "title": "Test Three", "desc": "", "state": "locked", "objectives": [], "reward": "", "reserve": "", "species": "", "weapon": "", "needs": ["Test Two"]},
          {"id": 4, "title": "Test Four", "desc": "", "state": "completed", "objectives": [], "reward": "", "reserve": "", "species": "", "weapon": ""}],
         "groups": [{"id": 10, "title": "Test Pack", "desc": "", "favourite": false, "missions": [1, 2, 3, 4]}],
         "completed": 0, "auto_mission_activate": false, "activate_only_favourite": false, "hide_completed_favourite": false}
        """;

    [Fact]
    public void ALockedRowShowsTheClosedPadlockAnAvailableOneTheOpenPadlockAndAnActiveOneAnEmptyPawAndACompletedOneASolidPaw()
    {
        Services.AddSingleton(new HostMode { Live = true });
        Services.AddSingleton<IMissionsService>(new ServerMissionsService(new HttpClient(new Answer(Server)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie { Value = "abc" }, NullLogger<ServerMissionsService>.Instance));

        var cut = Render<Missions>();
        cut.Find("button.row.pack").Click();

        var icons = cut.FindAll("li[data-pack] .more ul.rows button.row").Select(b => b.QuerySelector("use")!.GetAttribute("href")!.Split('#')[1]);
        Assert.Equal(["paw-regular", "unlock", "lock", "paw"], icons);
    }

    [Fact]
    public void TheLiveSiteHasTheDailyTabAndHelpAndNoFilterOptionForNone()
    {
        Services.AddSingleton(new HostMode { Live = true });
        Services.AddSingleton<IMissionsService>(new ServerMissionsService(new HttpClient(new Answer(Server)) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie { Value = "abc" }, NullLogger<ServerMissionsService>.Instance));

        var cut = Render<Missions>();

        Assert.NotEmpty(cut.FindAll("a[href='/missions/daily']"));
        Assert.Equal("/help/day-mission", cut.Find("a.help").GetAttribute("href"));
        // a locked mission says which mission to finish first and has no activation
        cut.Find("button.row.pack").Click();
        cut.FindAll("li[data-pack] .more ul.rows button.row")[2].Click();
        Assert.Equal(["Test Two"], cut.FindAll("li.open ul.rows li.open .more ul li").Select(l => l.TextContent));
        Assert.Empty(cut.FindAll("li.open ul.rows li.open .more button.btn"));
        // the select-all entry and the one name each of the three filters has
        Assert.Equal([2, 2, 2], cut.FindAll("ul.pop").Select(u => u.QuerySelectorAll("li").Length));
    }
}

public sealed class DailyMissionsPageTests : UiContext
{
    sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    // made-up: day 1 is today and frozen with the only mission; the other days have none
    static string Server(bool broken, int gender = 1) => """
        {"today": 1, "ends_in": 4000, "frozen": true, "broken": BROKEN,
         "days": [{"number": 1, "stars": 1, "gms": 100}, {"number": 2, "stars": 1, "gms": 100}],
         "missions": [{"title": "Test Daily", "desc": "Do it.", "objectives": ["Spot a Test Deer"], "reserve": "Test Reserve", "rewards": ["100 gm$"], "species": "TEST_DEER", "gender": GENDER, "giver": "test_giver", "done": false},
                      {"title": "", "desc": "", "objectives": [], "reserve": "", "rewards": [], "done": false}]}
        """.Replace("BROKEN", broken ? "true" : "false").Replace("GENDER", $"{gender}");

    IRenderedComponent<DailyMissions> Open(bool live, bool broken, int gender = 1)
    {
        Services.AddSingleton(new HostMode { Live = live });
        Services.AddSingleton<IMissionsService>(new ServerMissionsService(new HttpClient(new Answer(Server(broken, gender))) { BaseAddress = new Uri("http://server.test") },
            new ServerCookie { Value = "abc" }, NullLogger<ServerMissionsService>.Instance));
        return Render<DailyMissions>();
    }

    [Fact]
    public void TheLiveSiteShowsTheServersMissionWithItsSpeciesCardAndANewStreakIsNotBroken()
    {
        var cut = Open(live: true, broken: false);

        Assert.Equal("Test Daily", cut.Find(".detail h2").TextContent);
        Assert.Single(cut.FindAll("[aria-label='mission-giver-test-giver']"));
        Assert.Single(cut.FindAll("[aria-label='mission-species-test-deer-f']"));  // the server's gender 1: the female picture
        Assert.Empty(cut.FindAll("p.broken").Where(p => p.TextContent.StartsWith("Your streak is broken")));
        Assert.NotEmpty(cut.FindAll("#activate-dm"));
        Assert.Empty(cut.FindAll("#buy-resume-streak, #bottom-block-resume-streak"));  // the server has no resume item
    }

    [Fact]
    public void ABrokenStreakSaysSo()
    {
        var cut = Open(live: true, broken: true);

        Assert.Equal(2, cut.FindAll("p.broken").Count);  // the broken note and the notice
        Assert.Equal(2, cut.FindAll(".calendar button.card").Count);
    }

    [Fact]
    public void AMaleMissionShowsTheMalePictureAndOnlyTodaysCardCanBeOpened()
    {
        var cut = Open(live: true, broken: false, gender: 0);

        Assert.Single(cut.FindAll("[aria-label='mission-species-test-deer-m']"));
        Assert.Empty(cut.FindAll("[aria-label='mission-species-test-deer-f']"));
        var cards = cut.FindAll(".calendar button.card");
        Assert.Equal([false, true], cards.Select(c => c.HasAttribute("disabled")));  // today is day 1
        Assert.Equal(["true", null], cards.Select(c => c.GetAttribute("aria-pressed")));
        Assert.Equal([null, "true"], cards.Select(c => c.GetAttribute("aria-disabled")));
        Assert.Equal("Test Daily", cut.Find(".detail h2").TextContent);
    }
}
