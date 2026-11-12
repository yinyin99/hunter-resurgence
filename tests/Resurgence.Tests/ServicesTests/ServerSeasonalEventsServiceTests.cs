using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerSeasonalEventsServiceTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, string? Cookie, bool Own)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
                      request.Headers.TryGetValues("Cookie", out var c) ? c.Single() : null, request.Headers.Contains("X-Requested-With")));
            return answer(request);
        }
    }

    static ServerSeasonalEventsService Make(HttpMessageHandler handler, string? cookie = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = cookie, Host = "site.test" },
            NullLogger<ServerSeasonalEventsService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TheRunningEventAndTheSettingAreTheServers()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"play":false,"event":{"name":"halloween_2026","define":"HALLOWEEN_2026","reserves":[0,1,9]}}"""));
        var events = await Make(fake).GetAsync();
        Assert.Equal((false, "halloween_2026"), (events.Play, events.Event));
        Assert.Equal([0, 1, 9], events.Reserves);
        var seen = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Get, "/account/events", "hr_session=abc"), (seen.Method, seen.Path, seen.Cookie));
        Assert.False(events.Shows(2));  // the player opted out
    }

    [Fact]
    public async Task WithNoEventNoneShows()
    {
        var events = await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"play":true,"event":null}"""))).GetAsync();
        Assert.Equal((true, null), (events.Play, events.Event));
        Assert.False(events.Shows(2));
    }

    [Theory]
    [InlineData(1, true)]   // the game's reserve 0
    [InlineData(2, true)]
    [InlineData(5, false)]  // the game's reserve 4
    public async Task AnEventShowsOnItsReservesOnlyAndOnNoneListedItShowsOnEvery(int id, bool shows)
    {
        var listed = await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"play":true,"event":{"name":"e","define":"E","reserves":[0,1]}}"""))).GetAsync();
        Assert.Equal(shows, listed.Shows(id));
        var every = await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"play":true,"event":{"name":"e","define":"E","reserves":[]}}"""))).GetAsync();
        Assert.True(every.Shows(id));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"status":"signed_out"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task NoAnswerIsNoEventAndThePlayerPlays(HttpStatusCode code, string body)
    {
        var events = await Make(new Fake(_ => Json(code, body))).GetAsync();
        Assert.Equal((true, null), (events.Play, events.Event));
    }

    [Fact]
    public async Task SignedOutAsksNothing()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        var service = Make(fake, cookie: null);
        Assert.Null((await service.GetAsync()).Event);
        Assert.False((await service.SetAsync(false)).Ok);
        Assert.Empty(fake.Seen);
    }

    [Theory]
    [InlineData(false, "play=0")]
    [InlineData(true, "play=1")]
    public async Task TheSettingIsPostedAsTheFormOfOurOwnPage(bool play, string form)
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"play":true,"event":null}"""));
        Assert.True((await Make(fake).SetAsync(play)).Ok);
        var seen = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Post, "/account/events", form, "hr_session=abc", true), seen);
    }

    [Fact]
    public async Task ARefusedSettingIsAFailureTheDialogShows()
    {
        var result = await Make(new Fake(_ => Json(HttpStatusCode.BadRequest, "{}"))).SetAsync(false);
        Assert.Equal((false, "Events.Failed"), (result.Ok, result.Error));
    }
}
