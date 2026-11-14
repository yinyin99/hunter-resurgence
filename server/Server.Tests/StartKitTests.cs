using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The free start: the kit of a new account, the one-time top-up of an older one and the ammunition refill of every hunt. Item 90 is a weapon, 10 and 15 are the
/// free ammunition (a box of 10 each, three boxes at a hunt's start).</summary>
public class StartKitTests : StateTestBase
{
    const long A = 20, B = 21;
    const string FactsJson = """{"format": 1, "start": {"items": [90, 10, 15], "free_ammo": {"items": [10, 15], "boxes": 3}, "wallet": {"em": 1, "gm": 1}}}""";

    static JsonObject Catalogue()
    {
        var app = APP;
        foreach (var (id, units) in new[] { (90, "null"), (15, """{"count": 10}""") })
            app["items"]!.AsArray().Add(O($$"""
                {"id": {{id}}, "name": "Test Item {{id}}", "description": "", "define": "TEST_ITEM_{{id}}", "type": 41, "group": 13, "cat": 4, "client_type": 4,
                 "image": "x", "variationname": null, "units": {{units}}, "fit": {}, "inventory": {"slots": [0, 1, 2, 8], "capacity": 0, "weight": 1},
                 "price": 5, "silver_price": 100, "notbuyable": 0, "subcat": {"id": 0}, "hidden": 0}
                """));
        return app;
    }

    /// <summary>The neutral connect a new account starts from: the weapon in slot 2, a box of each ammunition (10 in slot 8, 15 in the backpack).</summary>
    static byte[] Neutral()
    {
        var connect = CONNECT;
        connect["inventory"]!["items"] = JsonNode.Parse("""
            [{"item_id": 90, "slot": 2, "guid": "1", "metadata": {}, "units": null}, {"item_id": 10, "slot": 8, "guid": "2", "metadata": {}, "units": 10},
             {"item_id": 15, "slot": 0, "guid": "3", "metadata": {}, "units": 10}]
            """);
        return Py.DumpBytes(connect);
    }

    State Open() => new(Db, Py.DumpBytes(CONNECT), BASE, ICON, Catalogue(), LOCALE, TEXTURES, offline: false, startBody: Neutral(), facts: Facts.Parse(FactsJson));

    /// <summary>The account's items as `item/slot/units`, by guid.</summary>
    string Own(long account) => string.Join(" ", state.Inventory(account).Select(i => $"{i!["item_id"]}/{i["slot"]}/{i["units"]}"));

    /// <summary>The units of each stack of an item, by guid.</summary>
    string Stacks(long account, int item)
    {
        using var con = state.Store.Open(account);
        return string.Join(" ", con.Rows("SELECT units FROM items WHERE account_id = $a AND item_id = ? ORDER BY guid", item).Select(r => r[0]));
    }

    void Launch(long account) => state.NoteLaunch(account, "start=100&reserve=2"u8.ToArray());

    [Fact]
    public void ANewAccountStartsWithTheKit()
    {
        state = Open();
        state.CreateAccount(A, "alpha");
        Assert.Equal("90/2/ 10/8/10 15/0/10", Own(A));
    }

    [Fact]
    public void AnOlderAccountGetsTheMissingItemsOnceAndNothingIsDoubled()
    {
        state = Open();
        state.CreateAccount(A, "alpha");
        state.CreateAccount(B, "bravo");
        using (var con = state.Store.Open(A))
        {
            con.Begin();
            con.Exec("DELETE FROM items WHERE account_id = $a AND item_id = 90");  // alpha has the ammunition, not the weapon
            con.Commit();
        }
        using (var con = state.Store.Open(B))
        {
            con.Begin();
            con.Exec("DELETE FROM items WHERE account_id = $a");
            con.Commit();
        }
        using (var con = state.Store.Open())
        {
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = ?", B);
            con.Exec("ALTER TABLE accounts DROP COLUMN start_kit");
            con.Exec("PRAGMA user_version = 21");  // before the start kit
        }
        state = Open();
        Assert.Equal("10/8/10 15/0/10 90/2/", Own(A));  // the weapon in its slot, the ammunition it owns not doubled
        Assert.Equal("", Own(B));  // banned: skipped
        using (var con = state.Store.Open(A))
        {
            con.Begin();
            con.Exec("DELETE FROM items WHERE account_id = $a AND item_id = 90");
            con.Commit();
        }
        state = Open();
        Assert.Equal("10/8/10 15/0/10", Own(A));  // once: a later start does not give it again
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 0 WHERE id = ?", B);
        state = Open();
        Assert.Equal("90/2/ 10/8/10 15/0/10", Own(B));
    }

    [Fact]
    public void AHuntFillsTheFreeAmmunitionToThreeBoxesAndNeverLowersAStock()
    {
        state = Open();
        state.CreateAccount(A, "alpha");
        using (var con = state.Store.Open(A))
        {
            con.Begin();
            con.Exec("UPDATE items SET units = 3 WHERE account_id = $a AND item_id = 10");  // used: 3 of 10 left
            PlayerRows.AddItem(con, 50, 10, 0, 5, new JsonObject());
            con.Commit();
        }
        state.GrantItems(A, 15, 19);  // bought: 20 stacks of 10 in all
        Assert.Equal("3 5", Stacks(A, 10));
        Launch(A);
        Assert.Equal("10 10 10", Stacks(A, 10));  // the used stacks filled, then a new full one
        Assert.Equal(string.Join(" ", Enumerable.Repeat(10, 20)), Stacks(A, 15));  // 200 stay 200
        Launch(A);
        Assert.Equal("10 10 10", Stacks(A, 10));  // a second hunt adds nothing
        var connect = Reply(state.ConnectReply(A))["inventory"]!["items"]!.AsArray();
        Assert.Equal(30L, connect.Where(i => (long)i!["item_id"]! == 10).Sum(i => (long)i!["units"]!));  // the refill is in the connect
    }

    [Fact]
    public void AnEmptyStackAndAMissingOneAreFilledToo()
    {
        state = Open();
        state.CreateAccount(A, "alpha");
        using (var con = state.Store.Open(A))
        {
            con.Begin();
            con.Exec("UPDATE items SET units = 0, slot = 0 WHERE account_id = $a AND item_id = 10");  // used up: the stack goes to the backpack
            con.Exec("DELETE FROM items WHERE account_id = $a AND item_id = 15");
            con.Commit();
        }
        Launch(A);
        Assert.Equal("10 10 10", Stacks(A, 10));
        Assert.Equal("10 10 10", Stacks(A, 15));
    }
}
