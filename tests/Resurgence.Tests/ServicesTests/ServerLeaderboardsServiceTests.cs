using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerLeaderboardsServiceTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    sealed class Down : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("no route");
    }

    static ServerLeaderboardsService Make(HttpMessageHandler handler, string? cookie = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = cookie, Host = "site.test" }, NullLogger<ServerLeaderboardsService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task HunterscoreBoardIsTheServersPage()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page": 2, "pages": 3, "total": 61, "rows": [{"rank": 31, "handle": "Owl", "hunterscore": 2000, "avatar": "https://web.test/media/avatar/256/1000005.png"}, {"rank": 32, "handle": "Fox", "hunterscore": 1234, "avatar": null}]}"""));
        var page = await Make(fake).RowsAsync("hunterscore", 0, 2, 0);
        Assert.Equal([new Ranked(31, "Owl", 2000, Avatar: "avatar-1000005"), new Ranked(32, "Fox", 1234)], page.Rows);
        Assert.Equal((2, 3, 61), (page.Number, page.Pages, page.Total));
        var request = Assert.Single(fake.Seen);
        Assert.Equal("/hunters?page=2", request.RequestUri!.PathAndQuery);
        Assert.Equal("site.test", request.Headers.Host);
    }

    const string Row = """{"rank": 1, "handle": "Owl", "value": 123.4567, "weapon": "not a number", "weaponName": "Test Bow", "ts": 1760000000, "kill": 9, "avatar": null}""";

    [Fact]
    public async Task SeasonsAreTheServersWithTheCurrentOne()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"current": 2, "seasons": [{"no": 1, "start": null, "end": 1700000000}, {"no": 2, "start": 1700000001, "end": null}]}"""));
        var (current, seasons) = await Make(fake).SeasonsAsync();
        Assert.Equal(2, current);
        Assert.Equal([new Season(1, DateTimeOffset.UnixEpoch, DateTimeOffset.FromUnixTimeSeconds(1700000000)), new Season(2, DateTimeOffset.FromUnixTimeSeconds(1700000001), null)], seasons);
        Assert.Equal("/leaderboards/seasons", Assert.Single(fake.Seen).RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ASpeciesBoardAsksItsSeasonAndKeepsTheWeaponAndTheDate()
    {
        var fake = new Fake(r => Json(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("range", StringComparison.Ordinal)
            ? $$"""{"boards": [{"species": 5, "name": "Moose", "top": [{{Row}}]}]}"""
            : $$"""{"page": 2, "pages": 4, "total": 100, "rows": [{{Row}}]}"""));
        var service = Make(fake);
        var expected = new Ranked(1, "Owl", 123.4567, 9, Weapon: "Test Bow", When: DateTimeOffset.FromUnixTimeSeconds(1760000000));

        var boards = await service.BoardsAsync("range", 0);
        var page = await service.RowsAsync("score", 5, 2, 1);

        var board = Assert.Single(boards);
        Assert.Equal((5, "Moose"), (board.Id, board.Name));
        Assert.Equal([expected], board.Top);
        Assert.Equal([expected], page.Rows);
        Assert.Equal((2, 4, 100), (page.Number, page.Pages, page.Total));
        Assert.Equal(["/leaderboards/range?season=0", "/leaderboards/score/5?season=1&page=2"], fake.Seen.Select(r => r.RequestUri!.PathAndQuery));
    }

    [Fact]
    public async Task TheHallOfFameIsTheServersBestPerSpecies()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, $$"""{"rows": [{"id": 5, "species": "Moose", "score": {{Row}}, "range": {{Row}}}]}"""));
        var hall = Assert.Single(await Make(fake).HallAsync(1));
        Assert.Equal(("Moose", "Owl", 123.4567), (hall.Species, hall.Score!.Handle, hall.Score.Value));
        Assert.Equal("/leaderboards/halloffame?season=1", Assert.Single(fake.Seen).RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task MyPlaceOnASpeciesBoardIsTheSessionsAndNoneWhenNotRanked()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Row));
        Assert.Equal(9, (await Make(fake).MyRankAsync("range", 5, 2))!.Sheet);
        var request = Assert.Single(fake.Seen);
        Assert.Equal("/leaderboards/range/5/me?season=2", request.RequestUri!.PathAndQuery);
        Assert.Equal("hr_session=abc", Assert.Single(request.Headers.GetValues("Cookie")));

        Assert.Null(await Make(new Fake(_ => Json(HttpStatusCode.NotFound, "{}"))).MyRankAsync("score", 5, 2));
        var signedOut = new Fake(_ => Json(HttpStatusCode.OK, Row));
        Assert.Null(await Make(signedOut, cookie: null).MyRankAsync("score", 5, 2));
        Assert.Empty(signedOut.Seen);
    }

    // the server has no antler boards: none of them, and none of the mock's
    [Fact]
    public async Task TheAntlerBoardsAreEmptyAndAskNothing()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        var service = Make(fake);
        Assert.Empty((await service.RowsAsync("antlers-single", 1, 1, 0)).Rows);
        Assert.Empty(await service.BoardsAsync("antlers-double", 0));
        Assert.Null(await service.MyRankAsync("antlers-single", 1, 0));
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task TheSeasonsAreNoneWhenTheServerDoesNotAnswer()
    {
        var (current, seasons) = await Make(new Down()).SeasonsAsync();
        Assert.Equal((0, 0), (current, seasons.Count));
        Assert.Empty(await Make(new Down()).HallAsync(1));
    }

    [Theory]
    [InlineData("lanebandit")]
    [InlineData("bustthrough")]
    public async Task AMinigameBoardIsTheServersPage(string kind)
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page": 2, "pages": 3, "total": 61, "rows": [{"rank": 31, "handle": "Fox", "value": 4200}]}"""));
        var page = await Make(fake).RowsAsync(kind, 0, 2, 0);
        Assert.Equal((2, 3, 61), (page.Number, page.Pages, page.Total));
        Assert.Equal(new Ranked(31, "Fox", 4200), Assert.Single(page.Rows));
        Assert.Equal($"/leaderboards/{kind}?page=2", Assert.Single(fake.Seen).RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task MyRankIsTheSessionsPlace()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"userId": 2000000001, "handle": "Fox", "hunterscore": 1234, "rank": 2}"""));
        Assert.Equal(new Ranked(2, "Fox", 1234), await Make(fake).MyRankAsync("hunterscore", 0, 0));
        Assert.Equal("hr_session=abc", Assert.Single(Assert.Single(fake.Seen).Headers.GetValues("Cookie")));

        var signedOut = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.Null(await Make(signedOut, cookie: null).MyRankAsync("hunterscore", 0, 0));
        Assert.Empty(signedOut.Seen);
    }

    [Fact]
    public async Task ServerDownIsAnEmptyBoardAndNoRank()
    {
        var service = Make(new Down());
        var page = await service.RowsAsync("hunterscore", 0, 1, 0);
        Assert.Equal((0, 1, 1), (page.Rows.Count, page.Number, page.Pages));
        Assert.Null(await service.MyRankAsync("hunterscore", 0, 0));
    }

    // PL24: fix 7bfa174
    [Fact]
    public async Task SignedOutVisitorsSeeTheBoardWithoutACookie()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page":1,"pages":1,"total":1,"rows":[{"rank":1,"handle":"Owl","hunterscore":9}]}"""));
        var page = await Make(fake, cookie: null).RowsAsync("hunterscore", 0, 1, 0);
        Assert.Equal([new Ranked(1, "Owl", 9)], page.Rows);
        Assert.False(Assert.Single(fake.Seen).Headers.Contains("Cookie"));
    }

    [Theory]
    [InlineData("hunterscore", HttpStatusCode.InternalServerError, "{}")]
    [InlineData("hunterscore", HttpStatusCode.OK, "<html>")]
    [InlineData("hunterscore", HttpStatusCode.OK, "{}")]  // a reply without rows
    [InlineData("lanebandit", HttpStatusCode.InternalServerError, "{}")]
    [InlineData("bustthrough", HttpStatusCode.OK, "{}")]
    public async Task AFailedOrGarbledBoardIsEmpty(string kind, HttpStatusCode code, string body)
    {
        var page = await Make(new Fake(_ => Json(code, body))).RowsAsync(kind, 0, 1, 0);
        Assert.Equal((0, 1, 1), (page.Rows.Count, page.Number, page.Pages));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.OK, """{"userId":1,"handle":""}""")]
    public async Task NoRankWithoutASessionOrAHandle(HttpStatusCode code, string body) => Assert.Null(await Make(new Fake(_ => Json(code, body))).MyRankAsync("hunterscore", 0, 0));

    [Fact]
    public async Task OtherBoardsAskNothingForTheRankEither()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.Null(await Make(fake).MyRankAsync("lanebandit", 0, 0));  // the server keeps a place on the hunterscore board only
        Assert.Empty(fake.Seen);
    }
}
