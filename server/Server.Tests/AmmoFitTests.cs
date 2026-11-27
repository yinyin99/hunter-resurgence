using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>#1009: an ammunition never sits in the ammunition slot (weapon slot + 6) of a weapon the catalogue says it does not fit: not after a restore, not from the start kit, not from a loadout,
/// and `admin ammo-fit` moves the ones already there to the lodge. 701 is a bow that fits the arrows 711, 702 a rifle that fits the slugs 712.</summary>
public class AmmoFitTests : StateTestBase
{
    const long Bow = 701, Rifle = 702, Arrows = 711, Slugs = 712;

    static JsonObject Catalogue()
    {
        var app = APP;
        foreach (var (id, cat, fits) in new[] { (Bow, 1, Arrows), (Rifle, 1, Slugs), (Arrows, 3, 0L), (Slugs, 3, 0L) })
        {
            var fit = fits == 0 ? "{}" : "{\"3\": [{\"id\": " + fits + "}]}";
            app["items"]!.AsArray().Add(O($$"""
                {"id": {{id}}, "name": "Test Item {{id}}", "description": "", "define": "TEST_ITEM_{{id}}", "type": {{cat}}, "group": 1, "cat": {{cat}}, "client_type": 1, "image": "x", "variationname": null,
                 "units": null, "fit": {{fit}}, "inventory": {"slots": [0, 1, 2, 3, 4, 8, 9, 10], "capacity": 0, "weight": 1},
                 "price": 5, "silver_price": 100, "notbuyable": 0, "ownMultiple": true, "subcat": {"id": 0}, "hidden": 0}
                """));
        }
        return app;
    }

    static string Start(params (long Item, int Slot)[] kit)
    {
        var connect = CONNECT;
        connect["inventory"] = new JsonObject
        {
            ["items"] = new JsonArray(kit.Select((k, i) => (JsonNode?)O($$"""{"item_id": {{k.Item}}, "slot": {{k.Slot}}, "guid": "{{i + 1}}", "metadata": {}, "units": null}""")).ToArray()),
            ["infos"] = new JsonArray(), ["active_services"] = new JsonObject(),
        };
        return Py.Dumps(connect);
    }

    State Make(string? start = null, bool unlimited = false) =>
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, Catalogue(), LOCALE, TEXTURES, offline: false, startBody: start is null ? null : Encoding.UTF8.GetBytes(start)) { UnlimitedAmmo = unlimited };

    void Give(long account, params (long Guid, long Item, long Slot)[] rows)
    {
        using var con = state.Store.Open(account);
        con.Begin();
        foreach (var (guid, item, slot) in rows)
            PlayerRows.AddItem(con, guid, item, slot, null, []);
        con.Commit();
    }

    /// <summary>{guid: slot} of the account's rows with a guid from 900.</summary>
    string Slots(long account = ME)
    {
        using var con = state.Store.Open(account);
        return string.Join(" ", con.Rows("SELECT guid, slot FROM items WHERE account_id = $a AND guid >= 900 AND item_id IN (701, 702, 711, 712) ORDER BY guid").Select(r => $"{r[0]}:{r[1]}"));
    }

    // ---- restore

    static string Rows(Db con, string where) => string.Join(" ", con.Rows($"SELECT guid, slot FROM items WHERE account_id = $a AND {where} ORDER BY guid").Select(r => $"{r[0]}:{r[1]}"));

    [Fact]
    public void TestARestoreOfAnOlderAndALaterReplyWithAnotherWeaponInTheSameSlotLeavesOneFittingPair()
    {
        Make();
        // the older list: the bow (1) in slot 2 and its arrows (2) in slot 8; the later reply: another weapon, the rifle (3), in slot 2 as well and its slugs (4) in slot 8
        var zip = TestZip.Own()
            .Replace("Inventory_dict.json", """
                {"702": [{"id": 3, "slot": 2, "units": null, "metadata": {}, "location": null}],
                 "712": [{"id": 4, "slot": 8, "units": null, "metadata": {}, "location": null}]}
                """)
            .Reply("Inventory_list.json", "/v1/Inventory/list", """
                [{"item": {"id": 701}, "own": [{"id": 1, "slot": 2, "units": null, "metadata": {}, "location": null}]},
                 {"item": {"id": 711}, "own": [{"id": 2, "slot": 8, "units": null, "metadata": {}, "location": null}]}]
                """, ("user_id", "1001"));
        var result = Restorer.Import(state, zip.Write(tmp), null, tmp);
        using var con = state.Store.Open(1001);
        Assert.Equal("1:0 2:0 3:2 4:8", Rows(con, "guid <= 4"));  // the later weapon and its ammunition are equipped, the other pair is in the lodge
        Assert.Contains("put in the lodge: 2", result.Report);  // each once: the bow and the arrows
        Assert.Empty(state.Misfits(con));
    }

    [Fact]
    public void TestARestoreOfTwoInventoryRepliesKeepsTheAmmunitionThatFitsTheWeaponBesideIt()
    {
        Make();
        // the bow (1) in slot 2 with its arrows (2); the rifle (3) in slot 3 with slugs (4) and slugs (5) bought for the bow's slot
        var zip = TestZip.Own()
            .Replace("Inventory_dict.json", """
                {"701": [{"id": 1, "slot": 0, "units": null, "metadata": {}, "location": null}],
                 "711": [{"id": 2, "slot": 0, "units": null, "metadata": {}, "location": null}],
                 "702": [{"id": 3, "slot": 3, "units": null, "metadata": {}, "location": null}],
                 "712": [{"id": 4, "slot": 9, "units": null, "metadata": {}, "location": null}, {"id": 5, "slot": 8, "units": null, "metadata": {}, "location": null}]}
                """)
            .Reply("Inventory_list.json", "/v1/Inventory/list", """
                [{"item": {"id": 701}, "own": [{"id": 1, "slot": 2, "units": null, "metadata": {}, "location": null}]},
                 {"item": {"id": 711}, "own": [{"id": 2, "slot": 8, "units": null, "metadata": {}, "location": null}]}]
                """, ("user_id", "1001"));
        var result = Restorer.Import(state, zip.Write(tmp), null, tmp);
        using var con = state.Store.Open(1001);
        Assert.Equal("1:2 2:8 3:3 4:9 5:0", Rows(con, "guid <= 5"));  // the slugs 5 are in the lodge
        Assert.Contains("put in the lodge: 1", result.Report);
        Assert.Empty(state.Misfits(con));
    }

    [Fact]
    public void TestTheStartKitAddedByARestoreNeverPutsAnAmmunitionUnderAWeaponItDoesNotFit()
    {
        Make(Start((Slugs, 8), (Arrows, 9)));  // the kit's ammunition is in the ammunition slots of the bow (slot 2) and the rifle (slot 3), the other's
        var zip = TestZip.Own().Replace("Inventory_dict.json", """
            {"701": [{"id": 1, "slot": 2, "units": null, "metadata": {}, "location": null}], "702": [{"id": 2, "slot": 3, "units": null, "metadata": {}, "location": null}]}
            """);
        Restorer.Import(state, zip.Write(tmp), null, tmp);
        using var con = state.Store.Open(1001);
        Assert.Equal("711:0 712:0", string.Join(" ", con.Rows("SELECT item_id, slot FROM items WHERE account_id = $a AND item_id IN (711, 712) ORDER BY item_id").Select(r => $"{r[0]}:{r[1]}")));
        Assert.Empty(state.Misfits(con));
    }

    [Fact]
    public void TestARestoredAmmunitionUnderAnEmptySlotIsJudgedByTheWeaponTheStartKitPutsThere()
    {
        Make(Start((Bow, 2)));
        var zip = TestZip.Own().Replace("Inventory_dict.json", """
            {"712": [{"id": 1, "slot": 8, "units": null, "metadata": {}, "location": null}]}
            """);
        Restorer.Import(state, zip.Write(tmp), null, tmp);
        using var con = state.Store.Open(1001);
        Assert.Equal("1:0", Rows(con, "item_id = 712"));  // the slugs do not fit the bow the kit gave slot 2
        Assert.Equal(2, (long)con.Scalar("SELECT slot FROM items WHERE account_id = $a AND item_id = 701")!);
        Assert.Empty(state.Misfits(con));
    }

    [Fact]
    public void TestTheStartUpKitTopUpJudgesTheEquippedSlotsAfterAddingTheKit()
    {
        var start = Start((Bow, 2));
        Make(start);
        state.CreateAccount(8, "other");
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            con.Exec("DELETE FROM items WHERE account_id = $a");
            PlayerRows.AddItem(con, 900, Slugs, 8, null, []);  // under the slot 2 the kit is about to fill
            con.Exec("UPDATE accounts SET start_kit = 0 WHERE id = 8");
            con.Commit();
        }
        Make(start);  // a start: the account's kit is older than the facts file's
        Assert.Equal("900:0 901:2", Slots(8));
    }

    // ---- loadout

    long Loadout(params string[] entries)
    {
        using var con = state.Store.Open(ME);
        con.Exec("INSERT INTO loadouts (account_id, name, color, entries) VALUES ($a, 'mine', 0, ?)", $"[{string.Join(", ", entries)}]");
        return (long)con.Scalar("SELECT MAX(id) FROM loadouts WHERE account_id = $a")!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TestALoadoutNeverPutsAnAmmunitionUnderAWeaponItDoesNotFit(bool unlimited)
    {
        Make(unlimited: unlimited);
        Give(ME, (901, Bow, 0), (902, Rifle, 0), (903, Arrows, 0), (904, Slugs, 0));
        // the ammunition entries come first: the slugs for the bow's slot, the arrows for the rifle's
        var id = Loadout("""{"slot_id": 8, "item_id": 712}""", """{"slot_id": 9, "item_id": 711}""", """{"slot_id": 2, "item_id": 701}""", """{"slot_id": 3, "item_id": 702}""");
        state.NoteLaunch(ME, Encoding.ASCII.GetBytes($"start=100&reserve=2&loadout_id={id}"));
        using var con = state.Store.Open(ME);
        Assert.Empty(state.Misfits(con));
        Assert.Equal(2, (long)con.Scalar("SELECT COUNT(*) FROM items WHERE account_id = $a AND slot IN (2, 3)")!);
        Assert.Equal(0, (long)con.Scalar("SELECT slot FROM items WHERE account_id = $a AND guid = 903")!);  // never placed by the loadout
        Assert.Equal(0, (long)con.Scalar("SELECT slot FROM items WHERE account_id = $a AND guid = 904")!);
    }

    [Fact]
    public void TestALoadoutPutsFittingAmmunitionInItsSlots()
    {
        Make();
        Give(ME, (901, Bow, 0), (902, Rifle, 0), (903, Arrows, 0), (904, Slugs, 0));
        var id = Loadout("""{"slot_id": 8, "item_id": 711}""", """{"slot_id": 9, "item_id": 712}""", """{"slot_id": 2, "item_id": 701}""", """{"slot_id": 3, "item_id": 702}""");
        state.NoteLaunch(ME, Encoding.ASCII.GetBytes($"start=100&reserve=2&loadout_id={id}"));
        Assert.Equal("901:2 902:3 903:8 904:9", Slots());
    }

    // ---- admin ammo-fit

    [Fact]
    public void TestTheOneOffListsTheNonFittingPairsMovesTheStacksToTheLodgeAndAnotherRunFindsNone()
    {
        Make();
        state.CreateAccount(8, "other");
        state.CreateAccount(9, "third");
        Give(ME, (901, Bow, 2), (902, Slugs, 8), (903, Rifle, 3), (904, Slugs, 9), (905, Arrows, 10));  // 902 is the one: the slugs do not fit the bow beside them; 905 has no weapon beside it, so no verdict
        Give(8, (901, Bow, 2), (902, Arrows, 8), (903, Rifle, 4), (904, Arrows, 10));  // 904 under the rifle (slot 4)
        Give(9, (901, Bow, 2), (902, Rifle, 2), (903, Arrows, 8));  // two weapons in slot 2 (a restore's merge): the arrows fit one of them and are listed and moved once
        var before = Slots() + " | " + Slots(8) + " | " + Slots(9);

        var dry = state.MoveMisfitAmmo(apply: false);
        Assert.Equal(3, dry.Count - 1);
        Assert.Contains("account 7 slot 8: weapon 701", dry[0]);
        Assert.Contains("ammunition 712", dry[0]);
        Assert.Contains("account 8 slot 10: weapon 702", dry[1]);
        Assert.Contains("account 9 slot 8: weapon 702", dry[2]);
        Assert.Equal("3 stack(s) to move: dry run, nothing written; add --apply to write", dry[^1]);
        Assert.Equal(before, Slots() + " | " + Slots(8) + " | " + Slots(9));  // nothing written

        var applied = state.MoveMisfitAmmo(apply: true);
        Assert.Equal(3, applied.Count - 1);
        Assert.Equal("3 stack(s) moved", applied[^1]);
        Assert.Equal("901:2 902:0 903:3 904:9 905:10", Slots());
        Assert.Equal("901:2 902:8 903:4 904:0", Slots(8));  // the stacks are whole, in the lodge (slot 0)
        Assert.Equal("901:2 902:2 903:0", Slots(9));
        Assert.Equal(new[] { "0 stack(s) moved" }, state.MoveMisfitAmmo(apply: true));
        Assert.Equal(new[] { "0 stack(s) to move: dry run, nothing written; add --apply to write" }, state.MoveMisfitAmmo(apply: false));
    }
}
