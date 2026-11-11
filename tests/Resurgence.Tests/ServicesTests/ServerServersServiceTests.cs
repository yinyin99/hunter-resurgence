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
        public void OpenFolder(string kind) { }
        public void ClearCache(string kind) { }
        public void OpenExternal(string url) { }
        public Result Next = Result.Success;
        public Result StartGame(string url) { Started.Add(url); return Next; }
        public void ToggleDevTools() { }
        public void RestartLauncher() { }
    }

    // fictional values only
    const string Reserves = """
        {"reserves": [
           {"id": 2, "name": "Test Reserve", "starts": [
              {"id": 101, "kind": "lodge", "name": "Lodge 1", "x": 10, "y": 80}, {"id": 102, "kind": "tower", "name": "Tower 1", "x": 50, "y": 20},
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
        Assert.Equal(["101 Lodge Lodge 1 10,80", "102 Tower Tower 1 50,20", "103 Campsite Campsite 1 60,60", "104 Interest Old Mill 90,20"],
            (await service.MarkersAsync(2)).Select(m => $"{m.Id} {m.Kind} {m.Name} {m.X},{m.Y}"));
        Assert.Empty(await service.MarkersAsync(1));
        Assert.Empty(await service.MarkersAsync(99));
        Assert.Equal([(1, "Test One", 2), (2, "Test Two", 1)], (await service.TutorialsAsync()).Select(t => (t.Id, t.Title, t.Reserve)));
        // one read for all of it, for everyone: the host of the website and no cookie
        var read = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Get, ServerCatalogApi.Reserves, null), (read.Method, read.Path, read.Cookie));
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

    [Fact]
    public async Task ThereIsNoMultiplayer()
    {
        var fake = new Fake(Answer);
        var bridge = new Bridge();
        var service = Make(fake, bridge);
        Assert.Empty(await service.ServersAsync());
        Assert.False((await service.CreateAsync(new("Test game", "", false, null, 4, null, null), 2, 101, 480, 0)).Ok);
        Assert.False((await service.JoinAsync(1, 101, null, 0)).Ok);
        Assert.Empty(fake.Seen);
        Assert.Empty(bridge.Started);
    }
}
