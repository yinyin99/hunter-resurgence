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

    // the server answers its website host only, and the store to a signed-in player only: the request carries the player's cookie
    [Fact]
    public async Task TheRequestCarriesTheWebsitesHostAndTheSessionCookie()
    {
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Store));
        await Make(fake).ItemsAsync("all", "", 1);
        var request = Assert.Single(fake.Seen);
        Assert.Equal("site.test", request.Headers.Host);
        Assert.Equal($"{ServerSessionApi.Cookie}=secret", request.Headers.GetValues("Cookie").Single());
    }

    // signed out the store is empty and the server is not asked, even when a signed-in player's read is kept
    [Fact]
    public async Task SignedOutTheStoreIsEmptyAndNotAsked()
    {
        var cache = new ServerCache<ServerStore>();
        await Make(new Fake(_ => Json(HttpStatusCode.OK, Store)), cache: cache).ItemsAsync("all", "", 1);  // a signed-in player filled the cache
        var fake = new Fake(_ => Json(HttpStatusCode.OK, Store));
        var service = new ServerStoreService(new HttpClient(fake) { BaseAddress = new Uri("http://server.test:8080") }, new ServerCookie { Value = null, Host = "site.test" },
            cache, new NoItems(), NullLogger<ServerStoreService>.Instance);
        Assert.Empty((await service.ItemsAsync("all", "", 1)).Rows);
        Assert.Null(await service.ItemAsync(1));
        Assert.Empty(fake.Seen);
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
        Assert.Equal(("Test Rifle", "Blue", 50, 100, 2000, 0, true, 0.0), (item!.Name, item.Variant, item.Ems, item.WasEms, item.Gms, item.Owned, item.Buyable, item.Capacity));
        Assert.Null(await Ok().ItemAsync(404));
    }

    // an operator's sale: today's prices, the prices before it (null for a price that is not on sale) and the sale itself; the sales view lists an item whose gm$ price alone is on sale
    const string SaleStore = """
        {"categories": [], "bundles": [
           {"id": 9, "name": "Test Pack", "description": "", "price": 72, "was": 90, "buyable": true, "items": [{"id": 1, "name": "Test Rifle"}], "sale": {"name": "Test weekend", "percent": 20, "until": "2026-11-30T07:00:00Z"}},
           {"id": 10, "name": "Test Plain Pack", "description": "", "price": 50, "was": null, "buyable": true, "items": [{"id": 1, "name": "Test Rifle"}], "sale": null}], "items": [
           {"id": 1, "name": "Test Rifle", "variant": "", "description": "", "category": "0", "ems": 75, "was": 100, "gms": 150, "gwas": 200, "buyable": true, "sale": {"name": "Test weekend", "percent": 25, "until": "2026-11-30T07:00:00Z"}},
           {"id": 2, "name": "Test Pistol", "variant": "", "description": "", "category": "0", "ems": 80, "was": null, "gms": 90, "gwas": 120, "buyable": true, "sale": {"name": "Test weekend", "percent": 25, "until": "2026-11-30T07:00:00Z"}},
           {"id": 3, "name": "Test Scope", "variant": "", "description": "", "category": "0", "ems": 200, "was": null, "gms": 0, "gwas": null, "buyable": true, "sale": null}]}
        """;

    [Fact]
    public async Task ASalesPricesBeforeItAndItsEndAreMapped()
    {
        var service = Make(new Fake(_ => Json(HttpStatusCode.OK, SaleStore)));
        var rifle = (await service.ItemAsync(1))!;
        Assert.Equal((75, 100, 150, 200), (rifle.Ems, rifle.WasEms, rifle.Gms, rifle.WasGms));
        Assert.Equal(new StoreSale("Test weekend", 25, new(2026, 11, 30, 7, 0, 0, TimeSpan.Zero)), rifle.Sale);
        var pistol = (await service.ItemAsync(2))!;
        Assert.Equal((80, (int?)null, 90, (int?)120), (pistol.Ems, pistol.WasEms, pistol.Gms, pistol.WasGms));
        var scope = (await service.ItemAsync(3))!;
        Assert.Equal((200, (int?)null, 0, (int?)null), (scope.Ems, scope.WasEms, scope.Gms, scope.WasGms));
        Assert.Null(scope.Sale);
        Assert.False(scope.OnSale);
        Assert.Equal([1, 2], (await service.ItemsAsync("sales", "", 1)).Rows.Select(i => i.Id));
        Assert.True(await service.HasSalesAsync());
        Assert.Contains("sales", (await service.CategoriesAsync()).Select(c => c.Key));
    }

    [Fact]
    public async Task ABundlesPriceBeforeItsSaleAndItsEndAreMapped()
    {
        var service = Make(new Fake(r => r.RequestUri!.AbsolutePath == ServerAccountApi.Bundles ? Json(HttpStatusCode.NotFound, "{}") : Json(HttpStatusCode.OK, SaleStore)));
        var pack = (await service.BundleAsync(9))!;
        Assert.Equal((72, 90, 90), (pack.Price, pack.PreSale, pack.StruckPrice));
        Assert.Equal(new StoreSale("Test weekend", 20, new(2026, 11, 30, 7, 0, 0, TimeSpan.Zero)), pack.Sale);
        Assert.True(pack.OnSale);
        var plain = (await service.BundleAsync(10))!;
        Assert.Equal((50, (int?)null, 0), (plain.Price, plain.PreSale, plain.StruckPrice));
        Assert.Null(plain.Sale);
        Assert.False(plain.OnSale);
    }

    // only bundles on sale make a Sales entry
    [Fact]
    public async Task ABundleOnSaleMakesTheSalesEntry()
    {
        var only = Make(new Fake(_ => Json(HttpStatusCode.OK, """{"categories": [], "items": [], "bundles": [{"id": 9, "name": "Test Pack", "description": "", "price": 72, "was": 90, "buyable": true, "items": []}]}""")));
        Assert.Contains("sales", (await only.CategoriesAsync()).Select(c => c.Key));
        Assert.True(await only.HasSalesAsync());
    }

    const string Page = """
        {"capacity": 0.45, "contents": {"count": 10, "name": "cartridges"},
         "fits": [{"title": "Weapons", "key": "0", "items": [{"id": 1, "name": "Test Rifle", "variant": "Blue"}]}],
         "ethical": {"ammo": [{"id": 9, "name": "Test Cartridge", "variant": "Red"}], "species": [{"id": 14, "name": "Test Fox", "permitted": [true]}, {"id": 8, "name": "Test Moose", "permitted": [false]}]}}
        """;

    // the list holds a thousand items, so what belongs to one (capacity, contents, fits, the species table) is the server's item route, asked with the cookie
    [Fact]
    public async Task AnItemHasWhatTheServersItemRouteAdds()
    {
        var fake = new Fake(r => Json(HttpStatusCode.OK, r.RequestUri!.AbsolutePath == "/catalog/item/1" ? Page : Store));
        var item = (await Make(fake).ItemAsync(1))!;
        Assert.Equal((0.45, new StoreContents(10, "cartridges")), (item.Capacity, item.Contents));
        var fit = Assert.Single(item.Fits!);
        Assert.Equal(("Weapons", "0", 1, "Test Rifle", "Blue"), (fit.Title, fit.Key, fit.Items[0].Id, fit.Items[0].Name, fit.Items[0].Variant));
        Assert.Equal([new StoreFit(9, "Test Cartridge", "Red")], item.Ethics!.Columns);
        Assert.Equal([(14, "Test Fox", true), (8, "Test Moose", false)], item.Ethics.Rows.Select(r => (r.Id, r.Species, r.Permitted[0])));
        var request = fake.Seen[^1];
        Assert.Equal(("/catalog/item/1", "site.test", true), (request.RequestUri!.AbsolutePath, request.Headers.Host, request.Headers.Contains("Cookie")));
    }

    [Fact]
    public async Task AnItemShowsWithoutItsDetailsWhenTheServerDoesNotAnswerThem()
    {
        var item = (await Make(new Fake(r => r.RequestUri!.AbsolutePath.StartsWith("/catalog/item/") ? Json(HttpStatusCode.NotFound, "{}") : Json(HttpStatusCode.OK, Store))).ItemAsync(1))!;
        Assert.Equal(("Test Rifle", 0.0), (item.Name, item.Capacity));
        Assert.Null(item.Contents);
        Assert.Null(item.Fits);
        Assert.Null(item.Ethics);
    }

    sealed class Items(params InventoryItem[] items) : IInventoryService
    {
        public Task<IReadOnlyList<InventoryItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<InventoryItem>>(items);
        public Task<Result> SellAsync(long guid) => Task.FromResult(Result.Fail("Inv.SellFailed"));
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

    const string LimitedStore = """
        {"categories": [], "items": [
           {"id": 1, "name": "Test Rifle", "variant": "", "description": "", "category": "0", "ems": 50, "was": null, "gms": 0, "buyable": true, "limit": 1},
           {"id": 2, "name": "Test Fuel", "variant": "", "description": "", "category": "0", "ems": 5, "was": null, "gms": 0, "buyable": false, "fuel": true}],
         "bundles": []}
        """;

    // one rifle at most: owning it ends the buying; a fuel the server gives every player is marked, not sold
    [Fact]
    public async Task AnItemAtItsLimitCannotBeBoughtAgainAndAFuelIsMarkedUnlimited()
    {
        var service = Make(new Fake(_ => Json(HttpStatusCode.OK, LimitedStore)), items: new Items(new InventoryItem(1, "Test Rifle", "", "0", "Rifles", null, false)));
        var rows = (await service.ItemsAsync("all", "", 1)).Rows;
        Assert.Equal([(1, true), (0, false)], rows.Select(r => (r.OwnLimit, r.LimitReached)));
        Assert.Equal([false, true], rows.Select(r => r.Fuel));
    }

    // what a bundle costs is the server's own offer for the player (the list is the same for everyone); asked with the cookie, kept until a page opens or a purchase goes through
    [Fact]
    public async Task ABundleCostsWhatTheServerOffersThePlayerAndShowsHowManyItemsHeOwns()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath == ServerAccountApi.Bundles
            ? Json(HttpStatusCode.OK, """{"bundles": [{"id": 9, "price": 60, "owned": 1, "of": 3, "buyable": true}]}""")
            : Json(HttpStatusCode.OK, Store));
        var service = Make(fake);
        var bundle = (await service.BundleAsync(9))!;
        Assert.Equal((60, 90, 1, true, 3), (bundle.Price, bundle.WasPrice, bundle.OwnedItems, bundle.Buyable, bundle.Lines.Count));
        Assert.True(Assert.Single(fake.Seen, r => r.RequestUri!.AbsolutePath == ServerAccountApi.Bundles).Headers.Contains("Cookie"));
        await service.BundlesAsync();
        Assert.Single(fake.Seen, r => r.RequestUri!.AbsolutePath == ServerAccountApi.Bundles);
        service.RefreshOwned();
        await service.BundlesAsync();
        Assert.Equal(2, fake.Seen.Count(r => r.RequestUri!.AbsolutePath == ServerAccountApi.Bundles));
    }

    [Fact]
    public async Task ABundlesGmPriceIsTheServersOwnOffer()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath == ServerAccountApi.Bundles
            ? Json(HttpStatusCode.OK, """{"bundles": [{"id": 9, "price": 60, "gms": 1020, "owned": 1, "of": 3, "buyable": true}]}""")
            : Json(HttpStatusCode.OK, Store.Replace("\"price\": 90, \"buyable\": true", "\"price\": 90, \"gms\": 1530, \"gwas\": 1700, \"buyable\": true")));
        var bundle = (await Make(fake).BundleAsync(9))!;
        Assert.Equal((60, 1020, 1700), (bundle.Price, bundle.Gms, bundle.PreSaleGms));
    }

    [Fact]
    public async Task ABundleTheServerWouldGiveNothingOfIsNotBuyableAndWithoutAnOfferTheListPriceStands()
    {
        var none = Make(new Fake(r => r.RequestUri!.AbsolutePath == ServerAccountApi.Bundles
            ? Json(HttpStatusCode.OK, """{"bundles": [{"id": 9, "price": 90, "owned": 3, "of": 3, "buyable": false}]}""") : Json(HttpStatusCode.OK, Store)));
        var owned = (await none.BundleAsync(9))!;
        Assert.Equal((90, 0, 3, false), (owned.Price, owned.WasPrice, owned.OwnedItems, owned.Buyable));
        var unanswered = Make(new Fake(r => r.RequestUri!.AbsolutePath == ServerAccountApi.Bundles ? Json(HttpStatusCode.NotFound, "{}") : Json(HttpStatusCode.OK, Store)));
        var listed = (await unanswered.BundleAsync(9))!;
        Assert.Equal((90, 0, 0, true), (listed.Price, listed.WasPrice, listed.OwnedItems, listed.Buyable));
    }

    sealed class Counting : IInventoryService
    {
        public int Reads;
        public Task<IReadOnlyList<InventoryItem>> ItemsAsync() { Reads++; return Task.FromResult<IReadOnlyList<InventoryItem>>([]); }
        public Task<Result> SellAsync(long guid) => Task.FromResult(Result.Fail("Inv.SellFailed"));
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

    // after a sale ends the server charges the full price: a read that shows a sale is not kept past its earliest end (but at least the pause, so a clock that is off does not ask on every call)
    [Fact]
    public void TheCacheDropsAStoreWhenTheFirstSaleEnds()
    {
        var start = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var store = new ServerStore([], [], []);

        var soon = new ServerCache<ServerStore>();
        soon.Set(start, store, new DateTimeOffset(start.AddMinutes(2), TimeSpan.Zero));
        Assert.Same(store, soon.Get(start.AddMinutes(2).AddTicks(-1)));
        Assert.Null(soon.Get(start.AddMinutes(2)));

        var later = new ServerCache<ServerStore>();   // a sale that ends after the usual five minutes changes nothing
        later.Set(start, store, new DateTimeOffset(start.AddHours(3), TimeSpan.Zero));
        Assert.Same(store, later.Get(start.AddMinutes(4).AddSeconds(59)));
        Assert.Null(later.Get(start.AddMinutes(5)));

        var over = new ServerCache<ServerStore>();    // an end already past is kept for the pause only
        over.Set(start, store, new DateTimeOffset(start.AddHours(-1), TimeSpan.Zero));
        Assert.Same(store, over.Get(start.AddSeconds(9)));
        Assert.Null(over.Get(start.AddSeconds(10)));
    }

    [Fact]
    public void TheFirstSaleEndIsTheEarliestOfItemsAndBundlesAndNoneWithoutASale()
    {
        var early = new ServerSale("A", 10, new(2026, 11, 29, 7, 0, 0, TimeSpan.Zero));
        var late = new ServerSale("B", 10, new(2026, 11, 30, 7, 0, 0, TimeSpan.Zero));
        ServerItem Item(ServerSale? sale) => new(1, "Test Rifle", "", "", "0", 10, null, 0, true, Sale: sale);
        ServerBundle Bundle(ServerSale? sale) => new(9, "Test Pack", "", 10, true, [], Sale: sale);

        Assert.Equal(early.Until, ServerStoreService.FirstSaleEnd(new([], [Item(late), Item(null)], [Bundle(early)])));
        Assert.Equal(early.Until, ServerStoreService.FirstSaleEnd(new([], [Item(early)], [Bundle(late)])));
        Assert.Null(ServerStoreService.FirstSaleEnd(new([], [Item(null)], [Bundle(null)])));
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
