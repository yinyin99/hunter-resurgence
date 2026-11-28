using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

/// <summary>Steam's Web API as the licence check calls it (IPlayerService/GetOwnedGames): what it was asked, and the answer a test sets.</summary>
sealed class FakeSteamApi : HttpMessageHandler
{
    public const string Owned = """{"response": {"game_count": 1, "games": [{"appid": 12345, "playtime_forever": 0}]}}""";
    public const string NotOwned = """{"response": {"game_count": 0}}""";
    public const string Private = """{"response": {}}""";

    public string Body = Owned;
    public HttpStatusCode Status = HttpStatusCode.OK;
    public bool Down;
    public readonly List<Uri> Asked = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Asked) Asked.Add(request.RequestUri!);
        if (Down) throw new HttpRequestException("down: https://api.steampowered.com/?key=secret-key");
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
    }
}

public class SteamLicenceTests
{
    const string Steam64 = "76561198000000001";
    readonly FakeSteamApi api = new();
    DateTimeOffset clock = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
    readonly SteamLicence licence;

    public SteamLicenceTests() => licence = new SteamLicence(new HttpClient(api), "secret-key", "12345", () => clock);

    [Fact]
    public async Task AnAccountThatHoldsTheGameReadsOwnedAndSteamIsAskedForThatGameOnly()
    {
        Assert.Equal("owned", await licence.OfAsync(1, Steam64));
        var url = Assert.Single(api.Asked);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("api.steampowered.com", url.Host);
        Assert.Equal("/IPlayerService/GetOwnedGames/v1/", url.AbsolutePath);
        Assert.Equal(("secret-key", Steam64, "1", "12345", "json"), (query["key"], query["steamid"], query["include_played_free_games"], query["appids_filter[0]"], query["format"]));
    }

    [Fact]
    public async Task APublicLibraryWithoutTheGameReadsNotOwned()
    {
        api.Body = FakeSteamApi.NotOwned;
        Assert.Equal("not_owned", await licence.OfAsync(1, Steam64));
    }

    [Fact]
    public async Task APrivateProfileSteamBeingDownOrAnyOddAnswerReadsUnknown()
    {
        api.Body = FakeSteamApi.Private;
        Assert.Equal("unknown", await licence.OfAsync(1, Steam64));
        api.Status = HttpStatusCode.Forbidden;  // a wrong key
        Assert.Equal("unknown", await licence.OfAsync(2, Steam64));
        api.Status = HttpStatusCode.OK;
        api.Body = "<html>not json</html>";
        Assert.Equal("unknown", await licence.OfAsync(3, Steam64));
        api.Body = """{"response": {"game_count": 1, "games": [{"appid": 999}]}}""";  // another game than the one asked for
        Assert.Equal("not_owned", await licence.OfAsync(4, Steam64));
        api.Down = true;
        Assert.Equal("unknown", await licence.OfAsync(5, Steam64));
        Assert.Equal("unknown", await licence.OfAsync(6, null));  // an account without a Steam id (the local one): no call
        Assert.Equal(5, api.Asked.Count);
    }

    [Fact]
    public async Task SteamIsAskedOncePerAccountAndDayAndTheAnswerIsKeptWhateverItWas()
    {
        Assert.Equal("owned", await licence.OfAsync(1, Steam64));
        api.Body = FakeSteamApi.NotOwned;
        clock = clock.AddHours(23);
        Assert.Equal("owned", await licence.OfAsync(1, Steam64));  // the cache
        Assert.Single(api.Asked);
        Assert.Equal("not_owned", await licence.OfAsync(2, "76561198000000002"));  // another account is its own call
        Assert.Equal(2, api.Asked.Count);

        clock = clock.AddHours(2);
        Assert.Equal("not_owned", await licence.OfAsync(1, Steam64));  // a day later: asked again
        Assert.Equal(3, api.Asked.Count);

        api.Body = FakeSteamApi.Private;
        Assert.Equal("unknown", await licence.OfAsync(3, "76561198000000003"));
        api.Body = FakeSteamApi.Owned;
        Assert.Equal("unknown", await licence.OfAsync(3, "76561198000000003"));  // unknown is kept for the day too
        Assert.Equal(4, api.Asked.Count);
    }

    [Fact]
    public async Task RequestsThatArriveTogetherMakeOneCall()
    {
        var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => licence.OfAsync(1, Steam64)));
        Assert.All(answers, a => Assert.Equal("owned", a));
        Assert.Single(api.Asked);
    }

    [Fact]
    public void WithoutTheKeyOrTheAppIdThereIsNoCheck()
    {
        var http = new HttpClient(api);
        Assert.Null(SteamLicence.FromCfg(new JsonObject(), http));
        Assert.Null(SteamLicence.FromCfg(new JsonObject { ["steam_web_api_key"] = "k" }, http));
        Assert.Null(SteamLicence.FromCfg(new JsonObject { ["steam_app_id"] = "12345" }, http));
        Assert.NotNull(SteamLicence.FromCfg(new JsonObject { ["steam_web_api_key"] = "k", ["steam_app_id"] = "12345" }, http));
        Assert.Empty(api.Asked);
    }
}
