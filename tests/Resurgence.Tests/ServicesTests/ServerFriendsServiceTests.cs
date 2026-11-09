using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerFriendsServiceTests
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

    static ServerFriendsService Make(HttpMessageHandler handler, string? session = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = session, Host = "site.test" }, NullLogger<ServerFriendsService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    const string Own = """{"friends":[{"handle":"Birch","online":true},{"handle":"Elm","online":false}],"requests":[{"handle":"Oak","online":false}],"pending":[{"handle":"Ash","online":false}]}""";

    [Fact]
    public async Task TheListsAreTheAccountsOnTheServer()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Own));
        var service = Make(fake);
        Assert.Equal([new Friend("Birch", true), new Friend("Elm", false)], await service.FriendsAsync());
        Assert.Equal(["Oak"], await service.RequestsAsync());
        Assert.Equal(["Ash"], await service.PendingAsync());
        var (request, _) = fake.Seen[0];
        Assert.Equal("/account/friends", request.RequestUri!.AbsolutePath);
        Assert.Equal("hr_session=abc", request.Headers.GetValues("Cookie").Single());
        Assert.Equal("site.test", request.Headers.Host);
    }

    [Fact]
    public async Task ASearchAsksTheServerForTheText()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"hunters":[{"handle":"Birch","online":false}]}"""));
        Assert.Equal([new Friend("Birch", false)], await Make(fake).SearchAsync("ir c&"));
        Assert.Equal("/account/friends/search?text=ir%20c%26", fake.Seen[0].Request.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData("add", true)]   // asks, or accepts the other's request
    [InlineData("add", false)]
    [InlineData("remove", true)]
    [InlineData("remove", false)]
    public async Task AChangeIsAFormPostFromOurOwnPage(string action, bool ask)
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        var service = Make(fake);
        var result = action == "add" ? await (ask ? service.SendRequestAsync("Oak") : service.AcceptAsync("Oak")) : await (ask ? service.DeclineAsync("Oak") : service.RemoveAsync("Oak"));
        Assert.True(result.Ok);
        var (request, body) = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Post, $"/account/friends/{action}", "handle=Oak"), (request.Method, request.RequestUri!.AbsolutePath, body));
        Assert.True(request.Headers.Contains("X-Requested-With"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"status":"invalid"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"status":"signed_out"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedAnswerIsNoFriendsAndARefusedChange(HttpStatusCode code, string body)
    {
        var service = Make(new Fake(_ => Json(code, body)));
        Assert.Empty(await service.FriendsAsync());
        Assert.Empty(await service.RequestsAsync());
        Assert.Empty(await service.PendingAsync());
        Assert.Empty(await service.SearchAsync("abc"));
        Assert.Equal(code == HttpStatusCode.OK, (await service.SendRequestAsync("Oak")).Ok);
        Assert.Equal(code == HttpStatusCode.OK, (await service.RemoveAsync("Oak")).Ok);
    }

    [Fact]
    public async Task WithoutASessionNothingIsAskedAndNothingChanges()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Own));
        var service = Make(fake, session: null);
        Assert.Empty(await service.FriendsAsync());
        Assert.Empty(await service.SearchAsync("abc"));
        Assert.False((await service.SendRequestAsync("Oak")).Ok);
        Assert.Empty(fake.Seen);
    }
}
