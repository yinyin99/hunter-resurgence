using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerNewsFeedServiceTests
{
    sealed class Fake(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];
        public string? Host { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Host = request.Headers.Host;
            Seen.Add(request.RequestUri!.PathAndQuery);
            if (code == 0) throw new HttpRequestException("down");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    static ServerNewsFeedService Make(HttpMessageHandler handler, string origin = "", string? host = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Host = host }, origin, NullLogger<ServerNewsFeedService>.Instance);

    const string Feed = """
        {"posts": [
          {"id": 4, "published": 1795000000, "pinned": true, "title": "Pinned", "body": "Body **one**", "link": "https://example.test/a", "linkText": "Read", "picture": "/news/4/picture?v=9"},
          {"id": 3, "published": 1794000000, "pinned": false, "title": "Plain", "body": "Body two", "link": "link:source", "linkText": null, "picture": null},
          {"id": 0, "published": 1, "pinned": false, "title": "No id", "body": "x"},
          {"id": 5, "published": 1, "pinned": false, "title": "", "body": "no title"}
        ]}
        """;

    [Fact]
    public async Task ThePostsAreAskedInTheReadersLanguageWithTheBrowsersHostAndComeInTheServersOrder()
    {
        var fake = new Fake(HttpStatusCode.OK, Feed);
        var posts = await Make(fake, host: "play.example.test").PostsAsync("pt-BR");

        Assert.Equal(["/news?lang=pt-BR"], fake.Seen);
        Assert.Equal("play.example.test", fake.Host);
        Assert.Equal([4L, 3L], posts.Select(p => p.Id));  // a post without an id or a title is left out
        Assert.Equal(("Pinned", "Body **one**", true, "https://example.test/a", "Read"), (posts[0].Title, posts[0].Body, posts[0].Pinned, posts[0].Link, posts[0].LinkText));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795000000), posts[0].Published);
        Assert.Null(posts[1].Link);  // a link without its text is no link
    }

    [Fact]
    public async Task APicturePathBecomesAnAddressOnTheOriginOfTheLauncherAndStaysAPathOnTheWebsite()
    {
        Assert.Equal("https://play.example.test/news/4/picture?v=9", (await Make(new Fake(HttpStatusCode.OK, Feed), "https://play.example.test").PostsAsync("en"))[0].Picture);
        Assert.Equal("/news/4/picture?v=9", (await Make(new Fake(HttpStatusCode.OK, Feed)).PostsAsync("en"))[0].Picture);
        Assert.Null((await Make(new Fake(HttpStatusCode.OK, Feed)).PostsAsync("en"))[1].Picture);
        Assert.Null((await Make(new Fake(HttpStatusCode.OK, """{"posts": [{"id": 1, "published": 1, "title": "t", "body": "b", "picture": "https://elsewhere.test/x.png"}]}""")).PostsAsync("en"))[0].Picture);
    }

    [Fact]
    public async Task NothingIsShownWhenTheServerDoesNotAnswerWell()
    {
        Assert.Empty(await Make(new Fake(HttpStatusCode.InternalServerError, Feed)).PostsAsync("en"));
        Assert.Empty(await Make(new Fake(HttpStatusCode.OK, "not json")).PostsAsync("en"));
        Assert.Empty(await Make(new Fake(HttpStatusCode.OK, """{"other": []}""")).PostsAsync("en"));
        Assert.Empty(await Make(new Fake(HttpStatusCode.OK, """{"posts": [{"id": 1, "published": 99999999999999999, "title": "t", "body": "b"}]}""")).PostsAsync("en"));
        Assert.Empty(await Make(new Fake(0, "")).PostsAsync("en"));
    }
}
