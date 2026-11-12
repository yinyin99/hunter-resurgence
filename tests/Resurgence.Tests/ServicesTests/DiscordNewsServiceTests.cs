using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class DiscordNewsServiceTests
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // fictional values only
    static HttpResponseMessage Discord(HttpRequestMessage r, bool widgetOn) => r.RequestUri!.AbsolutePath switch
    {
        "/api/v10/invites/abc" => Json(HttpStatusCode.OK, """{"code": "abc", "guild": {"id": "42", "name": "Test"}}"""),
        "/api/v10/guilds/42/widget.json" when widgetOn => Json(HttpStatusCode.OK, """{"id": "42", "presence_count": 3, "members": [{"username": "Ann"}]}"""),
        "/api/v10/guilds/42/widget.json" => Json(HttpStatusCode.Forbidden, """{"message": "Widget Disabled", "code": 50004}"""),
        _ => new(HttpStatusCode.NotFound),
    };

    static DiscordNewsService Make(Fake fake, string? invite) =>
        new(new HttpClient(fake), invite, new(), new(), NullLogger<DiscordNewsService>.Instance);

    [Fact]
    public async Task TheInvitesServerWithItsWidgetOnIsShown() =>
        Assert.Equal("42", await Make(new Fake(r => Discord(r, true)), "https://discord.gg/abc").DiscordGuildAsync());

    [Fact]
    public async Task AWidgetThatIsOffIsNoWidget() =>
        Assert.Null(await Make(new Fake(r => Discord(r, false)), "https://discord.com/invite/abc").DiscordGuildAsync());

    [Fact]
    public async Task WithoutAnInviteDiscordIsNotAsked()
    {
        var fake = new Fake(r => Discord(r, true));
        Assert.Null(await Make(fake, null).DiscordGuildAsync());
        Assert.Empty(fake.Seen);
    }
}
