using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerNewsServiceTests
{
    sealed class Fake(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    // fictional values only
    const string News = """
        {"posts": [{"title": "First post", "date": "2026-10-29T14:05:00", "slug": "first-post", "excerpt": "Big news…"}],
         "featured": [{"title": "SALES", "lead": "Half off", "linkText": "See them", "target": "/store/sales", "date": "2026-01-01T00:00:00"}]}
        """;

    static ServerNewsService Make(Fake fake) =>
        new(new HttpClient(fake) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = "secret", Host = "site.test" }, new ServerCache<ServerNews>(), NullLogger<ServerNewsService>.Instance);

    [Fact]
    public async Task ThePostsAndSlidesAreTheServersAskedWithTheHostAndNoCookie()
    {
        var fake = new Fake(HttpStatusCode.OK, News);
        var service = Make(fake);

        Assert.Equal([new NewsPost("First post", new DateTime(2026, 10, 29, 14, 5, 0), "first-post", "Big news…")], await service.RecentAsync());
        Assert.Equal([new Featured("SALES", "Half off", "See them", "/store/sales", new DateTime(2026, 1, 1))], await service.FeaturedAsync());
        var request = Assert.Single(fake.Seen);  // the second read is the shared copy
        Assert.Equal(("/catalog/news", "site.test", false), (request.RequestUri!.AbsolutePath, request.Headers.Host, request.Headers.Contains("Cookie")));
    }

    // no news is no news, never the mock's; and the server has no Discord widget, so the feed shows none
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error": "no news"}""")]
    [InlineData(HttpStatusCode.OK, """{"posts": []}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task NoNewsIsNothingAndThereIsNoDiscordWidget(HttpStatusCode code, string body)
    {
        var service = Make(new Fake(code, body));
        Assert.Empty(await service.RecentAsync());
        Assert.Empty(await service.FeaturedAsync());
        Assert.Null(await service.DiscordAsync());
    }
}
