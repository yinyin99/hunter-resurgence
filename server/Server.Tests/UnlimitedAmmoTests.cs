using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>`unlimited_ammo`: the connect carries the game's unlimited-ammo service, every hunt starts with every owned ammunition full, a cloned box is full with a guid of its own (never past its stack size, never twice for a placeholder),
/// and no ammunition is sold. Off: ammunition is an item like any other. Item 77 is an ammunition (a box of 10) that is not the free kit's; 19 is a lure.</summary>
public class UnlimitedAmmoTests : StateTestBase
{
    const long Ammo = 77;
    static readonly byte[] BuyAmmo = "item_id=77&amount=1&wallet=GOLD"u8.ToArray(), BuyLure = "item_id=19&amount=1&wallet=GOLD"u8.ToArray();

    State Make(bool on, bool installFit = false)
    {
        var connect = CONNECT;
        connect["inventory"]!["active_services"] = O("""{"UNLIMITED_FUEL": 1, "STREAK_PROTECTION": 0}""");
        var app = APP;
        app["items"]!.AsArray().Add(O("""
            {"id": 77, "name": "Test Cartridge", "description": "", "define": "TEST_CARTRIDGE", "type": 3, "group": 13, "cat": 3, "client_type": 3, "image": "x", "variationname": null,
             "units": {"count": 10, "name": "cartridges"}, "fit": {}, "inventory": {"slots": [0, 1, 8], "capacity": 0, "weight": 1},
             "price": 5, "silver_price": 100, "notbuyable": 0, "subcat": {"id": 0}, "hidden": 0}
            """));
        // a weapon (78) that fits 77, 79 and 80; the game has a definition of 77 and 79 (as the install gives every ammunition) but not of 80
        app["items"]!.AsArray().Add(O("""
            {"id": 78, "name": "Test Rifle", "description": "", "define": "TEST_RIFLE", "type": 1, "group": 1, "cat": 1, "client_type": 1, "image": "x", "variationname": null,
             "units": null, "fit": {"3": [{"id": 77}, {"id": 79}, {"id": 80}]}, "inventory": {"slots": [0, 1, 2, 3, 4], "capacity": 0, "weight": 1},
             "price": 50, "silver_price": 100, "notbuyable": 0, "subcat": {"id": 0}, "hidden": 0}
            """));
        foreach (var id in new[] { 79, 80 })
            app["items"]!.AsArray().Add(O($$"""
                {"id": {{id}}, "name": "Test Cartridge {{id}}", "description": "", "define": "TEST_CARTRIDGE_{{id}}", "type": 3, "group": 13, "cat": 3, "client_type": 3, "image": "x", "variationname": null,
                 "units": {"count": 5, "name": "cartridges"}, "fit": {}, "inventory": {"slots": [0, 1, 8], "capacity": 0, "weight": 1},
                 "price": 5, "silver_price": 100, "notbuyable": 0, "subcat": {"id": 0}, "hidden": 0}
                """));
        app["bundles"] = JsonNode.Parse("""[{"id": 9, "name": "Test Bundle", "price": 100, "items": [19, 77]}]""");
        JsonArray? defs = null;
        if (installFit)  // no recorded `fit` list: the install's definition of the weapon names its ammunition (and an attachment that is none)
        {
            app["items"]!.AsArray().Single(i => (long)i!["id"]! == 78)!["fit"] = new JsonObject();
            defs = (JsonArray)JsonNode.Parse("""
                [{"id": 77, "define": "TEST_CARTRIDGE"}, {"id": 78, "define": "TEST_RIFLE", "extra": {"weapon": {"attachments": ["TEST_SCOPE", "TEST_CARTRIDGE"]}}}]
                """)!;
        }
        return state = new State(Path.Combine(tmp, $"{on}.db"), Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES, itemDefs: defs, sampleInfos: (JsonArray)JsonNode.Parse("""[{"item_id": 77, "units": 10}, {"item_id": 79, "units": 5}]""")!) { UnlimitedAmmo = on };
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
    public void AClonedBoxIsFullWithANewIntegerGuidWhateverTheRequestSaysAndOnlyOfAnAmmunitionTheAccountOwns()
    {
        Make(true);
        Assert.Equal("null"u8.ToArray(), Clone("""[{"item_guid": "new_item_1", "item_id": 77}]"""));  // an ammunition it does not own yet
        SetStacks(3);
        var reply = Reply(Clone("""[{"item_guid": "new_item_1", "item_id": 77, "units": 9999}, {"item_guid": "new_item_2", "item_id": 77}]"""));
        Assert.Equal(["new_item_1", "new_item_2"], reply.AsObject().Select(kv => kv.Key));
        var guids = reply.AsObject().Select(kv => kv.Value is JsonValue v && v.TryGetValue<long>(out var g) ? g : -1).ToArray();  // numbers: the page keeps a text as a placeholder and copies it again
        Assert.Equal(2, guids.Distinct().Count());
        Assert.All(guids, g => Assert.Contains(g.ToString(), Items().Select(kv => kv.Key)));
        Assert.Equal("3 10 10", Stacks());
        // an item that is no ammunition (a lure it does not own, a bullet the catalogue does not class) is never created
        Assert.Equal("null"u8.ToArray(), Clone("""[{"item_guid": "new_item_3", "item_id": 19}, {"item_guid": "new_item_4", "item_id": 10}]"""));
        Assert.DoesNotContain(19L, state.Inventory(ME).Select(i => (long)i!["item_id"]!));
        // a name that is no placeholder of the page creates nothing
        Assert.Equal("null"u8.ToArray(), Clone("""[{"item_guid": "x", "item_id": 77}, {"item_guid": 5, "item_id": 77}, {"item_guid": "new_item_", "item_id": 77}]"""));
        Assert.Equal("3 10 10", Stacks());
    }

    [Fact]
    public void ARepeatedPlaceholderCreatesNothingAgainAndItsSlotIsStillApplied()
    {
        Make(true);
        SetStacks(3);
        var guid = Py.Str(Reply(Clone("""[{"item_guid": "new_item_5", "item_id": 77}]"""))["new_item_5"]);
        // a tab change and a close overlap: the page asks again before it has the answer
        Assert.Equal("null"u8.ToArray(), state.MoveMultiple2Reply(ME, Form(
            JsonNode.Parse("""[{"item_guid": "new_item_5", "slot_id": 9}]""")!, JsonNode.Parse("""[{"item_guid": "new_item_5", "item_id": 77}]"""))));
        Assert.Equal("3 10", Stacks());
        Eq("9", Items()[guid]!["slot"]);
        // and the answered placeholder is a guid from then on
        state.MoveMultiple2Reply(ME, Form(JsonNode.Parse($$"""[{"item_guid": {{guid}}, "slot_id": 1}]""")!));
        Eq("1", Items()[guid]!["slot"]);
    }

    [Fact]
    public void AnAmmunitionHasAtMostAHundredRowsAndAGuidNeverPassesTheGamesNumber()
    {
        Make(true);
        SetStacks(3);
        var entries = string.Join(", ", Enumerable.Range(1, 150).Select(i => $$"""{"item_guid": "new_item_{{i}}", "item_id": 77}"""));
        Assert.Equal(99, Reply(Clone($"[{entries}]")).AsObject().Count);
        Assert.Equal(100, Stacks().Split(' ').Length);
        using (var con = state.Store.Open(ME))
            con.Exec("DELETE FROM items WHERE account_id = $a AND item_id = 77 AND guid > (SELECT MIN(guid) FROM items WHERE account_id = $a AND item_id = 77)");
        using (var con = state.Store.Open(ME))
            con.Exec("UPDATE items SET guid = ? WHERE account_id = $a AND item_id = 77", (long)int.MaxValue);
        Assert.Equal("null"u8.ToArray(), Clone("""[{"item_guid": "new_item_200", "item_id": 77}]"""));
    }

    [Fact]
    public void AMarkedBoxLeftInTheLodgeGoesAtTheNextLaunchAndTheGameIsNeverShownTheMark()
    {
        Make(true);
        SetStacks(3, 4);
        var reply = Reply(state.MoveMultiple2Reply(ME, Form(JsonNode.Parse("""[{"item_guid": "new_item_1", "slot_id": 1}]""")!,
            JsonNode.Parse("""[{"item_guid": "new_item_1", "item_id": 77}, {"item_guid": "new_item_2", "item_id": 77}, {"item_guid": "new_item_3", "item_id": 77}]"""))));
        Assert.Equal("3 4 10 10 10", Stacks());  // the first went to the backpack, the others stayed in the lodge
        Assert.All(Items().Where(i => (long)i.Value!["item_id"]! == Ammo), i => Eq("{}", i.Value!["metadata"]));
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal("10 10 10", Stacks());  // the two stacks it owns stay, and the box in the backpack
        Eq("1", Items()[Py.Str(reply["new_item_1"])]!["slot"]);
    }

    [Fact]
    public void TheMarkIsNeverInTheReadsAndALoneMarkedBoxInTheLodgeStaysAtALaunch()
    {
        Make(true);
        SetStacks(3);
        var guid = long.Parse(Py.Str(Reply(Clone("""[{"item_guid": "new_item_1", "item_id": 77}]"""))["new_item_1"]));
        Assert.DoesNotContain("__clone", System.Text.Encoding.UTF8.GetString(state.InventoryListReply(ME)));
        Assert.DoesNotContain("__clone", System.Text.Encoding.UTF8.GetString(state.InventoryDictReply(ME)));
        using (var con = state.Store.Open(ME))
            con.Exec("DELETE FROM items WHERE account_id = $a AND item_id = 77 AND guid <> ?", guid);  // the only stack in the lodge is a copy
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal("0:10", Held(77));
    }

    [Fact]
    public void ATutorialCreatesNoCopyAndAnswersNull()
    {
        var s = Make(true);
        SetStacks(3);
        s.NoteTutorial(ME, "tutorial-session", 1);
        Assert.Equal("null"u8.ToArray(), Clone("""[{"item_guid": "new_item_1", "item_id": 77}]"""));
        Assert.Equal("3", Stacks());
    }

    /// <summary>The slot and units of each row of an item, oldest first.</summary>
    string Held(long item)
    {
        using var con = state.Store.Open(ME);
        return string.Join(" ", con.Rows("SELECT slot, units FROM items WHERE account_id = $a AND item_id = ? ORDER BY guid", item).Select(r => $"{r[0]}:{r[1]}"));
    }

    void SetSlot(long item, long slot)
    {
        using var con = state.Store.Open(ME);
        con.Exec("UPDATE items SET slot = ? WHERE account_id = $a AND item_id = ?", slot, item);
    }

    [Fact]
    public void EveryLaunchGivesOneFullBoxOfEachAmmunitionThatFitsAnOwnedWeaponButOnlyOneTheGameHasADefinitionOf()
    {
        Make(true);
        Assert.True(state.GrantItems(ME, 78, 1));
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal(("0:10", "0:5", ""), (Held(77), Held(79), Held(80)));  // 80 has no definition: the game would close after the connect
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal(("0:10", "0:5"), (Held(77), Held(79)));  // never twice
        Assert.All(Items().Where(i => (long)i.Value!["item_id"]! is 77 or 79), i => Eq("{}", i.Value!["metadata"]));
    }

    [Fact]
    public void AnEquippedWeaponWithAnEmptyAmmunitionSlotGetsACopyOfItsFirstAmmunitionAndTheLodgeKeepsItsStack()
    {
        Make(true);
        Assert.True(state.GrantItems(ME, 78, 1));
        SetSlot(78, 3);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal(("0:10 9:10", "0:5"), (Held(77), Held(79)));  // the weapon's slot plus 6
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal("0:10 9:10", Held(77));  // the slot is filled now
        Assert.All(Items().Where(i => (long)i.Value!["item_id"]! == 77), i => Eq("{}", i.Value!["metadata"]));
    }

    [Fact]
    public void EveryOwnedAmmunitionHasOneStackInTheLodgeAlsoWhenItsOnlyBoxIsEquippedAndOffNothingIsAdded()
    {
        Make(true);
        Assert.True(state.GrantItems(ME, Ammo, 1));
        SetSlot(Ammo, 8);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal("8:10 0:10", Held(Ammo));
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal("8:10 0:10", Held(Ammo));  // never twice
        Make(false);
        Assert.True(state.GrantItems(ME, Ammo, 1));
        SetSlot(Ammo, 8);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal("8:10", Held(Ammo));
    }

    [Fact]
    public void TheWeaponAmmunitionRuleNeedsNoRecordedFitList()
    {
        Make(true, installFit: true);
        Assert.True(state.GrantItems(ME, 78, 1));
        SetSlot(78, 3);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal(("0:10 9:10", ""), (Held(77), Held(79)));  // the install names 77 only
    }

    [Fact]
    public void AnEquippedWeaponWithAmmunitionInItsSlotIsLeftAloneAndOffNothingIsGranted()
    {
        Make(true);
        Assert.True(state.GrantItems(ME, 78, 1));
        Assert.True(state.GrantItems(ME, 79, 1));
        SetSlot(78, 2);
        SetSlot(79, 8);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal(("0:10", "8:5 0:5"), (Held(77), Held(79)));  // the equipped box is not the lodge's: the lodge gets its own stack
        Make(false);
        Assert.True(state.GrantItems(ME, 78, 1));
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Assert.Equal(("", ""), (Held(77), Held(79)));
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
