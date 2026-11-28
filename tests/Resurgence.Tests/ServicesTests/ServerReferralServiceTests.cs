using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerReferralServiceTests
{
    sealed class Fake(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Cookie, string? Host, bool Own, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("Cookie", out var c) ? c.Single() : null, request.Headers.Host, request.Headers.Contains("X-Requested-With"),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    static ServerReferralService Make(HttpMessageHandler handler, ServerCookie? cookie = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, cookie ?? new ServerCookie { Value = "abc", Host = "site.example" }, NullLogger<ServerReferralService>.Instance);

    const string Page = """
        {"code":"K7M2QX9P","link":"https://site.example/join/K7M2QX9P","canEnter":true,"enterUntil":1796000000,"enteredCode":null,"recruiter":null,
         "qualification":{"hours":2.0,"score":10},
         "recruits":[{"handle":"Elk","hours":1.25,"score":4,"qualified":false,"qualifiedAt":null,"discordLinked":true,"paid":false},
                     {"handle":"Bear","hours":3,"score":12,"qualified":true,"qualifiedAt":1795000000,"discordLinked":false,"paid":true}],
         "qualifiedCount":1,
         "tiers":[{"recruits":1,"reward":null,"reached":true},{"recruits":3,"reward":{"gm":0,"em":100,"items":[{"id":384,"name":"Shotgun","count":1}]},"reached":false}],
         "discord":{"linkedCount":1,"milestones":[{"recruits":1,"reward":null,"reached":true}]},
         "recruitReward":{"gm":0,"em":250,"items":[]}}
        """;

    [Fact]
    public async Task TheReferralPageIsTheServersWithTheSessionAndTheHost()
    {
        var fake = new Fake(HttpStatusCode.OK, Page);
        var page = await Make(fake).GetAsync();

        Assert.Equal((HttpMethod.Get, "/account/referral", "hr_session=abc", "site.example", false, null), Assert.Single(fake.Seen));
        Assert.NotNull(page);
        Assert.Equal(("K7M2QX9P", "https://site.example/join/K7M2QX9P", true, (long?)1796000000), (page.Code, page.Link, page.CanEnter, page.EnterUntil));
        Assert.Equal([("Elk", false), ("Bear", true)], page.Recruits.Select(r => (r.Handle, r.Qualified)));
        Assert.Equal(1795000000L, page.Recruits[1].QualifiedAt);
        Assert.Equal((2.0, 10), (page.Qualification.Hours, page.Qualification.Score));
        Assert.Null(page.Tiers[0].Reward);
        Assert.Equal(100, page.Tiers[1].Reward!.Em);
        Assert.Equal("Shotgun", Assert.Single(page.Tiers[1].Reward!.Items!).Name);
        Assert.Equal(1, page.Discord.LinkedCount);
        Assert.True(page.RecruitReward!.Any);
    }

    [Fact]
    public async Task SignedOutOrWithoutAnAnswerThereIsNoPage()
    {
        var none = new Fake(HttpStatusCode.OK, Page);
        Assert.Null(await Make(none, new ServerCookie()).GetAsync());
        Assert.Empty(none.Seen);
        var cookie = new ServerCookie { Value = "old" };
        Assert.Null(await Make(new Fake(HttpStatusCode.Unauthorized, """{"status":"signed_out"}"""), cookie).GetAsync());
        Assert.Null(cookie.Value);
        Assert.Null(await Make(new Fake(HttpStatusCode.OK, "not json")).GetAsync());
    }

    [Fact]
    public async Task ACodeIsPostedAsJsonAndTheRecruiterAndRewardComeBack()
    {
        var fake = new Fake(HttpStatusCode.OK, """{"status":"ok","recruiter":"Elk","reward":{"gm":500,"em":0,"items":[]}}""");
        var r = await Make(fake).EnterAsync("ab-cd 2345");

        Assert.Equal((HttpMethod.Post, "/account/referral/code", "hr_session=abc", true, """{"code":"ab-cd 2345"}"""), (fake.Seen[0].Method, fake.Seen[0].Path, fake.Seen[0].Cookie, fake.Seen[0].Own, fake.Seen[0].Body));
        Assert.True(r.Ok);
        Assert.Equal(("Elk", 500), (r.Value!.Recruiter, r.Value.Reward!.Gm));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("own")]
    [InlineData("loop")]
    [InlineData("already")]
    [InlineData("expired")]
    [InlineData("banned")]
    public async Task EachReasonTheServerRefusesWithIsItsOwnTextKey(string status)
    {
        var r = await Make(new Fake(HttpStatusCode.Conflict, $$"""{"status":"{{status}}"}""")).EnterAsync("ABCD2345");
        Assert.False(r.Ok);
        Assert.Equal("Referral.Error." + status, r.Error);
    }

    [Fact]
    public async Task AnythingElseIsOneFailureText()
    {
        Assert.Equal("Referral.Error.failed", (await Make(new Fake(HttpStatusCode.Conflict, """{"status":"surprise"}""")).EnterAsync("ABCD2345")).Error);
        Assert.Equal("Referral.Error.failed", (await Make(new Fake(HttpStatusCode.InternalServerError, "")).EnterAsync("ABCD2345")).Error);
        var none = new Fake(HttpStatusCode.OK, "{}");
        Assert.Equal("Referral.Error.failed", (await Make(none, new ServerCookie()).EnterAsync("ABCD2345")).Error);
        Assert.Empty(none.Seen);
    }
}
