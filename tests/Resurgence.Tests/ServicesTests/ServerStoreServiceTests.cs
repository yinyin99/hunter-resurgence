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
           {"id": 1, "name": "Test Rifle", "variant": "Blue", "description": "A rifle.", "category": "0", "ems": 50, "was": 100, "gms": 2000, "buyable": true},
           {"id": 2, "name": "Test Pistol", "variant": "", "description": "", "category": "1", "ems": 80, "was": null, "gms": 0, "buyable": true},
           {"id": 3, "name": "Test Scope", "variant": "Silver", "description": "", "category": "5", "ems": 200, "was": null, "gms": 0, "buyable": false}],
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
        Assert.Equal(["all"], categories[1].Children.Select(c => c.Key));      // all and bundles open their one sub-link, as the store does
        Assert.Equal(["bundles"], categories[2].Children.Select(c => c.Key));
        Assert.Equal(["0", "1"], categories[3].Children.Select(c => c.Key));
        Assert.Equal("Handguns", categories[3].Children[1].Name);
    }

    [Fact]
    public async Task WithoutASaleOrABundleThoseEntriesAreNotThere()
    {
        var plain = Make(new Fake(_ => Json(HttpStatusCode.OK, """{"categories": [], "items": [{"id": 2, "name": "Test Pistol", "variant": "", "description": "", "category": "1", "ems": 80, "was": null, "gms": 0, "buyable": true}], "bundles": []}""")));
        Assert.Equal(["all"], (await plain.CategoriesAsync()).Select(c => c.Key));
        Assert.False(await plain.HasSalesAsync());
    }

    [Theory]
    [InlineData("all", "", new[] { 1, 2, 3 })]
    [InlineData("sales", "", new[] { 1 })]
    [InlineData("0", "", new[] { 1 })]         // a top-level category opens its first sub-category: that one's items only
    [InlineData("1", "", new[] { 2 })]
    [InlineData("5", "", new[] { 3 })]
    [InlineData("all", "silver", new[] { 3 })] // the variant counts
    [InlineData("1", "pistol rifle", new int[0])]
    public async Task ItemsAreFilteredByViewAndSearch(string view, string search, int[] ids) =>
        Assert.Equal(ids, (await Ok().ItemsAsync(view, search, 1)).Rows.Select(i => i.Id));

    [Fact]
    public async Task AnItemHasTodaysPriceTheOldOneAndNoOwnershipOrCapacity()
    {
        var item = await Ok().ItemAsync(1);
        Assert.Equal(("Test Rifle", "Blue", 50, 100, 2000, 0, true, 0), (item!.Name, item.Variant, item.Ems, item.WasEms, item.Gms, item.Owned, item.Buyable, item.Capacity));
        Assert.Null(await Ok().ItemAsync(404));
    }

    [Fact]
    public async Task APageHoldsTwelveAndThePageNumberIsKept()
    {
        var items = string.Join(",", Enumerable.Range(1, 30).Select(i => $$"""{"id": {{i}}, "name": "Item {{i}}", "variant": "", "description": "", "category": "0", "ems": 1, "was": null, "gms": 0, "buyable": true}"""));
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
    [InlineData(HttpStatusCode.OK, "{}")]                                      // a reply that lacks a list is not a store
    [InlineData(HttpStatusCode.OK, """{"categories": [], "items": []}""")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    public async Task AFailedOrIncompleteAnswerIsAnEmptyStoreNotTheMocks(HttpStatusCode code, string body)
    {
        var service = Make(new Fake(_ => Json(code, body)));
        Assert.Empty((await service.ItemsAsync("all", "", 1)).Rows);
        Assert.Empty(await service.BundlesAsync());
        Assert.Equal(["all"], (await service.CategoriesAsync()).Select(c => c.Key));
    }

    [Fact]
    public async Task ASuccessIsKeptForEveryone()
    {
        var cache = new StoreCache();
        var good = new Fake(_ => Json(HttpStatusCode.OK, Store));
        await Make(good, cache).CategoriesAsync();
        await Make(good, cache).ItemsAsync("all", "", 1);  // another visitor
        Assert.Equal("/catalog/store", Assert.Single(good.Seen).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task AFailureIsNotAskedAgainAtOnceBecauseAPageReadsTheStoreSeveralTimes()
    {
        var bad = new Fake(_ => Json(HttpStatusCode.NotFound, "{}"));
        var service = Make(bad);
        await service.CategoriesAsync();
        await service.ItemsAsync("all", "", 1);
        await service.BundlesAsync();
        Assert.Single(bad.Seen);
    }

    [Fact]
    public void TheCacheKeepsAStoreForFiveMinutesAndPausesAFailureForTen()
    {
        var cache = new StoreCache();
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(cache.Get(start));
        Assert.False(cache.Paused(start));

        var store = new ServerStore([], [], []);
        cache.Set(start, store);
        Assert.Same(store, cache.Get(start.AddMinutes(4).AddSeconds(59)));
        Assert.Null(cache.Get(start.AddMinutes(5)));

        cache.Fail(start);
        Assert.True(cache.Paused(start.AddSeconds(9)));
        Assert.False(cache.Paused(start.AddSeconds(10)));

        cache.Fail(start);
        cache.Set(start, store);  // an answer ends the pause
        Assert.False(cache.Paused(start));
    }

    [Fact]
    public async Task BuyingIsNotServedYet() => Assert.False((await Ok().BuyAsync(1, false, false)).Ok);
}
