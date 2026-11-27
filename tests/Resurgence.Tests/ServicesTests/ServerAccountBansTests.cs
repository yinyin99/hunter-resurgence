using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

/// <summary>The multiplayer bans of the signed-in player (<see cref="IAccountService.GameBansAsync"/>, <see cref="IAccountService.UnbanAsync"/>) against the community server's <c>/account/bans</c>.</summary>
public class ServerAccountBansTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    static ServerAccountService Make(HttpMessageHandler handler, string? session = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = session, Host = "site.test" }, new MockAccountService(), NullLogger<ServerAccountService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TheBansAreTheAccountsOnTheServerWithTheDayOfEach()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"bans":[{"handle":"Birch","avatar":"https://web.test/media/avatar/256/1000005.png","ts":1700000000},{"handle":"Elm","avatar":null,"ts":1699900000}]}"""));
        Assert.Equal([new GameBan("Birch", new(2023, 11, 14), "avatar-1000005"), new GameBan("Elm", new(2023, 11, 13))], await Make(fake).GameBansAsync());  // no avatar: the default one
        var (request, _) = Assert.Single(fake.Seen);
        Assert.Equal("/account/bans", request.RequestUri!.AbsolutePath);
        Assert.Equal("hr_session=abc", request.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task WithoutASessionOrAnAnswerThereAreNoBans()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"bans":[{"handle":"Birch","avatar":null,"ts":1700000000}]}"""));
        Assert.Empty(await Make(fake, session: null).GameBansAsync());
        Assert.Empty(fake.Seen);  // the server is not asked
        Assert.Empty(await Make(new Fake(_ => Json(HttpStatusCode.Unauthorized, """{"status":"signed_out"}"""))).GameBansAsync());
        Assert.Empty(await Make(new Fake(_ => throw new HttpRequestException("down"))).GameBansAsync());
    }

    [Fact]
    public async Task AnUnbanIsAFormPostFromOurOwnPageAndChangesNothingElse()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        var service = Make(fake);
        var changed = false;
        service.Changed += () => changed = true;  // the bar's avatar and the units are not read again for a ban
        Assert.True((await service.UnbanAsync("Oak")).Ok);
        var (request, body) = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Post, "/account/bans/unban", "handle=Oak"), (request.Method, request.RequestUri!.AbsolutePath, body));
        Assert.True(request.Headers.Contains("X-Requested-With"));
        Assert.False(changed);
        Assert.False((await Make(new Fake(_ => Json(HttpStatusCode.NotFound, """{"status":"invalid"}"""))).UnbanAsync("Oak")).Ok);  // not a player the account banned
    }

    [Fact]
    public async Task TheMockListsTwoBansAndLiftsOne()
    {
        var mock = new MockAccountService();
        Assert.Equal(["Hunter04", "Hunter02"], (await mock.GameBansAsync()).Select(b => b.Handle));
        Assert.True((await mock.UnbanAsync("Hunter04")).Ok);
        Assert.Equal(["Hunter02"], (await mock.GameBansAsync()).Select(b => b.Handle));
        Assert.False((await mock.UnbanAsync("Hunter04")).Ok);
    }
}
