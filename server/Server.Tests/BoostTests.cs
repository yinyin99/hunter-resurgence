using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The connect reply's `boost`: an entry's `own` is the account's own, not the template's.</summary>
public class BoostTests : StateTestBase
{
    const long Owner = ME, Earner = 8, Neither = 9;

    // pouch 178 unlocks at hunterscore 300, pouch 179 at 900, the backpack 92 only by owning it
    static JsonObject Entry(long id, long? level) => new() { ["own"] = true, ["item"] = new JsonObject { ["id"] = id, ["hunterscoreGiveLevel"] = level } };

    [Fact]
    public void OwnIsOwnedOrEarned()
    {
        var connect = CONNECT;
        connect["boost"] = new JsonObject { ["default_inventory_capacity"] = 10000, ["boost"] = new JsonArray(Entry(92, null), Entry(178, 300), Entry(179, 900)) };
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES);
        state.CreateAccount(Earner, "earner");
        state.CreateAccount(Neither, "neither");
        using (var con = state.Store.Open(Owner)) con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, 900, 178, 7, '{}', NULL)");
        using (var con = state.Store.Open(Earner)) con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 900) WHERE id = $a");
        using (var con = state.Store.Open(Neither)) con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 299) WHERE id = $a");

        string Own(long account) => string.Join(",", Reply(state.ConnectReply(account))["boost"]!["boost"]!.AsArray().Select(b => (bool)b!["own"]! ? "1" : "0"));
        Assert.Equal("0,1,0", Own(Owner));
        Assert.Equal("0,1,1", Own(Earner));
        Assert.Equal("0,0,0", Own(Neither));
        Assert.Equal(20000, (int)Reply(state.ConnectReply(Owner))["boost"]!["default_inventory_capacity"]!);
    }

    [Fact]
    public void CapacityIsTheDefaultPlusOneBackpackRow()
    {
        var connect = CONNECT;
        connect["boost"] = new JsonObject { ["default_inventory_capacity"] = 10000, ["boost"] = new JsonArray() };
        connect["inventory"]!["infos"]!.AsArray().Add(new JsonObject { ["item_id"] = 92, ["capacity"] = 10000 });
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES) { InventoryCapacity = 20000 };
        state.CreateAccount(Earner, "earner");
        using (var con = state.Store.Open(Owner))
        {
            con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, 901, 92, 0, '{}', NULL)");
            con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, 902, 92, 0, '{}', NULL)");  // a duplicate row
        }

        // the game's total: the default plus the capacity of each owned row
        long Total(long account)
        {
            var reply = Reply(state.ConnectReply(account));
            var capacity = reply["inventory"]!["infos"]!.AsArray().ToDictionary(i => (long)i!["item_id"]!, i => i!["capacity"] is { } c ? (long)c : 0);
            return (long)reply["boost"]!["default_inventory_capacity"]! + reply["inventory"]!["items"]!.AsArray().Sum(r => capacity.GetValueOrDefault((long)r!["item_id"]!));
        }
        Assert.Equal(30000, Total(Owner));
        Assert.Equal(20000, Total(Earner));
    }
}
