using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>GET /catalog/reserves (the reserves with their starts, public) and POST /auth/launch with a reserve and a lodge (the hunt opens there, as start_single_player does).</summary>
public class LaunchCatalogueTests : StateTestBase
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    // fictional values: reserve 1 has three lodges and a tower, its map the 2000 x 2000 square from (-500, -500); reserve 2 a lodge and a named point of interest, its map empty
    // (so the square around the starts); reserve 3 no start; a start of another type (a stash) is not offered; item 811 is the loadout slot of the store
    static readonly Facts MapFacts = Facts.Parse("""{"format": 1, "reserves": [{"id": 1, "name": "First Reserve", "map": [-500, -500, 1500, 1500]}, {"id": 2, "name": "Test Reserve", "map": [5, 5, 5, 9]}]}""");
    static JsonObject Catalogue()
    {
        var app = APP;
        app["reserves"] = JsonNode.Parse("""[{"id": 2, "name": "Test Reserve"}, {"id": 1, "name": "First Reserve"}, {"id": 3, "name": "Empty Reserve"}]""");
        app["locations"] = JsonNode.Parse("""
            [{"id": 13, "pos": {"x": 333, "y": 1, "z": -77}, "type": "LODGE", "reserve": 1},
             {"id": 12, "pos": {"x": 500, "y": 1, "z": 1000}, "type": "HUNTING_TOWER", "reserve": 1},
             {"id": 11, "pos": {"x": 1000, "y": 1, "z": 500}, "type": "LODGE", "reserve": 1},
             {"id": 10, "pos": {"x": 0, "y": 1, "z": 0}, "type": "LODGE", "reserve": 1},
             {"id": 101, "pos": {"x": -6420, "y": 150, "z": 3310}, "type": "LODGE", "reserve": 2},
             {"id": 102, "pos": {"x": -6000, "y": 150, "z": 3000}, "type": "INTEREST", "reserve": 2, "name": "Old Mill"},
             {"id": 103, "pos": {"x": -6000, "y": 150, "z": 3000}, "type": "STASH", "reserve": 2}]
            """);
        app["items"]!.AsArray().Add(JsonNode.Parse("""
            {"id": 811, "name": "Test Loadout Slot", "description": "", "define": "TEST_ITEM_811", "type": 41, "group": 13, "cat": 41, "client_type": 4,
             "image": "x", "variationname": null, "units": null, "fit": {}, "inventory": {"slots": [0, 1, 8], "capacity": 0, "weight": 1},
             "price": 10, "silver_price": 100, "notbuyable": 0, "ownMultiple": true, "subcat": {"id": 0}, "hidden": 0}
            """));
        foreach (var gear in new[] { Gear(901, "TENT02", "Test Tent"), Gear(902, "TREE_STAND01", "Test Stand"), Gear(903, "ELECTRONIC_CALLER", ""), Gear(904, "GROUND_BLIND01", "Test Blind"),
                                     Gear(905, "CLIMBING_GEAR_01", "Test Climbing Gear") })
            app["items"]!.AsArray().Add(gear);
        return app;
    }

    /// <summary>An item that can be placed (slot 23), with an invented name and define.</summary>
    static JsonNode Gear(int id, string define, string name) => JsonNode.Parse($$"""
        {"id": {{id}}, "name": "{{name}}", "description": "", "define": "{{define}}", "type": 41, "group": 13, "cat": 41, "client_type": 4,
         "image": "x", "variationname": null, "units": null, "fit": {}, "inventory": {"slots": [0, 23], "capacity": 0, "weight": 1},
         "price": 10, "silver_price": 100, "notbuyable": 0, "ownMultiple": true, "subcat": {"id": 0}, "hidden": 0}
        """)!;

    async Task<Loopback> Start(JsonObject? app = null, bool offline = false, Facts? facts = null)
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, app ?? Catalogue(), LOCALE, TEXTURES, offline: offline, facts: facts);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        return await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    async Task<Reply> Reserves(JsonObject? app = null, bool offline = false)
    {
        await using var server = await Start(app, offline, MapFacts);
        return await server.SendAsync("GET", "web.test", "/catalog/reserves", []);
    }

    [Fact]
    public async Task TheReservesAreListedWithTheirStartsAsPercentOfTheirMapPicture()
    {
        var reply = await Reserves();
        Assert.Equal(200, reply.Status);
        var catalogue = JsonNode.Parse(reply.Body)!;
        var reserves = catalogue["reserves"]!.AsArray();
        Assert.Equal(["2:Test Reserve", "1:First Reserve", "3:Empty Reserve"], reserves.Select(r => $"{r!["id"]}:{r["name"]}"));  // the catalogue's order
        // a start is at its percent of the facts file's map square, world x to the right and z down, two decimals; a start without a name is its kind and its number
        Assert.Equal("""[{"id":10,"kind":"lodge","name":"Lodge 1","x":25.0,"y":25.0},{"id":11,"kind":"lodge","name":"Lodge 2","x":75.0,"y":50.0},{"id":12,"kind":"tower","name":"Tower 1","x":50.0,"y":75.0},{"id":13,"kind":"lodge","name":"Lodge 3","x":41.65,"y":21.15}]""",
            reserves[1]!["starts"]!.ToJsonString());
        // a named start keeps its name; the stash is not offered; no usable map square: the square around the starts with a tenth of margin
        Assert.Equal("""[{"id":101,"kind":"lodge","name":"Lodge 1","x":10.0,"y":79.52},{"id":102,"kind":"interest","name":"Old Mill","x":90.0,"y":20.48}]""", reserves[0]!["starts"]!.ToJsonString());
        Assert.Empty(reserves[2]!["starts"]!.AsArray());
        Assert.Equal(["1:Test One:2", "2:Test Two:4", "3:Test Three:1", "4:Test Four:10", "5:Test Five:2"],
            catalogue["tutorials"]!.AsArray().Select(t => $"{t!["id"]}:{t["name"]}:{t["reserve"]}"));
    }

    [Fact]
    public async Task WithoutReservesThereIsNoCatalogue()
    {
        var app = Catalogue();
        app["reserves"] = new JsonArray();
        Assert.Equal(404, (await Reserves(app)).Status);
    }

    [Fact]
    public async Task ALocalServerDoesNotServeIt() => Assert.Equal("miss", (await Reserves(offline: true)).Source);

    /// <summary>POST /auth/launch as the launcher sends it, signed in as ME; prepare runs on the started state first.</summary>
    async Task<Reply> Launch(string? form, bool signedIn = true, bool ownPage = true, Action? prepare = null)
    {
        await using var server = await Start();
        prepare?.Invoke();
        List<(string, string)> headers = [("Content-Type", "application/x-www-form-urlencoded")];
        if (ownPage) headers.Add(("X-Requested-With", "XMLHttpRequest"));
        if (signedIn) headers.Add(("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}"));
        return await server.SendAsync("POST", "web.test", "/auth/launch", headers, form is null ? [] : Encoding.UTF8.GetBytes(form));
    }

    /// <summary>"reserve:location" of each hunt ME has open, joined.</summary>
    string Open()
    {
        using var con = state.Store.Open(ME);
        return string.Join(",", con.Rows("SELECT reserve, location_id FROM expeditions WHERE account_id = $a AND end_ts IS NULL").Select(r => $"{r[0]}:{r[1]}"));
    }

    [Fact]
    public async Task AReserveAndALodgeOpenTheHuntThereAndGiveTheLaunchUrl()
    {
        var reply = await Launch("reserve=1&lodge=11&time=480&loadout=0");
        Assert.Equal(200, reply.Status);
        Assert.Matches($@"\A{ProtocolLiterals.LaunchScheme}://game_web\.test/[0-9a-f]{{40}}/lang/en_US/access_token/[\w.-]+/refresh_token/[\w.-]+\z", (string)JsonNode.Parse(reply.Body)!["url"]!);
        Assert.Equal("1:11", Open());
        // the connect of the hunt starts at that lodge, as one after start_single_player
        Assert.Equal(11L, (long)JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!["lodge"]!);
        Assert.Equal(480L, (long)JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!["start_time"]!);  // at the picked hour
    }

    [Fact]
    public async Task TheLoadoutIsPutOnAsAtStartSinglePlayer()
    {
        long id = 0;
        long RifleSlot() => state.Inventory(ME).Where(i => (long)i!["item_id"]! == 5).Select(i => (long)i!["slot"]!).Single();
        var reply = await Launch(null, prepare: () =>
        {
            using (var rifle = state.Store.Open(ME))
            {
                rifle.Begin();
                PlayerRows.AddItem(rifle, 301, 5, 0, null, new JsonObject());
                rifle.Commit();
            }
            state.GrantItems(ME, 811);
            using (var con = state.Store.Open(ME))
                id = (long)con.Rows("SELECT id FROM loadouts WHERE account_id = $a ORDER BY id")[0][0];
            Assert.Equal(200, state.SaveLoadout(ME, id, Encoding.UTF8.GetBytes("""{"name": "My hunt", "color": 3, "items": [{"slot_id": 2, "item_id": 5}]}""")).Status);
            Assert.Equal(0, RifleSlot());
        });
        Assert.Equal(200, reply.Status);
        Assert.Equal(0, RifleSlot());  // no fields: the equipment stays
        reply = await Launch($"reserve=1&lodge=11&loadout={id}");  // a new state over the same database
        Assert.Equal(200, reply.Status);
        Assert.Equal(2, RifleSlot());
    }

    [Fact]
    public async Task WithoutTheFieldsTheLaunchUrlComesAloneAsBefore()
    {
        var reply = await Launch(null);
        Assert.Equal(200, reply.Status);
        Assert.StartsWith(ProtocolLiterals.LaunchScheme + "://", (string)JsonNode.Parse(reply.Body)!["url"]!);
        Assert.Equal("", Open());
    }

    [Theory]
    [InlineData("reserve=1")]                          // no lodge
    [InlineData("lodge=11")]                           // no reserve
    [InlineData("reserve=1&lodge=11&time=soon")]
    [InlineData("reserve=1&lodge=11&loadout=-1")]
    [InlineData("reserve=x&lodge=11")]
    [InlineData("reserve=99999999999999999999&lodge=11")]  // more digits than a whole number holds
    [InlineData("reserve=1&lodge=11&loadout=99999999999999999999")]
    public async Task AnIncompleteOrMalformedStartIsRefusedAndOpensNothing(string form)
    {
        Assert.Equal(400, (await Launch(form)).Status);
        Assert.Equal("", Open());
    }

    [Fact]
    public async Task TheLoadoutsOfThePlayerAreListedForTheSignedInPlayerOnly()
    {
        await using var server = await Start();
        state.GrantItems(ME, 811);  // one loadout per unit
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/loadouts", [])).Status);
        var reply = await server.SendAsync("GET", "web.test", "/account/loadouts", [cookie]);
        Assert.Equal(200, reply.Status);
        var loadouts = JsonNode.Parse(reply.Body)!.AsArray();
        using var con = state.Store.Open(ME);
        Assert.Equal(con.Rows("SELECT id FROM loadouts WHERE account_id = $a ORDER BY id").Select(r => (long)r[0]), loadouts.Select(l => (long)l!["id"]!));
        Assert.NotEmpty(loadouts);
        Assert.All(loadouts, l => Assert.False(string.IsNullOrEmpty((string)l!["name"]!)));
    }

    [Fact]
    public async Task OnlyASignedInPlayerFromAPageOfTheServerStartsAHunt()
    {
        Assert.Equal(401, (await Launch("reserve=1&lodge=11", signedIn: false)).Status);
        Assert.Equal(400, (await Launch("reserve=1&lodge=11", ownPage: false)).Status);
        Assert.Equal("", Open());
    }

    [Fact]
    public async Task ATutorialOpensItsSessionAndNoHunt()
    {
        var reply = await Launch("tutorial=2");
        Assert.Equal(200, reply.Status);
        var session = System.Text.RegularExpressions.Regex.Match((string)JsonNode.Parse(reply.Body)!["url"]!, @"://game_web\.test/([0-9a-f]{40})/").Groups[1].Value;
        Assert.Equal(ME, state.Store.ResolveToken(session));
        var expedition = JsonNode.Parse(state.ConnectReply(ME, session))!["expedition"]!;
        Assert.Equal((1, 2), ((int)expedition["mode"]!, (int)expedition["tutorial"]!));
        Assert.Equal("", Open());
        Assert.Equal(400, (await Launch("tutorial=99")).Status);
    }

    /// <summary>Two hunts from different lodges and one with no start: the game's list carries each one's location id, the website's history and hunt page its name
    /// (the catalogue's `name`, else the game's `locations_descr` text of the connect, else the kind and its number); no start is no lodge.</summary>
    [Fact]
    public void EachHuntListsItsOwnStartingLodgeInTheGameAndOnTheWebsite()
    {
        var connect = CONNECT;
        connect["locations_descr"] = JsonNode.Parse("""[{"id": 11, "name": "Border Lodge", "description": null}, {"id": 102, "name": "Ignored", "description": null}]""");
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, Catalogue(), LOCALE, TEXTURES);
        state.NoteLaunch(ME, "start=10&reserve=1"u8.ToArray());
        state.NoteLaunch(ME, "reserve=1&lodge_id=11"u8.ToArray());  // the launcher's form
        state.NoteLaunch(ME, "start=102&reserve=2"u8.ToArray());  // a point of interest with a name of its own
        state.NoteLaunch(ME, "reserve=2"u8.ToArray());  // no start

        var game = JsonNode.Parse(state.ExpeditionListReply(ME, "{}"u8.ToArray(), []))!["expeditions"]!.AsArray();
        Eq("[null, 102, 11, 10]", new JsonArray(game.Select(e => e!["location_id"]?.DeepClone()).ToArray()));
        var rows = state.PublicHunts(ME, 1)["rows"]!.AsArray();
        Eq("""["", "Old Mill", "Border Lodge", "Lodge 1"]""", new JsonArray(rows.Select(r => (JsonNode?)r!["lodge"]!.DeepClone()).ToArray()));
        Eq("""["", "Old Mill", "Border Lodge", "Lodge 1"]""", new JsonArray(rows.Select(r => (JsonNode?)state.PublicHunt(ME, (long)r!["id"]!)!["lodge"]!.DeepClone()).ToArray()));
    }

    // ---- placed items and tents

    /// <summary>The game's way of placing: the player owns the item in slot 0, has a hunt in the reserve, then heartbeat event 25 (millimetres, direction x 1000) and event 8 into slot 23.</summary>
    void Place(long guid, long itemId, long reserve, long mmX, long mmZ)
    {
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            PlayerRows.AddItem(con, guid, itemId, 0, null, new JsonObject());
            con.Commit();
        }
        state.NoteLaunch(ME, Encoding.ASCII.GetBytes($"reserve={reserve}"));
        state.HeartbeatReply(ME, Events(O($$"""{"event": 25, "item": "{{guid}}", "position_x": {{mmX}}, "position_y": 1000, "position_z": {{mmZ}}, "direction_x": 500, "direction_y": 0, "direction_z": -500}"""),
                                        O($$"""{"event": 8, "item": "{{guid}}", "slot": 23}""")));
    }

    [Fact]
    public async Task ThePlacedTentsCallersBlindsAndStandsOfThePlayerAreListedAtTheirPlace()
    {
        await using var server = await Start(facts: MapFacts);
        Place(501, 901, 1, 500_000, 500_000);        // a tent in the middle of the first reserve's map
        Place(502, 902, 1, -500_000, 1_500_000);     // a stand at its left edge, at the bottom
        Place(503, 903, 1, 1_500_000, -500_000);     // a caller the catalogue has no name for, at the right edge, at the top
        Place(504, 904, 2, -6_420_000, 3_310_000);   // a blind where the second reserve's lodge is
        Place(505, 905, 1, 0, 0);                    // climbing gear is not on the map
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            PlayerRows.AddItem(con, 506, 901, 0, null, new JsonObject());                                       // a tent that is not placed
            PlayerRows.AddItem(con, 507, 901, 23, null, O("""{"__position": {"x": 1, "y": 2, "z": 3}}"""));   // a placed one with no reserve
            con.Commit();
        }
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/placed", [])).Status);
        var reply = await server.SendAsync("GET", "web.test", "/account/placed", [cookie]);
        Assert.Equal(200, reply.Status);
        var lodge = JsonNode.Parse(Py.DumpBytes(state.LaunchCatalogue()))!["reserves"]!.AsArray().Single(r => (int)r!["id"]! == 2)!["starts"]!.AsArray().Single(s => (int)s!["id"]! == 101)!;
        Eq($$"""
            {"items": [{"id": 501, "reserve": 1, "kind": "tent", "name": "Test Tent", "x": 50, "y": 50},
                       {"id": 502, "reserve": 1, "kind": "treestand", "name": "Test Stand", "x": 0, "y": 100},
                       {"id": 503, "reserve": 1, "kind": "caller", "name": "Caller", "x": 100, "y": 0},
                       {"id": 504, "reserve": 2, "kind": "groundblind", "name": "Test Blind", "x": {{lodge["x"]}}, "y": {{lodge["y"]}}}]}
            """, JsonNode.Parse(reply.Body));  // the second reserve's map is the square round its starts, so the blind has the lodge's own percent
    }

    [Fact]
    public async Task AReserveAndAPlacedTentOpenTheHuntAndTheGameSpawnsAtTheTent()
    {
        var reply = await Launch("reserve=1&tent=501&time=480", prepare: () => Place(501, 901, 1, 500_000, 500_000));
        Assert.Equal(200, reply.Status);
        Assert.Equal("1:0", Open());  // the hunt is at the reserve's first location number
        var expedition = JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!;  // lodge 0: the game reads reserve 1 from it (lodge / 100)
        Eq("[0, 500.0, 1.0, 500.0, 500, 0, -500, 480]", T(expedition["lodge"], expedition["x"], expedition["y"], expedition["z"], expedition["dir_x"], expedition["dir_y"], expedition["dir_z"], expedition["start_time"]));
        Eq("[0, 500.0]", T(JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!["lodge"], JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!["x"]));  // a second connect of the hunt starts there too

        // the tent is picked up: the hunt starts at its location, which the catalogue does not list, so at the recorded start
        state.HeartbeatReply(ME, Events(O("""{"event": 8, "item": "501", "slot": 0}""")));
        var recorded = JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!;
        Eq("[100, -9150.0]", T(recorded["lodge"], recorded["x"]));
    }

    [Theory]
    [InlineData("reserve=2&tent=501")]                // a tent of another reserve
    [InlineData("reserve=1&tent=502")]                // a stand, not a tent
    [InlineData("reserve=1&tent=999")]                // no item of the player's
    [InlineData("reserve=1&tent=506")]                // a tent that is not placed
    [InlineData("reserve=1&tent=501&lodge=11")]       // two starts
    [InlineData("reserve=1&tent=x")]
    public async Task OnlyAPlacedTentOfThePlayerInThatReserveIsATentStart(string form)
    {
        var reply = await Launch(form, prepare: () =>
        {
            Place(501, 901, 1, 500_000, 500_000);
            Place(502, 902, 1, 0, 0);
            using var con = state.Store.Open(ME);
            con.Begin();
            PlayerRows.AddItem(con, 506, 901, 0, null, new JsonObject());
            con.Commit();
        });
        Assert.Equal(400, reply.Status);
        Assert.Equal("1:", Open());  // still the hunt the placing opened: nothing new
    }

    [Fact]
    public void LaunchStartWithATentLoctypeStartsAtTheTentAndAnythingElseAtNoStart()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, Catalogue(), LOCALE, TEXTURES);
        Place(501, 901, 1, 500_000, 500_000);
        state.NoteLaunch(ME, "start=501&loctype=TENT02&reserve=1&time=540"u8.ToArray());  // the original sent the item's define as the loctype
        var expedition = JsonNode.Parse(state.ConnectReply(ME))!["expedition"]!;
        Eq("[0, 500.0, 540]", T(expedition["lodge"], expedition["x"], expedition["start_time"]));
        state.NoteLaunch(ME, "start=501&loctype=HEATED_TENT&reserve=2"u8.ToArray());  // not placed in reserve 2: no start, as with any start that is nothing
        Assert.Equal("2:", Open());
        state.NoteLaunch(ME, "start=501&loctype=LODGE&reserve=1"u8.ToArray());  // a lodge number, never an item guid
        Assert.Equal("1:501", Open());
    }

    [Fact]
    public void TheTentColumnIsAddedToAnOlderDatabase()
    {
        var file = Path.Combine(tmp, "v20.db");
        var store = new Store(file);
        store.CreateAccount(5, "old", new JsonObject());
        using (var con = store.Open())
        {
            con.Exec("ALTER TABLE expeditions DROP COLUMN tent");
            con.Exec("PRAGMA user_version = 20");
        }
        using var check = new Store(file).Open();
        Assert.Equal(1L, check.Scalar("SELECT COUNT(*) FROM pragma_table_info('expeditions') WHERE name = 'tent'"));
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }
}
