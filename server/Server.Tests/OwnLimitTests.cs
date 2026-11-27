using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The ownership limits (facts file `own_limits`, `State.OwnLimit`): each kind's limit at purchase (the store, `Inventory/buy`), at restore, in the start kit and in admin grants, the price and gift of a bundle
/// whose items the player owns, `unlimited_fuel`, and the pass at start. Items from 2000 up are the test's own, with the client type the original gives the kind.</summary>
public class OwnLimitTests : StateTestBase
{
    static JsonObject Item(long id, long clientType, bool multiple, long price = 10, int cat = 1) => O($$"""
        {"id": {{id}}, "name": "Item {{id}}", "description": "", "define": "TEST_{{id}}", "type": 41, "group": 13, "cat": {{cat}}, "client_type": {{clientType}}, "image": "x", "variationname": null,
         "units": null, "fit": {}, "inventory": {"slots": [0, 1], "capacity": 0, "weight": 1},
         "price": {{price}}, "silver_price": 100, "notbuyable": 0, "ownMultiple": {{(multiple ? "true" : "false")}}, "subcat": {"id": 0}, "hidden": 0}
        """);

    // (client type, what the original catalogue marks, whether the player owns one at most): the issue's table
    static readonly (long Type, bool Multiple, bool Limited)[] Kinds =
    [
        (1, true, true), (22, true, true), (0, false, true), (4, false, true), (43, true, true),
        (7, true, true), (8, true, true), (9, true, true), (10, true, true), (11, true, true), (34, true, true), (35, true, true), (36, true, true),
        (15, true, true), (21, true, true), (39, true, true), (31, true, true), (40, true, true), (24, true, true), (6, true, true),
        (42, true, true), (26, true, true), (27, true, true), (29, true, true), (30, true, true),
        (2, true, false), (2, false, false), (3, true, false), (25, true, false), (25, false, false), (12, true, false), (32, true, false),
        (14, true, false), (28, true, false), (38, true, false), (0, true, false), (4, true, false),
    ];

    // the horn is marked multiple but limited to 1, the Electronic Caller is of a limited type and not limited
    const long Horn = 619, ECaller = 792, Fuel25 = 953, Fuel50 = 954, Fuel100 = 955;

    State Make(IEnumerable<JsonObject> catalogue, JsonArray? bundles = null, bool ammo = false, bool fuel = false, JsonObject? connect = null, string name = "state.db")
    {
        var app = APP;
        foreach (var i in catalogue)
            app["items"]!.AsArray().Add(i);
        if (bundles is not null)
            app["bundles"] = bundles;
        state = new State(Path.Combine(tmp, name), Py.DumpBytes(connect ?? CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false) { UnlimitedAmmo = ammo, UnlimitedFuel = fuel };
        using var con = state.Store.Open(ME);
        State.AddCurrency(con, "em", 1_000_000, "test");
        return state;
    }

    static IEnumerable<JsonObject> KindItems() =>
        Kinds.Select((k, n) => Item(2000 + 2 * n, k.Type, k.Multiple)).Concat(Kinds.Select((k, n) => Item(2001 + 2 * n, k.Type, k.Multiple)))
             .Append(Item(Horn, 0, true)).Append(Item(ECaller, 43, true));

    bool Web(long id) => state.WebBuy(ME, id, false, 1) is not null;

    bool Game(long id) => !state.BuyReply(ME, System.Text.Encoding.ASCII.GetBytes($"item_id={id}&amount=1&wallet=GOLD")).SequenceEqual(State.BUY_FAILED);

    double Em() => (double)state.Wallet(ME)["em"]!;

    long Owned(long item, long account = ME)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar("SELECT COUNT(*) FROM items WHERE account_id = $a AND item_id = ?", item)!;
    }

    [Fact]
    public void EachKindHasItsLimitAtPurchaseInTheStoreAndInInventoryBuy()
    {
        Make(KindItems());
        var wrong = new List<string>();
        for (var n = 0; n < Kinds.Length; n++)
        {
            var (type, multiple, limited) = Kinds[n];
            foreach (var (what, buy, id) in new (string, Func<long, bool>, long)[] { ("store", i => Web(i), 2000 + 2 * n), ("Inventory/buy", i => Game(i), 2001 + 2 * n) })
            {
                var (first, second) = (buy(id), buy(id));
                if (!first || second == limited)
                    wrong.Add($"{what}: client type {type}, catalogue {(multiple ? "multiple" : "one")}: first {first}, second {second}, limited {limited}");
            }
        }
        // the Hunting Horn is limited to 1 although the catalogue marks it multiple, the Electronic Caller is not limited although its type is
        if (!Web(Horn) || Web(Horn)) wrong.Add("Hunting Horn");
        if (!Web(ECaller) || !Web(ECaller)) wrong.Add("Electronic Caller");
        Assert.Empty(wrong);
    }

    [Fact]
    public void ARefusedPurchaseChargesNothing()
    {
        Make(KindItems());
        Assert.True(Web(2000));  // a rifle (client type 1)
        var before = Em();
        Assert.False(Web(2000));
        Assert.False(Game(2000));
        Assert.Equal(before, Em());
        Assert.Equal(1, Owned(2000));
    }

    [Fact]
    public void ACatalogueLimitStillCapsWhatTheTableLeavesUnlimited()
    {
        var capped = Item(2500, 2, true);
        capped["ownLimit"] = 2;
        Make([capped]);
        Assert.True(Web(2500));
        Assert.True(Web(2500));
        Assert.False(Web(2500));
    }

    [Fact]
    public void TheStoreListsTheLimitOfEachItem()
    {
        Make(KindItems());
        var items = state.StoreCatalogue()["items"]!.AsArray().ToDictionary(i => (long)i!["id"]!, i => (long)i!["limit"]!);
        Assert.Equal((1L, 0L), (items[2000], items[2000 + 2 * Array.FindIndex(Kinds, k => k.Type == 2)]));  // a rifle, a scope
        Assert.Equal(1L, items[Horn]);
        Assert.Equal(0L, items[ECaller]);
    }

    [Fact]
    public void AdminGrantsRefuseWhatWouldPutTheAccountOverTheLimit()
    {
        Make(KindItems());
        Assert.True(state.GrantItems(ME, 2000));
        Assert.False(state.GrantItems(ME, 2000));
        Assert.False(state.GrantItems(ME, 2001, 2));
        Assert.Equal(1, Owned(2000));
        var scope = 2000 + 2 * Array.FindIndex(Kinds, k => k.Type == 2);
        Assert.True(state.GrantItems(ME, scope, 3));
        Assert.Equal(3, Owned(scope));
    }

    /// <summary>The saved Inventory/dict of a zip: copies (guid, slot) of one item.</summary>
    static string Dict(long item, params (long Guid, long Slot)[] copies) =>
        new JsonObject { [item.ToString()] = new JsonArray([.. copies.Select(c => (JsonNode?)new JsonObject { ["id"] = c.Guid, ["slot"] = c.Slot, ["units"] = 0, ["metadata"] = new JsonObject(), ["location"] = null })]) }.ToJsonString();

    // the start kit: a connect that lists an item more often than its limit gives the account one, the copy in use first
    [Fact]
    public void TheStartKitIsCutToTheLimitToo()
    {
        var connect = CONNECT;
        foreach (var (guid, slot) in new[] { (300, 0), (301, 0), (302, 2), (303, 0) })
            connect["inventory"]!["items"]!.AsArray().Add(O($$"""{"item_id": 2000, "slot": {{slot}}, "guid": "{{guid}}", "metadata": {}, "units": null}"""));
        Make(KindItems(), connect: connect);
        using var con = state.Store.Open(ME);
        Assert.Equal(new[] { 302L }, con.Rows("SELECT guid FROM items WHERE account_id = $a AND item_id = 2000").Select(r => (long)r[0]));
    }

    [Fact]
    public void ARestoreBringingFiveBinocularsGivesOneTheEquippedCopyFirst()
    {
        Make(KindItems());  // 2004: client type 0 marked one
        var result = Restorer.Import(state, TestZip.Own().Replace("Inventory_dict.json", Dict(2004, (7001, 0), (7002, 0), (7003, 5), (7004, 0), (7005, 0))).Write(tmp), null, tmp);
        using var con = state.Store.Open(1001);
        Assert.Equal(new[] { (7003L, 5L) }, con.Rows("SELECT guid, slot FROM items WHERE account_id = $a AND item_id = 2004").Select(r => ((long)r[0], (long)r[1])));
        Assert.Contains("items over their own limit, not restored: 4", result.Report);
    }

    [Fact]
    public void ARestoreKeepsEveryCopyOfWhatIsNotLimited()
    {
        Make(KindItems());
        var scope = 2000 + 2 * Array.FindIndex(Kinds, k => k.Type == 2);
        Restorer.Import(state, TestZip.Own().Replace("Inventory_dict.json", Dict(scope, (7001, 0), (7002, 0), (7003, 0))).Write(tmp), null, tmp);
        Assert.Equal(3, Owned(scope, 1001));
    }

    // each kind's limit at restore: 3 copies come in, a limited kind keeps 1 and the others all 3
    [Theory]
    [InlineData(1L, true, true)]     // rifle
    [InlineData(22L, true, true)]    // handgun
    [InlineData(7L, true, true)]     // clothing
    [InlineData(4L, false, true)]    // caller
    [InlineData(31L, true, true)]    // pose
    [InlineData(42L, true, true)]    // vehicle
    [InlineData(27L, true, true)]    // dog vest
    [InlineData(43L, true, true)]    // electronic caller add-on
    [InlineData(2L, true, false)]    // scope
    [InlineData(25L, true, false)]   // dog
    [InlineData(4L, true, false)]    // scent, spray, bait
    public void ARestoreKeepsAsManyCopiesAsTheKindsLimit(long type, bool multiple, bool limited)
    {
        Make(KindItems());
        var item = 2000 + 2 * Array.FindIndex(Kinds, k => k.Type == type && k.Multiple == multiple);
        Restorer.Import(state, TestZip.Own().Replace("Inventory_dict.json", Dict(item, (7001, 0), (7002, 0), (7003, 0))).Write(tmp), null, tmp);
        Assert.Equal(limited ? 1 : 3, Owned(item, 1001));
    }

    [Fact]
    public void ARestoreKeepsEveryElectronicCallerAndLimitsTheHuntingHornToOne()
    {
        Make(KindItems());
        Restorer.Import(state, TestZip.Own().Replace("Inventory_dict.json", Dict(ECaller, (7001, 0), (7002, 0), (7003, 0))).Write(tmp), null, tmp);
        Assert.Equal(3, Owned(ECaller, 1001));
        Restorer.Import(state, TestZip.Own().Replace("Inventory_dict.json", Dict(Horn, (7001, 0), (7002, 0), (7003, 0))).Write(tmp, "horn.zip"), null, tmp);
        Assert.Equal(1, Owned(Horn, 1001));
    }

    // a bundle of 5 items at 60 em$ (10, 10, 20, 10, 10 em$): A and B are limited, C, D and E are not
    const long A = 6001, B = 6002, C = 6003, D = 6004, E = 6005, Ammo1 = 6101, Ammo2 = 6102;

    static IEnumerable<JsonObject> BundleItems() =>
    [
        Item(A, 1, true), Item(B, 7, true), Item(C, 28, true, 20), Item(D, 28, true), Item(E, 28, true),
        Item(Ammo1, 3, true, 5, cat: 3), Item(Ammo2, 3, true, 5, cat: 3),
        Item(Fuel25, 28, true, 5), Item(Fuel50, 28, true, 5), Item(Fuel100, 28, true, 5),
    ];

    static JsonArray Bundles() => (JsonArray)JsonNode.Parse($$"""
        [{"id": 9, "name": "Pack", "price": 60, "items": [{{A}}, {{B}}, {{C}}, {{D}}, {{E}}]},
         {"id": 10, "name": "Owned Pack", "price": 50, "items": [{{A}}, {{B}}]},
         {"id": 21, "name": "Ammo Pack", "price": 20, "items": [{{Ammo1}}, {{Ammo2}}]},
         {"id": 22, "name": "Mixed Pack", "price": 30, "items": [{{Ammo1}}, {{C}}]},
         {"id": 23, "name": "Fuel Pack", "price": 30, "items": [{{Fuel25}}, {{C}}]},
         {"id": 24, "name": "Twice Pack", "price": 40, "items": [{{A}}, {{A}}, {{C}}, {{D}}]}]
        """)!;

    JsonNode Offer(long id) => state.BundleOffers(ME)["bundles"]!.AsArray().Single(b => (long)b!["id"]! == id)!;

    [Fact]
    public void ABundleWithTwoOwnedLimitedItemsWorthAThirdCostsTwoThirdsAndGivesTheOtherThree()
    {
        Make(BundleItems(), Bundles(), ammo: true, fuel: true);
        Assert.True(state.GrantItems(ME, A));
        Assert.True(state.GrantItems(ME, B));
        Eq("""{"id": 9, "price": 40, "owned": 2, "of": 5, "buyable": true}""", Offer(9));
        var before = Em();
        Assert.NotNull(state.WebBuy(ME, 9, true, 1));
        Assert.Equal(40, before - Em());
        Assert.Equal(new long[] { 1, 1, 1, 1, 1 }, new[] { A, B, C, D, E }.Select(id => Owned(id)));  // A and B were not given again, C, D and E were
    }

    [Fact]
    public void ABundleWithNothingOwnedCostsItsPriceAndGivesEveryItem()
    {
        Make(BundleItems(), Bundles(), ammo: true, fuel: true);
        Eq("""{"id": 9, "price": 60, "owned": 0, "of": 5, "buyable": true}""", Offer(9));
        var before = Em();
        Assert.NotNull(state.WebBuy(ME, 9, true, 1));
        Assert.Equal(60, before - Em());
        Assert.Equal(new long[] { 1, 1, 1, 1, 1 }, new[] { A, B, C, D, E }.Select(id => Owned(id)));
    }

    [Fact]
    public void ABundleThatListsALimitedItemTwiceGivesItOnceAndTheSecondOneIsNotPaidFor()
    {
        Make(BundleItems(), Bundles(), ammo: true, fuel: true);
        Eq("""{"id": 24, "price": 32, "owned": 0, "of": 3, "buyable": true}""", Offer(24));  // A twice: the second copy is not given, not paid for and not counted; the items are worth 10, 10, 20 and 10 of 50 em$: 4/5 of 40 em$
        Assert.NotNull(state.WebBuy(ME, 24, true, 1));
        Assert.Equal(new long[] { 1, 1, 1 }, new[] { A, C, D }.Select(id => Owned(id)));
    }

    [Fact]
    public void ABundleWhoseLimitedItemsAreAllOwnedIsNotSoldAndChargesNothing()
    {
        Make(BundleItems(), Bundles(), ammo: true, fuel: true);
        Assert.True(state.GrantItems(ME, A));
        Assert.True(state.GrantItems(ME, B));
        Eq("""{"id": 10, "price": 50, "owned": 2, "of": 2, "buyable": false}""", Offer(10));
        var before = Em();
        Assert.Null(state.WebBuy(ME, 10, true, 1));
        Assert.Equal(before, Em());
    }

    [Fact]
    public void TheAmmunitionOnlyBundlesAreLeftOutWhileUnlimitedAmmoIsOnAndBackWhenItIsOff()
    {
        Make(BundleItems(), Bundles(), ammo: true, fuel: false, name: "on.db");
        long[] Listed() => [.. state.StoreCatalogue()["bundles"]!.AsArray().Select(b => (long)b!["id"]!)];
        Assert.Equal(new long[] { 9, 10, 22, 23, 24 }, Listed());  // the mixed one stays, shown as not for sale
        Assert.False(state.StoreCatalogue()["bundles"]!.AsArray().Single(b => (long)b!["id"]! == 22)!["buyable"]!.GetValue<bool>());
        Make(BundleItems(), Bundles(), ammo: false, fuel: false, name: "off.db");
        Assert.Equal(new long[] { 9, 10, 21, 22, 23, 24 }, Listed());
    }

    [Fact]
    public void FuelIsRefusedWhileUnlimitedFuelIsOnAndTheConnectCarriesTheService()
    {
        Make(BundleItems(), Bundles(), fuel: true);
        var before = Em();
        foreach (var id in new[] { Fuel25, Fuel50, Fuel100 })
        {
            Assert.False(Web(id));
            Assert.False(Game(id));
        }
        Assert.Null(state.WebBuy(ME, 23, true, 1));  // a bundle that lists a fuel
        Assert.Equal(before, Em());
        var item = state.StoreCatalogue()["items"]!.AsArray().Single(i => (long)i!["id"]! == Fuel25)!;
        Assert.False(item["buyable"]!.GetValue<bool>());
        Assert.True(item["fuel"]!.GetValue<bool>());
        Assert.DoesNotContain(23L, state.StoreCatalogue()["bundles"]!.AsArray().Select(b => (long)b!["id"]!));
        Assert.Equal(1, (int)Reply(state.ConnectReply(ME))["inventory"]!["active_services"]!["UNLIMITED_FUEL"]!);
    }

    [Fact]
    public void FuelIsAnItemLikeAnyOtherWhenItIsOff()
    {
        Make(BundleItems(), Bundles(), fuel: false);
        Assert.True(Web(Fuel25));
        Assert.True(Game(Fuel50));
        Assert.True(Web(Fuel25));  // not limited: several
        Assert.Contains(23L, state.StoreCatalogue()["bundles"]!.AsArray().Select(b => (long)b!["id"]!));
        Assert.Null(Reply(state.ConnectReply(ME))["inventory"]!["active_services"]!["UNLIMITED_FUEL"]);
    }

    /// <summary>The account owns the copies of an item as the rows say, bypassing every rule (as the inventories of before the limits are).</summary>
    void Rows(long account, long item, params (long Guid, long Slot, string Metadata)[] copies)
    {
        using var con = state.Store.Open(account);
        foreach (var (guid, slot, metadata) in copies)
            con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, ?, ?, ?, ?, NULL)", guid, item, slot, metadata);
    }

    long[] Guids(long item, long account = ME)
    {
        using var con = state.Store.Open(account);
        return [.. con.Rows("SELECT guid FROM items WHERE account_id = $a AND item_id = ? ORDER BY guid", item).Select(r => (long)r[0])];
    }

    [Fact]
    public void ThePassKeepsTheCopiesInUseFirstThenTheOldestAndNeverTouchesAmmunitionScopesOrFuel()
    {
        var scope = 2000 + 2 * Array.FindIndex(Kinds, k => k.Type == 2);
        Make([.. KindItems(), .. BundleItems()], fuel: true);
        const string Placed = """{"__position": {"x": 1, "y": 2, "z": 3}}""";
        Rows(ME, 2000, (1001, 0, "{}"), (1002, 0, "{}"), (1003, 0, "{}"));                      // a rifle, none in use: the oldest stays
        Rows(ME, 2012, (1101, 0, "{}"), (1102, 18, "{}"), (1103, 0, "{}"));                     // a jacket (client type 7): the equipped copy stays
        Rows(ME, 2014, (1201, 0, "{}"), (1202, 3, "{}"), (1203, 4, "{}"));                      // two in use: the lowest guid of those
        Rows(ME, 2016, (1301, 0, "{}"), (1302, 23, Placed));                                    // a placed one stays
        Rows(ME, scope, (1401, 0, "{}"), (1402, 0, "{}"));                                      // scopes: not limited
        Rows(ME, Ammo1, (1501, 0, "{}"), (1502, 0, "{}"), (1503, 1, "{}"));                    // ammunition: never touched
        Rows(ME, Fuel25, (1601, 0, "{}"), (1602, 0, "{}"));                                     // fuel rows stay
        Rows(ME, 2002, (1701, 0, "{}"));                                                        // one copy of a limited item: nothing to remove
        state.CreateAccount(8, "other");
        Rows(8, 2000, (2001, 0, "{}"), (2002, 0, "{}"));

        var lines = state.EnforceOwnLimits();

        Assert.Equal(2, lines.Count);  // one line per account that lost copies
        Assert.Equal(new long[] { 1001 }, Guids(2000));
        Assert.Equal(new long[] { 1102 }, Guids(2012));
        Assert.Equal(new long[] { 1202 }, Guids(2014));
        Assert.Equal(new long[] { 1302 }, Guids(2016));
        Assert.Equal(new long[] { 1401, 1402 }, Guids(scope));
        Assert.Equal(new long[] { 1501, 1502, 1503 }, Guids(Ammo1));
        Assert.Equal(new long[] { 1601, 1602 }, Guids(Fuel25));
        Assert.Equal(new long[] { 1701 }, Guids(2002));
        Assert.Equal(new long[] { 2001 }, Guids(2000, 8));
        using var check = state.Store.Open(ME);
        Assert.Equal(new long[] { 1002, 1003, 1101, 1103, 1201, 1203, 1301 }, check.Rows("SELECT guid FROM own_limit_removals WHERE account_id = $a ORDER BY guid").Select(r => (long)r[0]));

        // it runs once: a later start (a tighter table, another catalogue) removes nothing, also a row that is over the limit now
        Assert.Empty(state.EnforceOwnLimits());
        Rows(ME, 2000, (9001, 0, "{}"));
        Assert.Empty(state.EnforceOwnLimits());
        Assert.Equal(new long[] { 1001, 9001 }, Guids(2000));
        Assert.Equal(7L, check.Scalar("SELECT COUNT(*) FROM own_limit_removals WHERE account_id = $a"));
    }

    [Fact]
    public void ThePassRemovesNothingFromAnAccountWithinTheLimits()
    {
        Make(KindItems());
        Assert.True(Web(2000));
        Assert.Empty(state.EnforceOwnLimits());
        Assert.Equal(1, Owned(2000));
    }

    [Fact]
    public void ADatabaseAtTheCurrentVersionWithoutTheRemovalsTableGetsIt()
    {
        Assert.Equal(43, Store.Version);
        var file = Path.Combine(tmp, "current.db");
        using (var con = new Store(file).Open())
            con.Exec("DROP TABLE own_limit_removals");  // user_version stays at Store.Version
        using var check = new Store(file).Open();
        Assert.Equal(1L, check.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'own_limit_removals'"));
    }

    // a server with no recording has the catalogue built from the install's definitions, which carry no `ownMultiple`: binoculars, callers and the horn have `units` -2, a scent has 50
    static IEnumerable<JsonObject> InstallItems() => Facts.Default.ApplicationItems((JsonArray)JsonNode.Parse("""
        [{"id": 1, "define": "BINOCULARS", "name": "8x42mm Binoculars", "type": "GENERAL", "cat": "EQUIPMENT", "group": "ITEM_GROUP_EQUIPMENT", "price": 40, "units": -2},
         {"id": 20, "define": "DEER_BLEAT_CALL", "name": "Deer Bleat Call", "type": "CALLER", "cat": "CALLERS", "group": "ITEM_GROUP_CALLER", "price": 40, "units": -2},
         {"id": 117, "define": "SCENT_ELIMINATOR", "name": "Scent Eliminator", "type": "GENERAL", "cat": "EQUIPMENT", "group": "ITEM_GROUP_EQUIPMENT", "price": 10, "units": 50, "unit_name": "uses"},
         {"id": 688, "define": "ROCKY_MOUNTAIN_ELK_SCENT", "name": "Elk Scent", "type": "CALLER", "cat": "CALLERS", "group": "ITEM_GROUP_CALLER", "price": 10, "units": 50, "unit_name": "uses"},
         {"id": 619, "define": "HUNTING_HORN_01", "name": "Hunting Horn", "type": "GENERAL", "cat": "EQUIPMENT", "group": "ITEM_GROUP_EQUIPMENT", "price": 40, "units": -2}]
        """)!).OfType<JsonObject>().Select(i => (JsonObject)i.DeepClone());

    [Fact]
    public void InAnInstallBuiltCatalogueBinocularsCallersAndTheHornAreOneAndTheConsumablesSeveral()
    {
        Make(InstallItems());
        Assert.Equal(new[] { true, false, true, false, true, false, true, true, true, true }, new long[] { 1, 20, 619 }.SelectMany(id => new[] { Web(id), Web(id) }).Concat(new long[] { 117, 688 }.SelectMany(id => new[] { Web(id), Web(id) })));
    }

    [Fact]
    public void ThePassBringsBinocularsAndCallersDownToOneAndLeavesTheConsumables()
    {
        Make(InstallItems());
        Rows(ME, 1, (1001, 0, "{}"), (1002, 0, "{}"), (1003, 0, "{}"));
        Rows(ME, 20, (1101, 0, "{}"), (1102, 0, "{}"));
        Rows(ME, 117, (1201, 0, "{}"), (1202, 0, "{}"));
        Rows(ME, 688, (1301, 0, "{}"), (1302, 0, "{}"));
        state.EnforceOwnLimits();
        Assert.Equal(new long[] { 1001 }, Guids(1));
        Assert.Equal(new long[] { 1101 }, Guids(20));
        Assert.Equal(new long[] { 1201, 1202 }, Guids(117));
        Assert.Equal(new long[] { 1301, 1302 }, Guids(688));
    }
}
