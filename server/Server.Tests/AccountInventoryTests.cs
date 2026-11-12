using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>GET /account/inventory: the signed-in player's own items with the catalogue's names and categories.</summary>
public class AccountInventoryTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    // item 0 is a real item; item 2 is counted (a refill with units); item 3 is not in the catalogue's categories
    const string Catalogue = """
        {"species": [], "reserves": [], "locations": [],
         "items": [
           {"id": 0, "name": "Test Rifle", "variationname": "Blue", "subcat": {"id": 4}, "price": 100, "inventory": {}, "fit": {}},
           {"id": 2, "name": "Test Refill", "subcat": {"id": 5}, "price": 5, "units": {"count": 3, "name": "uses"}, "inventory": {}, "fit": {}},
           {"id": 3, "name": "Test Loose", "price": 5, "inventory": {}, "fit": {}}],
         "categories": [{"id": 1, "title": "Weapons", "subcategories": [{"id": 4, "title": "Rifles"}], "default": 4},
                        {"id": 2, "title": "Supplies", "subcategories": [{"id": 5, "title": "Refills"}], "default": 5}],
         "bundles": []}
        """;

    [Fact]
    public async Task AnAccountSeesItsOwnItemsAndNobodyElsesDo()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, (JsonObject)JsonNode.Parse(Catalogue)!, LOCALE, TEXTURES, offline: false);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        await using var server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
        Assert.True(state.GrantItems(ME, 0));
        Assert.True(state.GrantItems(ME, 2));
        using (var con = state.Store.Open(ME))
        {
            con.Exec("UPDATE items SET slot = 4 WHERE account_id = $a AND item_id = 0");
            con.Exec("UPDATE items SET metadata = json_set(metadata, '$.expires', 1800000000) WHERE account_id = $a AND item_id = 2");
        }
        var other = state.Store.NextServerId();
        state.CreateAccount(other, "Other", "76561190000001000");
        string Cookie(long id) => $"{SignIn.SessionCookie}={state.Store.CreateWebSession(id, TimeSpan.FromHours(1))}";
        Task<Reply> Get(params (string, string)[] headers) => server.SendAsync("GET", "web.test", "/account/inventory", headers);

        Assert.Equal(401, (await Get()).Status);
        var mine = JsonNode.Parse((await Get(("Cookie", Cookie(ME)))).Body)!["items"]!.AsArray();
        Assert.Equal(["0 Test Rifle Blue 4 Rifles  4 true ", "2 Test Refill  5 Refills 3 0 false 1800000000"],
            mine.TakeLast(2).Select(i => $"{i!["id"]} {i["name"]} {i["variant"]} {i["category"]} {i["categoryName"]} {i["units"]} {i["slot"]} {i["equipped"]} {i["expires"]}"));
        // a new account has its starter items only: none of the two granted to ME
        var theirs = JsonNode.Parse((await Get(("Cookie", Cookie(other)))).Body)!["items"]!.AsArray();
        Assert.DoesNotContain(theirs, i => (long)i!["id"]! is 0 or 2);
    }
}
