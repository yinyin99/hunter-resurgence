using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

public class ServerProfileServiceTests
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

    /// <summary>Signed in as "fox": the server spells the handle "Fox".</summary>
    sealed class Fox : ISessionService
    {
        public event Action? Changed { add { } remove { } }
        public Task<Session?> CurrentAsync() => Task.FromResult<Session?>(new Session("fox", 0));
        public string? SignInUrl(string next) => null;
        public Task<Result> RequestCodeAsync(string email) => throw new NotSupportedException();
        public Task<Result> SignInAsync(string email, string code, bool register = false) => throw new NotSupportedException();
        public Task<Result> SetHandleAsync(string handle) => throw new NotSupportedException();
        public Task SignOutAsync() => throw new NotSupportedException();
        public Task<StartOutcome> StartAsync() => throw new NotSupportedException();
        public Task<bool> LicenseAcceptedAsync() => throw new NotSupportedException();
        public Task AcceptLicenseAsync() => throw new NotSupportedException();
    }

    static ServerProfileService Make(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = "abc", Host = "site.test" }, new Fox(),
            new MockProfileService(), NullLogger<ServerProfileService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ARealPlayersHeaderIsTheServers()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"handle": "Fox", "hunterscore": 1234, "rank": 2, "membership": "Hunter", "avatar": "https://web.test/media/avatar/256/1000000.png"}"""));
        var p = await Make(fake).ProfileAsync("fox");
        Assert.Equal(("Fox", 1234, 2, true, "Hunter", "avatar-1000000"), (p!.Handle, p.HunterScore, p.Rank, p.Own, p.Membership, p.Avatar));
        Assert.Same(ServerProfileService.Tabs, p.Tabs);
        Assert.Equal("/hunters/fox", Assert.Single(fake.Seen).RequestUri!.AbsolutePath);
    }

    // the live site never shows the mock: a handle the server does not know is no profile, not a fictional hunter
    [Theory]
    [InlineData("Hunter05")]
    [InlineData("Hunter01")]
    public async Task AnUnknownHandleIsNoProfile(string handle) =>
        Assert.Null(await Make(new Fake(_ => Json(HttpStatusCode.NotFound, """{"error": "not found"}"""))).ProfileAsync(handle));

    [Theory]
    [InlineData("https://web.test/media/avatar/256/1000005.png", "avatar-1000005")]
    [InlineData("https://avatars.test/uploads/a.jpg", "avatar-default")]
    [InlineData(null, "avatar-default")]
    public void TheAvatarSlotIsNamedByThePicturesId(string? url, string slot) => Assert.Equal(slot, ServerProfileService.AvatarSlot(url));

    // PL20: a visitor-typed handle goes into the request path
    [Fact]
    public async Task TheHandleIsEscapedIntoOnePathSegment()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.NotFound, "{}"));
        await Make(fake).ProfileAsync("a/b ?c");
        Assert.EndsWith("/hunters/a%2Fb%20%3Fc", Assert.Single(fake.Seen).RequestUri!.OriginalString);
    }

    // PL21: fix b22a8c9
    [Fact]
    public async Task AnotherPlayersProfileIsNotOwnAndNotOnline()
    {
        var p = await Make(new Fake(_ => Json(HttpStatusCode.OK, """{"handle":"Owl","hunterscore":5,"rank":3}"""))).ProfileAsync("Owl");
        Assert.Equal(("Owl", 5, 3, false, false), (p!.Handle, p.HunterScore, p.Rank, p.Own, p.Online));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedOrGarbledAnswerIsNoProfile(HttpStatusCode code, string body) => Assert.Null(await Make(new Fake(_ => Json(code, body))).ProfileAsync("Owl"));
}
