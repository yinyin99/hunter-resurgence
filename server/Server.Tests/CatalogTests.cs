using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>GET /catalog/store on the website's host: the store of the application catalogue, with today's price.</summary>
public class CatalogTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    // fictional values only; item 3 is hidden, and a bundle lists it; item 4 is a service, item 5 costs nothing; bundles 11 to 13 are not sold;
    // a player may own 5 of item 1 and one of item 3
    const string Store = """
        {"species": [], "reserves": [], "locations": [],
         "items": [
           {"id": 1, "name": "Test Rifle", "variationname": "Blue", "description": "A rifle.", "subcat": {"id": 0}, "price": 100, "silver_price": 4000, "notbuyable": 0, "hidden": 0, "ownMultiple": true, "ownLimit": 5, "inventory": {"weight": 500, "capacity": 3}, "fit": {}},
           {"id": 2, "name": "Test Scope", "subcat": {"id": 5}, "price": 200, "silver_price": null, "notbuyable": 1, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 3, "name": "Test Hidden", "subcat": {"id": 0}, "price": 10, "hidden": 1, "ownMultiple": false, "inventory": {}, "fit": {}},
           {"id": 4, "name": "Test Service", "subcat": {"id": 0}, "price": 50, "type": 45, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 5, "name": "Test Starter", "subcat": {"id": 0}, "price": 0, "silver_price": 0, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 6, "name": "Test Refill", "subcat": {"id": 0}, "price": 5, "hidden": 0, "ownMultiple": false, "units": {"count": 1, "name": "uses"}, "inventory": {}, "fit": {}}],
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

    async Task<Loopback> Start(string application, bool offline = false)
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, (JsonObject)JsonNode.Parse(application)!, LOCALE, TEXTURES,
            discounts: (JsonArray)JsonNode.Parse(Discounts)!, offline: offline);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        return await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    async Task<Reply> Get(string application, bool offline = false, string path = "/catalog/store")
    {
        await using var server = await Start(application, offline);
        return await server.SendAsync("GET", "web.test", path, []);
    }

    /// <summary>POST /account/buy as the website's server sends it, signed in as ME (182 em$) who already owns the owned items; its reply and
    /// the items ME got; <paramref name="gms"/> is added to ME's 33 gm$ first.</summary>
    async Task<(Reply Reply, long[] Got)> Buy(string form, bool signedIn = true, bool ownPage = true, long[]? owned = null, double gms = 0)
    {
        await using var server = await Start(Store);
        if (gms > 0)
        {
            using var con = state.Store.Open(ME);
            State.AddCurrency(con, "gm", gms, "test");
        }
        foreach (var id in owned ?? [])
            state.GrantItems(ME, id);
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
    [InlineData("id=1&count=3", 32L, new long[] { 1, 1, 1 }, new long[] { 1, 1 })]  // up to the own limit
    [InlineData("id=6", 177L, new long[] { 6 }, new long[] { 6 })]  // a one-use item the player holds is bought again
    [InlineData("id=9&bundle=1", 92L, new long[] { 1, 2, 3 })]  // the bundle's price, each of its items, a hidden one too
    public async Task BuyingChargesTheStorePriceAndDeliversTheItems(string form, long left, long[] got, long[]? owned = null)
    {
        var (reply, items) = await Buy(form, owned: owned);
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

    /// <summary>gm$ pays the catalogue's silver price, the 50 % discount of the em$ price not taken off, and only gm$ is debited; the change is in the wallet history.</summary>
    [Theory]
    [InlineData("id=1&wallet=SILVER", 4000.0, 1033.0)]
    public async Task BuyingWithGmChargesTheSilverPriceAndDebitsOnlyGm(string form, double price, double left)
    {
        var (reply, items) = await Buy(form, gms: 5000);
        Assert.Equal(200, reply.Status);
        var body = JsonNode.Parse(reply.Body)!;
        Assert.Equal(left, body["gms"]!.GetValue<double>());
        Assert.Null(body["ems"]);
        Assert.Equal([1L], items);
        Eq("182.0", state.Wallet(ME)["em"]);
        Eq($"{left}", state.Wallet(ME)["gm"]);
        using var con = state.Store.Open(ME);
        var log = con.Rows("SELECT currency, amount, after, summary, item_ids FROM wallet_log WHERE account_id = $a AND currency = 'gm' ORDER BY n DESC")[0];
        Assert.Equal(("gm", -price, left, "In-game payment.", "[1]"), ((string)log[0]!, Convert.ToDouble(log[1]), Convert.ToDouble(log[2]), (string)log[3]!, (string)log[4]!));
    }

    [Theory]
    [InlineData("id=1&wallet=SILVER", 0.0, 409)]              // 4000 gm$ for 33
    [InlineData("id=1&wallet=SILVER&count=2", 5000.0, 409)]   // 8000 for 5033
    [InlineData("id=2&wallet=SILVER", 5000.0, 409)]           // not buyable
    [InlineData("id=3&wallet=SILVER", 5000.0, 409)]           // hidden
    [InlineData("id=4&wallet=SILVER", 5000.0, 409)]           // a service
    [InlineData("id=5&wallet=SILVER", 5000.0, 409)]           // a gm$ price of 0 would be free
    [InlineData("id=6&wallet=SILVER", 5000.0, 409)]           // no gm$ price
    [InlineData("id=9&bundle=1&wallet=SILVER", 5000.0, 409)]  // bundles are em$ only
    [InlineData("id=1&wallet=PLATINUM", 5000.0, 400)]         // not a wallet
    public async Task WhatGmCannotPayChangesNothing(string form, double gms, int status)
    {
        var (reply, items) = await Buy(form, gms: gms);
        Assert.Equal(status, reply.Status);
        Assert.Empty(items);
        Eq("182.0", state.Wallet(ME)["em"]);
        Eq($"{33 + gms}", state.Wallet(ME)["gm"]);
        using var con = state.Store.Open(ME);
        Assert.Empty(con.Rows("SELECT n FROM wallet_log WHERE account_id = $a AND summary = 'In-game payment.'"));
    }

    [Theory]
    [InlineData("id=1&count=3", new long[] { 1, 1, 1 })]  // 6 of a rifle the player may own 5 of
    [InlineData("id=9&bundle=1", new long[] { 3 })]       // the bundle's hidden item may be owned once
    public async Task APurchaseOverTheOwnLimitChangesNothing(string form, long[] owned)
    {
        var (reply, items) = await Buy(form, owned: owned);
        Assert.Equal(409, reply.Status);
        Assert.Empty(items);
        Eq("182.0", state.Wallet(ME)["em"]);
    }

    // the game's call: an item with a gm$ price of 0 or none is not given away, whatever the balance
    [Theory]
    [InlineData("item_id=5&amount=1&wallet=SILVER")]
    [InlineData("item_id=6&amount=1&wallet=SILVER")]
    public async Task TheGamesGmPurchaseOfAFreeItemChangesNothing(string form)
    {
        await using var server = await Start(Store);
        var count = state.Inventory(ME).Count;
        Eq("""{"success": false}""", Reply(state.BuyReply(ME, System.Text.Encoding.ASCII.GetBytes(form))));
        Assert.Equal(count, state.Inventory(ME).Count);
        Eq("33.0", state.Wallet(ME)["gm"]);
    }

    [Theory]
    [InlineData("item_id=1&amount=3&wallet=GOLD", new long[] { 1, 1, 1 })]
    [InlineData("item_id=3&amount=1&wallet=GOLD", new long[] { 3 })]
    [InlineData("item_id=3&amount=2&wallet=GOLD", new long[0])]
    public async Task TheGamesPurchaseOverTheOwnLimitChangesNothing(string form, long[] owned)
    {
        await using var server = await Start(Store);
        foreach (var id in owned)
            state.GrantItems(ME, id);
        var count = state.Inventory(ME).Count;
        Eq("""{"success": false}""", Reply(state.BuyReply(ME, System.Text.Encoding.ASCII.GetBytes(form))));
        Assert.Equal(count, state.Inventory(ME).Count);
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
        Assert.Equal([1, 2, 4, 5, 6], items.Select(i => (int)i!["id"]!));
        Assert.Equal([true, false, false, false, true], items.Select(i => (bool)i!["buyable"]!));  // not buyable, a service, no price
        var rifle = items[0]!;
        Assert.Equal(("Test Rifle", "Blue", "A rifle.", "0", 50L, 100L, 4000L, true),  // the silver price takes no discount
            ((string)rifle["name"]!, (string)rifle["variant"]!, (string)rifle["description"]!, (string)rifle["category"]!, (long)rifle["ems"]!, (long)rifle["was"]!, (long)rifle["gms"]!, (bool)rifle["buyable"]!));
        var scope = items[1]!;
        Assert.Equal((200L, 0L, false), ((long)scope["ems"]!, (long)scope["gms"]!, (bool)scope["buyable"]!));
        Assert.Null(scope["was"]);
    }

    [Fact]
    public async Task TheListCarriesNeitherCapacityNorWeightTheItemPageDoes()
    {
        var rifle = JsonNode.Parse((await Get(Store)).Body)!["items"]![0]!.AsObject();
        Assert.DoesNotContain(rifle, p => p.Key is "capacity" or "weight");
    }

    // fictional: a rifle (5) that takes two ammunitions (14, a pack of 30, and 300) and a scope, a hidden ammunition (99) it also lists; species 17 (the connect's) is permitted 14 and 241,
    // species 117 is its non-typical variant, species 1 has no list (no rule)
    const string Fitting = """
        {"species": [{"id": 17, "name": "Test Rabbit"}, {"id": 117, "name": "Test Rabbit (Non-Typical)"}, {"id": 1, "name": "Test Deer"}], "reserves": [], "locations": [], "bundles": [],
         "items": [
           {"id": 5, "name": "Test Rifle", "variationname": "Blue", "cat": 1, "subcat": {"id": 0}, "price": 100, "inventory": {"weight": 3250}, "fit": {"2": [{"id": 2}], "3": [{"id": 14}, {"id": 99}, {"id": 300}]}},
           {"id": 2, "name": "Test Scope", "cat": 2, "subcat": {"id": 5}, "price": 20, "inventory": {"weight": 0}, "fit": {"1": [{"id": 5}]}},
           {"id": 14, "name": "Test Shot A", "cat": 3, "subcat": {"id": 10}, "price": 4, "units": {"count": 30, "name": "shotshells"}, "inventory": {"weight": 850}, "fit": {"1": [{"id": 5}]}},
           {"id": 300, "name": "Test Slug", "cat": 3, "subcat": {"id": 10}, "price": 4, "inventory": {}, "fit": {"1": [{"id": 5}]}},
           {"id": 99, "name": "Test Hidden Shot", "cat": 3, "hidden": 1, "price": 1, "inventory": {}, "fit": {}}],
         "categories": [{"id": "all", "title": "All Items", "subcategories": [{"id": "all", "title": "All Items"}], "default": "all"},
                        {"id": 1, "title": "Weapons", "subcategories": [{"id": 0, "title": "Rifles"}], "default": 0},
                        {"id": 2, "title": "Sights", "subcategories": [{"id": 5, "title": "Rifle Sights"}], "default": 5},
                        {"id": 3, "title": "Ammo", "subcategories": [{"id": 10, "title": "Rifle Ammo"}], "default": 10}]}
        """;

    async Task<JsonNode> Page(int id)
    {
        var reply = await Get(Fitting, path: $"/catalog/item/{id}");
        Assert.Equal(200, reply.Status);
        return JsonNode.Parse(reply.Body)!;
    }

    [Fact]
    public async Task AWeaponsPageHasItsCapacityItsFitsAndTheSpeciesItsAmmunitionIsPermittedFor()
    {
        var page = await Page(5);
        Assert.Equal(3.25, (double)page["capacity"]!);
        Assert.Null(page["contents"]);
        var fits = page["fits"]!.AsArray();
        Assert.Equal([("Sights", "5"), ("Ammo", "10")], fits.Select(f => ((string)f!["title"]!, (string)f["key"]!)));  // the key is the store's, the one its category texts are by
        Assert.Equal(["Test Scope"], fits[0]!["items"]!.AsArray().Select(i => (string)i!["name"]!));
        Assert.Equal(["Test Shot A", "Test Slug"], fits[1]!["items"]!.AsArray().Select(i => (string)i!["name"]!));  // the hidden one is left out
        var ethical = page["ethical"]!;
        Assert.Equal(["Test Shot A", "Test Slug"], ethical["ammo"]!.AsArray().Select(a => (string)a!["name"]!));
        // 14 is in species 17's list, 300 is not; species 1 has none and no row
        Assert.Equal(["Test Rabbit", "Test Rabbit (Non-Typical)"], ethical["species"]!.AsArray().Select(s => (string)s!["name"]!));
        Assert.Equal([17, 117], ethical["species"]!.AsArray().Select(s => (int)s!["id"]!));
        Assert.All(ethical["species"]!.AsArray(), s => Assert.Equal([true, false], s!["permitted"]!.AsArray().Select(p => (bool)p!)));
    }

    [Fact]
    public async Task AnAmmunitionsPageHasItsContentsTheWeaponsThatFireItAndItsOwnColumn()
    {
        var page = await Page(14);
        Assert.Equal((0.85, 30L, "shotshells"), ((double)page["capacity"]!, (long)page["contents"]!["count"]!, (string)page["contents"]!["name"]!));
        var fit = page["fits"]![0]!;
        Assert.Equal(("Weapons", "0", "Test Rifle", "Blue"), ((string)fit["title"]!, (string)fit["key"]!, (string)fit["items"]![0]!["name"]!, (string)fit["items"]![0]!["variant"]!));
        Assert.Equal(["Test Shot A"], page["ethical"]!["ammo"]!.AsArray().Select(a => (string)a!["name"]!));
        Assert.All(page["ethical"]!["species"]!.AsArray(), s => Assert.Equal([true], s!["permitted"]!.AsArray().Select(p => (bool)p!)));
    }

    [Fact]
    public async Task AnItemThatFitsNothingHasNoTableAndAnUnlistedOneNoPage()
    {
        var scope = await Page(2);
        Assert.Null(scope["ethical"]);
        Assert.Equal(0.0, (double)scope["capacity"]!);
        Assert.Equal(404, (await Get(Fitting, path: "/catalog/item/99")).Status);  // hidden
        Assert.Equal(404, (await Get(Fitting, path: "/catalog/item/404")).Status);
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

    /// <summary>The website's wallet history: after a purchase, the player's own rows and totals; a signed-out request is refused.</summary>
    [Fact]
    public async Task TheWalletRoutesAnswerTheSignedInPlayerOnly()
    {
        await using var server = await Start(Store);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/wallet/ems", [])).Status);
        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/wallet/spent", [])).Status);
        Assert.NotNull(state.WebBuy(ME, 1, false, 2));  // 2 x 50 em$
        var ems = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/wallet/ems?offset=0", [cookie])).Body)!.AsArray();
        Assert.Equal("D 100 182 82 2", $"{ems[0]!["type"]!["id"]} {(double)ems[0]!["amount"]!} {(double)ems[0]!["preBalance"]!} {(double)ems[0]!["postBalance"]!} {ems[0]!["qty"]}");
        Assert.Empty(JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/wallet/gms", [cookie])).Body)!.AsArray());
        Assert.Equal(100.0, (double)JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/wallet/spent", [cookie])).Body)!["spent"]!);
    }
}
