using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerServersServiceTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Cookie, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("Cookie", out var c) ? c.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    sealed class Bridge : ILauncherBridge
    {
        public List<string> Started { get; } = [];
        public bool IsSteam => false;
        public bool WaitingForSteam => false;
        public void StartWithoutSteam() { }
        public event Action? StartStateChanged { add { } remove { } }
        public string? LastStartMode => null;
        public Result OpenFolder(string kind) => Result.Success;
        public Result ShowFile(string path) => Result.Success;
        public GameInstallInfo GameInstall => new(null, null, []);
        public Result ChooseGameFolder() => Result.Success;
        public void UseAutomaticGameFolder() { }
        public Task<IReadOnlyList<InstallCheck>> CheckInstallAsync() => Task.FromResult<IReadOnlyList<InstallCheck>>([]);
        public Result FixDisplay() => Result.Success;
        public Result ClearCache(string kind) => Result.Success;
        public void OpenExternal(string url) { }
        public Result Next = Result.Success;
        public Result StartGame(string url) { Started.Add(url); return Next; }
        public Task<GameExit?> WhenGameExitsAsync() => Task.FromResult<GameExit?>(new(0, 1800, "steam", false, null));
        public Result RunGameRuntimes() => Result.Success;
        public void ToggleDevTools() { }
        public void RestartLauncher() { }
        public GameRun? LastRun => null;
        public Task<ProblemReport> BuildReportAsync(string trigger) => Task.FromResult(new ProblemReport("", [], [], 0, trigger));
        public Task<ReportSent> SendReportAsync(ProblemReport report) => Task.FromResult(new ReportSent(null, null));
        public string SaveReport(ProblemReport report) => "";
        public string ReportConsent { get; set; } = "unset";
    }

    // fictional values only
    const string Reserves = """
        {"reserves": [
           {"id": 2, "name": "Test Reserve", "starts": [
              {"id": 101, "kind": "lodge", "name": "Lodge 1", "x": 10.25, "y": 79.5}, {"id": 102, "kind": "tower", "name": "Tower 1", "x": 50, "y": 20},
              {"id": 103, "kind": "campsite", "name": "Campsite 1", "x": 60, "y": 60}, {"id": 104, "kind": "interest", "name": "Old Mill", "x": 90, "y": 20},
              {"id": 105, "kind": "stash", "name": "Not on the map", "x": 1, "y": 1}]},
           {"id": 1, "name": "First Reserve", "starts": []}],
         "tutorials": [{"id": 1, "name": "Test One", "reserve": 2}, {"id": 2, "name": "Test Two", "reserve": 1}]}
        """;

    const string Launched = """{"url": "game://play.test/session"}""";

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    static HttpResponseMessage Answer(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        ServerCatalogApi.Reserves => Json(HttpStatusCode.OK, Reserves),
        ServerAccountApi.Loadouts => Json(HttpStatusCode.OK, """[{"id": 4, "name": "My hunt", "color": 3, "items": []}, {"id": 9, "name": "Second", "color": 0, "items": []}]"""),
        ServerLaunchApi.Launch => Json(HttpStatusCode.OK, Launched),
        _ => Json(HttpStatusCode.NotFound, "{}"),
    };

    static ServerServersService Make(Fake fake, Bridge bridge, string? session = "secret") =>
        new(new HttpClient(fake) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = session, Host = "site.test" }, new ServerCache<ServerLaunchCatalogue>(), bridge, NullLogger<ServerServersService>.Instance);

    [Fact]
    public async Task TheReservesAndTheirStartsAreTheServers()
    {
        var fake = new Fake(Answer);
        var service = Make(fake, new Bridge());
        Assert.Equal([(2, "Test Reserve"), (1, "First Reserve")], (await service.ReservesAsync()).Select(r => (r.Id, r.Name)));
        // a start of a kind the map does not draw is left out
        Assert.Equal(["101 Lodge Lodge 1 10.25,79.5", "102 Tower Tower 1 50,20", "103 Campsite Campsite 1 60,60", "104 Interest Old Mill 90,20"],
            (await service.MarkersAsync(2)).Select(m => FormattableString.Invariant($"{m.Id} {m.Kind} {m.Name} {m.X},{m.Y}")));
        Assert.Empty(await service.MarkersAsync(1));
        Assert.Empty(await service.MarkersAsync(99));
        Assert.Equal([(1, "Test One", 2), (2, "Test Two", 1)], (await service.TutorialsAsync()).Select(t => (t.Id, t.Title, t.Reserve)));
        // one read of the catalogue for all of it, for everyone: the host of the website and no cookie (the player's placed items are asked apart, with the cookie)
        var read = Assert.Single(fake.Seen, s => s.Path == ServerCatalogApi.Reserves);
        Assert.Equal((HttpMethod.Get, ServerCatalogApi.Reserves, null), (read.Method, read.Path, read.Cookie));
    }

    const string PlacedItems = """
        {"items": [{"id": 501, "reserve": 2, "kind": "tent", "name": "Test Tent", "x": 50, "y": 60.5},
                   {"id": 502, "reserve": 2, "kind": "treestand", "name": "Test Stand", "x": 20, "y": 30},
                   {"id": 503, "reserve": 2, "kind": "caller", "name": "Caller", "x": 70, "y": 80},
                   {"id": 504, "reserve": 2, "kind": "groundblind", "name": "Test Blind", "x": 5, "y": 6},
                   {"id": 507, "reserve": 2, "kind": "tripod", "name": "Test Tripod", "x": 21, "y": 31},
                   {"id": 508, "reserve": 2, "kind": "deployabletower", "name": "Test Tower", "x": 22, "y": 32},
                   {"id": 509, "reserve": 2, "kind": "climbing", "name": "Test Rope", "x": 23, "y": 33},
                   {"id": 505, "reserve": 1, "kind": "tent", "name": "Tent of another reserve", "x": 1, "y": 2},
                   {"id": 506, "reserve": 2, "kind": "decoy", "name": "Not on the map", "x": 1, "y": 1}]}
        """;

    static HttpResponseMessage AnswerWithPlaced(HttpRequestMessage r) => r.RequestUri!.AbsolutePath == ServerAccountApi.Placed ? Json(HttpStatusCode.OK, PlacedItems) : Answer(r);

    [Fact]
    public async Task ThePlayersPlacedItemsOfTheReserveAreMarkersAndTheirIdIsTheNegatedGuid()
    {
        var fake = new Fake(AnswerWithPlaced);
        Assert.Equal(["101 Lodge Lodge 1 10.25,79.5", "102 Tower Tower 1 50,20", "103 Campsite Campsite 1 60,60", "104 Interest Old Mill 90,20",
                      "-501 Tent Test Tent 50,60.5", "-502 TreeStand Test Stand 20,30", "-503 Caller Caller 70,80", "-504 GroundBlind Test Blind 5,6",
                      "-507 Tripod Test Tripod 21,31", "-508 DeployableTower Test Tower 22,32", "-509 Climbing Test Rope 23,33"],
            (await Make(fake, new Bridge()).MarkersAsync(2)).Select(m => FormattableString.Invariant($"{m.Id} {m.Kind} {m.Name} {m.X},{m.Y}")));
        Assert.Equal("hr_session=secret", fake.Seen.Single(s => s.Path == ServerAccountApi.Placed).Cookie);
        Assert.Equal(["-505"], (await Make(new Fake(AnswerWithPlaced), new Bridge()).MarkersAsync(1)).Select(m => m.Id.ToString()));
        // signed out, or a server that does not answer: the starts only, and signed out asks nothing
        var signedOut = new Fake(AnswerWithPlaced);
        Assert.Equal(4, (await Make(signedOut, new Bridge(), session: null).MarkersAsync(2)).Count);
        Assert.DoesNotContain(signedOut.Seen, s => s.Path == ServerAccountApi.Placed);
        Assert.Equal(4, (await Make(new Fake(r => r.RequestUri!.AbsolutePath == ServerAccountApi.Placed ? Json(HttpStatusCode.InternalServerError, "{}") : Answer(r)), new Bridge()).MarkersAsync(2)).Count);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerIsAnEmptyMap()
    {
        var service = Make(new Fake(_ => Json(HttpStatusCode.NotFound, "{}")), new Bridge());
        Assert.Empty(await service.ReservesAsync());
        Assert.Empty(await service.MarkersAsync(2));
        Assert.Empty(await service.TutorialsAsync());
    }

    [Fact]
    public async Task TheLoadoutsAreThePlayersOwn()
    {
        var fake = new Fake(Answer);
        Assert.Equal([(4, "My hunt"), (9, "Second")], (await Make(fake, new Bridge()).LoadoutsAsync()).Select(l => (l.Id, l.Name)));
        Assert.Equal(("hr_session=secret", ServerAccountApi.Loadouts), (fake.Seen.Single().Cookie, fake.Seen.Single().Path));
        Assert.Empty(await Make(new Fake(Answer), new Bridge(), session: null).LoadoutsAsync());  // signed out: none, and no request
        Assert.Empty(await Make(new Fake(_ => Json(HttpStatusCode.Unauthorized, "{}")), new Bridge()).LoadoutsAsync());
    }

    [Fact]
    public async Task AStartAsksTheServerAndHandsTheAddressToTheGame()
    {
        var fake = new Fake(Answer);
        var bridge = new Bridge();
        Assert.True((await Make(fake, bridge).StartSingleAsync(2, 101, 480, 4)).Ok);
        Assert.Equal(["game://play.test/session"], bridge.Started);
        var post = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Post, ServerLaunchApi.Launch, "hr_session=secret", "reserve=2&lodge=101&time=480&loadout=4"), (post.Method, post.Path, post.Cookie, post.Body));
    }

    [Fact]
    public async Task APlacedTentStartsBySendingItsGuidAsTheTent()
    {
        var fake = new Fake(Answer);
        Assert.True((await Make(fake, new Bridge()).StartSingleAsync(2, -501, 480, 4)).Ok);  // the marker id of a placed item is its negated guid
        Assert.Equal("reserve=2&tent=501&time=480&loadout=4", Assert.Single(fake.Seen).Body);
    }

    [Fact]
    public async Task AGameThatDoesNotStartFailsTheStartWithTheBridgesError()
    {
        var bridge = new Bridge { Next = Result.Fail("Launch.NoGame") };
        var result = await Make(new Fake(Answer), bridge).StartSingleAsync(2, 101, 480, 0);
        Assert.Equal((false, "Launch.NoGame"), (result.Ok, result.Error));
    }

    [Fact]
    public async Task ATutorialStartsBySendingItsId()
    {
        var fake = new Fake(Answer);
        var bridge = new Bridge();
        Assert.True((await Make(fake, bridge).StartTutorialAsync(3)).Ok);
        Assert.Equal("tutorial=3", Assert.Single(fake.Seen).Body);
        Assert.Single(bridge.Started);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ARefusedStartFailsAndTheGameIsNotStarted(HttpStatusCode code)
    {
        var bridge = new Bridge();
        var result = await Make(new Fake(_ => Json(code, """{"error": "no"}""")), bridge).StartSingleAsync(2, 101, 480, 0);
        Assert.False(result.Ok);
        Assert.Equal("Launch.Failed", result.Error);
        Assert.Empty(bridge.Started);
    }

    [Fact]
    public async Task SignedOutOrWithoutAnAddressNothingStarts()
    {
        var fake = new Fake(Answer);
        var bridge = new Bridge();
        Assert.False((await Make(fake, bridge, session: null).StartSingleAsync(2, 101, 480, 0)).Ok);
        Assert.False((await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"url": ""}""")), bridge).StartSingleAsync(2, 101, 480, 0)).Ok);
        Assert.Empty(fake.Seen);
        Assert.Empty(bridge.Started);
    }

    // the shape of the server's games list; ids are 10 digits, the host is the game's handle, a game without a Hunter Score range has null for it, and no game of a community server has a country.
    // The third game was hosted with limits outside the game's own range: they are kept within 0 to 99999 and do not break the list
    const string Games = """
        [{"id": 4000000001, "name": "Test game", "handle": "HostOne", "host": 7, "description": "Come and hunt.", "password": false, "public": true, "singleplayer": false, "freeplayers": true,
          "hs_min": 100, "hs_max": 2000, "max_players": 4, "start_ts": 1700000000, "clients_len": 2,
          "clients": [{"user_id": 7, "handle": "HostOne", "guid": "1", "color": 1, "avatar": "https://site.test/images/avatars/12.png", "x": 1.5, "y": 2, "z": 3, "hunterscore": 1500},
                      {"user_id": 8, "handle": "Guest", "guid": "2", "color": 2, "avatar": null, "x": 4, "y": 5, "z": 6, "hunterscore": 300}],
          "reserve": {"id": 2, "name": "Test Reserve"}},
         {"id": 4000000002, "name": "Locked game", "handle": "HostTwo", "description": null, "password": true, "hs_min": null, "hs_max": null, "max_players": 8, "start_ts": 4102444800,
          "clients": [{"user_id": 9, "handle": "HostTwo", "guid": "3", "hunterscore": 900}], "reserve": {"id": 4}},
         {"id": 4000000003, "name": "Wide game", "handle": "HostThree", "password": false, "hs_min": -5, "hs_max": 99999999999, "max_players": 8, "start_ts": 1700000000,
          "clients": [{"user_id": 10, "handle": "HostThree", "guid": "4", "hunterscore": 99999999999}], "reserve": {"id": 4}}]
        """;

    [Fact]
    public async Task TheGamesAreTheCatalogsListWithTheCookieAndAFutureStartIsAgeZero()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath == ServerCatalogApi.Games ? Json(HttpStatusCode.OK, Games) : Answer(r));
        var games = await Make(fake, new Bridge()).ServersAsync();  // the list is for signed-in players: the cookie is sent

        Assert.Equal([(HttpMethod.Get, ServerCatalogApi.Games, (string?)"hr_session=secret")], fake.Seen.Select(s => (s.Method, s.Path, s.Cookie)));
        Assert.Equal([(4000000001L, "Test game", "HostOne", 2, 2, 4, false, 100, 2000), (4000000002L, "Locked game", "HostTwo", 4, 1, 8, true, 0, 99999), (4000000003L, "Wide game", "HostThree", 4, 1, 8, false, 0, 99999)],
            games.Select(g => (g.Id, g.Name, g.Host, g.Reserve, g.Players, g.Max, g.Password, g.MinScore, g.MaxScore)));
        Assert.Equal(["Come and hunt.", "", ""], games.Select(g => g.Description));
        Assert.Equal([("HostOne", 1500, "avatar-12"), ("Guest", 300, Avatars.Default)], games[0].Roster.Select(p => (p.Handle, p.HunterScore, p.Avatar)));
        Assert.Equal(int.MaxValue, games[2].Roster.Single().HunterScore);
        Assert.Equal([true, false, false], games.Select(g => g.Limited));
        Assert.All(games, g => Assert.Equal("", g.Country));
        Assert.True(games[0].Seconds > 0);
        Assert.Equal(0, games[1].Seconds);
    }

    // signed out there are no games, and the server is not asked
    [Fact]
    public async Task SignedOutHasNoGamesAndAsksNothing()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Games));
        Assert.Empty(await Make(fake, new Bridge(), session: null).ServersAsync());
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerHasNoGames()
    {
        Assert.Empty(await Make(new Fake(_ => Json(HttpStatusCode.InternalServerError, "{}")), new Bridge()).ServersAsync());
        Assert.Empty(await Make(new Fake(_ => Json(HttpStatusCode.OK, "not json")), new Bridge()).ServersAsync());
    }

    [Fact]
    public async Task HostingAsksTheServerForAGameWithEveryFieldOfTheForm()
    {
        var fake = new Fake(Answer);
        var bridge = new Bridge();
        Assert.True((await Make(fake, bridge).CreateAsync(new("Test game", "Come & hunt", true, "letmein", 4, 100, 2000), 2, 101, 480, 4)).Ok);
        Assert.Equal(["game://play.test/session"], bridge.Started);
        var post = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Post, ServerLaunchApi.Launch, "hr_session=secret",
            "mp=host&reserve=2&lodge=101&time=480&loadout=4&name=Test+game&description=Come+%26+hunt&public_game=0&free_players=1&max_players=4&hs_min=100&hs_max=2000&password=letmein"),
            (post.Method, post.Path, post.Cookie, post.Body));
    }

    [Fact]
    public async Task HostingWithoutPasswordOrLimitSendsNeitherAndAtATentSendsItsGuid()
    {
        var fake = new Fake(Answer);
        Assert.True((await Make(fake, new Bridge()).CreateAsync(new("Test game", "", false, null, 8, null, null), 2, -501, 480, 0)).Ok);
        Assert.Equal("mp=host&reserve=2&tent=501&time=480&loadout=0&name=Test+game&description=&public_game=1&free_players=1&max_players=8", Assert.Single(fake.Seen).Body);
    }

    [Fact]
    public async Task JoiningSendsTheNetworkIdTheStartAndThePasswordButNeitherReserveNorTime()
    {
        var fake = new Fake(Answer);
        var bridge = new Bridge();
        var service = Make(fake, bridge);
        Assert.True((await service.JoinAsync(4000000002, 101, "letmein", 4)).Ok);
        Assert.True((await service.JoinAsync(4000000001, -501, null, 0)).Ok);
        Assert.Equal(["mp=join&network_id=4000000002&lodge=101&loadout=4&password=letmein", "mp=join&network_id=4000000001&tent=501&loadout=0"], fake.Seen.Select(s => s.Body));
        Assert.Equal(2, bridge.Started.Count);
    }

    [Theory]
    [InlineData("password", "Join.WrongPassword")]
    [InlineData("full", "Join.Full")]
    [InlineData("not_allowed", "Join.NotAllowed")]
    [InlineData("host_not_ready", "Join.HostNotReady")]
    [InlineData("gone", "Launch.Failed")]
    [InlineData("something else", "Launch.Failed")]
    public async Task ARefusedJoinFailsWithTheMessageOfItsReasonAndTheGameIsNotStarted(string why, string key)
    {
        var bridge = new Bridge();
        var result = await Make(new Fake(_ => Json(HttpStatusCode.BadRequest, $$"""{"error": "{{why}}"}""")), bridge).JoinAsync(4000000001, 101, null, 0);
        Assert.Equal((false, key), (result.Ok, result.Error));
        Assert.Empty(bridge.Started);
    }

    [Fact]
    public async Task SignedOutNothingIsHostedOrJoined()
    {
        var fake = new Fake(Answer);
        var bridge = new Bridge();
        var service = Make(fake, bridge, session: null);
        Assert.False((await service.CreateAsync(new("Test game", "", false, null, 4, null, null), 2, 101, 480, 0)).Ok);
        Assert.False((await service.JoinAsync(1, 101, null, 0)).Ok);
        Assert.Empty(fake.Seen);
        Assert.Empty(bridge.Started);
    }
}
