using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>#928: an item bought here, or one a restore brought, is sold back for 40 % of its price, rounded down, in the currency paid; what the purchase records cannot prove, or is in use, is refused.
/// ME has 182 em$ and 33 gm$ and owns the start items 512 (lodge, guid 100) and 10 (backpack, guid 200).</summary>
public class SellBackTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    // fictional values only. 1: a rifle (own limit 5), half price today; 7: one of each, em$ 100 or gm$ 1000; 6: a one-use item; 8: an ammunition; 9: gm$ 1001; 11: not for sale; 12 and 13: cheap; 512: a start item
    const string Catalogue = """
        {"species": [], "reserves": [], "locations": [],
         "items": [
           {"id": 1, "name": "Test Rifle", "subcat": {"id": 0}, "price": 100, "silver_price": 4000, "hidden": 0, "ownMultiple": true, "ownLimit": 5, "inventory": {}, "fit": {}},
           {"id": 6, "name": "Test Refill", "subcat": {"id": 0}, "price": 5, "hidden": 0, "ownMultiple": false, "units": {"count": 1, "name": "uses"}, "inventory": {}, "fit": {}},
           {"id": 7, "name": "Test Limited", "subcat": {"id": 0}, "price": 100, "silver_price": 1000, "hidden": 0, "ownMultiple": false, "inventory": {}, "fit": {}},
           {"id": 8, "name": "Test Ammo", "cat": 3, "subcat": {"id": 0}, "price": 10, "silver_price": 100, "hidden": 0, "units": {"count": 10, "name": "rounds"}, "inventory": {}, "fit": {}},
           {"id": 9, "name": "Test Restored", "subcat": {"id": 0}, "price": 50, "silver_price": 1001, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 11, "name": "Test Event", "subcat": {"id": 0}, "price": 50, "silver_price": 100, "notbuyable": 1, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 12, "name": "Test Cheap", "subcat": {"id": 0}, "price": 2, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 13, "name": "Test Odd", "subcat": {"id": 0}, "price": 7, "hidden": 0, "inventory": {}, "fit": {}},
           {"id": 512, "name": "Test Start", "subcat": {"id": 0}, "price": 100, "silver_price": 2000, "hidden": 0, "inventory": {}, "fit": {}}],
         "categories": [{"id": 1, "title": "Weapons", "subcategories": [{"id": 0, "title": "Rifles"}], "default": 0}],
         "bundles": [{"id": 21, "name": "Test Pack", "price": 60.0, "items": [13]}]}
        """;

    const string Discounts = """[{"item_id": 1, "percentage": 50, "start_ts": 0, "end_ts": 1099511627776}]""";

    public SellBackTests() => state = Make(Db);

    static State Make(string file) =>
        new(file, Py.DumpBytes(CONNECT), BASE, ICON, (JsonObject)JsonNode.Parse(Catalogue)!, LOCALE, TEXTURES, discounts: (JsonArray)JsonNode.Parse(Discounts)!, offline: false);

    double Balance(string currency, long account = ME)
    {
        using var con = state.Store.Open(account);
        return Convert.ToDouble(con.Scalar("SELECT amount FROM wallet WHERE account_id = $a AND currency = ?", currency));
    }

    void AddGm(double amount)
    {
        using var con = state.Store.Open(ME);
        State.AddCurrency(con, "gm", amount, "test");
    }

    long ItemCount(long account = ME)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar("SELECT COUNT(*) FROM items WHERE account_id = $a")!;
    }

    long LastGuid()
    {
        using var con = state.Store.Open(ME);
        return (long)con.Scalar("SELECT MAX(guid) FROM items WHERE account_id = $a")!;
    }

    long Bought(long id, bool gms = false)
    {
        Assert.NotNull(state.WebBuy(ME, id, false, 1, gms));
        return LastGuid();
    }

    void Refused(long guid, string why)
    {
        var (items, em, gm) = (ItemCount(), Balance("em"), Balance("gm"));
        var (offer, balance, got) = state.WebSell(ME, guid);
        Assert.Null(offer);
        Assert.Null(balance);
        Assert.Equal(why, got);
        Assert.Equal((items, em, gm), (ItemCount(), Balance("em"), Balance("gm")));  // nothing changes
    }

    /// <summary>Paying 100 em$, 50 em$ (half price today), 7 em$ (2.8 rounds down to 2) or 4,000 gm$ and selling the copy back: the copy goes, 40 % of the price paid comes back in that currency, one wallet row says so.</summary>
    [Theory]
    [InlineData(7L, false, 100.0, 40L)]
    [InlineData(1L, false, 50.0, 20L)]   // a sale price: 40 % of what was paid, never of the list price
    [InlineData(13L, false, 7.0, 2L)]
    [InlineData(1L, true, 4000.0, 1600L)]
    public void SellingACopyBoughtHerePaysFortyPercentOfThePricePaidInTheCurrencyPaid(long item, bool gms, double price, long refund)
    {
        AddGm(5000);
        var currency = gms ? "gm" : "em";
        var (before, other, items) = (Balance(currency), Balance(gms ? "em" : "gm"), ItemCount());
        var guid = Bought(item, gms);
        Assert.Equal(before - price, Balance(currency));

        var (offer, balance, why) = state.WebSell(ME, guid);

        Assert.Equal((new State.SellBackOffer(item, currency, refund), ""), (offer, why));
        Assert.Equal(before - price + refund, balance!.GetValue<double>());
        Assert.Equal((before - price + refund, other, items), (Balance(currency), Balance(gms ? "em" : "gm"), ItemCount()));  // the other wallet is not touched, the copy is gone
        using var con = state.Store.Open(ME);
        var row = con.Rows("SELECT currency, amount, summary, item_ids FROM wallet_log WHERE account_id = $a ORDER BY n DESC")[0];
        Assert.Equal((currency, (double)refund, "Sold: " + (item == 7 ? "Test Limited" : item == 13 ? "Test Odd" : "Test Rifle"), $"[{item}]"), ((string)row[0], Convert.ToDouble(row[1]), (string)row[2], (string)row[3]));
        Assert.Equal(0L, con.Scalar("SELECT COUNT(*) FROM purchases WHERE account_id = $a AND guid = ?", guid));
        Assert.Equal("unknown", state.WebSell(ME, guid).Why);  // no undo, and no second sale
    }

    [Fact]
    public void WhatIsInUseOrNotProvenBoughtHereIsRefusedWithItsReason()
    {
        AddGm(10000);
        Refused(Bought(8, true), "ammunition");
        Refused(Bought(6), "consumable");
        Refused(Bought(12), "too_cheap");  // 40 % of 2 em$ is 0

        var equipped = Bought(7, true);
        using (var con = state.Store.Open(ME))
            con.Exec("UPDATE items SET slot = 4 WHERE account_id = $a AND guid = ?", equipped);
        Refused(equipped, "equipped");

        var placed = Bought(1, true);
        using (var con = state.Store.Open(ME))
            con.Exec("""UPDATE items SET metadata = '{"__position": {"x": 1.0, "y": 2.0, "z": 3.0}}' WHERE account_id = $a AND guid = ?""", placed);
        Refused(placed, "placed");

        Assert.True(state.GrantItems(ME, 1));  // a grant is not a purchase
        Refused(LastGuid(), "no_proof");
        Assert.NotNull(state.WebBuy(ME, 21, true, 1));  // nor is a bundle's copy: its price has no share per item
        Refused(LastGuid(), "no_proof");
        Refused(100, "no_proof");  // the start kit is not a purchase
        Refused(999_999, "unknown");

        var hunted = Bought(13);
        var now = (long)Py.Now();
        using (var con = state.Store.Open(ME))
            con.Exec("INSERT INTO expeditions (account_id, start_ts, last_seen) VALUES ($a, ?, ?)", now, now);
        Refused(hunted, "hunting");  // the game holds the inventory during a hunt
        using (var con = state.Store.Open(ME))
            con.Exec("UPDATE expeditions SET end_ts = ? WHERE account_id = $a", now);
        Assert.NotNull(state.WebSell(ME, hunted).Offer);
    }

    [Fact]
    public void ASoldLimitedItemCanBeBoughtAgain()
    {
        var guid = Bought(7);
        Assert.Null(state.WebBuy(ME, 7, false, 1));  // one of each
        Assert.NotNull(state.WebSell(ME, guid).Offer);
        Assert.NotNull(state.WebBuy(ME, 7, false, 1));
    }

    [Fact]
    public void ARestoredCopySellsForFortyPercentOfTheCatalogueGmPriceInGmAndARestoredStartItemOrOneTheStoreDoesNotSellIsRefused()
    {
        Assert.True(state.GrantItems(ME, 9));
        var guid = LastGuid();
        using (var con = state.Store.Open(ME))
            PlayerRows.MarkRestored(con, guid, 9);
        var (gm, em) = (Balance("gm"), Balance("em"));

        var (offer, _, why) = state.WebSell(ME, guid);

        Assert.Equal((new State.SellBackOffer(9, "gm", 400), ""), (offer, why));  // 40 % of 1001, rounded down
        Assert.Equal((gm + 400, em), (Balance("gm"), Balance("em")));

        using (var con = state.Store.Open(ME))
            PlayerRows.MarkRestored(con, 100, 512);
        Refused(100, "start_kit");

        Assert.True(state.GrantItems(ME, 11));
        var event11 = LastGuid();
        using (var con = state.Store.Open(ME))
            PlayerRows.MarkRestored(con, event11, 11);
        Refused(event11, "not_sold");
    }

    [Fact]
    public void ARestoreMarksTheCopiesItBringsAndNotTheStartItemsItAdds()
    {
        const string Item = """{"1": [{"id": 9001, "slot": 0, "units": 0, "metadata": {}, "location": null}]}""";
        var uploads = new Uploads(state, tmp) { MinFreeBytes = 0 };
        var player = state.Store.NextServerId();
        state.CreateAccount(player, "playerone", "76561190000000001");
        var zip = TestZip.Own(uid: "1001", handle: "h1001").Replace("Inventory_dict.json", Item);
        var id = uploads.Start(player)!;
        uploads.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(zip.Write(tmp, "z1001.zip")))).GetAwaiter().GetResult();
        Assert.Equal("review", uploads.Finish(player, id).GetAwaiter().GetResult().Kind);
        long row;
        using (var con = state.Store.Open(player)) row = (long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!;
        Assert.Equal("restored", uploads.Approve(row).Kind);

        var offers = state.WebInventory(1001)["items"]!.AsArray().Where(i => i!["sell"] is not null).Select(i => ((long)i!["guid"]!, (string)i["sell"]!["currency"]!, (long)i["sell"]!["refund"]!));
        Assert.Equal([(9001L, "gm", 1600L)], offers);  // 40 % of 4000 gm$; the start items the restore added have no proof
        var gm = Balance("gm", 1001);
        var sold = state.WebSell(1001, 9001);
        Assert.Equal(("gm", 1600L), (sold.Offer!.Currency, sold.Offer.Refund));
        Assert.Equal(gm + 1600, Balance("gm", 1001));
    }

    [Fact]
    public async Task TheEndpointSellsForTheSignedInPlayerAndTheInventoryOffersOnlyWhatSells()
    {
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        await using var server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
        var (sellable, equipped) = (Bought(1), Bought(1));
        using (var con = state.Store.Open(ME))
            con.Exec("UPDATE items SET slot = 3 WHERE account_id = $a AND guid = ?", equipped);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Task<Reply> Post(string form, bool signedIn = true, bool ownPage = true)
        {
            List<(string, string)> headers = [("Content-Type", "application/x-www-form-urlencoded")];
            if (signedIn) headers.Add(cookie);
            if (ownPage) headers.Add(("X-Requested-With", "XMLHttpRequest"));
            return server.SendAsync("POST", "web.test", "/account/sell", headers, System.Text.Encoding.ASCII.GetBytes(form));
        }

        var inventory = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/inventory", [cookie])).Body)!["items"]!.AsArray();
        Assert.Equal(("em", 20L), ((string)inventory.Single(i => (long)i!["guid"]! == sellable)!["sell"]!["currency"]!, (long)inventory.Single(i => (long)i!["guid"]! == sellable)!["sell"]!["refund"]!));
        Assert.Null(inventory.Single(i => (long)i!["guid"]! == equipped)!["sell"]);
        Assert.DoesNotContain(inventory, i => (long)i!["guid"]! != sellable && i!["sell"] is not null);

        Assert.Equal(401, (await Post($"guid={sellable}", signedIn: false)).Status);
        Assert.Equal(400, (await Post($"guid={sellable}", ownPage: false)).Status);
        Assert.Equal(400, (await Post("guid=abc")).Status);
        var refused = await Post($"guid={equipped}");
        Assert.Equal((409, "equipped"), (refused.Status, (string)JsonNode.Parse(refused.Body)!["status"]!));

        var sold = await Post($"guid={sellable}");
        var body = JsonNode.Parse(sold.Body)!;
        Assert.Equal((200, "sold", "em", 20L), (sold.Status, (string)body["status"]!, (string)body["currency"]!, (long)body["refund"]!));
        Assert.Equal(Balance("em"), body["balance"]!.GetValue<double>());
        Assert.Equal(409, (await Post($"guid={sellable}")).Status);
    }

    [Fact]
    public void ADatabaseAtTheCurrentVersionWithoutThePurchasesTableGetsItAndMarksWhatARestoreBroughtExceptWhatWasBoughtBefore()
    {
        var file = Path.Combine(tmp, "current.db");
        var first = Make(file);
        using (var con = first.Store.Open(ME))
        {
            con.Exec("INSERT INTO restore_audit (account_id, ts, zip_sha256, mode, report) VALUES ($a, 1, 'x', 'm', 'r')");
            con.Exec("INSERT INTO wallet_log (account_id, ts, currency, amount, after, summary, item_ids) VALUES ($a, 1, 'em', -5, 1, 'In-game payment.', '[512]')");
            con.Exec("DROP TABLE purchases");  // user_version stays at Store.Version
        }

        using var check = new Store(file).Open(ME);

        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
        // item 512 was also bought here before the table existed: which copy is which cannot be told, so only the other one is marked
        Assert.Equal([(200L, 10L, 1L)], check.Rows("SELECT guid, item_id, restored FROM purchases WHERE account_id = $a").Select(r => ((long)r[0], (long)r[1], (long)r[2])));
        using var again = new Store(file).Open(ME);  // a later start leaves the table as it is
        again.Exec("DELETE FROM purchases WHERE account_id = $a");
        using var third = new Store(file).Open(ME);
        Assert.Equal(0L, third.Scalar("SELECT COUNT(*) FROM purchases WHERE account_id = $a"));
    }
}
