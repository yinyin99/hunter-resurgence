using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;

namespace Resurgence.Tests.ServicesTests;

public class ServerStoreServiceTests
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

    // fictional values only
    const string Store = """
        {"categories": [
           {"key": "all", "name": "All Items", "children": [{"key": "all", "name": "All Items"}]},
           {"key": "0", "name": "Weapons", "children": [{"key": "0", "name": "Rifles"}, {"key": "1", "name": "Handguns"}]},
           {"key": "5", "name": "Sights", "children": [{"key": "5", "name": "Rifle Sights"}]}],
         "items": [
           {"id": 1, "name": "Test Rifle", "variant": "Blue", "description": "A rifle.", "category": "0", "ems": 50, "was": 100, "gms": 4000, "buyable": true, "capacity": 3},
           {"id": 2, "name": "Test Pistol", "variant": "", "description": "", "category": "1", "ems": 80, "was": null, "gms": 0, "buyable": true, "capacity": 1},
           {"id": 3, "name": "Test Scope", "variant": "Silver", "description": "", "category": "5", "ems": 200, "was": null, "gms": 0, "buyable": false, "capacity": 0}],
         "bundles": [{"id": 9, "name": "Test Pack", "description": "Two.", "price": 90, "items": [{"id": 1, "name": "Test Rifle"}, {"id": 1, "name": "Test Rifle"}, {"id": 77, "name": "Gone"}]}]}
        """;

    static ServerStoreService Make(HttpMessageHandler handler, StoreCache? cache = null, string? host = "site.test") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = "secret", Host = host }, cache ?? new StoreCache(), NullLogger<ServerStoreService>.Instance);

    static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    static ServerStoreService Ok() => Make(new Fake(_ => Json(HttpStatusCode.OK, Store)));

    // the server answers its website host only; the store is the same for everyone, so the player's cookie stays home
    [Fact]
    public async Task TheRequestCarriesTheWebsitesHostAndNoCookie()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Store));
        await Make(fake).ItemsAsync("all", "", 1);
        var request = Assert.Single(fake.Seen);
        Assert.Equal("site.test", request.Headers.Host);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task SalesAllAndBundlesLeadAndTheServersCategoriesFollowWithTheirChildren()
    {
        var categories = await Ok().CategoriesAsync();
        Assert.Equal(["sales", "all", "bundles", "0", "5"], categories.Select(c => c.Key));
        Assert.Equal(["0", "1"], categories[3].Children.Select(c => c.Key));
        Assert.Equal("Handguns", categories[3].Children[1].Name);
    }

    [Fact]
    public async Task WithoutASaleOrABundleThoseEntriesAreNotThere()
    {
        var plain = Make(new Fake(_ => Json(HttpStatusCode.OK, """{"categories": [], "items": [{"id": 2, "name": "Test Pistol", "variant": "", "description": "", "category": "1", "ems": 80, "was": null, "gms": 0, "buyable": true, "capacity": 1}], "bundles": []}""")));
        Assert.Equal(["all"], (await plain.CategoriesAsync()).Select(c => c.Key));
        Assert.False(await plain.HasSalesAsync());
    }

    [Theory]
    [InlineData("all", "", new[] { 1, 2, 3 })]
    [InlineData("sales", "", new[] { 1 })]
    [InlineData("0", "", new[] { 1, 2 })]      // a top-level category is its sub-categories
    [InlineData("1", "", new[] { 2 })]
    [InlineData("all", "silver", new[] { 3 })] // the variant counts
    [InlineData("0", "pistol rifle", new int[0])]
    public async Task ItemsAreFilteredByViewAndSearch(string view, string search, int[] ids) =>
        Assert.Equal(ids, (await Ok().ItemsAsync(view, search, 1)).Rows.Select(i => i.Id));

    [Fact]
    public async Task AnItemHasTodaysPriceTheOldOneAndNoOwnership()
    {
        var item = await Ok().ItemAsync(1);
        Assert.Equal(("Test Rifle", "Blue", 50, 100, 4000, 0, true, 3), (item!.Name, item.Variant, item.Ems, item.WasEms, item.Gms, item.Owned, item.Buyable, item.Capacity));
        Assert.Null(await Ok().ItemAsync(404));
    }

    [Fact]
    public async Task APageHoldsTwelveAndThePageNumberIsKept()
    {
        var items = string.Join(",", Enumerable.Range(1, 30).Select(i => $$"""{"id": {{i}}, "name": "Item {{i}}", "variant": "", "description": "", "category": "0", "ems": 1, "was": null, "gms": 0, "buyable": true, "capacity": 0}"""));
        var service = Make(new Fake(_ => Json(HttpStatusCode.OK, $$"""{"categories": [], "items": [{{items}}], "bundles": []}""")));
        var page = await service.ItemsAsync("all", "", 3);
        Assert.Equal((6, 3, 3), (page.Rows.Count, page.Number, page.Pages));
        Assert.Equal(3, (await service.ItemsAsync("all", "", 99)).Number);
    }

    [Fact]
    public async Task ABundleHasOneLineAUnitAtTheFullPriceAndAnUnknownItemKeepsItsName()
    {
        var bundle = await Ok().BundleAsync(9);
        Assert.Equal(("Test Pack", 90), (bundle!.Name, bundle.Price));
        Assert.Equal([(1, 100), (1, 100), (77, 0)], bundle.Lines.Select(l => (l.ItemId, l.Value)));  // the sale price is not the value
        Assert.Equal("Gone", bundle.Lines[2].Name);
        Assert.Null(await Ok().BundleAsync(404));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error": "no catalogue"}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    public async Task AFailedAnswerIsAnEmptyStoreNotTheMocks(HttpStatusCode code, string body)
    {
        var service = Make(new Fake(_ => Json(code, body)));
        Assert.Empty((await service.ItemsAsync("all", "", 1)).Rows);
        Assert.Empty(await service.BundlesAsync());
        Assert.Equal(["all"], (await service.CategoriesAsync()).Select(c => c.Key));
    }

    [Fact]
    public async Task ASuccessIsKeptForEveryoneAFailureIsAskedAgain()
    {
        var cache = new StoreCache();
        var good = new Fake(_ => Json(HttpStatusCode.OK, Store));
        await Make(good, cache).CategoriesAsync();
        await Make(good, cache).ItemsAsync("all", "", 1);  // another visitor
        Assert.Equal("/catalog/store", Assert.Single(good.Seen).RequestUri!.AbsolutePath);

        var bad = new Fake(_ => Json(HttpStatusCode.NotFound, "{}"));
        var service = Make(bad, new StoreCache());
        await service.CategoriesAsync();
        await service.CategoriesAsync();
        Assert.Equal(2, bad.Seen.Count);
    }

    [Fact]
    public async Task BuyingIsNotServedYet() => Assert.False((await Ok().BuyAsync(1, false, false)).Ok);
}
