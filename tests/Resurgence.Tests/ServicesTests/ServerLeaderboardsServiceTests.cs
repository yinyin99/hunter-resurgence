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
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page": 2, "pages": 4, "rows": [{"rank": 31, "handle": "Owl", "hunterscore": 2000, "avatar": "https://web.test/media/avatar/256/1000005.png"}, {"rank": 32, "handle": "Fox", "hunterscore": 1234, "avatar": null}], "around": [{"rank": 160, "handle": "Me", "hunterscore": 50}]}"""));
        var page = await Make(fake).RowsAsync("hunterscore", 0, 2, 0);
        Assert.Equal([new Ranked(31, "Owl", 2000, Avatar: "avatar-1000005"), new Ranked(32, "Fox", 1234)], page.Rows);
        Assert.Equal((2, 4), (page.Number, page.Pages));
        Assert.Equal([new Ranked(160, "Me", 50)], page.Around!);  // the player's own window past the top 100
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
            : $$"""{"page": 2, "pages": 4, "rows": [{{Row}}]}"""));
        var service = Make(fake);
        var expected = new Ranked(1, "Owl", 123.4567, 9, Weapon: "Test Bow", When: DateTimeOffset.FromUnixTimeSeconds(1760000000));

        var boards = await service.BoardsAsync("range", 0);
        var page = await service.RowsAsync("score", 5, 2, 1);

        var board = Assert.Single(boards);
        Assert.Equal((5, "Moose"), (board.Id, board.Name));
        Assert.Equal([expected], board.Top);
        Assert.Equal([expected], page.Rows);
        Assert.Equal((2, 4), (page.Number, page.Pages));
        Assert.Empty(page.Around!);  // no window in the reply: none
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
    public async Task TheSeasonsAndTheHallAreUnavailableWhenTheServerDoesNotAnswer()
    {
        await Assert.ThrowsAsync<BoardUnavailableException>(() => Make(new Down()).SeasonsAsync());
        await Assert.ThrowsAsync<BoardUnavailableException>(() => Make(new Down()).HallAsync(1));
        await Assert.ThrowsAsync<BoardUnavailableException>(() => Make(new Down()).BoardsAsync("score", 0));
    }

    /// <summary>A board the server is slow to answer: the client's timeout cancels the request.</summary>
    sealed class Slow : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new TaskCanceledException("the request timed out");
    }

    [Fact]
    public async Task ATimeoutIsUnavailableNotAnEmptyBoard() =>
        await Assert.ThrowsAsync<BoardUnavailableException>(() => Make(new Slow()).BoardsAsync("score", 0));

    [Theory]
    [InlineData("lanebandit")]
    [InlineData("bustthrough")]
    public async Task AMinigameBoardIsTheServersPage(string kind)
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page": 2, "pages": 4, "rows": [{"rank": 31, "handle": "Fox", "value": 4200}], "around": [{"rank": 120, "handle": "Me", "value": 10}]}"""));
        var page = await Make(fake).RowsAsync(kind, 0, 2, 0);
        Assert.Equal((2, 4), (page.Number, page.Pages));
        Assert.Equal([new Ranked(120, "Me", 10)], page.Around!);
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
    public async Task ServerDownIsUnavailableAndNoRank()
    {
        var service = Make(new Down());
        await Assert.ThrowsAsync<BoardUnavailableException>(() => service.RowsAsync("hunterscore", 0, 1, 0));
        Assert.Null(await service.MyRankAsync("hunterscore", 0, 0));
        Assert.Null(await Make(new Slow()).MyRankAsync("range", 5, 2));
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
    [InlineData("lanebandit", HttpStatusCode.InternalServerError, "{}")]
    [InlineData("score", HttpStatusCode.BadGateway, "{}")]
    public async Task AFailedOrGarbledBoardIsUnavailable(string kind, HttpStatusCode code, string body) =>
        await Assert.ThrowsAsync<BoardUnavailableException>(() => Make(new Fake(_ => Json(code, body))).RowsAsync(kind, 5, 1, 0));

    [Theory]
    [InlineData("hunterscore", HttpStatusCode.OK, "{}")]  // a reply without rows
    [InlineData("bustthrough", HttpStatusCode.OK, "{}")]
    [InlineData("score", HttpStatusCode.NotFound, "{}")]  // an answer: no such board
    public async Task ABoardTheServerAnsweredWithoutRowsIsEmpty(string kind, HttpStatusCode code, string body)
    {
        var page = await Make(new Fake(_ => Json(code, body))).RowsAsync(kind, 5, 1, 0);
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

    [Fact]
    public async Task TheHuntingStatisticsAreTheServersRows()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """
            {"reserves":[{"reserve":"Reserve A","hours":120.5,"gmPerHour":1234.5,"gmPerHarvest":310.25,"harvestsPerHour":5.5,"players":12}],
             "species":[{"species":"Fox","hours":40,"gmPerHour":900,"gmPerHarvest":150,"harvestsPerHour":6,"players":9,"bestReserve":"Reserve A"},
                        {"species":"Deer","hours":10,"gmPerHour":500,"gmPerHarvest":100,"harvestsPerHour":5,"players":6,"bestReserve":null}]}
            """));
        var stats = await Make(fake).HuntingAsync();
        Assert.Equal([new ReserveHunting("Reserve A", 120.5, 1234.5, 310.25, 5.5, 12)], stats.Reserves);
        Assert.Equal([new SpeciesHunting("Fox", 40, 900, 150, 6, 9, "Reserve A"), new SpeciesHunting("Deer", 10, 500, 100, 5, 6, null)], stats.Species);
        var request = Assert.Single(fake.Seen);
        Assert.Equal("/stats/hunting", request.RequestUri!.AbsolutePath);
        Assert.Equal("site.test", request.Headers.Host);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}")]               // a reply without the lists
    [InlineData(HttpStatusCode.Unauthorized, "{}")]     // signed out: an answer, not an outage
    [InlineData(HttpStatusCode.NotFound, "{}")]
    public async Task NoHuntingStatisticsAreAnEmptyOne(HttpStatusCode code, string body)
    {
        var stats = await Make(new Fake(_ => Json(code, body))).HuntingAsync();
        Assert.Equal((0, 0), (stats.Reserves.Count, stats.Species.Count));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task FailedHuntingStatisticsAreUnavailable(HttpStatusCode code, string body) =>
        await Assert.ThrowsAsync<BoardUnavailableException>(() => Make(new Fake(_ => Json(code, body))).HuntingAsync());
}
