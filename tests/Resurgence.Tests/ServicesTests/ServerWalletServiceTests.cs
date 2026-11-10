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

    const string Rows = """[{"id":3,"created":1700000000,"amount":100.0,"preBalance":182.0,"postBalance":82.0,"summary":"In-game payment.","items":[{"id":1,"name":"Test Rifle"}],"qty":2},{"id":2,"created":1699990000,"amount":7.0,"preBalance":82.0,"postBalance":89.0,"summary":"Reward (Id: 5)","items":[],"qty":1}]""";

    [Fact]
    public async Task TheHistoryAndTheTotalsAreTheServersWithTheItemsNamed()
    {
        var fake = new Fake(r => Json(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("spent")
            ? """{"spent": 196.0, "silver_spent": 595.0}"""
            : Rows));
        var wallet = Make(fake);
        var entries = await wallet.EntriesAsync(gms: false, offset: 50);
        Assert.Equal(["/account/wallet/ems?offset=50"], fake.Seen.Select(r => r.RequestUri!.PathAndQuery));
        Assert.Equal([(2023, 100, 182, 82, "2 × Test Rifle", "In-game payment."), (2023, 7, 82, 89, "", "Reward (Id: 5)")],
            entries.Select(e => (e.When.Year, e.Amount, e.Before, e.After, e.Item, e.Summary)));
        await wallet.EntriesAsync(gms: true, offset: 0);
        Assert.EndsWith("/account/wallet/gms?offset=0", fake.Seen[^1].RequestUri!.PathAndQuery);
        var summary = await wallet.SummaryAsync();
        Assert.Equal((196m, 595m), (summary.EmsSpent, summary.GmsSpent));
    }

    [Fact]
    public async Task NoAnswerIsNoHistoryNotTheMocksRows()
    {
        Assert.Empty(await Make(new Fake(_ => Json(HttpStatusCode.Unauthorized, "{}"))).EntriesAsync(false, 0));
        Assert.Empty(await Make(new Fake(_ => Json(HttpStatusCode.OK, "<html>"))).EntriesAsync(true, 0));
    }

    [Fact]
    public async Task NoCookieAsksNothing()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        var wallet = Make(fake, cookie: null);
        Assert.Equal(0, (await wallet.BalancesAsync()).Ems);
        Assert.Empty(await wallet.EntriesAsync(false, 0));
        Assert.Empty(fake.Seen);
    }
}
