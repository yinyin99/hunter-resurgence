using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Loadouts: read and saved through the real HTTP pipeline on both hosts of the current build, put on at a launch, and made by a purchase.</summary>
public class LoadoutTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    const long OWNER = 8, VIEWER = 9;
    const string V2 = "apiv2.test", Web = "game_web.test", Api = "api.test";  // API v2, the game web host (`/v1/...`) and the old build's API host
    const string VndType = "application/vnd.game_data.user.loadout+json; type=collection";
    string owner = "", viewer = "";
    Loopback server = null!;

    /// <summary>Item 811 is the loadout slot, 15 an ammunition (category 3); a bundle of three slots.</summary>
    static JsonObject Catalogue()
    {
        var app = APP;
        foreach (var (id, cat, price) in new[] { (811, 41, 10), (15, 3, 5) })
            app["items"]!.AsArray().Add(O($$"""
                {"id": {{id}}, "name": "Test Item {{id}}", "description": "", "define": "TEST_ITEM_{{id}}", "type": 41, "group": 13, "cat": {{cat}}, "client_type": 4,
                 "image": "x", "variationname": null, "units": null, "fit": {}, "inventory": {"slots": [0, 1, 8], "capacity": 0, "weight": 1},
                 "price": {{price}}, "silver_price": 100, "notbuyable": 0, "ownMultiple": true, "subcat": {"id": 0}, "hidden": 0}
                """));
        app["bundles"] = new JsonArray(O("""{"id": 293, "name": "Test Loadout Bundle", "price": 25, "items": [811, 811, 811]}"""));
        return app;
    }

    State Start(JsonObject? connect = null) =>
        new(Db, Py.DumpBytes(connect ?? Fixtures.CONNECT), BASE, ICON, Catalogue(), LOCALE, TEXTURES, offline: false);

    /// <summary>The owner has two rifles in the backpack and the lodge, a scope, two dogs, an ammunition, and a placed item.</summary>
    public async ValueTask InitializeAsync()
    {
        state = Start();
        state.CreateAccount(OWNER, "owner");
        state.CreateAccount(VIEWER, "viewer");
        (owner, viewer) = (state.Store.CreateToken(OWNER), state.Store.CreateToken(VIEWER));
        using (var con = state.Store.Open(OWNER))
        {
            con.Begin();
            foreach (var (guid, item, slot) in new[] { (301, 5, 3), (302, 5, 0), (303, 2, 5), (304, 700, 0), (305, 700, 29), (306, 610, 23), (307, 15, 0) })
                PlayerRows.AddItem(con, guid, item, slot, null, new JsonObject());
            con.Commit();
        }
        server = await Loopback.StartAsync(new Game(new HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    static (string, string) Bearer(string token) => ("Authorization", "Bearer " + token);

    /// <summary>A request as the page sends a save: JSON under the content type given, the JWT as a Bearer token.</summary>
    async Task<Reply> Call(string method, string host, string path, string? token, string body = "", string type = "text/plain") =>
        await server.SendAsync(method, host, path, [("Content-Type", type), .. token is null ? [] : new[] { Bearer(token) }], Encoding.UTF8.GetBytes(body));

    async Task<JsonArray> Read(string host, string path, string token)
    {
        var reply = await Call("GET", host, path, token);
        Assert.Equal(200, reply.Status);
        return JsonNode.Parse(reply.Body)!.AsArray();
    }

    List<long> Ids(long account)
    {
        using var con = state.Store.Open(account);
        return [.. con.Rows("SELECT id FROM loadouts WHERE account_id = $a ORDER BY id").Select(r => (long)r[0])];
    }

    /// <summary>{guid: slot} of the account's items, the loadout items (811) left out.</summary>
    string Slots(long account = OWNER) =>
        Py.Dumps(new JsonObject(state.Inventory(account).Where(i => (long)i!["item_id"]! != 811).Select(i => KeyValuePair.Create((string)i!["guid"]!, (JsonNode?)i["slot"]!.DeepClone()))
            .OrderBy(p => long.Parse(p.Key))));

    static string Items(params string[] entries) => $$"""{"name": "My hunt", "color": 3, "items": [{{string.Join(", ", entries)}}]}""";

    Task<Reply> Launch(string host, string path, string type, string body, string token) =>
        server.SendAsync("POST", host, path, [("Content-Type", type), Bearer(token)], Encoding.UTF8.GetBytes(body));

    [Fact]
    public async Task TestTheOwnLoadoutsAreReadOnBothHosts()
    {
        state.GrantItems(OWNER, 811, 2);
        foreach (var (host, path, type) in new[] { (V2, "/game/user/loadout", VndType), (Web, "/v1/game/user/loadout?locale=en_US", "application/json") })
        {
            var reply = await Call("GET", host, path, owner);
            Assert.Equal(("state", 200), (reply.Source, reply.Status));
            Assert.Equal(type, reply.Headers.Single(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value);
            var list = JsonNode.Parse(reply.Body)!.AsArray();
            Eq("""[["owner's Loadout #1", 0, []], ["owner's Loadout #2", 0, []]]""", new JsonArray(list.Select(l => (JsonNode?)T(l!["name"], l["color"], l["items"])).ToArray()));
            Assert.Equal("name,color,items,id", string.Join(",", list[0]!.AsObject().Select(p => p.Key)));
            Assert.Equal(Ids(OWNER), list.Select(l => (long)l!["id"]!));  // ascending by id
            Eq("[]", await Read(host, path, viewer));  // nobody else's
        }
        Assert.Equal(401, (await Call("GET", V2, "/game/user/loadout", null)).Status);
        Assert.Equal(401, (await Call("GET", V2, "/game/user/loadout", "unknown")).Status);
        Assert.Contains("\"errorCode\": 5", Encoding.UTF8.GetString((await Call("GET", Web, "/v1/game/user/loadout", null)).Body));  // the web API's own token-expired answer
    }

    [Fact]
    public async Task TestASavedLoadoutReplacesNameColorAndItemsWhateverTheContentType()
    {
        state.GrantItems(OWNER, 811, 2);
        var ids = Ids(OWNER);
        var other = await Read(V2, "/game/user/loadout", owner);
        foreach (var (host, prefix, type, name) in new[] { (V2, "", "json", "My hunt"), (Web, "/v1", "application/json", "Second"), (V2, "", "text/plain", "Third") })
        {
            var reply = await Call("PUT", host, $"{prefix}/game/user/loadout/{ids[0]}", owner,
                Items("""{"slot_id": 3, "item_id": 5}""", """{"slot_id": 29, "item_id": 700, "item_guid": 304}""").Replace("My hunt", name), type);
            Assert.Equal(("state", 200), (reply.Source, reply.Status));
            var saved = $$"""{"name": "{{name}}", "color": 3, "items": [{"slot_id": 3, "item_id": 5, "item_guid": null}, {"slot_id": 29, "item_id": 700, "item_guid": 304}], "id": {{ids[0]}}}""";
            Eq(saved, JsonNode.Parse(reply.Body));
            var list = await Read(V2, "/game/user/loadout", owner);
            Eq(saved, list[0]);
            Eq(other[1], list[1]);  // the other loadout is not touched
        }
    }

    [Fact]
    public async Task TestASaveOfAnotherAccountsOrAnUnknownLoadoutIsRefused()
    {
        state.GrantItems(OWNER, 811);
        state.GrantItems(VIEWER, 811);
        var (mine, theirs) = (Ids(OWNER)[0], Ids(VIEWER)[0]);
        var before = await Read(V2, "/game/user/loadout", owner);
        string[] saves = [Items(), Items("""{"slot_id": 3, "item_id": 5}""")];
        foreach (var body in saves)
        {
            Assert.Equal(404, (await Call("PUT", V2, $"/game/user/loadout/{theirs}", owner, body)).Status);  // the viewer's
            Assert.Equal(404, (await Call("PUT", V2, "/game/user/loadout/99999", owner, body)).Status);
            Assert.Equal(404, (await Call("PUT", V2, "/game/user/loadout/99999999999999999999", owner, body)).Status);
            Assert.Equal(401, (await Call("PUT", V2, $"/game/user/loadout/{mine}", null, body)).Status);
            Assert.Equal(401, (await Call("PUT", V2, $"/game/user/loadout/{mine}", "unknown", body)).Status);
        }
        Assert.Contains("\"errorCode\": 404", Encoding.UTF8.GetString((await Call("PUT", Web, $"/v1/game/user/loadout/{theirs}", owner, saves[0])).Body));
        Eq(before, await Read(V2, "/game/user/loadout", owner));
        Assert.Equal("viewer's Loadout #1", (string)(await Read(V2, "/game/user/loadout", viewer))[0]!["name"]!);
    }

    [Fact]
    public async Task TestABodyThatIsNotAsSentChangesNothing()
    {
        state.GrantItems(OWNER, 811);
        var path = $"/game/user/loadout/{Ids(OWNER)[0]}";
        var many = string.Join(", ", Enumerable.Repeat("""{"slot_id": 3, "item_id": 5}""", 201));
        string[] bodies =
        [
            "", "[]", "not json", "null", """{"color": 1, "items": []}""",
            """{"name": "x", "color": 1, "items": []}""", $$"""{"name": "{{new string('a', 51)}}", "color": 1, "items": []}""", """{"name": 5, "color": 1, "items": []}""",
            """{"name": "ok", "color": 12, "items": []}""", """{"name": "ok", "color": -1, "items": []}""", """{"name": "ok", "color": "1", "items": []}""",
            """{"name": "ok", "color": 1.5, "items": []}""", """{"name": "ok", "items": []}""",
            """{"name": "ok", "color": 1}""", """{"name": "ok", "color": 1, "items": {}}""", """{"name": "ok", "color": 1, "items": [5]}""",
            """{"name": "ok", "color": 1, "items": [{"slot_id": "3", "item_id": 5}]}""", """{"name": "ok", "color": 1, "items": [{"slot_id": 3}]}""",
            """{"name": "ok", "color": 1, "items": [{"slot_id": 3, "item_id": 5, "item_guid": "x"}]}""",
            """{"name": "ok", "color": 1, "items": [{"slot_id": 0, "item_id": 5}]}""", """{"name": "ok", "color": 1, "items": [{"slot_id": -1, "item_id": 5}]}""",
            """{"name": "ok", "color": 1, "items": [{"slot_id": 23, "item_id": 5}]}""", $$"""{"name": "ok", "color": 1, "items": [{{many}}]}""",
        ];
        var before = await Read(V2, "/game/user/loadout", owner);
        foreach (var body in bodies)
            Assert.True((await Call("PUT", V2, path, owner, body)).Status == 400, body);
        Eq(before, await Read(V2, "/game/user/loadout", owner));
        // the limits themselves are fine
        Assert.Equal(200, (await Call("PUT", V2, path, owner, $$"""{"name": "{{new string('a', 50)}}", "color": 11, "items": [{"slot_id": 1, "item_id": 5, "item_guid": null}]}""")).Status);
        Assert.Equal(200, (await Call("PUT", V2, path, owner, """{"name": "ab", "color": 0, "items": []}""")).Status);
    }

    [Fact]
    public async Task TestTheRestoredLoadoutsGetNewIdsAndAnotherRestoreReplacesThem()
    {
        const string saved = """
            [{"name": "Kit A", "color": 3, "items": [{"slot_id": 3, "item_id": 5, "item_guid": null}, {"slot_id": 29, "item_id": 700, "item_guid": 304}], "id": 4242},
             7, {"name": "Kit B", "color": 11, "items": [], "id": 4243}, {"name": 5, "color": 1.5, "items": "x", "id": 4244}]
            """;
        var path = TestZip.Own().Reply("game_user_loadout.json", "/game/user/loadout", saved).Write(tmp);
        var account = Restorer.Import(state, path, null, tmp).Account;
        var token = state.Store.CreateToken(account);
        var list = await Read(V2, "/game/user/loadout", token);
        Eq("""[["Kit A", 3], ["Kit B", 11], ["", 0]]""", new JsonArray(list.Select(l => (JsonNode?)T(l!["name"], l["color"])).ToArray()));  // a name, color or items of the wrong kind is read as none
        Eq("""[{"slot_id": 3, "item_id": 5, "item_guid": null}, {"slot_id": 29, "item_id": 700, "item_guid": 304}]""", list[0]!["items"]);
        var ids = list.Select(l => (long)l!["id"]!).ToList();
        Assert.True(ids[0] < ids[1] && !ids.Contains(4242) && !ids.Contains(4243), "new ids");
        Assert.Empty(await Read(V2, "/game/user/loadout", owner));  // not the owner's

        // the saved loadouts are the restore's: an edit and a loadout bought since go with the next restore
        Assert.Equal(200, (await Call("PUT", V2, $"/game/user/loadout/{ids[0]}", token, Items())).Status);
        state.GrantItems(account, 811);
        Assert.Equal(4, (await Read(V2, "/game/user/loadout", token)).Count);
        Restorer.Import(state, path, account, tmp);
        Eq("""["Kit A", "Kit B", ""]""", new JsonArray((await Read(V2, "/game/user/loadout", token)).Select(l => l!["name"]!.DeepClone()).ToArray()));
    }

    [Fact]
    public void TestTheSchemaOfVersion11ReadsTheRestoredLoadoutsOfEveryAccount()
    {
        var file = Path.Combine(tmp, "v11.db");
        var store = new Store(file);
        foreach (var (id, name) in new[] { (5L, "Kit A"), (6L, "Kit B") })
            store.CreateAccount(id, "h" + id, new JsonObject(), seed: con =>
                PlayerRows.SetExtra(con, "game_user_loadout.json", JsonNode.Parse($$"""[{"name": "{{name}}", "color": 2, "items": [{"slot_id": 3, "item_id": 5, "item_guid": null}]}, {"name": 5, "color": "x", "items": 3}, 4]""")));
        store.CreateAccount(7, "h7", new JsonObject(), seed: con => PlayerRows.SetExtra(con, "game_user_loadout.json", JsonNode.Parse("""{"slots": [1, 2]}""")));
        using (var con = store.Open())
        {
            con.Exec("DROP TABLE loadouts");
            con.Exec("DROP TABLE missions");
            con.Exec("DROP TABLE mission_prefs");
            con.Exec("DROP TABLE daily_missions");
            con.Exec("DROP TABLE claim_lookups");
            con.Exec("DROP TABLE claims");
            con.Exec("PRAGMA user_version = 11");
        }
        var migrated = new Store(file);
        string Rows(long account)
        {
            using var con = migrated.Open(account);
            return string.Join(";", con.Rows("SELECT name, color, entries FROM loadouts WHERE account_id = $a ORDER BY id").Select(r => string.Join("|", r)));
        }
        Assert.Equal("""Kit A|2|[{"slot_id":3,"item_id":5,"item_guid":null}];|0|[]""", Rows(5));  // a loadout without a name or color is kept with defaults; a non-object is not
        Assert.Equal("""Kit B|2|[{"slot_id":3,"item_id":5,"item_guid":null}];|0|[]""", Rows(6));
        Assert.Equal("", Rows(7));  // a file that is not a list
        using var check = migrated.Open();
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }

    // ---- at a launch

    [Fact]
    public async Task TestALaunchPutsTheLoadoutOnTheItemSlots()
    {
        state.GrantItems(OWNER, 811, 2);
        var ids = Ids(OWNER);
        // two rifles and a third entry for a rifle the owner has not, the scope, the second dog by its guid, and an item in the backpack
        Assert.Equal(200, (await Call("PUT", V2, $"/game/user/loadout/{ids[0]}", owner, Items(
            """{"slot_id": 2, "item_id": 5}""", """{"slot_id": 3, "item_id": 5}""", """{"slot_id": 4, "item_id": 5}""", """{"slot_id": 5, "item_id": 2}""",
            """{"slot_id": 29, "item_id": 700, "item_guid": 304}""", """{"slot_id": 8, "item_id": 10}"""))).Status);
        Assert.Equal(200, (await Call("PUT", V2, $"/game/user/loadout/{ids[1]}", owner, Items("""{"slot_id": 4, "item_id": 5}"""))).Status);
        Eq("""{"100": 0, "200": 1, "301": 3, "302": 0, "303": 5, "304": 0, "305": 29, "306": 23, "307": 0}""", JsonNode.Parse(Slots()));

        var launch = await Launch(Api, "/v1/Launch/start", "application/x-www-form-urlencoded", $"start=100&loctype=LODGE&reserve=2&time=480&loadout_id={ids[0]}", owner);
        Assert.Equal(("state", 200), (launch.Source, launch.Status));
        // the entry for a third rifle is skipped; the placed item stays; the dog of the guid is on, the other one is off
        Eq("""{"100": 0, "200": 8, "301": 2, "302": 3, "303": 5, "304": 29, "305": 0, "306": 23, "307": 0, "310": 0, "311": 0}""", JsonNode.Parse(Slots()));  // 310 and 311: the launch's refill of the owner's ammunition

        // the current build's start of a session, the loadout id in its JSON body
        var session = await Launch(Web, "/v1/session/start_single_player?locale=en_US", "application/json",
            $$"""{"oauth_access_token": "{{owner}}", "time": 480, "loadout_id": {{ids[1]}}, "lodge_id": 101, "reserve": 2}""", owner);
        Assert.Equal(("state", 200), (session.Source, session.Status));
        Eq("""{"100": 0, "200": 0, "301": 4, "302": 0, "303": 0, "304": 0, "305": 0, "306": 23, "307": 0, "310": 0, "311": 0}""", JsonNode.Parse(Slots()));
    }

    [Fact]
    public void TestALaunchWithNoLoadoutOrOneThatIsNotTheAccountsChangesNothing()
    {
        state.GrantItems(OWNER, 811);
        state.GrantItems(VIEWER, 811);
        var theirs = Ids(VIEWER)[0];
        Assert.Equal(200, state.SaveLoadout(VIEWER, theirs, Encoding.UTF8.GetBytes(Items("""{"slot_id": 2, "item_id": 5}"""))).Status);
        var mine = Ids(OWNER)[0];
        Assert.Equal(200, state.SaveLoadout(OWNER, mine, Encoding.UTF8.GetBytes(Items("""{"slot_id": 2, "item_id": 5}"""))).Status);
        state.NoteLaunch(OWNER, "start=100&reserve=2"u8.ToArray());  // the first launch refills the owner's ammunition: its rows are in `before`
        var before = Slots();
        var viewerBefore = Slots(VIEWER);
        foreach (var form in new[] { "", "loadout_id=", "loadout_id=0", "loadout_id=abc", $"loadout_id={theirs}", $"loadout_id={mine + 1000}", "loadout_id=99999999999999999999", "loadout_id=-1" })
        {
            state.NoteLaunch(OWNER, Encoding.ASCII.GetBytes("start=100&reserve=2&" + form));
            Assert.Equal(before, Slots());
        }
        state.NoteLaunch(OWNER, Encoding.ASCII.GetBytes($"start=100&reserve=2&loadout_id={mine}"));  // its own does
        Assert.NotEqual(before, Slots());
        Assert.Equal(viewerBefore, Slots(VIEWER));  // the viewer's items were never part of it
    }

    [Fact]
    public async Task TestATutorialLaunchDoesNotPutTheLoadoutOn()
    {
        state.GrantItems(OWNER, 811);
        var id = Ids(OWNER)[0];
        Assert.Equal(200, (await Call("PUT", V2, $"/game/user/loadout/{id}", owner, Items("""{"slot_id": 2, "item_id": 5}"""))).Status);
        var before = Slots();
        var started = await Launch(Web, "/v1/session/start_tutorial?locale=en_US", "application/json", $$"""{"oauth_access_token": "{{owner}}", "tutorial_id": 0, "loadout_id": {{id}}}""", owner);
        Assert.Equal(("state", 200), (started.Source, started.Status));
        Assert.Equal(before, Slots());
    }

    [Fact]
    public void TestARestoredEntryOfAPlacedItemsSlotOrBelowOneIsSkipped()
    {
        state.GrantItems(OWNER, 811);
        var id = Ids(OWNER)[0];
        using (var con = state.Store.Open(OWNER))  // a restore keeps the entries as they were saved
            con.Exec("UPDATE loadouts SET entries = ? WHERE account_id = $a AND id = ?",
                     """[{"slot_id": 23, "item_id": 5}, {"slot_id": 0, "item_id": 5}, {"slot_id": -2, "item_id": 5}, {"slot_id": 4, "item_id": 5}]""", id);
        state.NoteLaunch(OWNER, Encoding.ASCII.GetBytes($"start=100&reserve=2&loadout_id={id}"));
        Eq("""{"100": 0, "200": 0, "301": 4, "302": 0, "303": 0, "304": 0, "305": 0, "306": 23, "307": 0, "309": 0, "310": 0}""", JsonNode.Parse(Slots()));  // only the placed item is in slot 23; 309 and 310 are the refilled ammunition
    }

    [Fact]
    public void TestAnEntryWithNoFreeItemIsSkipped()
    {
        state.GrantItems(OWNER, 811);
        var id = Ids(OWNER)[0];
        // a rifle that is not owned, a dog of a guid the owner has not, a dog of a guid of another item, and the only scope twice
        Assert.Equal(200, state.SaveLoadout(OWNER, id, Encoding.UTF8.GetBytes(Items(
            """{"slot_id": 2, "item_id": 404}""", """{"slot_id": 29, "item_id": 700, "item_guid": 999}""", """{"slot_id": 30, "item_id": 700, "item_guid": 303}""",
            """{"slot_id": 5, "item_id": 2}""", """{"slot_id": 6, "item_id": 2}"""))).Status);
        state.NoteLaunch(OWNER, Encoding.ASCII.GetBytes($"start=100&reserve=2&loadout_id={id}"));
        Eq("""{"100": 0, "200": 0, "301": 0, "302": 0, "303": 5, "304": 0, "305": 0, "306": 23, "307": 0, "309": 0, "310": 0}""", JsonNode.Parse(Slots()));
    }

    [Fact]
    public void TestUnlimitedAmmunitionFillsSeveralSlotsFromOneRow()
    {
        state.GrantItems(OWNER, 811);
        var id = Ids(OWNER)[0];
        var entries = new[] { """{"slot_id": 8, "item_id": 15}""", """{"slot_id": 9, "item_id": 15}""", """{"slot_id": 10, "item_id": 15}""", """{"slot_id": 3, "item_id": 5}""", """{"slot_id": 4, "item_id": 5}""", """{"slot_id": 6, "item_id": 5}""" };
        Assert.Equal(200, state.SaveLoadout(OWNER, id, Encoding.UTF8.GetBytes(Items(entries))).Status);
        var form = Encoding.ASCII.GetBytes($"start=100&reserve=2&loadout_id={id}");
        long[] Ammo(State s) => [.. s.Inventory(OWNER).Where(i => (long)i!["item_id"]! == 15).Select(i => (long)i!["slot"]!).Order()];

        state.NoteLaunch(OWNER, form);
        Assert.Equal([8L], Ammo(state));  // one row: the first slot has it, the others find none

        var connect = Fixtures.CONNECT;
        connect["inventory"]!["active_services"] = O("""{"UNLIMITED_AMMO": 4102444800}""");
        var unlimited = Start(connect);
        unlimited.NoteLaunch(OWNER, form);
        Assert.Equal([8L, 9, 10], Ammo(unlimited));  // cloned for the other two slots; a rifle is not an ammunition
        Assert.Equal(2, unlimited.Inventory(OWNER).Count(i => (long)i!["item_id"]! == 5 && (long)i["slot"]! > 0));
    }

    // ---- purchases

    [Fact]
    public void TestAPurchaseOfTheLoadoutItemOrItsBundleAddsOneLoadoutPerUnit()
    {
        Eq("""{"price": 20.0, "success": true}""", Reply(state.BuyReply(OWNER, "item_id=811&amount=2&wallet=GOLD"u8.ToArray())));
        Assert.Equal(["owner's Loadout #1", "owner's Loadout #2"], Names());
        Assert.NotNull(state.WebBuy(OWNER, 293, true, 1));
        Assert.Equal(5, Names().Count);
        Assert.Equal("owner's Loadout #5", Names()[^1]);
        Eq("137.0", state.Wallet(OWNER)["em"]);  // 182 - 20 - 25
        Assert.Equal(5, state.Inventory(OWNER).Count(i => (long)i!["item_id"]! == 811));  // a loadout for each unit owned

        // what is refused creates nothing
        Eq("""{"success": false}""", Reply(state.BuyReply(OWNER, "item_id=811&amount=100&wallet=GOLD"u8.ToArray())));
        Assert.Null(state.WebBuy(OWNER, 293, true, 2));
        Assert.Equal(5, Names().Count);
        Assert.Empty(Names(VIEWER));
        state.GrantItems(VIEWER, 811);
        Assert.Equal(["viewer's Loadout #1"], Names(VIEWER));
    }

    List<string> Names(long account = OWNER)
    {
        using var con = state.Store.Open(account);
        return [.. con.Rows("SELECT name FROM loadouts WHERE account_id = $a ORDER BY id").Select(r => (string)r[0])];
    }
}
