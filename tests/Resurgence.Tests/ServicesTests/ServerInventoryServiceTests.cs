using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerInventoryServiceTests
{
    sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, bool Marked)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Headers.Contains("X-Requested-With")));
            var (code, body) = answer(request);
            return new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    static ServerInventoryService Make(Fake fake) =>
        new(new HttpClient(fake) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = "secret", Host = "site.test" }, NullLogger<ServerInventoryService>.Instance);

    [Fact]
    public async Task TheCopyAndItsOfferComeFromTheServersRow()
    {
        var fake = new Fake(_ => (HttpStatusCode.OK, """
            {"items": [{"guid": 11, "id": 1, "name": "Test Rifle", "variant": "", "category": "0", "categoryName": "Rifles", "units": null, "slot": 0, "equipped": false, "expires": null, "sell": {"currency": "gm", "refund": 800}},
                       {"guid": 12, "id": 2, "name": "Test Scope", "variant": "", "category": "5", "categoryName": "Sights", "units": null, "slot": 3, "equipped": true, "expires": null, "sell": null}]}
            """));

        var items = await Make(fake).ItemsAsync();

        Assert.Equal([(11L, new SellOffer("gm", 800)), (12L, (SellOffer?)null)], items.Select(i => (i.Guid, i.Sell)));
    }

    [Fact]
    public async Task ASaleIsAFormWithTheGuidFromOurOwnPage()
    {
        var fake = new Fake(_ => (HttpStatusCode.OK, """{"status": "sold", "currency": "gm", "refund": 800, "balance": 15800}"""));

        var result = await Make(fake).SellAsync(11);

        Assert.True(result.Ok);
        Assert.Equal((HttpMethod.Post, "/account/sell", "guid=11", true), Assert.Single(fake.Seen));
    }

    [Fact]
    public async Task ARefusedSaleIsAFailureTheDialogCanShow()
    {
        var result = await Make(new Fake(_ => (HttpStatusCode.Conflict, """{"status": "equipped"}"""))).SellAsync(12);

        Assert.Equal(Result.Fail("Inv.SellFailed"), result);
    }
}
