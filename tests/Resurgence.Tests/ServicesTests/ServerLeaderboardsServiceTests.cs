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
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"page": 2, "pages": 3, "total": 61, "rows": [{"rank": 31, "handle": "Owl", "hunterscore": 2000}, {"rank": 32, "handle": "Fox", "hunterscore": 1234}]}"""));
        var page = await Make(fake).RowsAsync("hunterscore", 0, 2);
        Assert.Equal([new Ranked(31, "Owl", 2000), new Ranked(32, "Fox", 1234)], page.Rows);
        Assert.Equal((2, 3, 61), (page.Number, page.Pages, page.Total));
        var request = Assert.Single(fake.Seen);
        Assert.Equal("/hunters?page=2", request.RequestUri!.PathAndQuery);
        Assert.Equal("site.test", request.Headers.Host);
    }

    [Fact]
    public async Task OtherBoardsAskNothing()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.NotEmpty((await Make(fake).RowsAsync("score", 1, 1)).Rows);
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task MyRankIsTheSessionsPlace()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"userId": 2000000001, "handle": "Fox", "hunterscore": 1234, "rank": 2}"""));
        Assert.Equal(new Ranked(2, "Fox", 1234), await Make(fake).MyRankAsync("hunterscore", 0));
        Assert.Equal("hr_session=abc", Assert.Single(Assert.Single(fake.Seen).Headers.GetValues("Cookie")));

        var signedOut = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.Null(await Make(signedOut, cookie: null).MyRankAsync("hunterscore", 0));
        Assert.Empty(signedOut.Seen);
    }

    [Fact]
    public async Task ServerDownIsAnEmptyBoardAndNoRank()
    {
        var service = Make(new Down());
        var page = await service.RowsAsync("hunterscore", 0, 1);
        Assert.Equal((0, 1, 1), (page.Rows.Count, page.Number, page.Pages));
        Assert.Null(await service.MyRankAsync("hunterscore", 0));
    }
}
