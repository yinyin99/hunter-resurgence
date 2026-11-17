using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerOnlineServiceTests
{
    sealed class Fake(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, bool Cookie)> Seen { get; } = [];
        public string? Host { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Host = request.Headers.Host;
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Contains("Cookie")));
            if (code == 0) throw new HttpRequestException("down");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    static ServerOnlineService Make(HttpMessageHandler handler, string? host = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Host = host }, NullLogger<ServerOnlineService>.Instance);

    [Fact]
    public async Task TheCountsAreReadFromThePublicPathWithoutACookie()
    {
        var fake = new Fake(HttpStatusCode.OK, """{"players":37,"games":4}""");
        Assert.Equal(new OnlineCounts(37, 4), await Make(fake).NowAsync());
        Assert.Equal((HttpMethod.Get, "/stats/online", false), Assert.Single(fake.Seen));
    }

    [Fact]
    public async Task TheHostOfTheBrowserIsSentWhenTheCookieHasOne()
    {
        var fake = new Fake(HttpStatusCode.OK, """{"players":1,"games":0}""");
        await Make(fake, "play.example.test").NowAsync();
        Assert.Equal("play.example.test", fake.Host);
        var none = new Fake(HttpStatusCode.OK, """{"players":1,"games":0}""");
        await Make(none).NowAsync();
        Assert.Null(none.Host);
    }

    [Fact]
    public async Task NothingIsCountedWhenTheServerDoesNotAnswerWell()
    {
        Assert.Null(await Make(new Fake(HttpStatusCode.InternalServerError, """{"players":37,"games":4}""")).NowAsync());
        Assert.Null(await Make(new Fake(HttpStatusCode.OK, "not json")).NowAsync());
        Assert.Null(await Make(new Fake(HttpStatusCode.OK, """{"players":-1,"games":4}""")).NowAsync());
        Assert.Null(await Make(new Fake(0, "")).NowAsync());
    }
}
