using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerMessagesServiceTests
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

    static ServerMessagesService Make(HttpMessageHandler handler, string? session = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = session, Host = "site.test" }, NullLogger<ServerMessagesService>.Instance);

    [Fact]
    public async Task TheConversationsAndTheThreadAreTheAccountsOnTheServer()
    {
        var fake = new Fake(HttpStatusCode.OK, """{"conversations":[{"handle":"Fern","preview":"see you","ts":1700000200,"unread":true,"avatar":"https://site.test/photos/9/avatar.jpg"}],"messages":[{"mine":true,"content":"where?","ts":1700000150}]}""");
        var service = Make(fake);
        Assert.Equal([new Conversation("Fern", "see you", new(2023, 11, 14, 22, 16, 40, DateTimeKind.Utc), true, "https://site.test/photos/9/avatar.jpg")], await service.ConversationsAsync());
        var thread = Assert.Single(await service.ThreadAsync("Fern & co"));
        Assert.Equal((true, "where?"), (thread.Mine, thread.Text));
        Assert.Equal(["/account/messages", "/account/messages/thread?handle=Fern%20%26%20co"], fake.Seen.Select(r => r.RequestUri!.PathAndQuery));
        Assert.Equal("hr_session=abc", fake.Seen[0].Headers.GetValues("Cookie").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"status":"invalid"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"status":"signed_out"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task AFailedAnswerIsNoMessagesAndNothingIsSent(HttpStatusCode code, string body)
    {
        var service = Make(new Fake(code, body));
        Assert.Empty(await service.ConversationsAsync());
        Assert.Empty(await service.ThreadAsync("Fern"));
        Assert.False((await service.SendAsync("Fern", "hi")).Ok);
    }

    [Fact]
    public async Task WithoutASessionNothingIsAsked()
    {
        var fake = new Fake(HttpStatusCode.OK, "{}");
        Assert.Empty(await Make(fake, session: null).ConversationsAsync());
        Assert.Empty(fake.Seen);
    }
}
