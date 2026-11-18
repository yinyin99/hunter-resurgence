using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The taxidermy of a harvest (#666), on invented data: the game's purchase on both of its paths, the player's own page and the operator's list and undo.
/// Harvests 20 (a deer, 1 hour old), 21 (a rabbit, no skull), 22 (a deer, 73 hours) and 23 (a deer, 71 hours) are ME's, 20 is also the other player's; 9 is a restored harvest.</summary>
public class TaxidermyTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    const long OTHER = 8;
    const string Current = "game_web.test", V2 = "apiv2.test", Gold = """{"currency": "gold"}""";
    string tokenMe = "", tokenOther = "", cookieMe = "", cookieOther = "";
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        var app = APP;
        app["items"]!.AsArray().Add(O("""{"id": 744, "name": "Taxidermy (immediate)", "price": 10, "silver_price": 2000}"""));
        app["items"]!.AsArray().Add(O("""{"id": 1111, "name": "Skull Mount (immediate)", "price": 10, "silver_price": 2000}"""));
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false);
        state.CreateAccount(OTHER, "second");
        (tokenMe, tokenOther) = (state.Store.CreateToken(ME), state.Store.CreateToken(OTHER));
        (cookieMe, cookieOther) = (state.Store.CreateWebSession(ME, TimeSpan.FromHours(1)), state.Store.CreateWebSession(OTHER, TimeSpan.FromHours(1)));
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        Scoring.Variants = new() { [1] = (30, 260, 1.1) };
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            State.AddCurrency(con, "gm", 5000, "test");  // 182 em$ and 5,033 gm$
            AddHarvest(con, 20, "MULE_DEER", 1);
            AddHarvest(con, 21, "COTTONTAIL_RABBIT", 1);
            AddHarvest(con, 22, "MULE_DEER", 73);
            AddHarvest(con, 23, "MULE_DEER", 71);
            PlayerRows.AddTrophy(con, "/game/user/harvest", "9", O("""{"id": 9, "taxidermized": false}"""));
            con.Commit();
        }
        using (var con = state.Store.Open(OTHER))
        {
            con.Begin();
            AddHarvest(con, 20, "MULE_DEER", 1);
            con.Commit();
        }
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    static void AddHarvest(Db con, long kill, string species, int hoursAgo) =>
        con.Exec("INSERT INTO harvests (account_id, kill_id, body) VALUES ($a, ?, ?)", kill, Py.Dumps(O($$"""
            {"species_define": "{{species}}", "timestamp": "{{DateTime.UtcNow.AddHours(-hoursAgo):yyyy-MM-dd'T'HH:mm:ss'Z'}}", "taxidermized": false, "is_skull": false,
             "species": 1, "score": 5, "weight": 100}
            """)));

    static readonly (string, string)[] Json = [("Content-Type", "application/json")];

    /// <summary>The game's call: JSON, the JWT as a Bearer token, on the host of the old build's API v2 or the current build's web host.</summary>
    async Task<Reply> Game(string host, long kill, string? token, string body) =>
        await server.SendAsync("POST", host, host == V2 ? $"/game/user/harvest/{kill}/taxidermize" : $"/v1/harvest/{kill}/taxidermize",
                               [.. Json, .. token is null ? [] : new[] { ("Authorization", "Bearer " + token) }], Encoding.UTF8.GetBytes(body));

    async Task<int> Status(long kill, string? token, string body) => (await Game(Current, kill, token, body)).Status;

    async Task<Reply> Web(long kill, string? cookie, string body, bool ownPage = true)
    {
        List<(string, string)> headers = [.. Json];
        if (cookie is not null) headers.Add(("Cookie", $"{SignIn.SessionCookie}={cookie}"));
        if (ownPage) headers.Add(("X-Requested-With", "XMLHttpRequest"));
        return await server.SendAsync("POST", "web.test", $"/account/harvests/{kill}/taxidermize", headers, Encoding.UTF8.GetBytes(body));
    }

    JsonNode Page(long account, long kill) => Reply(state.HarvestReply(account, kill)!);
    double Em(long account = ME) => state.Wallet(account)["em"]!.GetValue<double>();
    double Gm(long account = ME) => state.Wallet(account)["gm"]!.GetValue<double>();
    static string Text(Reply r) => Encoding.UTF8.GetString(r.Body);

    [Fact]
    public async Task TheCurrentBuildsPathChargesEmsAndWritesTheWalletRow()
    {
        var reply = await Game(Current, 20, tokenMe, Gold);
        Assert.Equal(200, reply.Status);
        Assert.Empty(reply.Body);
        Assert.Equal(172, Em());
        Assert.True((bool)Page(ME, 20)["taxidermized"]!);
        Assert.False((bool)Page(ME, 20)["is_skull"]!);
        var row = state.WalletPage(ME, "em", 0)[0]!;
        Eq("""["In-game payment.", 10.0, "D", ["Taxidermy (immediate)"]]""", T(row["summary"], row["amount"], row["type"]!["id"], new JsonArray(row["items"]!.AsArray().Select(i => i!["name"]!.DeepClone()).ToArray())));
        Eq("""[{"amount": 172.0, "type": "em", "update": 15}]""", new JsonArray(Hb().Where(u => (int)u!["update"]! == 15).Select(u => u!.DeepClone()).ToArray()));  // the game's wallet follows
        Assert.Equal(10.0, state.WalletSpent(ME)["spent"]!.GetValue<double>());
    }

    [Fact]
    public async Task GmIsPaidInSilverAndASkullMountIsTheOtherStoreItem()
    {
        Assert.Equal(200, await Status(20, tokenMe, """{"currency": "silver", "is_skull": true}"""));
        Assert.Equal((182, 3033), (Em(), Gm()));
        Assert.True((bool)Page(ME, 20)["is_skull"]!);
        var row = state.WalletPage(ME, "gm", 0)[0]!;
        Eq("""[2000.0, ["Skull Mount (immediate)"]]""", T(row["amount"], new JsonArray(row["items"]!.AsArray().Select(i => i!["name"]!.DeepClone()).ToArray())));
        // the old build's path, with the form its JSON becomes on the way, is the same purchase
        Assert.Equal(200, (await server.SendAsync("POST", V2, "/game/user/harvest/23/taxidermize",
            [("Content-Type", "application/x-www-form-urlencoded"), ("Authorization", "Bearer " + tokenMe)], "currency=gold&is_skull=true"u8.ToArray())).Status);
        Assert.Equal(172, Em());
        Assert.True((bool)Page(ME, 23)["is_skull"]!);
    }

    [Fact]
    public async Task ASkullIsRefusedForASpeciesWithoutOne()
    {
        var reply = await Game(Current, 21, tokenMe, """{"currency": "gold", "is_skull": true}""");
        Assert.Equal((400, """{"error": "no_skull"}"""), (reply.Status, Text(reply)));
        Assert.Equal(182, Em());
        Assert.Equal(200, await Status(21, tokenMe, Gold));
    }

    [Fact]
    public async Task NotEnoughMoneyRefusesAndChangesNothing()
    {
        var reply = await Game(Current, 20, tokenOther, """{"currency": "silver"}""");  // the other player has 33 gm$
        Assert.Equal((402, """{"error": "not_enough"}"""), (reply.Status, Text(reply)));
        Assert.Equal(33, Gm(OTHER));
        Assert.False((bool)Page(OTHER, 20)["taxidermized"]!);
        Assert.Empty(state.WalletPage(OTHER, "gm", 0));
    }

    [Fact]
    public async Task OnceOnlyWhateverTheProduct()
    {
        Assert.Equal(200, await Status(20, tokenMe, Gold));
        foreach (var body in new[] { Gold, """{"currency": "silver", "is_skull": true}""" })
            Assert.Equal(409, await Status(20, tokenMe, body));
        Assert.Equal((172, 5033), (Em(), Gm()));
    }

    [Fact]
    public async Task OnlyWithin72HoursOfTheHarvest()
    {
        Assert.Equal(410, await Status(22, tokenMe, Gold));  // 73 hours
        Assert.Equal(182, Em());
        Assert.Equal(200, await Status(23, tokenMe, Gold));  // 71 hours
        state.Clock = () => Py.Now() + 72 * 3600;  // kill 20 is 73 hours old now
        Assert.Equal(410, await Status(20, tokenMe, Gold));
        Assert.Equal(409, await Status(23, tokenMe, Gold));  // done is done, late or not
        Assert.Equal(172, Em());
    }

    [Fact]
    public async Task ARestoredHarvestAndAnotherPlayersAreRefused()
    {
        Assert.Equal(404, (await Game(V2, 9, tokenMe, Gold)).Status);  // a restored harvest: never
        Assert.Equal(404, await Status(99, tokenMe, Gold));
        Assert.Equal(404, await Status(22, tokenOther, Gold));  // ME's kill 22 is not the other's
        Assert.Equal(400, await Status(20, tokenMe, """{"currency": "euro"}"""));
        Assert.Equal(400, await Status(20, tokenMe, ""));
        Assert.Equal(200, await Status(20, tokenOther, Gold));  // the other's own kill 20
        Assert.False((bool)Page(ME, 20)["taxidermized"]!);  // ME's kill with the same number is its own
        Assert.True((bool)Page(OTHER, 20)["taxidermized"]!);
        Assert.Equal(182, Em());
    }

    [Fact]
    public async Task OnlyATaxidermizedHarvestIsInTheLodgeEditorsTrophies()
    {
        async Task<JsonArray> Trophies() => JsonNode.Parse((await server.SendAsync("GET", V2, "/game/user/trophies", [("Authorization", "Bearer " + tokenMe)])).Body)!.AsArray();
        Assert.Empty(await Trophies());
        await Game(Current, 20, tokenMe, Gold);
        await Game(Current, 23, tokenMe, """{"currency": "gold", "is_skull": true}""");
        var rows = await Trophies();
        Eq("""[[20, false], [23, true]]""", new JsonArray(rows.Select(t => (JsonNode?)T(t!["id"], t["is_skull"])).ToArray()));
    }

    /// <summary>The taxidermy went live 10 hours ago: harvests 22 (73 h old) and 23 (71 h old) were made before, 20 and 21 (1 h old) after; the grace window of the old ones ends 62 hours from now.</summary>
    void GoneLive()
    {
        var (start, end) = (DateTimeOffset.UtcNow.AddHours(-10).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddHours(62).ToUnixTimeSeconds());
        using var con = state.Store.Open();
        con.Exec("UPDATE taxidermy_grace SET start = ?, grace_end = ?", start, end);
        using var mine = state.Store.Open(ME);
        mine.Begin();
        PlayerRows.AddTrophy(mine, "/game/user/trophies", "5", O("""{"id": 5, "species": 1, "hidden": false}"""));
        mine.Commit();
    }

    [Fact]
    public async Task AHarvestMadeBeforeTheTaxidermyWentLiveHasAWindowThatEndsAtTheGraceEnd()
    {
        GoneLive();
        async Task<long[]> Editor() => [.. JsonNode.Parse((await server.SendAsync("GET", V2, "/game/user/trophies", [("Authorization", "Bearer " + tokenMe)])).Body)!.AsArray().Select(t => (long)t!["id"]!)];
        var (grace, offers) = (state.Store.TaxidermyGrace().End, state.TaxidermyOffers(ME));
        Assert.Equal(grace, new DateTimeOffset(DateTime.Parse((string)offers[22]["end"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)).ToUnixTimeSeconds());  // old: the grace end, not harvest + 72 h
        Assert.Equal(grace, new DateTimeOffset(DateTime.Parse((string)offers[23]["end"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)).ToUnixTimeSeconds());
        Assert.InRange(DateTime.Parse((string)offers[20]["end"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).Subtract(DateTime.UtcNow).TotalHours, 70.9, 71.1);  // new: 72 h from the harvest (1 h ago)
        Eq("[5, 22, 23]", new JsonArray((await Editor()).Select(i => (JsonNode)i).ToArray()));  // the restored trophy, and the old harvests while the window is open
        Assert.Equal(200, await Status(22, tokenMe, Gold));  // 73 hours old, yet inside the grace window
        Assert.Equal(172, Em());
        Assert.Equal(409, await Status(22, tokenMe, Gold));
        state.Clock = () => Py.Now() + 63 * 3600;  // the grace window is over; kill 20 (1 h old) has 8 hours left
        Assert.Equal(410, await Status(23, tokenMe, Gold));
        Assert.Equal(200, await Status(20, tokenMe, Gold));
        Eq("[5, 20, 22]", new JsonArray((await Editor()).Select(i => (JsonNode)i).ToArray()));  // taxidermized ones stay, the old one that was not is gone, the restored one is untouched
    }

    [Fact]
    public async Task AMountedHarvestKeepsItsDetailsInItsLodgeAfterTheWindowEnded()
    {
        GoneLive();
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            PlayerRows.AddLodge(con, "/game/user/lodges", "3", O("""{"id": 3, "name": "cabin", "locked": false, "style_id": 1, "animal_slots": [{"slot_id": 0, "trophy_id": 22, "pose_id": 1}], "weapon_slots": []}"""));
            con.Commit();
        }
        state.Clock = () => Py.Now() + 63 * 3600;  // the grace window is over and kill 22 was never taxidermized
        var lodges = JsonNode.Parse((await server.SendAsync("GET", V2, $"/game/user/{ME}/lodges", [("Authorization", "Bearer " + tokenMe)])).Body)!.AsArray();
        Eq("""[22, "MULE_DEER"]""", T(lodges[0]!["animal_slots"]![0]!["trophy_details"]!["id"], lodges[0]!["animal_slots"]![0]!["trophy_details"]!["species_define"]));
        var editor = JsonNode.Parse((await server.SendAsync("GET", V2, "/game/user/trophies", [("Authorization", "Bearer " + tokenMe)])).Body)!.AsArray();
        Assert.DoesNotContain(editor, t => (long)t!["id"]! == 22);  // yet it is not in the editor's list
    }

    [Fact]
    public void TheFirstStartWithTheTaxidermyStoresTheGraceEndOnce()
    {
        var file = Path.Combine(tmp, "old.db");
        var first = new Store(file);
        Assert.Equal((0L, 0L), first.TaxidermyGrace());  // a new database has no older harvest
        using (var con = first.Open())
        {
            con.Exec("DROP TABLE taxidermy_grace");
            con.Exec("PRAGMA user_version = 27");
        }
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (start, end) = new Store(file).TaxidermyGrace();
        Assert.InRange(start, before, before + 5);
        Assert.Equal(start + 72 * 3600, end);
        Assert.Equal((start, end), new Store(file).TaxidermyGrace());  // later starts keep it
    }

    [Fact]
    public async Task ThePlayersOwnPageBuysWithEitherCurrencyAndTellsWhyItRefuses()
    {
        var done = await Web(20, cookieMe, """{"product": "taxidermy", "currency": "ems"}""");
        Assert.Equal(200, done.Status);
        Eq("""{"done": "taxidermy", "ems": 172, "gms": 5033}""", JsonNode.Parse(done.Body)!);
        Eq("""{"error": "already"}""", JsonNode.Parse((await Web(20, cookieMe, """{"product": "skull", "currency": "gms"}""")).Body)!);
        done = await Web(23, cookieMe, """{"product": "skull", "currency": "gms"}""");
        Eq("""{"done": "skull", "ems": 172, "gms": 3033}""", JsonNode.Parse(done.Body)!);
        var late = await Web(22, cookieMe, """{"product": "taxidermy", "currency": "ems"}""");
        Assert.Equal((410, """{"error": "too_late"}"""), (late.Status, Text(late)));
        var poor = await Web(20, cookieOther, """{"product": "taxidermy", "currency": "gms"}""");
        Assert.Equal((402, """{"error": "not_enough", "currency": "gms"}"""), (poor.Status, Text(poor)));
        Assert.Equal(404, (await Web(9, cookieMe, """{"product": "taxidermy", "currency": "ems"}""")).Status);  // restored
        Assert.Equal(404, (await Web(22, cookieOther, """{"product": "taxidermy", "currency": "ems"}""")).Status);  // not the other's
        Assert.Equal(404, (await Web(777, cookieMe, """{"product": "taxidermy", "currency": "ems"}""")).Status);
        Assert.Equal(400, (await Web(21, cookieMe, """{"product": "skull", "currency": "ems"}""")).Status);  // no skull for a rabbit
        foreach (var body in new[] { "", "{}", """{"product": "mount", "currency": "ems"}""", """{"product": "taxidermy", "currency": "gold"}""", "[1]" })
            Assert.Equal(400, (await Web(21, cookieMe, body)).Status);
        Assert.Equal(401, (await Web(21, null, """{"product": "taxidermy", "currency": "ems"}""")).Status);
        Assert.Equal(400, (await Web(21, cookieMe, """{"product": "taxidermy", "currency": "ems"}""", ownPage: false)).Status);
        Assert.False((bool)Page(ME, 21)["taxidermized"]!);
    }

    async Task<JsonArray> Kills(string? cookie)
    {
        var reply = await server.SendAsync("GET", "web.test", "/hunters/second/hunts/latest", cookie is null ? [] : [("Cookie", $"{SignIn.SessionCookie}={cookie}")]);  // ME is the recorded player's account: no public page
        Assert.Equal(200, reply.Status);
        return JsonNode.Parse(reply.Body)!["kills"]!.AsArray();
    }

    [Fact]
    public async Task OnlyTheOwnersHuntPageOffersTheTaxidermy()
    {
        state.NoteLaunch(OTHER, "start=100&reserve=2"u8.ToArray());
        state.HeartbeatReply(OTHER, Events(AccountIsolationTests.Harvest(71, 50000)));  // a kill made by the game
        var kill = (long)(await Kills(cookieOther))[0]!["id"]!;
        var offer = (await Kills(cookieOther))[0]!["taxidermy"]!;
        Eq("""[10, 2000, true, null]""", T(offer["ems"], offer["gms"], offer["skull"], offer["done"]));
        var end = DateTime.Parse((string)offer["end"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        Assert.InRange((end - DateTime.UtcNow).TotalHours, 71.9, 72.01);
        Assert.Null((await Kills(null))[0]!["taxidermy"]);       // anyone
        Assert.Null((await Kills(cookieMe))[0]!["taxidermy"]);   // another player
        Assert.Equal(200, (await Web(kill, cookieOther, """{"product": "skull", "currency": "ems"}""")).Status);
        Assert.Equal("skull", (string?)(await Kills(cookieOther))[0]!["taxidermy"]!["done"]);
        state.Clock = () => Py.Now() + 73 * 3600;
        Assert.NotNull((await Kills(cookieOther))[0]!["taxidermy"]);  // the end stays: the page hides the button
    }

    [Fact]
    public void ThePriceIsAFactsValueAndZeroIsFree()
    {
        var facts = Facts.Parse("""{"format": 1, "taxidermy": {"ems": 0, "gms": 7}, "trophy_lodge": {"catalogue": {"skull": [{"define": "MULE_DEER"}]}}}""");
        state = new State(Path.Combine(tmp, "facts.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, facts: facts);
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            AddHarvest(con, 20, "MULE_DEER", 1);
            con.Commit();
        }
        Assert.Equal(7, state.TaxidermyOffers(ME)[20]["gms"]!.GetValue<long>());
        Assert.Equal((200, (string?)null), state.Taxidermize(ME, 20, State.TAXIDERMY, "em"));
        Assert.Equal(182, Em());  // free: no wallet row either
        Assert.DoesNotContain(state.WalletPage(ME, "em", 0), r => (string)r!["summary"]! == State.PAYMENT);
    }

    [Fact]
    public async Task TheOperatorListsAndUndoesATaxidermyAndTheMoneyComesBack()
    {
        await Game(Current, 20, tokenMe, """{"currency": "silver", "is_skull": true}""");
        var ct = TestContext.Current.CancellationToken;
        var row = Assert.Single(JsonNode.Parse(await server.Http.GetStringAsync("http://localhost/admin/taxidermies", ct))!.AsArray())!;
        Eq("""[20, "MULE_DEER", "skull", "gm", 2000]""", T(row["kill"], row["species"], row["done"], row["currency"], row["paid"]));
        Assert.Equal(404, (int)(await server.Http.PostAsync("http://localhost/admin/taxidermies/23/undo", null, ct)).StatusCode);  // not taxidermized
        var undone = await server.Http.PostAsync("http://localhost/admin/taxidermies/20/undo", null, ct);
        Assert.Equal("""{"kill":20,"currency":"gm","refunded":2000}""", await undone.Content.ReadAsStringAsync(ct));
        Assert.Equal(5033, Gm());
        Assert.Empty(JsonNode.Parse(await server.Http.GetStringAsync("http://localhost/admin/taxidermies", ct))!.AsArray());
        Assert.False((bool)Page(ME, 20)["taxidermized"]!);
        Assert.False((bool)Page(ME, 20)["is_skull"]!);
        Assert.Equal(200, await Status(20, tokenMe, Gold));  // it can be bought again within its window
        await server.Http.PostAsync("http://localhost/admin/taxidermies/20/undo?refund=false", null, ct);
        Assert.Equal(172, Em());  // kept
    }
}
