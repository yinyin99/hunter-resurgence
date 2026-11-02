using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerSessionServiceTests
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

    static ServerSessionService Make(HttpMessageHandler handler, string? cookie = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = cookie, Host = "site.test" }, NullLogger<ServerSessionService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task MeBecomesTheSessionAndCarriesTheCookieAndHost()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"userId": 2000000001, "handle": "Fox", "hunterscore": 1234, "rank": 2}"""));
        var session = await Make(fake).CurrentAsync();
        Assert.Equal(new Session("Fox", 1234), session);
        var request = Assert.Single(fake.Seen);
        Assert.Equal("/auth/me", request.RequestUri!.AbsolutePath);
        Assert.Equal("hr_session=abc", Assert.Single(request.Headers.GetValues("Cookie")));
        Assert.Equal("site.test", request.Headers.Host);
    }

    [Fact]
    public async Task SignedOutAnswerIsNoSession() =>
        Assert.Null(await Make(new Fake(_ => Json(HttpStatusCode.Unauthorized, """{"error":"signed out"}"""))).CurrentAsync());

    [Fact]
    public async Task NetworkFailureIsNoSession() => Assert.Null(await Make(new Down()).CurrentAsync());

    [Fact]
    public async Task NoCookieAsksNothing()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"userId": 1, "handle": "Fox"}"""));
        Assert.Null(await Make(fake, cookie: null).CurrentAsync());
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task SignOutPostsTheCookieThenThereIsNoSession()
    {
        var fake = new Fake(r => r.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.OK, """{"userId": 2000000001, "handle": "Fox"}"""));
        var service = Make(fake);
        var changed = 0;
        service.Changed += () => changed++;
        await service.SignOutAsync();
        var post = Assert.Single(fake.Seen);
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("/auth/signout", post.RequestUri!.AbsolutePath);
        Assert.Equal("hr_session=abc", Assert.Single(post.Headers.GetValues("Cookie")));
        Assert.Equal("XMLHttpRequest", Assert.Single(post.Headers.GetValues("X-Requested-With")));
        Assert.Equal(1, changed);
        Assert.Null(await service.CurrentAsync());
        Assert.Single(fake.Seen); // no second request: the cookie is dropped
    }

    [Fact]
    public async Task SignOutWhileTheServerIsDownStillSignsOutHere()
    {
        var service = Make(new Down());
        await service.SignOutAsync();
        Assert.Null(await service.CurrentAsync());
    }

    [Fact]
    public void SignInIsALinkBackToThePage() =>
        Assert.Equal("/auth/steam/login?next=%2Ffeed%3Fa%3D1", Make(new Down()).SignInUrl("/feed?a=1"));
}
