using System.Net;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

/// <summary>A visitor without a session costs the server no call and the journal no warning (#451); one the server refuses (401) is logged once and then signed out.</summary>
public class SignedOutCallsTests
{
    sealed class Fake : HttpMessageHandler
    {
        public int Seen;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    sealed class Warnings
    {
        public int Count;
    }

    sealed class Sink<T>(Warnings w) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => w.Count += logLevel == LogLevel.Warning ? 1 : 0;
    }

    public static TheoryData<string> Services => new() { "friends", "inventory", "wallet", "missions" };

    static async Task<object> AskAsync(string service, Fake fake, ServerCookie cookie, Warnings w)
    {
        var http = new HttpClient(fake) { BaseAddress = new Uri("http://server.test:8080") };
        switch (service)
        {
            case "friends": return await new ServerFriendsService(http, cookie, new Sink<ServerFriendsService>(w)).FriendsAsync();
            case "inventory": return await new ServerInventoryService(http, cookie, new Sink<ServerInventoryService>(w)).ItemsAsync();
            case "wallet": return await new ServerWalletService(http, cookie, new MockWalletService(new MockWalletState()), new Sink<ServerWalletService>(w)).EntriesAsync(false, 0);
            default: return (await new ServerMissionsService(http, cookie, new Sink<ServerMissionsService>(w)).DailyAsync()).Days;
        }
    }

    [Theory, MemberData(nameof(Services))]
    public async Task NoSessionIsNoCallAndNoWarning(string service)
    {
        foreach (var value in new string?[] { null, "" })
        {
            var (fake, w) = (new Fake(), new Warnings());
            Assert.Empty((System.Collections.IEnumerable)await AskAsync(service, fake, new ServerCookie { Value = value }, w));
            Assert.Equal((0, 0), (fake.Seen, w.Count));
        }
    }

    [Theory, MemberData(nameof(Services))]
    public async Task ARefusedSessionIsLoggedThenSignedOut(string service)
    {
        var (fake, w, cookie) = (new Fake(), new Warnings(), new ServerCookie { Value = "old" });
        Assert.Empty((System.Collections.IEnumerable)await AskAsync(service, fake, cookie, w));
        Assert.Equal((1, 1), (fake.Seen, w.Count));
        await AskAsync(service, fake, cookie, w);
        Assert.Equal((1, 1), (fake.Seen, w.Count));
    }
}
