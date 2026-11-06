using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>GET /catalog/store on the website's host: the store of the application catalogue, with today's price.</summary>
public class CatalogTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    // fictional values only; item 3 is hidden, and a bundle lists it
    const string Store = """
        {"species": [], "reserves": [], "locations": [],
         "items": [
           {"id": 1, "name": "Test Rifle", "variationname": "Blue", "description": "A rifle.", "subcat": {"id": 0}, "price": 100, "silver_price": 4000, "notbuyable": 0, "hidden": 0, "inventory": {"weight": 500, "capacity": 3}},
           {"id": 2, "name": "Test Scope", "subcat": {"id": 5}, "price": 200, "silver_price": null, "notbuyable": 1, "hidden": 0},
           {"id": 3, "name": "Test Hidden", "subcat": {"id": 0}, "price": 10, "hidden": 1}],
         "categories": [
           {"id": "all", "title": "All Items", "subcategories": [{"id": "all", "title": "All Items"}], "default": "all"},
           {"id": 1, "title": "Weapons", "subcategories": [{"id": 0, "title": "Rifles"}, {"id": 1, "title": "Handguns"}], "default": 0},
           {"id": 7, "title": "Bundles", "subcategories": [{"id": 50, "title": "Bundles"}], "default": 50},
           {"id": 2, "title": "Sights", "subcategories": [{"id": 5, "title": "Rifle Sights"}], "default": 5}],
         "bundles": [{"id": 9, "name": "Test Pack", "description": "Three things.", "price": 90.0, "items": [1, 2, 3]}]}
        """;

    // item 1 is half price
    const string Discounts = """[{"item_id": 1, "percentage": 50, "start_ts": 0, "end_ts": 1099511627776}]""";

    async Task<Reply> Get(string application, bool offline = false)
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, (JsonObject)JsonNode.Parse(application)!, LOCALE, TEXTURES,
            discounts: (JsonArray)JsonNode.Parse(Discounts)!, offline: offline);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        await using var server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
        return await server.SendAsync("GET", "web.test", "/catalog/store", []);
    }

    [Fact]
    public async Task TheStoreIsTheCataloguesWithTodaysPricesAndWithoutHiddenItems()
    {
        var reply = await Get(Store);
        Assert.Equal(200, reply.Status);
        var items = JsonNode.Parse(reply.Body)!["items"]!.AsArray();
        Assert.Equal([1, 2], items.Select(i => (int)i!["id"]!));
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
