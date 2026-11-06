using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

public class ServerWalletServiceTests
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

    static ServerWalletService Make(HttpMessageHandler handler, string? cookie = "abc") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = cookie, Host = "site.test" },
            new MockWalletService(new MockWalletState()), NullLogger<ServerWalletService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task BalancesAreTheAccountsNotTheMocks()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, """{"userId":7,"handle":"Fox","hunterscore":3,"rank":2,"ems":182,"gms":40}"""));
        var balances = await Make(fake).BalancesAsync();
        Assert.Equal((182, 40, null, null), (balances.Ems, balances.Gms, balances.AmmoDays, balances.FuelDays));
        Assert.Equal("hr_session=abc", Assert.Single(Assert.Single(fake.Seen).Headers.GetValues("Cookie")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"signed out"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task NoAnswerOfTheServerIsZeroNotMockMoney(HttpStatusCode code, string body)
    {
        var balances = await Make(new Fake(_ => Json(code, body))).BalancesAsync();
        Assert.Equal((0, 0), (balances.Ems, balances.Gms));
    }

    [Fact]
    public async Task NoCookieAsksNothing()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.Equal(0, (await Make(fake, cookie: null).BalancesAsync()).Ems);
        Assert.Empty(fake.Seen);
    }
}
