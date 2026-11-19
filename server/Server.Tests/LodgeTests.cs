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
        var app = APP;  // item 780 is the store's rustic lodge (item type 32)
        app["items"]!.AsArray().Add(O("""
            {"id": 780, "name": "Rustic Lodge", "description": "", "define": "TROPHY_LODGE_RUSTIC", "type": 32, "group": 19, "cat": 19, "client_type": 4,
             "image": "x", "variationname": null, "units": null, "fit": {}, "inventory": {"slots": [0], "capacity": 0, "weight": 0},
             "price": 50, "silver_price": 100, "notbuyable": 0, "ownMultiple": true, "subcat": {"id": 87}, "hidden": 0}
            """));
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false);
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
            con.Exec("INSERT INTO harvests (account_id, kill_id, body) VALUES ($a, 30, ?)", """{"species": 2, "score": 5, "taxidermized": true}""");  // only a taxidermized harvest is in the trophy list (TaxidermyTests: a mounted one that is not still shows in its lodge)
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
    public async Task TestLeavingALodgeChangesNothing()
    {
        const string Form = "application/x-www-form-urlencoded";
        // the game sends the name and style it read when it loaded the lodge: stored as they are, they stay
        Assert.Equal((200, ""), await Call("POST", Web, "/v1/game/user/lodges/3", owner, "name=left+lodge&style_id=4", Form));
        var one = await Read(V2, $"/game/user/{OWNER}/lodges/3", viewer);
        Eq("""["lodge 3", 1]""", T(one["name"], one["style_id"]));
        Assert.Equal((200, """{"errorCode": 404}"""), await Call("POST", Web, "/v1/game/user/lodges/3", viewer, "name=x&style_id=1", Form));  // own lodges only
        Assert.Equal((200, """{"errorCode": 404}"""), await Call("POST", Web, "/v1/game/user/lodges/99", owner, "name=x", Form));
        Assert.Equal(200, (await Call("POST", Web, "/v1/game/user/lodges/3", null, "name=x", Form)).Status);  // the web API's token-expired answer
        // what the editor saved is not undone by a save of the game that still has the old values
        Assert.Equal((200, ""), await Call("PUT", Web, "/v1/game/user/lodges/3/settings", owner, """{"name": "renamed", "style_id": 5}"""));
        Assert.Equal((200, ""), await Call("POST", Web, "/v1/game/user/lodges/3", owner, "name=lodge+3&style_id=1", Form));
        var after = await Read(V2, $"/game/user/{OWNER}/lodges/3", viewer);
        Eq("""["renamed", 5]""", T(after["name"], after["style_id"]));
    }

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
        Assert.Equal(1, (int)state.PublicTrophies(OWNER, 1)["total"]!);  // the restored Trophy/list row; the harvest made here is not a competition trophy
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
        Assert.Equal(1, (int)state.PublicTrophies(OWNER, 1)["total"]!);
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
        Assert.Equal(1, (int)state.PublicTrophies(OWNER, 1)["total"]!);
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
    public async Task TestAnAnimalOfARestoredLodgeIsInTheEditorsListOnceAndCanBeHidden()
    {
        using (var con = state.Store.Open(OWNER))
        {
            con.Begin();  // a public restore has no `/game/user/trophies`: 21 has a row, 40 only its lodge's details, 41 no details at all
            var lodge = O("""
                {"id": 6, "name": "six", "animal_slots": [{"slot_id": 0, "trophy_id": 21, "pose_id": 1, "trophy_details": {"id": 21, "species": 1}},
                                                          {"slot_id": 1, "trophy_id": 40, "pose_id": 2, "trophy_details": {"species": 7, "species_define": "ELK", "score": 9.5, "is_skull": true}},
                                                          {"slot_id": 2, "trophy_id": 41, "pose_id": 1}], "weapon_slots": []}
                """);
            PlayerRows.AddLodge(con, "/game/user/lodges", "6", lodge);
            PlayerRows.AddLodge(con, "/game/user/lodge", "6", lodge.DeepClone());
            con.Commit();
        }
        var listed = Reply(Encoding.UTF8.GetBytes((await Call("GET", V2, "/game/user/trophies", owner)).Body)).AsArray();
        Eq("[21, 40, 30]", new JsonArray(listed.Select(t => (JsonNode?)t!["id"]!.DeepClone()).ToArray()));
        Eq("""[7, "ELK", 9.5, true, false, 6, 1, 2]""", T(listed[1]!["species"], listed[1]!["species_define"], listed[1]!["score"], listed[1]!["is_skull"], listed[1]!["hidden"], listed[1]!["lodge_id"], listed[1]!["slot_id"], listed[1]!["pose_id"]));
        // the player moves 40 to another slot and takes 21 down: the game's save drops the slots' details, the list keeps the animals
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/lodges/6", owner, """{"animal_slots": [{"slot_id": 5, "trophy_id": 40, "pose_id": 1}]}"""));
        listed = Reply(Encoding.UTF8.GetBytes((await Call("GET", V2, "/game/user/trophies", owner)).Body)).AsArray();
        Eq("""[[21, null], [40, 5], [30, null]]""", new JsonArray(listed.Select(t => (JsonNode?)T(t!["id"], t["slot_id"])).ToArray()));
        Eq("\"ELK\"", (await Read(V2, $"/game/user/{OWNER}/lodges/6", viewer))["animal_slots"]![0]!["trophy_details"]!["species_define"]);
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/trophies/40", owner, """{"hidden": true}"""));
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/trophies/40", owner, """{"hidden": true}"""));  // twice: still one row
        Assert.Equal(404, (await Call("PUT", V2, "/game/user/trophies/41", owner, """{"hidden": true}""")).Status);
        listed = Reply(Encoding.UTF8.GetBytes((await Call("GET", V2, "/game/user/trophies", owner)).Body)).AsArray();
        Eq("[[21, false], [40, true], [30, false]]", new JsonArray(listed.Select(t => (JsonNode?)T(t!["id"], t["hidden"])).ToArray()));
    }

    [Fact]
    public async Task TestAnAnimalOfARestoredLodgeCanBeHiddenBeforeAnySave()
    {
        using (var con = state.Store.Open(OWNER))
        {
            con.Begin();
            var lodge = O("""{"id": 7, "name": "seven", "animal_slots": [{"slot_id": 0, "trophy_id": 50, "pose_id": 1, "trophy_details": {"species": 7}}], "weapon_slots": []}""");
            PlayerRows.AddLodge(con, "/game/user/lodges", "7", lodge);
            PlayerRows.AddLodge(con, "/game/user/lodge", "7", lodge.DeepClone());
            con.Commit();
        }
        Assert.Equal(404, (await Call("PUT", V2, "/game/user/trophies/51", owner, """{"hidden": true}""")).Status);
        Assert.Equal((200, ""), await Call("PUT", V2, "/game/user/trophies/50", owner, """{"hidden": true}"""));
        var listed = Reply(Encoding.UTF8.GetBytes((await Call("GET", V2, "/game/user/trophies", owner)).Body)).AsArray();
        Eq("[[21, false], [50, true], [30, false]]", new JsonArray(listed.Select(t => (JsonNode?)T(t!["id"], t["hidden"])).ToArray()));
    }

    [Fact]
    public async Task TestAJsonContentTypeIsNotTurnedIntoAFormOnTheGameWebHost()
    {
        Assert.Equal((200, ""), await Call("PUT", Web, "/v1/game/user/lodges/3/settings", owner, """{"name": "json", "locked": true, "style_id": null}""", "application/json; charset=UTF-8"));
        Assert.Equal((200, ""), await Call("PUT", Web, "/v1/game/user/trophies/21", owner, """{"hidden": true}""", "application/json"));
        var lodge = await Read(Web, $"/v1/game/user/{OWNER}/lodges/3", viewer);
        Eq("""["json", true, null]""", T(lodge["name"], lodge["locked"], lodge["style_id"]));
        Assert.Equal(0, (int)state.PublicTrophies(OWNER, 1)["total"]!);
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
    public async Task TestTheOwnListIsTheTokensAccountsLodgesOnBothHosts()
    {
        foreach (var (host, path) in new[] { (V2, "/game/user/lodges"), (Web, "/v1/game/user/lodges") })
        {
            var own = await Read(host, path, owner);
            Eq("[3, 4]", new JsonArray(own.AsArray().Select(l => (JsonNode?)l!["id"]!.DeepClone()).ToArray()));
            Eq(await Read(V2, $"/game/user/{OWNER}/lodges", viewer), own);  // the rows of the list by user id
            Eq("[]", await Read(host, path, viewer));  // a new account starts with none
        }
        Assert.Equal(401, (await Call("GET", V2, "/game/user/lodges", "unknown")).Status);
        Assert.Equal((200, """{"errorCode": 5, "message": "token expired"}"""), await Call("GET", Web, "/v1/game/user/lodges", null));
        Eq(Reply(state.TrophiesReply(OWNER)), await Read(Web, "/v1/game/user/trophies", owner));  // the editor's trophy list, on the current build too
    }

    [Fact]
    public async Task TestTheLodgePagesCatalogueIsServedToEveryoneOnBothHosts()
    {
        foreach (var (host, prefix) in new[] { (V2, ""), (Web, "/v1") })
        {
            var type = await Read(host, $"{prefix}/gameengine/trophylodge/lodge/3", owner);
            Eq("""[3, "rustic.png", "TROPHY_LODGE_RUSTIC"]""", T(type["id"], type["image_url"], type["item_define"]));
            Assert.Equal(54, (await Read(host, $"{prefix}/gameengine/trophylodge/animal", owner)).AsArray().Count);
            foreach (var kind in new[] { "skull", "weapon", "slot/animal", "slot/weapon", "lodge/1", "lodge/5", "lodge/6" })
                Assert.NotEmpty((await Read(host, $"{prefix}/gameengine/trophylodge/{kind}", owner)).ToJsonString());
        }
        Assert.Equal(200, (await Call("GET", V2, "/gameengine/trophylodge/slot/weapon", null)).Status);  // no token
        Assert.Equal(404, (await Call("GET", V2, "/gameengine/trophylodge/lodge/2", owner)).Status);  // a layout there is none of
    }

    [Fact]
    public async Task TestTheStartAddsTheMissingLodgesOfOwnedLodgeItemsOnce()
    {
        state.CreateAccount(10, "restored");
        state.CreateAccount(11, "matching");
        state.CreateAccount(12, "huge");
        state.CreateAccount(5, "broken");
        foreach (var (account, units) in new[] { (VIEWER, 1), (10, 2), (11, 1), (12, 2), (5, 1) })
        {
            using var con = state.Store.Open(account);
            for (var guid = 1; guid <= units; guid++)
                con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, ?, 780, 0, '{}', NULL)", guid);
        }
        foreach (var (account, id, type, source) in new[] { (10L, "7", "780", "/game/user/lodge"), (10, "8", "\"x\"", "/game/user/lodges"), (11, "9", "\"780\"", "/game/user/lodges"), (11, "9", "780", "/game/user/lodge"), (12, "9223372036854775807", "780", "/game/user/lodges") })
        {
            using var con = state.Store.Open(account);
            PlayerRows.AddLodge(con, source, id, O($$"""{"id": {{id}}, "name": "mine", "lodge_type_id": {{type}}}"""));  // 10's lodge 7 has no list row: it still counts
        }
        using (var con = state.Store.Open(5))
            con.Exec("INSERT INTO lodges (account_id, source, id, node) VALUES ($a, '/game/user/lodges', '1', '{')");  // unreadable: that account is left alone, the others go on
        string Rows(long account)
        {
            using var con = state.Store.Open(account);
            return string.Join("|", con.Rows("SELECT source, node FROM lodges WHERE account_id = $a ORDER BY n").Select(r => r[0] + " " + r[1]));
        }
        var (before11, before5) = (Rows(11), Rows(5));
        state.BackfillLodges();
        state.BackfillLodges();  // idempotent
        Assert.Equal([before11, before5], [Rows(11), Rows(5)]);  // the matching account (its type a text in one row) and the unreadable one are untouched
        long Count(long account)
        {
            using var con = state.Store.Open(account);
            return (long)con.Scalar("SELECT COUNT(*) FROM lodges WHERE account_id = $a AND source = '/game/user/lodges' AND CAST(json_extract(node, '$.lodge_type_id') AS INTEGER) = 780")!;
        }
        Assert.Equal([1, 1, 1, 1], [Count(VIEWER), Count(10), Count(11), Count(12)]);  // 10's lodge 7 (detail only) counts and 10 gained one; 11 matched; 12's lodge id cannot be followed: left as it was
        var made = (await Read(V2, "/game/user/lodges", viewer)).AsArray().Single()!;
        Eq("""["Rustic Lodge", 780, false]""", T(made["name"], made["lodge_type_id"], made["locked"]));
        Eq("""[1, [], []]""", T(made["id"], (await Read(V2, $"/game/user/{VIEWER}/lodges/1", owner))["animal_slots"], (await Read(V2, $"/game/user/{VIEWER}/lodges/1", owner))["weapon_slots"]));
    }

    [Fact]
    public async Task TestALodgeBoughtInTheStoreIsListedEnteredAndEdited()
    {
        Eq("""{"price": 100.0, "success": true}""", Reply(state.BuyReply(OWNER, "item_id=780&amount=2&wallet=GOLD"u8.ToArray())));
        var list = (await Read(V2, "/game/user/lodges", owner)).AsArray();
        Eq("[3, 4, 5, 6]", new JsonArray(list.Select(l => (JsonNode?)l!["id"]!.DeepClone()).ToArray()));
        Eq("""[5, "Rustic Lodge", 3, 780, "TROPHY_LODGE_RUSTIC", false, 0]""",
           T(list[2]!["id"], list[2]!["name"], list[2]!["lodge_engine_id"], list[2]!["lodge_type_id"], list[2]!["lodge_type_define"], list[2]!["locked"], list[2]!["style_id"]));
        var entered = await Read(Web, $"/v1/game/user/{OWNER}/lodges/6", viewer);  // another player enters it too
        Eq("""[6, [], []]""", T(entered["id"], entered["animal_slots"], entered["weapon_slots"]));
        Assert.Equal((200, ""), await Call("PUT", Web, "/v1/game/user/lodges/6", owner, Slots));
        var edited = await Read(V2, $"/game/user/{OWNER}/lodges/6", owner);
        Eq("""["my cabin", 21]""", T(edited["name"], edited["animal_slots"]![0]!["trophy_id"]));
        Assert.Empty((await Read(V2, "/game/user/lodges", viewer)).AsArray());  // the other account has none
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
            con.Exec("DROP TABLE claim_lookups");
            con.Exec("DROP TABLE claims");
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
