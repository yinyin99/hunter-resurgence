using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>GET /catalog/store on the website's host: the store of the application catalogue, with today's price.</summary>
public class CatalogTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    // fictional values only; item 3 is hidden, and a bundle lists it; item 4 is a service, item 5 costs nothing; bundles 11 to 13 are not sold
    const string Store = """
        {"species": [], "reserves": [], "locations": [],
         "items": [
           {"id": 1, "name": "Test Rifle", "variationname": "Blue", "description": "A rifle.", "subcat": {"id": 0}, "price": 100, "silver_price": 4000, "notbuyable": 0, "hidden": 0, "inventory": {"weight": 500, "capacity": 3}, "fit": {}},
           {"id": 2, "name": "Test Scope", "subcat": {"id": 5}, "price": 200, "silver_price": null, "notbuyable": 1, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 3, "name": "Test Hidden", "subcat": {"id": 0}, "price": 10, "hidden": 1, "inventory": {}, "fit": {}},
           {"id": 4, "name": "Test Service", "subcat": {"id": 0}, "price": 50, "type": 45, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 5, "name": "Test Starter", "subcat": {"id": 0}, "price": 0, "hidden": 0, "inventory": {}, "fit": {}}],
         "categories": [
           {"id": "all", "title": "All Items", "subcategories": [{"id": "all", "title": "All Items"}], "default": "all"},
           {"id": 1, "title": "Weapons", "subcategories": [{"id": 0, "title": "Rifles"}, {"id": 1, "title": "Handguns"}], "default": 0},
           {"id": 7, "title": "Bundles", "subcategories": [{"id": 50, "title": "Bundles"}], "default": 50},
           {"id": 2, "title": "Sights", "subcategories": [{"id": 5, "title": "Rifle Sights"}], "default": 5}],
         "bundles": [{"id": 9, "name": "Test Pack", "description": "Three things.", "price": 90.0, "items": [1, 2, 3]},
                     {"id": 11, "name": "Test Free Pack", "price": 0, "items": [1]},
                     {"id": 12, "name": "Test Paid Pack", "price": -5, "items": [1]},
                     {"id": 13, "name": "Test Gone Pack", "price": 20, "items": [1, 404]}]}
        """;

    // item 1 is half price
    const string Discounts = """[{"item_id": 1, "percentage": 50, "start_ts": 0, "end_ts": 1099511627776}]""";

    async Task<Loopback> Start(string application, bool offline = false, string? feed = null)
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, (JsonObject)JsonNode.Parse(application)!, LOCALE, TEXTURES,
            discounts: (JsonArray)JsonNode.Parse(Discounts)!, offline: offline);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        if (feed is not null) table.Entries[new("GET", "api.test", Replay.FEED_DATA, null)] = new Replay.Hit(2, "", 200, [], System.Text.Encoding.UTF8.GetBytes(feed));
        return await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    async Task<Reply> Get(string application, bool offline = false, string path = "/catalog/store", string? feed = null)
    {
        await using var server = await Start(application, offline, feed);
        return await server.SendAsync("GET", "web.test", path, []);
    }

    // fictional values only: a post without an address and one without a date are left out, and so are a slide that links to another site (a # route there too)
    // and one whose start time is out of range
    const string Feed = """
        {"posts": [
           {"url": "https://news.test/first-post/", "title": "First &amp; best", "date": "2026-10-29 14:05:00",
            "excerpt": "<p>Big news&hellip; <a class=\"more-link\" href=\"https://news.test/first-post/\">Continue reading <span>&rarr;</span></a></p>"},
           {"url": "", "title": "No address", "date": "2026-10-28 10:00:00", "excerpt": ""},
           {"url": "https://news.test/undated/", "title": "No date", "date": null, "excerpt": ""}],
         "splashes": [
           {"subject": "SALES", "description": "<b>Half</b> off", "linktext": "See them", "link": "https://web.test/#store/sales", "image_url": "https://news.test/uploads/2026/08/Big Sale_805x380.png", "start_ts": 1767225600},
           {"subject": "A post", "description": "Read it", "linktext": "Here", "link": "https://news.test/first-post/", "start_ts": 1767225600},
           {"subject": "Elsewhere", "description": "", "linktext": "", "link": "https://other.example/page", "start_ts": 1767225600},
           {"subject": "Elsewhere too", "description": "", "linktext": "", "link": "https://other.example/page#top", "start_ts": 1767225600},
           {"subject": "Never", "description": "", "linktext": "", "link": "https://web.test/#store", "start_ts": 1e300}]}
        """;

    [Fact]
    public async Task TheNewsIsTheRecordedFeedAsPlainText()
    {
        var reply = await Get(Store, path: "/catalog/news", feed: Feed);
        Assert.Equal(200, reply.Status);
        var news = JsonNode.Parse(reply.Body)!;
        var post = Assert.Single(news["posts"]!.AsArray())!;
        Assert.Equal(("First & best", "2026-10-29T14:05:00", "first-post", "Big news…"),
            ((string)post["title"]!, (string)post["date"]!, (string)post["slug"]!, (string)post["excerpt"]!));  // the website adds its own "continue reading"
        var featured = news["featured"]!.AsArray();
        Assert.Equal(["/store/sales", "news:first-post"], featured.Select(f => (string)f!["target"]!));
        Assert.Equal(("SALES", "Half off", "See them", "big-sale_805x380", "2026-01-01T00:00:00"),
            ((string)featured[0]!["title"]!, (string)featured[0]!["lead"]!, (string)featured[0]!["linkText"]!, (string)featured[0]!["image"]!, (string)featured[0]!["date"]!));
        Assert.Null(featured[1]!["image"]);
    }

    // the replay's order: without a recording, the reply the mirror saved on the game's web host
    [Fact]
    public async Task WithoutARecordingTheMirrorsFeedIsTheNews()
    {
        var file = Path.Combine(tmp, "game_web.test", "v1", "splash", "feed_data", "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Feed);
        var reply = await Get(Store, path: "/catalog/news");
        Assert.Equal(200, reply.Status);
        Assert.Equal("first-post", (string)JsonNode.Parse(reply.Body)!["posts"]![0]!["slug"]!);
    }

    [Theory]
    [InlineData(null)]                                  // no recording and no mirror: the live server before one is added
    [InlineData("""{"posts": [], "splashes": []}""")]
    [InlineData("not json")]
    public async Task WithoutRecordedPostsThereIsNoNews(string? feed) =>
        Assert.Equal(404, (await Get(Store, path: "/catalog/news", feed: feed)).Status);

    /// <summary>POST /account/buy as the website's server sends it, signed in as ME (182 em$); its reply and the items ME got.</summary>
    async Task<(Reply Reply, long[] Got)> Buy(string form, bool signedIn = true, bool ownPage = true)
    {
        await using var server = await Start(Store);
        var before = state.Inventory(ME).Count;
        List<(string, string)> headers = [("Content-Type", "application/x-www-form-urlencoded")];
        if (signedIn) headers.Add(("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}"));
        if (ownPage) headers.Add(("X-Requested-With", "XMLHttpRequest"));
        var reply = await server.SendAsync("POST", "web.test", "/account/buy", headers, System.Text.Encoding.ASCII.GetBytes(form));
        return (reply, [.. state.Inventory(ME).Skip(before).Select(i => (long)i!["item_id"]!)]);
    }

    [Theory]
    [InlineData("id=1", 132L, new long[] { 1 })]              // today's price, half off
    [InlineData("id=1&count=3", 32L, new long[] { 1, 1, 1 })]
    [InlineData("id=9&bundle=1", 92L, new long[] { 1, 2, 3 })]  // the bundle's price, each of its items, a hidden one too
    public async Task BuyingChargesTheStorePriceAndDeliversTheItems(string form, long left, long[] got)
    {
        var (reply, items) = await Buy(form);
        Assert.Equal(200, reply.Status);
        Assert.Equal(left, (long)JsonNode.Parse(reply.Body)!["ems"]!.AsValue().GetValue<double>());
        Assert.Equal(got, items);
        Eq($"{left}.0", state.Wallet(ME)["em"]);
    }

    [Theory]
    [InlineData("id=1&count=4")]           // 200 em$ for 182
    [InlineData("id=2")]                   // not buyable
    [InlineData("id=3")]                   // hidden
    [InlineData("id=77")]                  // unknown
    [InlineData("id=9&bundle=1&count=2")]  // one bundle at a time
    [InlineData("id=1&bundle=1")]          // not a bundle
    [InlineData("id=4")]                   // a service
    [InlineData("id=5")]                   // no price
    [InlineData("id=11&bundle=1")]         // a bundle at 0 would be free, again and again
    [InlineData("id=12&bundle=1")]         // one below 0 would pay the player
    [InlineData("id=13&bundle=1")]         // an item the catalogue does not have
    public async Task WhatIsNotForSaleOrTooDearChangesNothing(string form)
    {
        var (reply, items) = await Buy(form);
        Assert.Equal(409, reply.Status);
        Assert.Empty(items);
        Eq("182.0", state.Wallet(ME)["em"]);
    }

    [Theory]
    [InlineData(false, true, 401)]
    [InlineData(true, false, 400)]  // a cross-site form cannot set X-Requested-With
    public async Task OnlyTheSignedInPlayerFromOurPageBuys(bool signedIn, bool ownPage, int status)
    {
        var (reply, items) = await Buy("id=1", signedIn, ownPage);
        Assert.Equal(status, reply.Status);
        Assert.Empty(items);
        Eq("182.0", state.Wallet(ME)["em"]);
    }

    [Fact]
    public async Task TheStoreIsTheCataloguesWithTodaysPricesAndWithoutHiddenItems()
    {
        var reply = await Get(Store);
        Assert.Equal(200, reply.Status);
        var items = JsonNode.Parse(reply.Body)!["items"]!.AsArray();
        Assert.Equal([1, 2, 4, 5], items.Select(i => (int)i!["id"]!));
        Assert.Equal([true, false, false, false], items.Select(i => (bool)i!["buyable"]!));  // not buyable, a service, no price
        var rifle = items[0]!;
        Assert.Equal(("Test Rifle", "Blue", "A rifle.", "0", 50L, 100L, 2000L, true),  // the silver price takes the discount too
            ((string)rifle["name"]!, (string)rifle["variant"]!, (string)rifle["description"]!, (string)rifle["category"]!, (long)rifle["ems"]!, (long)rifle["was"]!, (long)rifle["gms"]!, (bool)rifle["buyable"]!));
        var scope = items[1]!;
        Assert.Equal((200L, 0L, false), ((long)scope["ems"]!, (long)scope["gms"]!, (bool)scope["buyable"]!));
        Assert.Null(scope["was"]);
    }

    [Fact]
    public async Task NoFieldTheCatalogueGivesWithoutAUnitIsPassedOn()
    {
        var rifle = JsonNode.Parse((await Get(Store)).Body)!["items"]![0]!.AsObject();
        Assert.DoesNotContain(rifle, p => p.Key is "capacity" or "weight");
    }

    [Fact]
    public async Task TheCategoriesAreTheAddressKeysAndOneWithoutItemsIsLeftOut()
    {
        var categories = JsonNode.Parse((await Get(Store)).Body)!["categories"]!.AsArray();
        Assert.Equal(["all", "0", "5"], categories.Select(c => (string)c!["key"]!));  // "Bundles" has no item
        Assert.Equal(["0", "1"], categories[1]!["children"]!.AsArray().Select(c => (string)c!["key"]!));
        Assert.Equal("Handguns", (string)categories[1]!["children"]![1]!["name"]!);
    }

    [Fact]
    public async Task TheStoreSellsTheBundlesThePurchaseTakes() =>
        Assert.Equal([true, false, false, false], JsonNode.Parse((await Get(Store)).Body)!["bundles"]!.AsArray().Select(b => (bool)b!["buyable"]!));

    [Fact]
    public async Task ABundleHasItsPriceRoundedAndItsItemsNamedAHiddenOneToo()
    {
        var bundle = JsonNode.Parse((await Get(Store)).Body)!["bundles"]![0]!;
        Assert.Equal((9, "Test Pack", 90L), ((int)bundle["id"]!, (string)bundle["name"]!, (long)bundle["price"]!));
        Assert.Equal(["Test Rifle", "Test Scope", "Test Hidden"], bundle["items"]!.AsArray().Select(i => (string)i!["name"]!));
    }

    [Fact]
    public async Task WithoutItemsThereIsNoCatalogue() =>
        Assert.Equal(404, (await Get("""{"species": [], "items": [], "reserves": [], "locations": []}""")).Status);

    [Fact]
    public async Task ALocalServerDoesNotServeIt() => Assert.Equal("miss", (await Get(Store, offline: true)).Source);
}
