using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The store's sales (settings.json `store_sales`, docs/server.md#store-sales): the price in the store and at purchase, the random pick, the event link, the overlap rule, the currencies, bundles,
/// the reload without a restart, the pick history and the admin listing. The catalogue has items 9001 to 9040 (the nth costs 100 + 10n em$ and 1000 + 10n gm$; 9001 to 9020 are rifles, the others sights)
/// and the bundle 9 of 9001, 9002 and 9003 at 400 em$. The clock is `at`.</summary>
public class StoreSalesTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    const long Day = 86400;
    static readonly long Friday = T("2026-11-27T15:00:00Z"), Monday = T("2026-11-30T07:00:00Z"), DailyStart = T("2026-11-28T00:00:00Z");

    readonly List<string> log = [];
    double at;
    int edits;

    static long T(string iso) => RewardEvents.ParseTime(iso)!.Value;
    static long Id(int n) => 9000 + n;
    static string Iso(long unix) => RewardEvents.Iso(unix);
    static JsonNode Json(string json) => JsonNode.Parse(json)!;
    string SettingsFile => Path.Combine(tmp, "settings.json");

    static JsonObject Catalogue()
    {
        var items = new JsonArray();
        for (var n = 1; n <= 40; n++)
            items.Add(new JsonObject
            {
                ["id"] = Id(n), ["name"] = $"Item {n}", ["subcat"] = new JsonObject { ["id"] = n <= 20 ? 0 : 5 }, ["price"] = 100 + n * 10, ["silver_price"] = 1000 + n * 10,
                ["notbuyable"] = 0, ["hidden"] = 0, ["ownMultiple"] = n != 1, ["inventory"] = new JsonObject(), ["fit"] = new JsonObject(),
            });
        return new JsonObject
        {
            ["species"] = new JsonArray(), ["reserves"] = new JsonArray(), ["locations"] = new JsonArray(), ["items"] = items,
            ["categories"] = Json("""
                [{"id": "all", "title": "All Items", "subcategories": [{"id": "all", "title": "All Items"}], "default": "all"},
                 {"id": 1, "title": "Weapons", "subcategories": [{"id": 0, "title": "Rifles"}], "default": 0},
                 {"id": 2, "title": "Sights", "subcategories": [{"id": 5, "title": "Rifle Sights"}], "default": 5}]
                """),
            ["bundles"] = Json("""[{"id": 9, "name": "Test Pack", "description": "Three things.", "price": 400.0, "items": [9001, 9002, 9003]}]"""),
        };
    }

    /// <summary>A state on the catalogue with 100000 em$ and gm$ more for ME, whose clock is `at`.</summary>
    State Open(string? db = null, SeasonalEvent? season = null, JsonObject? catalogue = null)
    {
        var s = new State(db ?? Db, Py.DumpBytes(CONNECT), BASE, ICON, catalogue ?? Catalogue(), LOCALE, TEXTURES, seasonal: season);
        s.Clock = () => at;
        using var con = s.Store.Open(ME);
        State.AddCurrency(con, "em", 100000, "test");
        State.AddCurrency(con, "gm", 100000, "test");
        return s;
    }

    void Sales(string json) => state.LoadStoreSales(Json(json));

    JsonArray Shop(State? s = null) => (s ?? state).StoreCatalogue()["items"]!.AsArray();

    JsonNode Item(int n) => Shop().Single(i => (long)i!["id"]! == Id(n))!;

    (long Ems, long Gms) Prices(int n) => ((long)Item(n)["ems"]!, (long)Item(n)["gms"]!);

    JsonNode Bundle() => state.StoreCatalogue()["bundles"]!.AsArray().Single()!;

    long OfferPrice() => (long)state.BundleOffers(ME)["bundles"]![0]!["price"]!;

    double Em() => state.Wallet(ME)["em"]!.GetValue<double>();

    double Gm() => state.Wallet(ME)["gm"]!.GetValue<double>();

    /// <summary>What Inventory/buy charges for one of item n.</summary>
    double Buy(int n, string wallet = "GOLD") => Reply(state.BuyReply(ME, Encoding.ASCII.GetBytes($"item_id={Id(n)}&amount=1&wallet={wallet}")))["price"]!.GetValue<double>();

    /// <summary>The items on sale at `t`: id, percentage, list price (`was`), today's price.</summary>
    List<(long Id, double Percent, long Was, long Ems)> OnSale(double t, State? s = null)
    {
        at = t;
        return [.. Shop(s).Where(i => i!["sale"] is not null).Select(i => ((long)i!["id"]!, (double)(int)i["sale"]!["percent"]!, i["was"] is { } w ? (long)w : 0, (long)i["ems"]!))];
    }

    static string Set(IEnumerable<(long Id, double Percent, long Was, long Ems)> sale) => string.Join(",", sale.OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Percent}"));

    void Write(string json)
    {
        File.WriteAllText(SettingsFile, json);
        File.SetLastWriteTimeUtc(SettingsFile, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++edits));
    }

    const string BlackFriday = """{"name": "Black Friday", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 25, "currencies": ["em", "gm"], "items": [9003, 9004]}""";

    // the rifles that cost 150 or more, but not item 7: 15 items
    const string Daily = """
        {"name": "Daily deals", "from": "2026-11-28T00:00:00Z", "until": "2026-12-05T00:00:00Z",
         "random": {"count": 8, "percent": [10, 40], "every": "24h", "categories": ["Rifles"], "min_price_em": 150, "exclude": [9007]}}
        """;

    [Fact]
    public void AFixedSaleStartsAndEndsByTimeWithoutARestartAndBuyingChargesThePriceOfTheMoment()
    {
        at = Friday - 3600;
        state = Open();
        Write("{}");
        var watch = new GmBonusWatch(state, SettingsFile, null) { Log = log.Add };
        Write($$"""{"store_sales": [{{BlackFriday}}]}""");
        watch.Check();  // the file changed: the sale is in force with no restart
        Assert.Single(log);
        Assert.Contains("store_sales: 1 read", log[0]);

        at = Friday - 1;
        Assert.Equal((130L, 1030L), Prices(3));
        Assert.Null(Item(3)["was"]);
        Assert.Null(Item(3)["sale"]);
        Assert.Equal(130, Buy(3));

        at = Friday;
        Assert.Equal((98L, 773L), Prices(3));  // 25 % off 130 and 1030, rounded half up
        var sale = Item(3)["sale"]!;
        Assert.Equal(("Black Friday", 25, "2026-11-30T07:00:00Z"), ((string)sale["name"]!, (int)sale["percent"]!, (string)sale["until"]!));
        Assert.Equal((130L, 1030L), ((long)Item(3)["was"]!, (long)Item(3)["gwas"]!));
        Assert.Equal((105L, 780L), Prices(4));
        Assert.Null(Item(5)["sale"]);
        var (em, gm) = (Em(), Gm());
        Assert.Equal(98, Buy(3));  // Inventory/buy
        Assert.Equal(773, Buy(3, "SILVER"));
        Assert.Equal((em - 98, gm - 773), (Em(), Gm()));
        Assert.NotNull(state.WebBuy(ME, Id(3), false, 2));  // the website's store
        Assert.Equal(em - 98 - 196, Em());
        Assert.NotNull(state.WebBuy(ME, Id(3), false, 1, gms: true));
        Assert.Equal(gm - 773 - 773, Gm());

        at = Monday - 1;
        Assert.Equal(98, Buy(3));
        at = Monday;
        Assert.Equal((130L, 1030L), Prices(3));
        Assert.Null(Item(3)["sale"]);
        Assert.Equal(130, Buy(3));
        Assert.NotNull(state.WebBuy(ME, Id(3), false, 1));
        Assert.Equal(em - 98 - 196 - 98 - 130 - 130, Em());
    }

    [Fact]
    public void ARandomSaleShowsItsCountEveryDayTheSameAfterARestartAndOtherItemsTheNextDay()
    {
        at = DailyStart;
        state = Open();
        Sales($"[{Daily}]");

        var first = OnSale(DailyStart + 3600);
        Assert.Equal(8, first.Count);
        Assert.All(first, x =>
        {
            Assert.InRange(x.Id, Id(5), Id(20));  // the rifles of 150 em$ and more
            Assert.NotEqual(Id(7), x.Id);
            Assert.True(x.Percent is >= 10 and <= 40 && x.Percent % 5 == 0, $"{x.Percent} %");
            Assert.Equal(100 + (x.Id - 9000) * 10, x.Was);
            Assert.True(x.Ems < x.Was);
        });
        Assert.Equal(first.Count, first.Select(x => x.Id).Distinct().Count());
        Assert.Equal(Set(first), Set(OnSale(DailyStart + Day - 1)));  // the same all day
        foreach (var i in Shop().Where(i => i!["sale"] is not null))
            Assert.Equal(Iso(DailyStart + Day), (string)i!["sale"]!["until"]!);  // the pick ends with the day

        var restarted = Open();  // the same database, a new process that reads the same setting
        restarted.LoadStoreSales(Json($"[{Daily}]"));
        Assert.Equal(Set(first), Set(OnSale(DailyStart + 7200, restarted)));
        state.LoadStoreSales(Json($"[{Daily}]"));  // and a reload of it
        Assert.Equal(Set(first), Set(OnSale(DailyStart + 7200)));

        var second = OnSale(DailyStart + Day + 3600);
        Assert.Equal(8, second.Count);
        Assert.NotEqual(Set(first), Set(second));  // a new set the next day
        Assert.All(second, x => Assert.True(x.Id >= Id(5) && x.Id <= Id(20) && x.Id != Id(7)));
        foreach (var i in Shop().Where(i => i!["sale"] is not null))
            Assert.Equal(Iso(DailyStart + 2 * Day), (string)i!["sale"]!["until"]!);

        Assert.Equal(8, OnSale(DailyStart + 6 * Day + 10).Count);
        Assert.Empty(OnSale(DailyStart + 7 * Day));  // the sale's end
        Assert.Empty(OnSale(DailyStart - 1));

        // the pick of each period is written down: the current one and the next, once
        at = DailyStart + 3600;
        state.RecordSalePicks();
        state.RecordSalePicks();
        using var con = state.Store.Open();
        Assert.Equal(new[] { (DailyStart, 8L), (DailyStart + Day, 8L) },
            con.Rows("SELECT period_start, COUNT(*) FROM store_sale_picks WHERE sale = 'Daily deals' GROUP BY period_start ORDER BY period_start").Select(r => ((long)r[0], (long)r[1])));
        Assert.Equal(first.Select(x => x.Id).Order(), con.Rows("SELECT item_id FROM store_sale_picks WHERE period_start = ?", DailyStart).Select(r => (long)r[0]).Order());
        Assert.Equal(DailyStart + Day, con.Scalar("SELECT MAX(period_end) FROM store_sale_picks WHERE period_start = ?", DailyStart));
    }

    [Fact]
    public void TheFiltersOfARandomSaleHold()
    {
        at = Friday;
        state = Open();
        Sales("""
            [{"name": "Cheap sights", "from": "2026-11-27T00:00:00Z", "until": "2026-12-05T00:00:00Z",
              "random": {"count": 8, "percent": 20, "categories": ["Sights"], "max_price_em": 330}},
             {"name": "Gm rifles", "from": "2026-11-27T00:00:00Z", "until": "2026-12-05T00:00:00Z", "currencies": ["gm"],
              "random": {"count": 2, "percent": [5, 5], "categories": ["Rifles"], "min_price_gm": 1100}}]
            """);
        var onSale = OnSale(Friday);
        // sights (the top category's title) up to 330 em$: 9021 to 9023, fewer than the count; gm$ only, on two rifles of 1100 gm$ and more
        Assert.Equal(new[] { Id(21), Id(22), Id(23) }, onSale.Where(x => x.Percent == 20).Select(x => x.Id).Order());
        var gmOnly = Shop().Where(i => (int?)i!["sale"]?["percent"] == 5).ToList();
        Assert.Equal(2, gmOnly.Count);
        Assert.All(gmOnly, i =>
        {
            var n = (long)i!["id"]! - 9000;
            Assert.InRange(n, 10, 20);
            Assert.Equal(100 + n * 10, (long)i["ems"]!);  // em$ unchanged
            Assert.Null(i["was"]);
            Assert.True((long)i["gms"]! < (long)i["gwas"]!);
        });

        // the store key and the title of the sub-category name a category too
        Sales("""[{"name": "Keys", "from": "2026-11-27T00:00:00Z", "until": "2026-12-05T00:00:00Z", "random": {"count": 40, "percent": 10, "categories": ["5", "rifle sights"]}}]""");
        var keys = OnSale(Friday);
        Assert.Equal(20, keys.Count);
        Assert.All(keys, x => Assert.InRange(x.Id, Id(21), Id(40)));
    }

    [Fact]
    public void ASaleWhoseEventIsUnknownIsLoggedOncePerLoadAndNeverRuns()
    {
        at = Friday;
        state = Open(season: SeasonalEvent.Parse(Json("""{"name": "halloween_2026", "define": "HALLOWEEN", "reserves": [], "start": "2026-10-22T00:00:00Z", "end": "2026-11-02T00:00:00Z"}""")));
        state.SetHarvestGmBonuses([new State.GmBonus(2, Friday, Monday, "Weekend")]);
        var lines = state.LoadStoreSales(Json("""
            [{"name": "Known", "event": "halloween_2026", "percent": 30, "items": [9003]},
             {"name": "Bonus", "event": "weekend", "percent": 30, "items": [9003]},
             {"name": "Typo", "event": "haloween_2026", "percent": 30, "items": [9004]}]
            """));
        Assert.Equal(new[] { "store_sales: Typo: event haloween_2026 unknown, the sale never runs" }, lines);
        Assert.Equal(140, Prices(4).Ems);
    }

    [Fact]
    public void ASaleTiedToAnEventRunsOnlyWhileTheEventRunsInsideItsOwnTimes()
    {
        at = T("2026-10-21T00:00:00Z");
        state = Open(season: SeasonalEvent.Parse(Json("""{"name": "halloween_2026", "define": "HALLOWEEN", "reserves": [], "start": "2026-10-22T00:00:00Z", "end": "2026-11-02T00:00:00Z"}""")));
        state.SetHarvestGmBonuses([new State.GmBonus(2, Friday, Monday, "Harvest gm$ x2 weekend")]);
        Sales("""
            [{"name": "Spooky", "event": "halloween_2026", "percent": 30, "items": [9003]},
             {"name": "Early spooky", "event": "halloween_2026", "until": "2026-10-25T00:00:00Z", "percent": 30, "items": [9004]},
             {"name": "Weekend", "event": "Harvest gm$ x2 weekend", "from": "2026-11-28T00:00:00Z", "percent": 20, "items": [9005]},
             {"name": "Nowhere", "event": "no_such_event", "percent": 20, "items": [9006]}]
            """);
        (long, long) Em34(string iso) { at = T(iso); return (Prices(3).Ems, Prices(4).Ems); }
        long EmOf(int n, double t) { at = t; return Prices(n).Ems; }

        Assert.Equal((130L, 140L), Em34("2026-10-21T23:59:59Z"));
        Assert.Equal((91L, 98L), Em34("2026-10-22T00:00:00Z"));  // 30 % off 130 and 140, the sale takes the event's times
        Assert.Equal((91L, 98L), Em34("2026-10-24T23:59:59Z"));
        Assert.Equal((91L, 140L), Em34("2026-10-25T00:00:00Z"));  // the second one has its own until
        Assert.Equal((91L, 140L), Em34("2026-11-01T23:59:59Z"));
        Assert.Equal((130L, 140L), Em34("2026-11-02T00:00:00Z"));  // the event is over
        at = T("2026-10-23T00:00:00Z");
        Assert.Equal("2026-11-02T00:00:00Z", (string)Item(3)["sale"]!["until"]!);
        Assert.Equal("2026-10-25T00:00:00Z", (string)Item(4)["sale"]!["until"]!);

        // a harvest bonus is an event too: its weekend, from the Saturday the sale says
        Assert.Equal(150, EmOf(5, Friday - 3600));
        Assert.Equal(150, EmOf(5, Friday + 3600));  // the bonus runs, the sale has not begun
        Assert.Equal(120, EmOf(5, T("2026-11-28T00:00:00Z")));
        Assert.Equal(120, EmOf(5, Monday - 1));
        Assert.Equal(150, EmOf(5, Monday));  // the bonus is over
        Assert.Equal(160, EmOf(6, Friday + Day));  // an event that does not exist: never
    }

    [Fact]
    public void TwoSalesOnOneItemTakeTheLargerDiscountNeverBoth()
    {
        at = Friday;
        state = Open();
        Sales("""
            [{"name": "A", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 20, "items": [9003, 9004]},
             {"name": "B", "from": "2026-11-28T00:00:00Z", "until": "2026-11-29T00:00:00Z", "percent": 30, "currencies": ["em", "gm"], "items": [9003]}]
            """);
        Assert.Equal((104L, 1030L), Prices(3));  // A: 20 % off 130, em$ only
        Assert.Equal("A", (string)Item(3)["sale"]!["name"]!);
        at = T("2026-11-28T12:00:00Z");
        Assert.Equal((91L, 721L), Prices(3));  // B: 30 % off the list price, not 30 % off A's price
        Assert.Equal("B", (string)Item(3)["sale"]!["name"]!);
        Assert.Equal(30, (int)Item(3)["sale"]!["percent"]!);
        Assert.Equal("2026-11-29T00:00:00Z", (string)Item(3)["sale"]!["until"]!);
        Assert.Equal(112, Prices(4).Ems);  // only A lists item 4
        Assert.Equal(91, Buy(3));
        Assert.Equal(721, Buy(3, "SILVER"));
        at = T("2026-11-29T00:00:00Z");
        Assert.Equal((104L, 1030L), Prices(3));  // B is over, A again
    }

    [Fact]
    public void TheCurrenciesOfASaleAreTheOnesLowered()
    {
        at = Friday;
        state = Open();
        Sales("""
            [{"name": "Em", "from": "2026-11-27T00:00:00Z", "until": "2026-12-05T00:00:00Z", "percent": 50, "items": [9003]},
             {"name": "Gm", "from": "2026-11-27T00:00:00Z", "until": "2026-12-05T00:00:00Z", "percent": 50, "currencies": ["gm"], "items": [9004]},
             {"name": "Both", "from": "2026-11-27T00:00:00Z", "until": "2026-12-05T00:00:00Z", "percent": 50, "currencies": ["em", "gm"], "items": [9005]}]
            """);
        Assert.Equal((65L, 1030L), Prices(3));
        Assert.Equal((140L, 520L), Prices(4));  // gm$ only: the em$ price is not touched
        Assert.Equal((75L, 525L), Prices(5));
        Assert.Null(Item(3)["gwas"]);
        Assert.Null(Item(4)["was"]);
        Assert.Equal(1040L, (long)Item(4)["gwas"]!);
        Assert.Equal("Gm", (string)Item(4)["sale"]!["name"]!);  // a gm$ sale is a sale
        Assert.Equal((130L, 150L, 1050L), ((long)Item(3)["was"]!, (long)Item(5)["was"]!, (long)Item(5)["gwas"]!));
        var (em, gm) = (Em(), Gm());
        Assert.Equal(520, Buy(4, "SILVER"));  // gm$ pays the sale gm$ price
        Assert.Equal((em, gm - 520), (Em(), Gm()));
        Assert.Equal(140, Buy(4));  // and its em$ price is the list price
        Assert.Equal(75, Buy(5));
        Assert.Equal(525, Buy(5, "SILVER"));
        Assert.Equal(1030, Buy(3, "SILVER"));
        // the game's store takes em$ discounts only: items 3 and 5
        var rows = Reply(state.DiscountsReply()).AsArray();
        Assert.Equal(new[] { Id(3), Id(5) }, rows.Select(r => (long)r!["item_id"]!).Order());
        Assert.All(rows, r => Assert.Equal((50L, T("2026-11-27T00:00:00Z"), T("2026-12-05T00:00:00Z")), ((long)r!["percentage"]!, (long)r["start_ts"]!, (long)r["end_ts"]!)));
    }

    [Fact]
    public async Task WithoutStoreSalesNothingIsOnSaleAndTheRecordedDiscountsAreNotUsed()
    {
        at = Friday;
        state = Open();
        Assert.All(Shop(), i =>
        {
            Assert.Null(i!["was"]);
            Assert.Null(i["gwas"]);
            Assert.Null(i["sale"]);
        });
        Assert.Null(Bundle()["was"]);
        Assert.Null(Bundle()["sale"]);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        table.Entries[new("GET", "api.test", Replay.DISCOUNTS, null)] = new Replay.Hit(2, "", 200, [("Content-Type", "application/json")],
            "[{\"item_id\": 9003, \"percentage\": 95, \"start_ts\": 0, \"end_ts\": 1099511627776}]"u8.ToArray());
        await using var server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
        Assert.Equal("[]", Encoding.ASCII.GetString((await server.SendAsync("GET", "api.test", Replay.DISCOUNTS)).Body));  // the recording is not served
        Assert.Equal(130, Buy(3));
        Assert.Equal((130L, 1030L), Prices(3));

        Sales($"[{BlackFriday}]");
        var game = Reply((await server.SendAsync("POST", "api.test", Replay.DISCOUNTS)).Body).AsArray();
        Assert.Equal(2, game.Count);  // the game's store gets the sale as em$ discount rows
        Assert.Equal((Id(3), 25L, Friday, Monday), ((long)game[0]!["item_id"]!, (long)game[0]!["percentage"]!, (long)game[0]!["start_ts"]!, (long)game[0]!["end_ts"]!));
        Assert.Equal(98, Buy(3));
    }

    [Fact]
    public void ABundleOnSaleIsChargedItsSalePriceBeforeWhatThePlayerOwnsTakesMoreOff()
    {
        at = Friday - 1;
        state = Open();
        Sales("""[{"name": "Pack sale", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 20, "bundles": [9]}]""");
        Assert.Equal(400L, (long)Bundle()["price"]!);  // not yet
        Assert.Null(Bundle()["was"]);
        Assert.Null(Bundle()["sale"]);
        Assert.Equal(400L, OfferPrice());

        at = Friday;
        var bundle = Bundle();
        Assert.Equal((320L, 400L), ((long)bundle["price"]!, (long)bundle["was"]!));
        Assert.Equal(("Pack sale", 20, "2026-11-30T07:00:00Z"), ((string)bundle["sale"]!["name"]!, (int)bundle["sale"]!["percent"]!, (string)bundle["sale"]!["until"]!));
        Assert.Equal(320L, OfferPrice());
        var em = Em();
        Assert.NotNull(state.WebBuy(ME, 9, true, 1));
        Assert.Equal(em - 320, Em());  // charged the sale price
        Assert.Equal(3, state.Inventory(ME).Count(i => Py.L(i!["item_id"]) is >= 9001 and <= 9003));

        // item 9001 may be owned once, and is: the second purchase gives two items and takes the share off the sale price, 320 x 250 / 360 rounded up
        // (the other way round, 400 x 250 / 360 = 278 and then 20 % off, it would be 222)
        Assert.Equal(223L, OfferPrice());
        em = Em();
        Assert.NotNull(state.WebBuy(ME, 9, true, 1));
        Assert.Equal(em - 223, Em());

        at = Monday;
        Assert.Equal(400L, (long)Bundle()["price"]!);  // not any more
        Assert.Null(Bundle()["was"]);
        Assert.Null(Bundle()["sale"]);
        Assert.Equal(278L, OfferPrice());
        em = Em();
        Assert.NotNull(state.WebBuy(ME, 9, true, 1));
        Assert.Equal(em - 278, Em());
    }

    [Fact]
    public void AGmSaleLowersTheGmPriceOfABundleAndOfAnItemWithoutASilverPriceAndAnEmSaleDoesNot()
    {
        at = Friday;
        var catalogue = Catalogue();
        catalogue["items"]![0]!["silver_price"] = null;  // item 9001: 110 em$, no gm$ price in the catalogue
        state = Open(catalogue: catalogue);
        Assert.Equal((110L, 1870L), Prices(1));  // 17 x 110
        Assert.Equal((400L, 6800L), ((long)Bundle()["price"]!, (long)Bundle()["gms"]!));  // 17 x 400

        string Sale(string currency) => $$"""[{"name": "Pack sale", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 25, "currencies": ["{{currency}}"], "items": [9001], "bundles": [9]}]""";
        Sales(Sale("em"));
        Assert.Equal((83L, 1870L), Prices(1));  // 110 less 25 %, rounded half up; gm$ not lowered
        Assert.Equal((300L, 6800L, true), ((long)Bundle()["price"]!, (long)Bundle()["gms"]!, Bundle()["gwas"] is null));

        Sales(Sale("gm"));
        Assert.Equal((110L, 1403L), Prices(1));  // 1,870 less 25 %, rounded half up
        var bundle = Bundle();
        Assert.Equal((400L, 5100L, 6800L), ((long)bundle["price"]!, (long)bundle["gms"]!, (long)bundle["gwas"]!));
        Assert.Equal(5100L, (long)state.BundleOffers(ME)["bundles"]![0]!["gms"]!);
        var (em, gm) = (Em(), Gm());
        Assert.NotNull(state.WebBuy(ME, 9, true, 1, gms: true));
        Assert.Equal((em, gm - 5100), (Em(), Gm()));  // gm$ only, the sale price
        Assert.Equal(3, state.Inventory(ME).Count(i => Py.L(i!["item_id"]) is >= 9001 and <= 9003));
    }

    [Fact]
    public void TheWebsiteAndTheLauncherReadTheStoreWhileASaleIsOn()
    {
        at = Friday;
        state = Open();
        Sales($"[{BlackFriday.Replace("\"items\": [9003, 9004]", "\"items\": [9003], \"bundles\": [9]")}]");
        // what GET /catalog/store sends, read as the website and the launcher read it (ServerCache: ReadFromJsonAsync, web defaults)
        var store = System.Text.Json.JsonSerializer.Deserialize<Resurgence.Services.ServerStore>(Py.DumpBytes(state.StoreCatalogue()), System.Text.Json.JsonSerializerOptions.Web)!;
        Assert.Equal(25, store.Items.Single(i => i.Id == Id(3)).Sale!.Percent);
        Assert.Equal(25, store.Bundles.Single(b => b.Id == 9).Sale!.Percent);
    }

    [Fact]
    public void TheAdminListingShowsThePicksAndPricesOfTheCurrentAndTheNextPeriodBeforeTheyStart()
    {
        at = Friday - 3600;
        state = Open();
        var fixedSale = BlackFriday.Replace("\"items\": [9003, 9004]", "\"items\": [9003], \"bundles\": [9]");
        Sales($"[{fixedSale}, {Daily}]");
        var report = state.SalesReport();
        Assert.Equal("Black Friday: 25 %, em$+gm$", report[0]);
        Assert.Equal("  now: none", report[1]);
        Assert.Equal("  next: 2026-11-27T15:00:00Z .. 2026-11-30T07:00:00Z", report[2]);
        Assert.Equal("    9003 Item 3: 25 %, em$ 130 -> 98, gm$ 1030 -> 773", report[3]);
        Assert.Equal("    bundle 9 Test Pack: 25 %, em$ 400 -> 300, gm$ 6800 -> 5100", report[4]);
        Assert.StartsWith("Daily deals: random 8 every 24 h, 10-40 %, em$", report[5]);

        var daily = report.SkipWhile(l => !l.StartsWith("Daily deals")).Skip(1).ToList();
        Assert.Equal("  now: none", daily[0]);
        Assert.Equal("  next: 2026-11-28T00:00:00Z .. 2026-11-29T00:00:00Z", daily[1]);
        var next = daily.Skip(2).TakeWhile(l => l.StartsWith("    ")).Select(l => long.Parse(l.Trim().Split(' ')[0])).ToList();
        Assert.Equal(8, next.Count);
        // what the report says will be on sale is what is on sale when the period starts
        Assert.Equal(next.Order(), OnSale(DailyStart).Select(x => x.Id).Where(id => id != Id(3)).Order());  // item 3 is Black Friday's
        var running = state.SalesReport();
        Assert.Contains("  now: 2026-11-28T00:00:00Z .. 2026-11-29T00:00:00Z", running);
        Assert.Contains("  next: 2026-11-29T00:00:00Z .. 2026-11-30T00:00:00Z", running);

        state.LoadStoreSales(null);
        Assert.Equal(new[] { "no store_sales" }, state.SalesReport());
    }

    [Fact]
    public void AWrongEditKeepsTheSalesInForceAndALaterOneChangesThem()
    {
        at = Friday;
        state = Open();
        Write("{}");
        var watch = new GmBonusWatch(state, SettingsFile, null) { Log = log.Add };
        Write($$"""{"store_sales": [{{BlackFriday}}]}""");
        watch.Check();
        Assert.Equal(98, Prices(3).Ems);

        // a wrong sale beside a good bonus: the sales in force stay, the bonus is applied
        var wrong = BlackFriday.Replace("25", "250");
        Write($$"""{"harvest_gm_bonus": [{"multiplier": 2, "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z"}], "store_sales": [{{wrong}}]}""");
        watch.Check();
        Assert.Equal(98, Prices(3).Ems);
        Assert.Equal(2, state.GmAt(Friday).Multiplier);
        Assert.Contains(log, l => l.StartsWith("store_sales: not applied, the sales in force stay: "));
        Write("{ not json");
        watch.Check();
        Assert.Equal(98, Prices(3).Ems);
        Assert.Equal(2, log.Count(l => l.Contains("in force stay")));

        Write("""{"store_sales": [{"name": "Black Friday", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 50, "items": [9003]}]}""");
        watch.Check();  // a new percentage
        Assert.Equal(65, Prices(3).Ems);
        Assert.Equal(1, state.GmAt(Friday).Multiplier);  // the bonus left the file
        Write("""{"max_body_bytes": 5}""");
        watch.Check();  // no sale any more
        Assert.Equal(130, Prices(3).Ems);
        Assert.Null(Item(3)["sale"]);
    }

    [Theory]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 0, "items": [1]}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 12.5, "items": [1]}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 100, "items": [1]}""")]
    [InlineData("""{"from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 10, "items": [1]}""")]
    [InlineData("""{"name": "x", "from": "2026-11-30T07:00:00Z", "until": "2026-11-27T15:00:00Z", "percent": 10, "items": [1]}""")]
    [InlineData("""{"name": "x", "percent": 10, "items": [1]}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 10}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 10, "items": [1], "random": {"count": 1, "percent": 10}}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 10, "items": []}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 10, "items": [1], "currencies": ["eur"]}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 10, "items": [1], "currencies": []}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "random": {"count": 0, "percent": 10}}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "random": {"count": 3, "percent": [12, 14]}}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "random": {"count": 3, "percent": [40, 10]}}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "random": {"count": 3, "percent": 10, "every": "soon"}}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "random": {"count": 3, "percent": 10, "categories": [4]}}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "random": {"count": 3, "percent": 10, "min_price_em": -1}}""")]
    [InlineData("""{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 10, "bundles": [9], "random": {"count": 3, "percent": 10}}""")]
    [InlineData("""{"name": "x", "from": "yesterday", "until": "2026-11-30T07:00:00Z", "percent": 10, "items": [1]}""")]
    public void AWrongSaleIsRefusedAndSaysWhere(string sale) =>
        Assert.StartsWith("store_sales[0]", Assert.Throws<InvalidDataException>(() => Settings.ParseStoreSales(Json($"[{sale}]"))).Message);

    [Fact]
    public void TwoSalesWithOneNameAndAnythingButAListAreRefused()
    {
        const string one = """{"name": "x", "from": "2026-11-27T15:00:00Z", "until": "2026-11-30T07:00:00Z", "percent": 10, "items": [1]}""";
        var other = one.Replace("\"x\"", "\"X\"");
        Assert.Throws<InvalidDataException>(() => Settings.ParseStoreSales(Json($"[{one}, {other}]")));
        Assert.Throws<InvalidDataException>(() => Settings.ParseStoreSales(Json(one)));
        Assert.Null(Settings.ParseStoreSales(null));
        Assert.Empty((JsonArray)Settings.ParseStoreSales(Json("[]"))!);
    }

    [Fact]
    public void ADatabaseAtTheCurrentVersionWithoutThePickTableGetsIt()
    {
        var file = Path.Combine(tmp, "picks.db");
        using (var con = new Store(file).Open())
            con.Exec("DROP TABLE store_sale_picks");  // user_version stays at Store.Version
        using var check = new Store(file).Open();
        Assert.Equal(1L, check.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'store_sale_picks'"));
    }
}
