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
         "bundles": [{"id": 9, "name": "Test Pack", "description": "Two.", "price": 90, "buyable": true, "items": [{"id": 1, "name": "Test Rifle"}, {"id": 1, "name": "Test Rifle"}, {"id": 77, "name": "Gone"}]},
                     {"id": 10, "name": "Test Gone Pack", "description": "", "price": 0, "buyable": false, "items": [{"id": 77, "name": "Gone"}]}]}
        """;

    static ServerStoreService Make(HttpMessageHandler handler, ServerCache<ServerStore>? cache = null, string? host = "site.test", IInventoryService? items = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = "secret", Host = host }, cache ?? new ServerCache<ServerStore>(), items ?? new NoItems(), NullLogger<ServerStoreService>.Instance);

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

    sealed class Items(params InventoryItem[] items) : IInventoryService
    {
        public Task<IReadOnlyList<InventoryItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<InventoryItem>>(items);
    }

    // units are summed over a counted item's copies, otherwise the copies are counted; a license carries its end (0 unlimited); an item the player lacks is 0
    [Fact]
    public async Task WhatThePlayerOwnsComesFromTheInventory()
    {
        var service = Make(new Fake(_ => Json(HttpStatusCode.OK, Store)), items: new Items(
            new(1, "Test Rifle", "Blue", "0", "Rifles", null, false), new(1, "Test Rifle", "Blue", "0", "Rifles", null, true),
            new(2, "Test Pistol", "", "1", "Handguns", 3, false), new(2, "Test Pistol", "", "1", "Handguns", 4, false),
            new(3, "Test Scope", "Silver", "5", "Rifle Sights", null, false, 1800000000)));
        var rows = (await service.ItemsAsync("all", "", 1)).Rows;
        Assert.Equal([(2, false, null), (7, true, null), (1, false, 1800000000)], rows.Select(r => (r.Owned, r.Counted, r.Expires)));
        Assert.Equal(0, (await Make(new Fake(_ => Json(HttpStatusCode.OK, Store))).ItemAsync(1))!.Owned);
        Assert.Equal(2, (await service.ItemAsync(1))!.Owned);
    }

    sealed class Counting : IInventoryService
    {
        public int Reads;
        public Task<IReadOnlyList<InventoryItem>> ItemsAsync() { Reads++; return Task.FromResult<IReadOnlyList<InventoryItem>>([]); }
    }

    // a search is many reads of the store, not of the inventory: it is read again after a page opens or a purchase
    [Fact]
    public async Task TheInventoryIsReadOnceUntilAPageOpensOrAPurchaseGoesThrough()
    {
        var inv = new Counting();
        var service = Make(new Fake(r => Json(HttpStatusCode.OK, r.Method == HttpMethod.Get ? Store : "{}")), items: inv);
        await service.ItemsAsync("all", "r", 1);
        await service.ItemsAsync("all", "ri", 1);
        await service.ItemAsync(1);
        Assert.Equal(1, inv.Reads);
        service.RefreshOwned();
        await service.ItemsAsync("all", "", 1);
        Assert.Equal(2, inv.Reads);
        Assert.True((await service.BuyAsync(1, false, false)).Ok);
        await service.ItemAsync(1);
        Assert.Equal(3, inv.Reads);
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
        Assert.False((await Ok().BundleAsync(10))!.Buyable);  // the server does not sell it: the page's button is disabled
        Assert.True(bundle.Buyable);
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
        var cache = new ServerCache<ServerStore>();
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
        var cache = new ServerCache<ServerStore>();
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

    // a purchase is the player's: it carries the session cookie, the header a cross-site form cannot set, and the form the server reads
    [Fact]
    public async Task BuyingPostsTheFormWithTheSessionCookie()
    {
        string? form = null;
        var fake = new Fake(r => { form = r.Content!.ReadAsStringAsync().Result; return Json(HttpStatusCode.OK, """{"status": "bought", "ems": 10}"""); });

        Assert.True((await Make(fake).BuyAsync(9, true, false)).Ok);

        var request = Assert.Single(fake.Seen);
        Assert.Equal((HttpMethod.Post, "/account/buy", "site.test"), (request.Method, request.RequestUri!.AbsolutePath, request.Headers.Host));
        Assert.Contains("secret", string.Join(";", request.Headers.GetValues("Cookie")));
        Assert.True(request.Headers.Contains("X-Requested-With"));
        Assert.Equal("id=9&count=1&bundle=1&wallet=GOLD", form);
    }

    // gm$ is the SILVER wallet of the same call
    [Fact]
    public async Task BuyingWithGmsAsksForTheSilverWallet()
    {
        string? form = null;
        var fake = new Fake(r => { form = r.Content!.ReadAsStringAsync().Result; return Json(HttpStatusCode.OK, """{"status": "bought", "gms": 10}"""); });

        Assert.True((await Make(fake).BuyAsync(1, false, true, 2)).Ok);

        Assert.Equal("id=1&count=2&bundle=0&wallet=SILVER", form);
    }

    [Fact]
    public async Task ARefusalIsAFailedPurchase() =>
        Assert.Equal("Store.Oops", (await Make(new Fake(_ => Json(HttpStatusCode.Conflict, """{"status": "refused"}"""))).BuyAsync(1, false, false, 3)).Error);

    // a signed-out visitor buys nothing, with either currency: the server is not asked
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignedOutIsNotSent(bool gms)
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, "{}"));
        var service = new ServerStoreService(new HttpClient(fake) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = null, Host = "site.test" },
            new ServerCache<ServerStore>(), new NoItems(), NullLogger<ServerStoreService>.Instance);
        Assert.False((await service.BuyAsync(1, false, gms)).Ok);
        Assert.Empty(fake.Seen);
    }
}
