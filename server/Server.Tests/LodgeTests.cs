using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Lodge edits and trophy visibility, and the lodges of any account, through the real HTTP pipeline on both hosts of the current build.</summary>
public class LodgeTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    const long OWNER = 8, VIEWER = 9;
    string owner = "", viewer = "";
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        state.CreateAccount(OWNER, "owner");
        state.CreateAccount(VIEWER, "viewer");
        (owner, viewer) = (state.Store.CreateToken(OWNER), state.Store.CreateToken(VIEWER));
        using (var con = state.Store.Open(OWNER))
        {
            con.Begin();
            foreach (var id in new[] { 3, 4 })
            {
                var lodge = O($$"""{"id": {{id}}, "name": "lodge {{id}}", "locked": false, "style_id": 1, "animal_slots": [], "weapon_slots": []}""");
                PlayerRows.AddLodge(con, "/game/user/lodges", id.ToString(), lodge);
                PlayerRows.AddLodge(con, "/game/user/lodge", id.ToString(), lodge.DeepClone());
            }
            PlayerRows.AddTrophy(con, "/game/user/trophies", "21", O("""{"id": 21, "species": 1, "hidden": false}"""));
            PlayerRows.AddTrophy(con, "/v1/Trophy/list", "21", O("""{"id": 21, "species": 1, "hidden": false}"""));
            con.Exec("INSERT INTO harvests (account_id, kill_id, body) VALUES ($a, 30, ?)", """{"species": 2, "score": 5}""");
            con.Commit();
        }
        server = await Loopback.StartAsync(new Game(new HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    /// <summary>A request as the game sends an edit: JSON as text/plain (or the type given), the JWT as a Bearer token.</summary>
    async Task<(int Status, string Body)> Call(string method, string host, string path, string? token, string body = "", string type = "text/plain")
    {
        var reply = await server.SendAsync(method, host, path, [("Content-Type", type), .. token is null ? [] : new[] { ("Authorization", "Bearer " + token) }],
                                           Encoding.UTF8.GetBytes(body));
        return (reply.Status, Encoding.UTF8.GetString(reply.Body));
    }

    const string V2 = "apiv2.test", Web = "game_web.test";  // the current build's two hosts: API v2 and the game web host (`/v1/...`)

    async Task<JsonNode> Read(string host, string path, string token)
    {
        var (status, body) = await Call("GET", host, path, token);
        Assert.Equal(200, status);
        return JsonNode.Parse(body)!;
    }

    const string Slots = """
        {"name": "my cabin",
         "animal_slots": [{"slot_id": 0, "trophy_id": 21, "pose_id": 2, "rotation": 90, "base_texture": "", "is_skull": false, "junk": {"a": 1}},
                          {"slot_id": 1, "trophy_id": 30, "pose_id": 1, "rotation": 0, "base_texture": "skull_01", "is_skull": true},
                          {"slot_id": 2, "trophy_id": null, "pose_id": null, "rotation": 0, "base_texture": null, "is_skull": false}],
         "weapon_slots": [{"slot_id": 0, "weapon_id": 5, "rotation": 1.5}]}
        """;

    [Fact]
    public async Task TestSavedSlotsAreReadByEveryPlayerWithTheirTrophies()
    {
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/lodges/3", owner, Slots));
        foreach (var (host, prefix) in new[] { (V2, ""), (Web, "/v1") })
        {
            var one = await Read(host, $"{prefix}/game/user/{OWNER}/lodges/3", viewer);  // another player reads it
            Eq("""[3, "my cabin", 1]""", T(one["id"], one["name"], one["style_id"]));
            Eq("""
                [{"slot_id": 0, "trophy_id": 21, "pose_id": 2, "rotation": 90, "base_texture": "", "is_skull": false,
                  "trophy_details": {"id": 21, "species": 1, "hidden": false, "lodge_id": 3, "slot_id": 0, "pose_id": 2}},
                 {"slot_id": 1, "trophy_id": 30, "pose_id": 1, "rotation": 0, "base_texture": "skull_01", "is_skull": true,
                  "trophy_details": {"id": 30, "hidden": false, "species": 2, "score": 5, "species_define": null, "score_type": null, "is_skull": null, "gender": null, "weight": null,
                                     "scale": null, "reserve_id": null, "timestamp": null, "texture_id": null, "base_texture": null, "fur_texture": null, "antlers_seed": null,
                                     "antlers_score_percentage": null, "holed": null, "lodge_id": 3, "slot_id": 1, "pose_id": 1}},
                 {"slot_id": 2, "trophy_id": null, "pose_id": null, "rotation": 0, "base_texture": null, "is_skull": false}]
                """, one["animal_slots"]);
            Eq("""[{"slot_id": 0, "weapon_id": 5, "rotation": 1.5}]""", one["weapon_slots"]);
            var list = (await Read(host, $"{prefix}/game/user/{OWNER}/lodges", viewer)).AsArray();
            Eq("""[[3, "my cabin"], [4, "lodge 4"]]""", new JsonArray(list.Select(l => (JsonNode?)T(l!["id"], l["name"])).ToArray()));
            Eq(one, list[0]);  // the list row is edited too
        }
    }

    [Fact]
    public async Task TestSettingsKeepLockedAndAreSentToOtherPlayers()
    {
        Assert.Equal((200, ""), await Call("PUT", Web, "/v1/game/user/lodges/4/settings", owner, """{"name": "mine", "locked": true, "style_id": 7}"""));
        var list = await Read(V2, $"/game/user/{OWNER}/lodges", viewer);
        Eq("""[[3, "lodge 3", false, 1], [4, "mine", true, 7]]""", new JsonArray(list.AsArray().Select(l => (JsonNode?)T(l!["id"], l["name"], l["locked"], l["style_id"])).ToArray()));
        Eq(list[1], await Read(Web, $"/v1/game/user/{OWNER}/lodges/4", viewer));
    }

    [Fact]
    public async Task TestAHiddenTrophyIsLeftOutOfThePublicViewsAndStaysMounted()
    {
        await Call("PUT", V2, "/game/user/lodges/3", owner, Slots);
        Assert.Equal(2, (int)state.PublicTrophies(OWNER, 1)["total"]!);
        foreach (var (host, path) in new[] { (V2, "/game/user/trophies/21"), (Web, "/v1/game/user/trophies/30") })  // a restored trophy and a harvest made here
            Assert.Equal((200, ""), await Call("PUT", host, path, owner, """{"hidden": true}"""));
        Assert.Equal(0, (int)state.PublicTrophies(OWNER, 1)["total"]!);
        Eq("[true, true]", new JsonArray(Reply(state.TrophiesReply(OWNER)).AsArray().Select(t => (JsonNode?)t!["hidden"]!.DeepClone()).ToArray()));
        using (var con = state.Store.Open(OWNER))
            Assert.True((bool)JsonNode.Parse((string)con.Scalar("SELECT node FROM trophies WHERE account_id = $a AND source = '/v1/Trophy/list'")!)!["hidden"]!);  // the trophy list's row too
        var slots = (await Read(V2, $"/game/user/{OWNER}/lodges/3", viewer))["animal_slots"]!;
        Eq("[21, 30]", T(slots[0]!["trophy_details"]!["id"], slots[1]!["trophy_details"]!["id"]));
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/trophies/21", owner, """{"hidden": false}"""));
        Assert.Equal(1, (int)state.PublicTrophies(OWNER, 1)["total"]!);
    }

    [Fact]
    public async Task TestEditsNeedTheOwnersTokenAndAKnownLodgeOrTrophy()
    {
        var before = await Read(V2, $"/game/user/{OWNER}/lodges", viewer);
        Assert.Equal(401, (await Call("PUT", V2, "/game/user/lodges/3", null, Slots)).Status);
        Assert.Equal(401, (await Call("PUT", V2, "/game/user/trophies/21", "unknown", """{"hidden": true}""")).Status);
        Assert.Equal(200, (await Call("PUT", Web, "/v1/game/user/lodges/3", null, Slots)).Status);  // the web API's own token-expired answer
        Assert.Equal(404, (await Call("PUT", V2, "/game/user/lodges/3", viewer, Slots)).Status);  // the viewer has no lodge 3: it edits its own rows only
        Assert.Equal(404, (await Call("PUT", V2, "/game/user/lodges/99", owner, Slots)).Status);
        Assert.Equal(404, (await Call("PUT", V2, "/game/user/lodges/99999999999999999999/settings", owner, """{"locked": true}""")).Status);
        Assert.Equal(404, (await Call("PUT", V2, "/game/user/trophies/99", owner, """{"hidden": true}""")).Status);
        Assert.Equal(404, (await Call("PUT", V2, "/game/user/trophies/30", viewer, """{"hidden": true}""")).Status);
        Eq(before, await Read(V2, $"/game/user/{OWNER}/lodges", viewer));
        Assert.Equal(2, (int)state.PublicTrophies(OWNER, 1)["total"]!);
    }

    [Theory]
    [InlineData("/game/user/lodges/3", "")]
    [InlineData("/game/user/lodges/3", "[]")]
    [InlineData("/game/user/lodges/3", """{"name": 5}""")]
    [InlineData("/game/user/lodges/3", """{"animal_slots": {}}""")]
    [InlineData("/game/user/lodges/3", """{"animal_slots": [{"trophy_id": 1}]}""")]
    [InlineData("/game/user/lodges/3", """{"weapon_slots": [{"slot_id": 0, "rotation": {"x": 1}}]}""")]
    [InlineData("/game/user/lodges/3/settings", """{"locked": "yes"}""")]
    [InlineData("/game/user/lodges/3", """{"name": null}""")]
    [InlineData("/game/user/lodges/3/settings", """{"locked": null}""")]
    [InlineData("/game/user/lodges/3", """{"animal_slots": [{"slot_id": 0, "trophy_id": "7"}]}""")]
    [InlineData("/game/user/lodges/3/settings", """{"style_id": "1"}""")]
    [InlineData("/game/user/trophies/21", """{}""")]
    [InlineData("/game/user/trophies/21", """{"hidden": 1}""")]
    [InlineData("/game/user/trophies/21", "not json")]
    public async Task TestABodyThatIsNotAsSentChangesNothing(string path, string body)
    {
        var before = await Read(V2, $"/game/user/{OWNER}/lodges", viewer);
        var (status, reply) = await Call("PUT", V2, path, owner, body);
        Assert.Equal(400, status);
        Assert.Contains("bad_request", reply);
        Assert.Equal((200, """{"errorCode": 400}"""), await Call("PUT", Web, "/v1" + path, owner, body));  // the web API's shape of the same refusal
        Eq(before, await Read(V2, $"/game/user/{OWNER}/lodges", viewer));
        Assert.Equal(2, (int)state.PublicTrophies(OWNER, 1)["total"]!);
    }

    [Fact]
    public async Task TestALodgeHasLimitsOnItsNameAndSlots()
    {
        var before = await Read(V2, $"/game/user/{OWNER}/lodges", viewer);
        string Slots(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"slot_id": {{i}}, "trophy_id": null}""")) + "]";
        foreach (var body in new[] { $$"""{"name": "{{new string('x', 101)}}"}""", $$"""{"animal_slots": {{Slots(201)}}}""", $$"""{"weapon_slots": {{Slots(201)}}}""",
                                     $$"""{"animal_slots": [{"slot_id": 0, "base_texture": "{{new string('x', 101)}}"}]}""" })
            Assert.Equal(400, (await Call("PUT", V2, "/game/user/lodges/3", owner, body)).Status);
        Eq(before, await Read(V2, $"/game/user/{OWNER}/lodges", viewer));
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/lodges/3", owner, $$"""{"name": "{{new string('x', 100)}}", "animal_slots": {{Slots(200)}}}"""));
        Assert.Equal(200, (await Read(V2, $"/game/user/{OWNER}/lodges/3", viewer))["animal_slots"]!.AsArray().Count);
    }

    [Fact]
    public async Task TestASaveKeepsWhatTheGameDoesNotSendOfTheStoredSlots()
    {
        using (var con = state.Store.Open(OWNER))
        {
            con.Begin();
            PlayerRows.AddTrophy(con, "/game/user/trophies", "22", O("""{"id": 22, "species": 3, "hidden": false}"""));
            var lodge = O("""
                {"id": 5, "name": "five", "animal_slots": [{"slot_id": 0, "trophy_id": 21, "pose_id": 1, "is_full_body": true, "trophy_details": {"id": 21, "own": "restored"}},
                                                          {"slot_id": 1, "trophy_id": 30, "pose_id": 1, "is_full_body": true, "trophy_details": {"id": 30, "own": "restored"}}], "weapon_slots": []}
                """);
            PlayerRows.AddLodge(con, "/game/user/lodges", "5", lodge);
            PlayerRows.AddLodge(con, "/game/user/lodge", "5", lodge.DeepClone());
            con.Commit();
        }
        // the game saves a lodge when the player leaves it: slot 0 as it was, slot 1 with another trophy, slot 2 is new
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/lodges/5", owner,
            """{"animal_slots": [{"slot_id": 0, "trophy_id": 21, "pose_id": 3}, {"slot_id": 1, "trophy_id": 22, "pose_id": 1}, {"slot_id": 2, "trophy_id": 30, "pose_id": 1}]}"""));
        var slots = (await Read(V2, $"/game/user/{OWNER}/lodges/5", viewer))["animal_slots"]!;
        Eq("""[3, true, "restored"]""", T(slots[0]!["pose_id"], slots[0]!["is_full_body"], slots[0]!["trophy_details"]!["own"]));
        Eq("""[true, 22]""", T(slots[1]!["is_full_body"], slots[1]!["trophy_details"]!["id"]));  // another trophy: the platform's flag, the new trophy's details
        Eq("""[null, 30]""", T(slots[2]!["is_full_body"], slots[2]!["trophy_details"]!["id"]));  // a new slot: nothing stored
    }

    JsonArray Mounts() => new(Reply(state.TrophiesReply(OWNER)).AsArray().OrderBy(t => (long)t!["id"]!)
        .Select(t => (JsonNode?)T(t!["lodge_id"], t["slot_id"], t["pose_id"])).ToArray());

    [Fact]
    public async Task TestTrophyRowsSayWhereTheyAreMountedNow()
    {
        using (var con = state.Store.Open(OWNER))
        {
            con.Begin();
            PlayerRows.AddTrophy(con, "/game/user/trophies", "22", O("""{"id": 22, "species": 3, "hidden": false, "lodge_id": 5, "slot_id": 1, "pose_id": 1}"""));
            PlayerRows.AddTrophy(con, "/game/user/trophies", "23", O("""{"id": 23, "species": 3, "hidden": false, "lodge_id": 99, "slot_id": 4, "pose_id": 2}"""));  // a lodge the account has not
            var lodge = O("""{"id": 5, "name": "five", "animal_slots": [{"slot_id": 0, "trophy_id": 21, "pose_id": 1}, {"slot_id": 1, "trophy_id": 22, "pose_id": 1}], "weapon_slots": []}""");
            PlayerRows.AddLodge(con, "/game/user/lodges", "5", lodge);
            PlayerRows.AddLodge(con, "/game/user/lodge", "5", lodge.DeepClone());
            con.Commit();
        }
        Eq("""[[5, 0, 1], [5, 1, 1], [99, 4, 2], [null, null, null]]""", Mounts());  // trophies 21, 22, 23 and the harvest 30
        // 22 is taken down, the harvest is mounted in its slot and 21 turns
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/lodges/5", owner, """{"animal_slots": [{"slot_id": 0, "trophy_id": 21, "pose_id": 3}, {"slot_id": 1, "trophy_id": 30, "pose_id": 1}]}"""));
        Eq("""[[5, 0, 3], [null, null, null], [99, 4, 2], [5, 1, 1]]""", Mounts());
        var listed = Reply(Encoding.UTF8.GetBytes((await Call("GET", V2, "/game/user/trophies", owner)).Body)).AsArray();
        Eq("""[5, 1, 1]""", T(listed[3]!["lodge_id"], listed[3]!["slot_id"], listed[3]!["pose_id"]));  // `/game/user/trophies` sends them
    }

    [Fact]
    public async Task TestAJsonContentTypeIsNotTurnedIntoAFormOnTheGameWebHost()
    {
        Assert.Equal((200, ""), await Call("PUT", Web, "/v1/game/user/lodges/3/settings", owner, """{"name": "json", "locked": true, "style_id": null}""", "application/json; charset=UTF-8"));
        Assert.Equal((200, ""), await Call("PUT", Web, "/v1/game/user/trophies/21", owner, """{"hidden": true}""", "application/json"));
        var lodge = await Read(Web, $"/v1/game/user/{OWNER}/lodges/3", viewer);
        Eq("""["json", true, null]""", T(lodge["name"], lodge["locked"], lodge["style_id"]));
        Assert.Equal(1, (int)state.PublicTrophies(OWNER, 1)["total"]!);
    }

    [Fact]
    public async Task TestALodgeOfAnUnknownAccountIsTheEmptyReplyAndTheEditsAreRepeatable()
    {
        Assert.Equal((200, "[]"), await Call("GET", V2, "/game/user/55/lodges", viewer));
        Assert.Equal(404, (await Call("GET", V2, "/game/user/55/lodges/3", viewer)).Status);
        Assert.Equal(404, (await Call("GET", V2, $"/game/user/{OWNER}/lodges/99", viewer)).Status);
        Assert.Equal(401, (await Call("GET", V2, $"/game/user/{OWNER}/lodges", null)).Status);
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/lodges/3", owner, Slots));
        var once = await Read(V2, $"/game/user/{OWNER}/lodges/3", viewer);
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/lodges/3", owner, Slots));
        Eq(once, await Read(V2, $"/game/user/{OWNER}/lodges/3", viewer));
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/lodges/3", owner, """{"animal_slots": [], "weapon_slots": []}"""));  // a lodge emptied; the name stays
        var emptied = await Read(V2, $"/game/user/{OWNER}/lodges/3", owner);
        Eq("""["my cabin", [], []]""", T(emptied["name"], emptied["animal_slots"], emptied["weapon_slots"]));
    }

    [Fact]
    public void TestTheSchemaOfVersion8IsMigratedToTheHiddenHarvests()
    {
        var file = Path.Combine(tmp, "v8.db");
        using (var con = new Store(file).Open())
        {
            con.Exec("ALTER TABLE harvests DROP COLUMN hidden");
            con.Exec("DROP TABLE loadouts");
            con.Exec("DROP TABLE wallet_log");
            con.Exec("DROP TABLE missions");
            con.Exec("DROP TABLE mission_prefs");
            con.Exec("DROP TABLE daily_missions");
            con.Exec("PRAGMA user_version = 8");
        }
        var migrated = new Store(file);
        migrated.CreateAccount(5, "five", new JsonObject());
        using var check = migrated.Open(5);
        check.Exec("INSERT INTO harvests (account_id, kill_id, body) VALUES ($a, 1, '{}')");
        Assert.Equal(0L, check.Scalar("SELECT hidden FROM harvests WHERE account_id = $a"));
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }
}
