using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>`unlimited_ammo`: the connect carries the game's unlimited-ammo service, every hunt starts with every owned ammunition full, a cloned box is full and never past its stack size,
/// and no ammunition is sold. Off: ammunition is an item like any other. Item 77 is an ammunition (a box of 10) that is not the free kit's; 19 is a lure.</summary>
public class UnlimitedAmmoTests : StateTestBase
{
    const long Ammo = 77;
    static readonly byte[] BuyAmmo = "item_id=77&amount=1&wallet=GOLD"u8.ToArray(), BuyLure = "item_id=19&amount=1&wallet=GOLD"u8.ToArray();

    State Make(bool on)
    {
        var connect = CONNECT;
        connect["inventory"]!["active_services"] = O("""{"UNLIMITED_FUEL": 1, "STREAK_PROTECTION": 0}""");
        var app = APP;
        app["items"]!.AsArray().Add(O("""
            {"id": 77, "name": "Test Cartridge", "description": "", "define": "TEST_CARTRIDGE", "type": 3, "group": 13, "cat": 3, "client_type": 3, "image": "x", "variationname": null,
             "units": {"count": 10, "name": "cartridges"}, "fit": {}, "inventory": {"slots": [0, 1, 8], "capacity": 0, "weight": 1},
             "price": 5, "silver_price": 100, "notbuyable": 0, "subcat": {"id": 0}, "hidden": 0}
            """));
        app["bundles"] = JsonNode.Parse("""[{"id": 9, "name": "Test Bundle", "price": 100, "items": [19, 77]}]""");
        return state = new State(Path.Combine(tmp, $"{on}.db"), Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES) { UnlimitedAmmo = on };
    }

    /// <summary>The units of each stack of the ammunition, by guid.</summary>
    string Stacks()
    {
        using var con = state.Store.Open(ME);
        return string.Join(" ", con.Rows("SELECT units FROM items WHERE account_id = $a AND item_id = 77 ORDER BY guid").Select(r => r[0]));
    }

    void SetStacks(params long[] units)
    {
        Assert.True(state.GrantItems(ME, Ammo, units.Length));
        using var con = state.Store.Open(ME);
        var guids = con.Rows("SELECT guid FROM items WHERE account_id = $a AND item_id = 77 ORDER BY guid").Select(r => (long)r[0]).ToArray();
        for (var i = 0; i < units.Length; i++)
            con.Exec("UPDATE items SET units = ? WHERE account_id = $a AND guid = ?", units[i], guids[i]);
    }

    [Fact]
    public void TheConnectCarriesTheServiceAsTheIntegerOneWhenOnAndKeepsTheOtherServices()
    {
        Eq("""{"UNLIMITED_FUEL": 1, "STREAK_PROTECTION": 0, "UNLIMITED_AMMO": 1}""", Reply(Make(true).ConnectReply(ME))["inventory"]!["active_services"]);
        Eq("""{"UNLIMITED_FUEL": 1, "STREAK_PROTECTION": 0}""", Reply(Make(false).ConnectReply(ME))["inventory"]!["active_services"]);
    }

    [Fact]
    public void TheTutorialConnectCarriesItToo()
    {
        var s = Make(true);
        s.NoteTutorial(ME, "tutorial-session", 1);
        Eq("1", Reply(s.ConnectReply(ME, "tutorial-session"))["inventory"]!["active_services"]!["UNLIMITED_AMMO"]);
    }

    [Fact]
    public void EveryLaunchFillsEveryOwnedAmmunitionStackAndNeverLowersALargerOne()
    {
        Make(true);
        SetStacks(3, 0, 25);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal("10 10 25", Stacks());
    }

    [Fact]
    public void OffALaunchLeavesTheStacksAlone()
    {
        Make(false);
        SetStacks(3, 0, 25);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal("3 0 25", Stacks());
    }

    byte[] Clone(string entries) => state.MoveMultiple2Reply(ME, Form(new JsonArray(), JsonNode.Parse(entries)));

    [Fact]
    public void AClonedBoxIsFullEachTimeWhateverTheRequestSaysAndOnlyOfAnAmmunitionTheAccountOwns()
    {
        Make(true);
        Assert.Equal("null"u8.ToArray(), Clone("""[{"item_guid": "new_item_1", "item_id": 77}]"""));  // an ammunition it does not own yet
        SetStacks(3);
        var reply = Reply(Clone("""[{"item_guid": "new_item_1", "item_id": 77, "units": 9999}, {"item_guid": "new_item_2", "item_id": 77}, {"item_guid": "new_item_3", "item_id": 77}]"""));
        Assert.Equal(3, reply.AsObject().Count);
        Assert.Equal("3 10 10 10", Stacks());
        // an item that is no ammunition (a lure it does not own, a bullet the catalogue does not class) is never created
        Assert.Equal("null"u8.ToArray(), Clone("""[{"item_guid": "new_item_1", "item_id": 19}, {"item_guid": "new_item_2", "item_id": 10}]"""));
        Assert.DoesNotContain(19L, state.Inventory(ME).Select(i => (long)i!["item_id"]!));
    }

    [Fact]
    public void OffNothingIsCloned()
    {
        Make(false);
        SetStacks(3);
        Assert.Equal("null"u8.ToArray(), Clone("""[{"item_guid": "new_item_1", "item_id": 77}]"""));
        Assert.Equal("3", Stacks());
    }

    [Fact]
    public void ARefillByEvent6NeverPassesTheStackSizeOrLowersALargerStack()
    {
        Make(true);
        SetStacks(3, 25);
        var guids = Items().Where(i => (long)i.Value!["item_id"]! == Ammo).Select(i => i.Key).ToArray();
        Hb(O($$"""{"event": 6, "item": "{{guids[0]}}", "quantity": -1000}"""), O($$"""{"event": 6, "item": "{{guids[1]}}", "quantity": -1000}"""),
           O("""{"event": 6, "item": "100", "quantity": -1000}"""));  // 100 has no stack size: nothing to refill
        Assert.Equal("10 25", Stacks());
        Eq("null", Items()["100"]!["units"]);
        Hb(O($$"""{"event": 6, "item": "{{guids[0]}}", "quantity": 4}"""));  // shots still count
        Assert.Equal("6 25", Stacks());
    }

    [Fact]
    public void AmmunitionIsRefusedOnBothRoutesAndTheCatalogueSaysItIsUnlimited()
    {
        var s = Make(true);
        Eq("""{"success": false}""", Reply(s.BuyReply(ME, BuyAmmo)));
        Assert.Null(s.WebBuy(ME, Ammo, false, 1));
        Assert.Null(s.WebBuy(ME, Ammo, false, 1, gms: true));
        Assert.Null(s.WebBuy(ME, 9, true, 1));  // a bundle with an ammunition is not for sale either
        Eq("182.0", s.Wallet(ME)["em"]);
        Eq("33.0", s.Wallet(ME)["gm"]);
        Assert.DoesNotContain(Ammo, s.Inventory(ME).Select(i => (long)i!["item_id"]!));
        var rows = s.StoreCatalogue()["items"]!.AsArray();
        Eq("[false, true]", T(rows.Single(i => (long)i!["id"]! == Ammo)!["buyable"], rows.Single(i => (long)i!["id"]! == Ammo)!["unlimited"]));
        Eq("[true, false]", T(rows.Single(i => (long)i!["id"]! == 19)!["buyable"], rows.Single(i => (long)i!["id"]! == 19)!["unlimited"]));
        Assert.False((bool)s.StoreCatalogue()["bundles"]!.AsArray().Single()!["buyable"]!);
        Eq("""{"price": 170.0, "success": true}""", Reply(s.BuyReply(ME, BuyLure)));  // another item still sells
    }

    [Fact]
    public void OffAmmunitionIsSoldLikeAnyItem()
    {
        var s = Make(false);
        Eq("""{"price": 5.0, "success": true}""", Reply(s.BuyReply(ME, BuyAmmo)));
        Assert.NotNull(s.WebBuy(ME, Ammo, false, 1));
        Assert.Equal("10 10", Stacks());
        var rows = s.StoreCatalogue()["items"]!.AsArray();
        Eq("[true, false]", T(rows.Single(i => (long)i!["id"]! == Ammo)!["buyable"], rows.Single(i => (long)i!["id"]! == Ammo)!["unlimited"]));
    }
}
