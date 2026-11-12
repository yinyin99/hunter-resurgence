using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerDiscordInviteServiceTests
{
    sealed class Fake(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Cookie, bool Own)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("Cookie", out var c) ? c.Single() : null, request.Headers.Contains("X-Requested-With")));
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    static ServerDiscordInviteService Make(HttpMessageHandler handler, string? cookie = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = cookie }, NullLogger<ServerDiscordInviteService>.Instance);

    [Theory]
    [InlineData("""{"first":true}""", true)]
    [InlineData("""{"first":false}""", false)]
    public async Task TheServerSaysWhetherThisIsTheFirstTime(string body, bool first)
    {
        var fake = new Fake(HttpStatusCode.OK, body);
        Assert.Equal(first, await Make(fake).FirstAsync());
        Assert.Equal((HttpMethod.Post, "/account/discord", "hr_session=abc", true), Assert.Single(fake.Seen));
    }

    [Fact]
    public async Task SignedOutOrWithoutAnAnswerThereIsNoInvitation()
    {
        var none = new Fake(HttpStatusCode.OK, """{"first":true}""");
        Assert.False(await Make(none, cookie: null).FirstAsync());
        Assert.Empty(none.Seen);
        Assert.False(await Make(new Fake(HttpStatusCode.Unauthorized, """{"status":"signed_out"}""")).FirstAsync());
        Assert.False(await Make(new Fake(HttpStatusCode.OK, "not json")).FirstAsync());
    }
}
