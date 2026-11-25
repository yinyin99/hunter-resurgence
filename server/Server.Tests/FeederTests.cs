using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Bait feeders: the model of the fields (fresh fill, warm-up, top-up, last unit, penalty), the refill by event 23 and by Inventory/refillBarrel, and the backfill of feeders refilled before the rule.</summary>
public class FeederTests : StateTestBase
{
    const long Start = 1_800_000_000, Barrel = 300, Bait = 301, Pack = 302, Hour = 3600, Day = 86400;

    static JsonObject Catalogue()
    {
        var app = APP;
        app["items"]!.AsArray().Add(O("""{"id": 136, "name": "Test Barrel", "define": "TEST_BARREL", "type": 12, "bait_item": "TEST_BAIT", "units": null}"""));
        app["items"]!.AsArray().Add(O("""{"id": 137, "name": "Test Bait", "define": "TEST_BAIT", "type": 4, "units": {"count": 50, "name": "uses"}}"""));
        app["items"]!.AsArray().Add(O("""{"id": 787, "name": "Test Refill Pack", "define": "TEST_PACK", "type": 38, "units": {"count": 25, "name": "units"}, "price": 105, "silver_price": 1785, "notbuyable": 0}"""));
        app["items"]!.AsArray().Add(O("""{"id": 791, "name": "Bait Site Refill", "define": "FEEDER_REFILL", "type": 37, "units": {"count": 1, "name": "uses"}, "price": 5, "silver_price": 85, "notbuyable": 0, "sellable": null}"""));
        foreach (var item in app["items"]!.AsArray().OfType<JsonObject>().Where(i => (long)i["id"]! is 136 or 137 or 787 or 791))  // what a built item definition reads
        {
            item["fit"] = new JsonObject();
            item["inventory"] = O("""{"slots": [0, 1], "capacity": 0, "weight": 1}""");
            item["group"] = 1;
            item["cat"] = 4;
            item["client_type"] = 4;
            item["image"] = "test_image";
            item["description"] = "Test.";
            item["variationname"] = null;
        }
        return app;
    }

    State Open(double clock) => new(Db, Py.DumpBytes(CONNECT), BASE, ICON, Catalogue(), LOCALE, TEXTURES) { Clock = () => clock };

    public FeederTests()
    {
        state = Open(Start);
    }

    /// <summary>A feeder's metadata after a refill to `amount` at `at`.</summary>
    static JsonObject Filled(long amount, long at, JsonObject? md = null)
    {
        md ??= new JsonObject();
        foreach (var (key, value) in Feeders.Refill(md, amount, at)!)
            md[key] = value!.DeepClone();
        return md;
    }

    /// <summary>(status, amount, attractiveness, penalty_remaining) of the row at t.</summary>
    static JsonArray At(JsonObject md, long t)
    {
        var row = (JsonObject)md.DeepClone();
        Feeders.Refresh(row, t);
        return Shown(row);
    }

    static JsonArray Shown(JsonNode? md) => T(md!["feeder_status"], md["bait_amount"], md["bait_attractiveness"], md["penalty_remaining"]);

    [Fact]
    public void AFreshFillIsInactiveFor4HoursThenActiveAndLosesTenAUnitsADay()
    {
        var md = Filled(50, Start);
        Eq("""["Inactive", 50, 0, 0]""", At(md, Start));
        Eq("""["Inactive", 50, 0, 0]""", At(md, Start + 8639));
        Eq("""["Inactive", 49, 0, 0]""", At(md, Start + 4 * Hour - 1));  // one unit every 8,640 s
        Eq("""["Active", 49, 100, 0]""", At(md, Start + 4 * Hour));
        Eq("""["Active", 40, 100, 0]""", At(md, Start + Day + 1));
        Eq("""[0, "Inactive"]""", T(md["bait_attractiveness_org"], md["feeder_status"]));  // the stored fields are those of the fill: empty before, so no attractiveness
        Eq($"[{Start}, {Start}, {Start}, 50]", T(md["start_bait_ts"], md["start_attr_ts"], md["delay_start_ts"], md["bait_amount_org"]));
    }

    [Fact]
    public void ATopUpOfAnActiveFeederKeepsItActive()
    {
        var md = Filled(50, Start);
        var later = Start + Day + 1;
        Eq("""["Active", 40, 100, 0]""", At(md, later));
        Filled(50, later, md);
        Eq("""["Active", 50, 100, 0]""", At(md, later));  // no new warm-up
        Eq($"[{Start}, {later}, 100, 50]", T(md["delay_start_ts"], md["start_bait_ts"], md["bait_attractiveness_org"], md["bait_amount_org"]));
        Eq("""["Active", 49, 100, 0]""", At(md, later + 8640));
    }

    [Fact]
    public void TheLastUnitIsActiveAndTheFeederEmptiesAfterIt()
    {
        var md = Filled(50, Start);
        Eq("""["Active", 1, 100, 0]""", At(md, Start + 49 * 8640));
        Eq("""["Inactive", 0, 0, 0]""", At(md, Start + 50 * 8640));
        Eq("""["Inactive", 0, 0, 0]""", At(md, Start + 90 * 8640));
    }

    [Fact]
    public void AFillOfAnEmptyFeederRestartsTheWarmUp()
    {
        var md = Filled(50, Start);
        var empty = Start + 60 * 8640;
        Filled(30, empty, md);
        Eq($"[{empty}, 0]", T(md["delay_start_ts"], md["bait_attractiveness_org"]));
        Eq("""["Inactive", 30, 0, 0]""", At(md, empty + 8639));
        Eq("""["Inactive", 29, 0, 0]""", At(md, empty + 4 * Hour - 1));
        Eq("""["Active", 29, 100, 0]""", At(md, empty + 4 * Hour));
    }

    [Fact]
    public void AShotPutsAFeederInPenaltyFor30Minutes()
    {
        var md = Filled(50, Start);
        var shot = Start + 20000;
        md["shot_ts"] = shot;
        Eq("""["Penalty", 48, 0, 20]""", At(md, shot + 600));  // 20 minutes left
        Eq("""["Penalty", 48, 0, 1]""", At(md, shot + 1799));
        Eq("""["Active", 48, 100, 0]""", At(md, shot + 1800));
        Eq("""["Inactive", 0, 0, 0]""", At(md, Start + 60 * 8640 + 10));
        md["shot_ts"] = Start + 60 * 8640;
        Eq("""["Inactive", 0, 0, 0]""", At(md, Start + 60 * 8640 + 10));  // an empty feeder is not in penalty
    }

    [Fact]
    public void BaitThatIsNotAboveWhatIsLeftIsNoRefill()
    {
        var md = Filled(50, Start);
        Assert.Null(Feeders.Refill(md, 40, Start + Day + 1));  // 40 left: the game reports bait eaten too
        Assert.Null(Feeders.Refill(md, 10, Start + Day + 1));
        Assert.Null(Feeders.Refill(new JsonObject(), 0, Start));
        Assert.NotNull(Feeders.Refill(md, 41, Start + Day + 1));
    }

    [Fact]
    public void ARowWithoutTheModelsInputsIsLeftAsItIs()
    {
        var md = O("""{"reserve": 1, "bait_amount": 5, "feeder_status": "Active"}""");
        Feeders.Refresh(md, Start);
        Eq("""{"reserve": 1, "bait_amount": 5, "feeder_status": "Active"}""", md);
    }

    void Place(JsonObject md, long guid = Barrel)
    {
        using var con = state.Store.Open(ME);
        PlayerRows.AddItem(con, guid, 136, 23, null, md);
    }

    JsonObject Meta(long guid = Barrel) => Items()[guid.ToString()]!["metadata"]!.AsObject();

    JsonNode Listed(long guid, bool dict)
    {
        var own = dict
            ? Reply(state.InventoryDictReply(ME)).AsObject().SelectMany(kv => kv.Value!.AsArray())
            : Reply(state.InventoryListReply(ME)).AsArray().SelectMany(g => g!["own"]!.AsArray());
        return own.Single(o => (long)o!["id"]! == guid)!["metadata"]!;
    }

    // the broken state of the report: refilled to 50, the other fields still those of the old, empty, inactive feeder
    const string Broken = """
        {"reserve": 1, "bait_amount": 50, "bait_amount_org": 20, "start_bait_ts": 1000, "start_attr_ts": 1000, "delay_start_ts": 1000,
         "bait_attractiveness_org": 0, "bait_attractiveness": 0, "feeder_status": "Inactive", "penalty_remaining": 0}
        """;

    [Fact]
    public void BaitAddedInTheHuntRestartsTheFeederAndEveryReplyShowsItAtItsOwnTime()
    {
        Place(O(Broken));
        Eq("""["Inactive", 0, 0, 0]""", Shown(Meta()));  // the old fields: empty
        Hb(O("""{"event": 23, "item": "300", "metadata": {"bait_amount": 50}}"""));
        Eq("""["Inactive", 50, 0, 0]""", Shown(Meta()));
        state.Clock = () => Start + 4 * Hour;
        Eq("""["Active", 49, 100, 0]""", Shown(Meta()));
        Eq("""["Active", 49, 100, 0]""", Shown(Listed(Barrel, dict: false)));
        Eq("""["Active", 49, 100, 0]""", Shown(Listed(Barrel, dict: true)));
        Eq("""["Active", 49, 100, 0]""", Shown(state.Inventory(ME).Single(i => (string?)i!["guid"] == "300")!["metadata"]));
        Eq($"[{Start}, {Start}, 50, 0]", T(Meta()["start_bait_ts"], Meta()["delay_start_ts"], Meta()["bait_amount_org"], Meta()["bait_attractiveness_org"]));
        // bait added to a feeder that still has some: the amount restarts, the warm-up is over for good
        Hb(O("""{"event": 23, "item": "300", "metadata": {"bait_amount": 50}}"""));
        Eq("""["Active", 50, 100, 0]""", Shown(Meta()));
        Eq($"[{Start}, {Start + 4 * Hour}]", T(Meta()["delay_start_ts"], Meta()["start_bait_ts"]));
        // bait eaten (a lower amount) changes nothing
        Hb(O("""{"event": 23, "item": "300", "metadata": {"bait_amount": 10}}"""));
        Eq("""["Active", 50, 100, 0]""", Shown(Meta()));
    }

    [Fact]
    public void ABaitAmountOnAnItemThatIsNotAFeederIsMerged()
    {
        Hb(O("""{"event": 23, "item": "100", "metadata": {"bait_amount": 7}}"""));
        Eq("""{"bait_amount": 7}""", Items()["100"]!["metadata"]);
    }

    [Fact]
    public void BaitAddedToAFeederNeverFilledSetsItsFields()
    {
        Place(O("""{"reserve": 1, "bait_attractiveness": 0}"""));
        Hb(O("""{"event": 23, "item": "300", "metadata": {"bait_amount": 10}}"""));
        Eq("""["Inactive", 10, 0, 0]""", Shown(Meta()));
        Eq($"[10, {Start}, {Start}]", T(Meta()["bait_amount_org"], Meta()["start_bait_ts"], Meta()["delay_start_ts"]));
        Eq("1", Meta()["reserve"]);
    }

    [Fact]
    public void AFeederRefilledBeforeTheRuleIsRecomputedAtTheNextStartFromItsRefillEvents()
    {
        Place(O(Broken));
        using (var con = state.Store.Open(ME))
            foreach (var (at, amount) in new[] { (Start, 30), (Start + 60, 40), (Start + 120, 50) })  // the game's three refills, which the server stored as `bait_amount` alone
                con.Exec("INSERT INTO events (account_id, event, body, ts) VALUES ($a, 23, ?, ?)", """{"event": 23, "item": "300", "metadata": {"bait_amount": """ + amount + "}}", (double)at + 0.5);
        for (var run = 0; run < 2; run++)  // the second start changes nothing
        {
            var restarted = Open(Start + 4 * Hour);
            var md = Items(restarted)["300"]!["metadata"]!;
            Eq("""["Active", 49, 100, 0]""", Shown(md));  // warm-up from the first add, bait from the last
            Eq($"[{Start}, {Start + 120}, 50, 100]", T(md["delay_start_ts"], md["start_bait_ts"], md["bait_amount_org"], md["bait_attractiveness_org"]));
        }
    }

    [Fact]
    public void ARefillMadeAfterTheRuleIsLeftAloneByTheBackfill()
    {
        Place(O(Broken));
        Hb(O("""{"event": 23, "item": "300", "metadata": {"bait_amount": 50}}"""));
        var before = Meta().ToJsonString();
        var restarted = Open(Start);
        Assert.Equal(before, Items(restarted)["300"]!["metadata"]!.ToJsonString());
    }

    void Stock(long units = 20, long amount = 500)
    {
        using var con = state.Store.Open(ME);
        PlayerRows.AddItem(con, Bait, 137, 0, units, new JsonObject());
        con.Exec("UPDATE wallet SET amount = ? WHERE account_id = $a AND currency = 'gm'", amount);
    }

    byte[] Refill(string currency, long max = 50, long barrel = Barrel) =>
        state.RefillBarrelReply(ME, Encoding.UTF8.GetBytes($$"""{"oauth_access_token": "x", "barrel_id": {{barrel}}, "currency": "{{currency}}", "max_amount": {{max}}}"""));

    [Fact]
    public void TheBaitSitesRefillMovesTheBaitIntoTheFeederAndCharges()
    {
        Place(O(Broken));
        Stock();
        var reply = Reply(Refill("silver"));
        Eq($$$"""{"price": 85.0, "success": true, "metadata": {"start_attr_ts": {{{Start}}}, "bait_attractiveness": 0, "feeder_status": "Inactive", "bait_amount": 20, "start_bait_ts": {{{Start}}}, "bait_amount_org": 20}}""", reply);
        Eq("""["Inactive", 20, 0, 0]""", Shown(Meta()));
        Eq($"[{Start}, 0]", T(Meta()["delay_start_ts"], Meta()["bait_attractiveness_org"]));
        Eq("415", state.Wallet(ME)["gm"]);
        using (var con = state.Store.Open(ME))
            Eq("[0, 0]", T(Py.FromSql(con.Scalar("SELECT units FROM items WHERE account_id = $a AND guid = 301")), Py.FromSql(con.Scalar("SELECT slot FROM items WHERE account_id = $a AND guid = 301"))));
        Eq("""{"success": false}""", Reply(Refill("silver")));  // no bait left
        Eq("415", state.Wallet(ME)["gm"]);
    }

    [Fact]
    public void ARefillPackPaysOneUseAndAFeederThatHasBaitKeepsItsWarmUp()
    {
        Place(Filled(30, Start - Day));
        using (var con = state.Store.Open(ME))
            PlayerRows.AddItem(con, Pack, 787, 0, 2, new JsonObject());
        Stock(units: 100);
        var reply = Reply(Refill("pack", max: 45));
        Eq("""[0.0, true, "Active", 45]""", T(reply["price"], reply["success"], reply["metadata"]!["feeder_status"], reply["metadata"]!["bait_amount"]));  // 20 left, 25 added
        using (var con = state.Store.Open(ME))
            Eq("[1, 75]", T(Py.FromSql(con.Scalar("SELECT units FROM items WHERE account_id = $a AND guid = 302")), Py.FromSql(con.Scalar("SELECT units FROM items WHERE account_id = $a AND guid = 301"))));
        Eq("500", state.Wallet(ME)["gm"]);
    }

    [Fact]
    public void ARefillThatCannotBePaidOrIsNotAFeederChangesNothing()
    {
        Place(O(Broken));
        Stock(amount: 84);
        Eq("""{"success": false}""", Reply(Refill("silver")));  // 85 gm$ asked
        Eq("""{"success": false}""", Reply(Refill("pack")));  // no pack
        Eq("""{"success": false}""", Reply(Refill("bitcoin")));
        Eq("""{"success": false}""", Reply(Refill("silver", max: 0)));
        Eq("""{"success": false}""", Reply(Refill("silver", barrel: Bait)));  // not a feeder
        Eq("""{"success": false}""", Reply(state.RefillBarrelReply(ME, "not json"u8.ToArray())));
        Eq("""["Inactive", 0, 0, 0]""", Shown(Meta()));
        using var con = state.Store.Open(ME);
        Eq("[20, 84.0]", T(Py.FromSql(con.Scalar("SELECT units FROM items WHERE account_id = $a AND guid = 301")), Py.FromSql(con.Scalar("SELECT amount FROM wallet WHERE account_id = $a AND currency = 'gm'"))));
    }
}
