using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerMissionsServiceTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, bool Ajax)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Headers.Contains("X-Requested-With")));
            return answer(request);
        }
    }

    static ServerMissionsService Make(HttpMessageHandler handler, string? cookie = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = cookie, Host = "site.test" }, NullLogger<ServerMissionsService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // made-up missions: 1 active, 2 available, 3 locked, 4 completed; the pack holds 3 and 4, the other one 1 and 2
    const string Page = """
        {"missions": [
          {"id": 1, "title": "Test One", "desc": "First.", "state": "active", "objectives": ["Harvest a Test Deer"], "reward": "100 gm$", "reserve": "Test Reserve", "species": "Test Deer", "weapon": ""},
          {"id": 2, "title": "Test Two", "desc": "Second.", "state": "available", "objectives": ["Spot a Test Deer", "Then, harvest it"], "reward": "200 gm$", "reserve": "", "species": "", "weapon": "Test Rifle"},
          {"id": 3, "title": "Test Three", "desc": "", "state": "locked", "objectives": [], "reward": "", "reserve": "", "species": "", "weapon": ""},
          {"id": 4, "title": "Test Four", "desc": "", "state": "completed", "objectives": [], "reward": "", "reserve": "", "species": "", "weapon": ""}],
         "groups": [{"id": 10, "title": "Test Pack", "desc": "Both.", "favourite": true, "missions": [3, 4]}, {"id": 11, "title": "Other Pack", "desc": "", "favourite": false, "missions": [1, 2, 99]}],
         "completed": 1, "auto_mission_activate": true, "activate_only_favourite": false, "hide_completed_favourite": true}
        """;

    [Fact]
    public async Task TheMissionsAndTheirPacksAreTheServers()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Page));
        var data = await Make(fake).RegularAsync();
        Assert.Equal(("GET", "/account/missions"), (fake.Seen[0].Method.Method, fake.Seen[0].Path));
        Assert.Equal(["Test One", "Test Two"], data.Missions.Select(m => m.Name));  // the active and the available
        Assert.Equal(("Test Reserve", "Test Deer", "", "active"), (data.Missions[0].Reserve, data.Missions[0].Species, data.Missions[0].Weapon, data.Missions[0].State));
        Assert.Equal(["Spot a Test Deer", "Then, harvest it"], data.Missions[1].Objectives);
        Assert.Equal([("Test Pack", 1, 2, true), ("Other Pack", 0, 2, false)], data.Packs.Select(p => (p.Name, p.Done, p.Total, p.Favourite)));  // 99 has no definition
        Assert.Equal(["Test Three", "Test Four"], data.Packs[0].Missions.Select(m => m.Name));
        Assert.Equal((1, 4, 0, 2, true, false, true), (data.Completed, data.Total, data.PacksDone, data.PacksTotal, data.Auto, data.OnlyFavourite, data.HideCompletedFavourite));
        Assert.Equal(["Test One"], (await Make(fake).ActiveAsync()).Select(m => m.Name));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"status":"signed_out"}""")]
    [InlineData(HttpStatusCode.NotFound, """{"status":"invalid"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task NoAnswerIsAnEmptyPageNotTheMocks(HttpStatusCode code, string body)
    {
        var data = await Make(new Fake(_ => Json(code, body))).RegularAsync();
        Assert.Empty(data.Missions);
        Assert.Empty(data.Packs);
        Assert.Empty(await Make(new Fake(_ => Json(code, body))).ActiveAsync());
    }

    [Fact]
    public async Task NoCookieAsksNothingAndFailsTheWrites()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Page));
        var missions = Make(fake, cookie: null);
        Assert.Empty((await missions.RegularAsync()).Packs);
        Assert.False((await missions.ActivateAsync(2)).Ok);
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task ActivatingSendsTheMissionOrAllOfThemFromOurPage()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        var missions = Make(fake);
        Assert.True((await missions.ActivateAsync(2)).Ok);
        Assert.True((await missions.ActivateAsync(0)).Ok);
        Assert.Equal([("POST", "/account/missions/activate", "mission_id=2", true), ("POST", "/account/missions/activate_all_available", "", true)],
            fake.Seen.Select(s => (s.Method.Method, s.Path, s.Body, s.Ajax)));
        Assert.True((await missions.ActivateAsync(-1)).Ok);  // the daily streak
        Assert.Equal(("POST", "/account/missions/daily/activate", "", true), (fake.Seen[2].Method.Method, fake.Seen[2].Path, fake.Seen[2].Body, fake.Seen[2].Ajax));
    }

    // made-up: day 2 is today, frozen, and holds the only mission; the other days have none
    const string Daily = """
        {"today": 2, "ends_in": 4000, "frozen": true,
         "days": [{"number": 1, "stars": 1, "gms": 100}, {"number": 2, "stars": 1, "gms": 100}, {"number": 3, "stars": 2, "gms": 500}],
         "missions": [{"title": "", "desc": "", "objectives": [], "reserve": "", "rewards": [], "done": false},
                      {"title": "Test Daily", "desc": "Do it.", "objectives": ["Spot a Test Deer"], "reserve": "Test Reserve", "rewards": ["100 gm$"], "done": true},
                      {"title": "", "desc": "", "objectives": [], "reserve": "", "rewards": [], "done": false}]}
        """;

    [Fact]
    public async Task TheDailyCalendarIsTheServersAndOnlyTodayIsFrozen()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Daily));
        var d = await Make(fake).DailyAsync();
        Assert.Equal(("GET", "/account/missions/daily"), (fake.Seen[0].Method.Method, fake.Seen[0].Path));
        Assert.Equal((2, 4000, null), (d.Today, d.EndsIn, d.Resume));  // the server has no item to resume a streak
        Assert.Equal([(1, 1, 100), (2, 1, 100), (3, 2, 500)], d.Days.Select(x => (x.Number, x.Stars, x.Gms)));
        Assert.Equal([("", false, false), ("Test Daily", true, true), ("", false, false)], d.Missions.Select(m => (m.Title, m.Frozen, m.Done)));
        Assert.Equal(("Do it.", "Test Reserve", "Spot a Test Deer", "100 gm$"), (d.Missions[1].Description, d.Missions[1].Reserve, d.Missions[1].Objectives.Single(), d.Missions[1].Rewards.Single()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"status":"signed_out"}""")]
    [InlineData(HttpStatusCode.NotFound, """{"status":"invalid"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task NoDailyAnswerIsAnEmptyCalendarThatAsksAgainInAMinuteAndResumingFails(HttpStatusCode code, string body)
    {
        var missions = Make(new Fake(_ => Json(code, body)));
        var d = await missions.DailyAsync();
        Assert.Equal((0, 0, 60), (d.Days.Count, d.Missions.Count, d.EndsIn));
        Assert.False((await missions.ResumeStreakAsync()).Ok);
    }

    [Fact]
    public async Task ARefusedActivationFailsWithTheGamesMessage()
    {
        var result = await Make(new Fake(_ => Json(HttpStatusCode.Conflict, """{"status":"refused"}"""))).ActivateAsync(3);
        Assert.Equal((false, "Mis.Failed"), (result.Ok, result.Error));
    }

    [Fact]
    public async Task TheSwitchesAndFavouritesAreStoredAndOneAutomaticSettingTurnsTheOtherOff()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        var missions = Make(fake);
        await missions.SetFavouriteAsync(10, true);
        await missions.SetHideCompletedFavouriteAsync(false);
        await missions.SetAutomaticAsync("onlyfavourite", true);
        await missions.SetAutomaticAsync("auto", false);
        Assert.Equal([("update_favourite", "group_id=10&is_favourite=true"), ("update_hide_completed_favourite", "hide_completed_favourite=false"),
                      ("update_activate_only_favourite", "activate_only_favourite=true"), ("update_automatic_activation", "auto_mission_activate=false"),  // the first turned the other off
                      ("update_automatic_activation", "auto_mission_activate=false")],
            fake.Seen.Select(s => (s.Path["/account/missions/".Length..], s.Body)));
    }
}
