using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The community-mode rule of ReplayScope: a recording answers global endpoints only; per-player ones come from the
/// database or are empty. Two accounts see only their own data. A local server still replays everything.</summary>
public class PerPlayerTests : StateTestBase, IAsyncLifetime
{
    const string Recorded = "RECORDED-PLAYER";
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    static readonly (string, string)[] FormHeader = [("Content-Type", "application/x-www-form-urlencoded")];
    Loopback community = null!, local = null!;
    string tokenA = "", tokenB = "";

    static string HostOf(ReplayScope.Rule rule) => rule.Host == "gs" ? "gameserver.test" : rule.Host + ".test";

    /// <summary>A concrete path of a rule: the first alternative, optional parts left out.</summary>
    static string Sample(ReplayScope.Rule rule) =>
        Regex.Replace(rule.Path, @"\(([^()]*)\)(\?)?", m => m.Groups[2].Success ? "" : m.Groups[1].Value.Split('|')[0]).Replace(@"\d+", "5").Replace(".*", "x");

    static Replay.HarTable Recordings()
    {
        var table = new Replay.HarTable();
        foreach (var rule in ReplayScope.Rules)
            foreach (var method in new[] { "GET", "POST" })
                table.Entries[new(method, HostOf(rule), Sample(rule), null)] = new Replay.Hit(2, "", 200, [], Encoding.ASCII.GetBytes(Recorded));
        table.Entries[new("POST", "api.test", "/v1/Unlisted/endpoint", null)] = new Replay.Hit(2, "", 200, [], Encoding.ASCII.GetBytes(Recorded));
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        return table;
    }

    State NewState(string name, bool offline) =>
        new(Path.Combine(tmp, name), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: offline);

    public async ValueTask InitializeAsync()
    {
        state = NewState("community.db", offline: false);
        state.CreateAccount(8, "second");
        state.GrantItems(8, 19);
        (tokenA, tokenB) = (state.Store.CreateToken(ME), state.Store.CreateToken(8));
        community = await Loopback.StartAsync(new Replay.Game(Recordings(), Cfg, state, tmp), tmp);
        local = await Loopback.StartAsync(new Replay.Game(Recordings(), Cfg, NewState("local.db", offline: true), tmp), tmp);
    }

    public async ValueTask DisposeAsync()
    {
        await community.DisposeAsync();
        await local.DisposeAsync();
    }

    Task<Reply> Send(Loopback server, string method, string host, string path, string? token, string body = "") => server.SendAsync(method, host, path,
        [.. FormHeader, .. token is null ? [] : new[] { ("X-Hun-Session", token), ("Authorization", "Bearer " + token) }],
        Encoding.ASCII.GetBytes(body + (token is null ? "" : "&oauth_access_token=" + token)));

    Task<JsonNode> Api(string path, string? token, string body = "") => Send(community, "POST", "api.test", path, token, body).ContinueWith(t => JsonNode.Parse(t.Result.Body)!);

    [Fact]
    public void TestEveryRuleMatchesItsOwnSample()
    {
        var game = new Replay.Game(Recordings(), Cfg, null, tmp);
        foreach (var rule in ReplayScope.Rules)
        {
            var req = new Req("POST", HostOf(rule), Sample(rule), "", _ => null, [], false);
            Assert.Same(rule, ReplayScope.Find(game, req));
        }
    }

    [Fact]
    public void TestEveryEmptyReplyIsJson()
    {
        foreach (var rule in ReplayScope.Rules.Where(r => !r.Global))
        {
            Assert.True(rule.Status is 200 or 404, $"{rule.Host} {rule.Path}: {rule.Status}");
            if (rule.ContentType == "application/json")
                JsonNode.Parse(rule.Empty!);
        }
    }

    [Fact]
    public async Task TestACommunityServerNeverReturnsARecordingForAPerPlayerEndpoint()
    {
        foreach (var rule in ReplayScope.Rules.Where(r => !r.Global))
            foreach (var method in new[] { "GET", "POST" })
                foreach (var token in new string?[] { tokenA, null })
                {
                    var reply = await Send(community, method, HostOf(rule), Sample(rule), token);
                    Assert.True(!Encoding.ASCII.GetString(reply.Body).Contains(Recorded) && reply.Source != "har", $"{method} {rule.Host} {rule.Path} ({token is null}): {reply.Source}");
                }
        // an endpoint the table does not list is per player too
        var unlisted = await Send(community, "POST", "api.test", "/v1/Unlisted/endpoint", tokenA);
        Assert.Equal(("miss", "null"), (unlisted.Source, Encoding.ASCII.GetString(unlisted.Body)));
    }

    [Fact]
    public async Task TestAGlobalEndpointIsStillReplayedAndALocalServerReplaysEverything()
    {
        foreach (var rule in ReplayScope.Rules.Where(r => r.Global))
        {
            var reply = await Send(community, "POST", HostOf(rule), Sample(rule), tokenA);
            Assert.Equal(Recorded, Encoding.ASCII.GetString(reply.Body));
        }
        foreach (var rule in ReplayScope.Rules.Where(r => !r.Global && r.Host == "api" && !r.Path.Contains("Me/") && !r.Path.Contains("Expedition/list") && !r.Path.Contains("Inventory/list")))
            Assert.Equal(Recorded, Encoding.ASCII.GetString((await Send(local, "POST", HostOf(rule), Sample(rule), null)).Body));
    }

    [Fact]
    public async Task TestTwoAccountsSeeOnlyTheirOwnMeAndPoll()
    {
        state.Give(8, "em", 5);
        foreach (var (token, id, handle) in new[] { (tokenA, 7, "hunter"), (tokenB, 8, "second") })
        {
            var me = await Api(Replay.ME, token);
            Eq(JsonNode.Parse($"[{id}, \"{handle}\", \"{handle}\"]"), T(me["id"], me["handle"], me["hostname"]));
            Assert.Equal(state.Wallet(id)["em"]!.GetValue<double>(), me["ems"]!.GetValue<double>());
            var poll = await Api(Replay.POLL, token);
            Assert.Equal(state.Wallet(id)["em"]!.GetValue<double>(), poll["ems"]!.GetValue<double>());
        }
        Assert.NotEqual(state.Wallet(7)["em"]!.GetValue<double>(), state.Wallet(8)["em"]!.GetValue<double>());
    }

    [Fact]
    public async Task TestTwoAccountsSeeOnlyTheirOwnItemsAndHunts()
    {
        state.NoteLaunch(8, "start=100&reserve=2"u8.ToArray());
        IEnumerable<long> Ids(JsonNode list) => list.AsArray().Select(row => (long)row!["item"]!["id"]!);
        Assert.DoesNotContain(19L, Ids(await Api(Replay.INVENTORY_LIST, tokenA)));
        Assert.Contains(19L, Ids(await Api(Replay.INVENTORY_LIST, tokenB)));
        Assert.Contains(10L, Ids(await Api(Replay.INVENTORY_LIST, tokenB, "user_id=8")));
        // another player's id is the empty reply, whoever asks
        Assert.Empty((await Api(Replay.INVENTORY_LIST, tokenA, "user_id=8")).AsArray());
        // the same through Inventory/dict: keyed by item id, owner only
        Assert.DoesNotContain("19", (await Api(Replay.INVENTORY_DICT, tokenA)).AsObject().Select(kv => kv.Key));
        Assert.Contains("19", (await Api(Replay.INVENTORY_DICT, tokenB)).AsObject().Select(kv => kv.Key));
        Assert.Empty((await Api(Replay.INVENTORY_DICT, tokenA, "user_id=8")).AsObject());
        Assert.Equal(1, (int)(await Api(Replay.EXPEDITION_LIST, tokenB, "user_id=8&limit=10&offset=0"))["total"]!);
        Assert.Equal(0, (int)(await Api(Replay.EXPEDITION_LIST, tokenA, "user_id=7&limit=10&offset=0"))["total"]!);
        Assert.Equal(0, (int)(await Api(Replay.EXPEDITION_LIST, tokenA, "user_id=8&limit=10&offset=0"))["total"]!);
        Assert.Null(await Api(Replay.PUBLIC_EXPEDITION, tokenA, "user_id=7"));
        Assert.NotNull(await Api(Replay.PUBLIC_EXPEDITION, tokenB, "user_id=8"));
        Assert.Null(await Api(Replay.PUBLIC_EXPEDITION, tokenA, "user_id=8"));
    }

    [Fact]
    public async Task TestRestoredTrophiesAndLodgesAreTheAccountsOwnAndEmptyForTheOthers()
    {
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            PlayerRows.AddTrophy(con, "/v1/Trophy/list", "1", O("""{"id": 1, "name": "first"}"""));
            PlayerRows.AddTrophy(con, "/v1/Trophy/list", "2", O("""{"id": 2, "name": "second"}"""));
            PlayerRows.AddTrophy(con, "/game/user/harvest", "5", O("""{"id": 5, "weight": 9}"""));
            PlayerRows.AddTrophy(con, "/game/user/trophies", "9", O("""{"id": 9, "own": true}"""));
            PlayerRows.AddLodge(con, "/game/user/lodges", "3", O("""{"id": 3, "name": "cabin"}"""));
            PlayerRows.AddLodge(con, "/game/user/lodge", "3", O("""{"id": 3, "name": "cabin", "full": true}"""));
            con.Commit();
        }
        var mine = await Api(Replay.TROPHY_LIST, tokenA, "user_id=7&limit=1&offset=1");
        Eq("""[2, [{"id": 2, "name": "second"}]]""", T(mine["total"], mine["trophies"]));
        Eq("0", (await Api(Replay.TROPHY_LIST, tokenB, "user_id=8"))["total"]);
        Eq("0", (await Api(Replay.TROPHY_LIST, tokenB, "user_id=7"))["total"]);  // the recorded player's account: no player here owns it, so nobody reads it
        async Task<(int, string)> V2(string path, string token)
        {
            var reply = await Send(community, "GET", "apiv2.test", path, token);
            return (reply.Status, Encoding.ASCII.GetString(reply.Body));
        }
        Assert.Equal((200, """[{"id": 3, "name": "cabin"}]"""), await V2("/game/user/7/lodges", tokenA));
        Assert.Equal((200, """{"id": 3, "name": "cabin", "full": true}"""), await V2("/game/user/7/lodges/3", tokenA));
        Assert.Equal((200, """{"id": 5, "weight": 9}"""), await V2("/game/user/harvest/5", tokenA));
        Assert.Equal((200, "[]"), await V2("/game/user/8/lodges", tokenB));
        Assert.Equal((200, "[]"), await V2("/game/user/7/lodges", tokenB));
        Assert.Equal(404, (await V2("/game/user/8/lodges/3", tokenB)).Item1);
        Assert.Equal(404, (await V2("/game/user/harvest/5", tokenB)).Item1);
        Assert.Equal((200, "[]"), await V2("/game/user/trophies", tokenB));
        Assert.Equal((200, """[{"id": 9, "own": true}]"""), await V2("/game/user/trophies", tokenA));
    }

    [Fact]
    public async Task TestAnyAccountsTrophiesAreReadWithoutTheHiddenOnesAndPagedOnTheWebsite()
    {
        state.CreateAccount(9, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = 9");
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            PlayerRows.AddTrophy(con, "/v1/Trophy/list", "1", O("""{"id": 1, "name": "shown"}"""));
            PlayerRows.AddTrophy(con, "/v1/Trophy/list", "2", O("""{"id": 2, "name": "secret", "hidden": true}"""));
            for (var id = 1; id <= 26; id++)
                PlayerRows.AddTrophy(con, "/game/user/trophies", id.ToString(), O($$"""{"id": {{id}}, "hidden": {{(id == 5 ? "true" : "false")}}, "species": 1, "timestamp": {{(id == 1 ? "\"2020-09-13T10:00:00Z\"" : "1600000000")}}}"""));
            con.Commit();
        }
        state.NoteLaunch(8, "start=100&reserve=2"u8.ToArray());
        state.HeartbeatReply(8, Events(AccountIsolationTests.Harvest(71, 72270)));  // a harvest made here: its timestamp is an ISO time
        // Trophy/list of another account's user id: its rows without the hidden ones; its own owner sees all; a banned or unknown id is the empty reply
        var other = await Api(Replay.TROPHY_LIST, tokenA, "user_id=8");
        Eq("""[1, [{"id": 1, "name": "shown"}]]""", T(other["total"], other["trophies"]));
        Eq("2", (await Api(Replay.TROPHY_LIST, tokenB, "user_id=8"))["total"]);
        foreach (var id in new[] { 9, 99 })
            Eq("0", (await Api(Replay.TROPHY_LIST, tokenA, $"user_id={id}"))["total"]);

        async Task<JsonNode> Page(string query) => JsonNode.Parse((await Send(community, "GET", "web.test", "/hunters/second/trophies" + query, null)).Body)!;
        var first = await Page("?page=1");
        Eq("""[1, 2, 26]""", T(first["page"], first["pages"], first["total"]));
        Assert.Equal(24, first["rows"]!.AsArray().Count);
        Eq("""{"name": "Test Deer", "date": "2020-09-13"}""", first["rows"]![0]);
        var last = await Page("?page=3");  // past the end: the last page, the harvest made here after the restored rows
        Eq("""[2, 2]""", T(last["page"], last["rows"]!.AsArray().Count));
        Eq($$"""{"name": "Test Deer", "date": "{{DateTime.UtcNow:yyyy-MM-dd}}"}""", last["rows"]![1]);
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/hunters/banned/trophies", null)).Status);
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/hunters/nobody/trophies", null)).Status);
    }

    [Fact]
    public async Task TestAServerHarvestIsInTheOwnTrophiesAboveEveryRestoredIdAndEachCanBeTaxidermized()
    {
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            PlayerRows.AddTrophy(con, "/v1/Trophy/list", "1", O("""{"id": 1, "name": "first"}"""));
            PlayerRows.AddTrophy(con, "/v1/Trophy/list", "500", O("""{"id": 500, "name": "listed"}"""));  // another kind of row: it does not raise the kill id
            PlayerRows.AddTrophy(con, "/game/user/trophies", "9", O("""{"id": 9, "own": true}"""));
            PlayerRows.AddTrophy(con, "/game/user/harvest", "9", O("""{"id": 9, "taxidermized": false}"""));
            con.Commit();
        }
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Hb(AccountIsolationTests.Harvest(71, 50000));
        string[] fields = ["id", "hidden", "species", "species_define", "score", "score_type", "is_skull", "gender", "weight", "scale", "reserve_id", "timestamp",
                           "texture_id", "base_texture", "fur_texture", "antlers_seed", "antlers_score_percentage", "holed"];
        // Trophy/list stays the restored rows only
        var page = await Api(Replay.TROPHY_LIST, tokenA, "user_id=7");
        Eq("""[2, [{"id": 1, "name": "first"}, {"id": 500, "name": "listed"}]]""", T(page["total"], page["trophies"]));
        var own = JsonNode.Parse((await Send(community, "GET", "apiv2.test", "/game/user/trophies", tokenA)).Body)!.AsArray();
        var row = own[1]!.AsObject();
        Eq("""[2, 9, 10]""", T(own.Count, own[0]!["id"], row["id"]));  // the server kill got an id above the restored 9
        Assert.Equal(fields, row.Select(kv => kv.Key));
        Eq("""[false, "MULE_DEER", 50000, 0.9, 46658, false]""", T(row["hidden"], row["species_define"], row["weight"], row["scale"], row["antlers_seed"], row["is_skull"]));

        async Task<JsonNode> Harvest(int id) => JsonNode.Parse((await Send(community, "GET", "apiv2.test", "/game/user/harvest/" + id, tokenA)).Body)!;
        Assert.Equal(200, (await Send(community, "POST", "apiv2.test", "/game/user/harvest/9/taxidermize", tokenA)).Status);
        Assert.True((bool)(await Harvest(9))["taxidermized"]!);
        Assert.False((bool)(await Harvest(10))["taxidermized"]!);  // the server kill is its own
        Assert.Equal(200, (await Send(community, "POST", "apiv2.test", "/game/user/harvest/10/taxidermize", tokenA)).Status);
        Assert.True((bool)(await Harvest(10))["taxidermized"]!);
        Assert.Equal(404, (await Send(community, "POST", "apiv2.test", "/game/user/harvest/11/taxidermize", tokenA)).Status);
    }

    [Fact]
    public async Task TestAnyAccountsPublicProfileIsReadWithoutAToken()
    {
        state.CreateAccount(9, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = 9");
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            PlayerRows.AddAchievement(con, 1, 1, 1, 1, O("""{"1": 1600000000}"""));
            PlayerRows.AddAchievement(con, 2, 1, 1, 0, new JsonObject());  // not earned
            PlayerRows.SetExtra(con, "game_user_8_profile.json", O("""{"background": 3, "ribbon": "gold", "hunterscore": 1234}"""));
            con.Commit();
        }
        state.NoteLaunch(8, "start=100&reserve=2"u8.ToArray());  // a hunt just opened: online
        var p = (await Api(Replay.PUBLIC_USER, null, "hostname=SECOND"))!;
        Eq("""[8, "second", "second", 0, 1, 0, "freeplay", true, {"distance": 0, "duration": 0}]""",
           T(p["id"], p["handle"], p["hostname"], p["hunterscore"], p["rank"], p["membership"], p["membershipType"], p["online"], p["stats"]));
        Assert.EndsWith($"/256/{State.DefaultAvatar}.png", (string)p["avatar"]!);
        // unknown, banned, and the recorded player's account (no player here owns it): null, never an empty profile
        foreach (var handle in new[] { "nobody", "banned", "hunter" })
            Assert.Equal("null", Encoding.ASCII.GetString((await Send(community, "POST", "api.test", Replay.PUBLIC_USER, null, "hostname=" + handle)).Body));

        var v2 = await Send(community, "GET", "apiv2.test", "/game/user/8/profile", null);
        Assert.Equal((200, "state"), (v2.Status, v2.Source));
        Eq("""{"background": 3, "ribbon": "gold", "hunterscore": 0, "earned_achievements": 1}""", JsonNode.Parse(v2.Body));
        Assert.Equal(v2.Body, (await Send(community, "GET", "api.test", "/v1/game/user/8/profile", null)).Body);  // the current build's path
        var ranks = await Send(community, "GET", "apiv2.test", "/game/user/8/ranks", null);
        Assert.Equal((200, "state"), (ranks.Status, ranks.Source));
        Assert.Equal(ranks.Body, (await Send(community, "GET", "api.test", "/v1/game/user/8/ranks", null)).Body);  // the current build's path
        foreach (var id in new[] { 7, 9, 99 })
        {
            Assert.Equal(404, (await Send(community, "GET", "apiv2.test", $"/game/user/{id}/profile", tokenB)).Status);
            Assert.Equal(404, (await Send(community, "GET", "apiv2.test", $"/game/user/{id}/ranks", tokenB)).Status);
        }
    }

    [Fact]
    public async Task TestAnyAccountsStatisticsAreReadWithAnotherPlayersToken()
    {
        using (var con = state.Store.Open(8))
            PlayerRows.SetStatistics(con, O("""{"collectables": {}, "weapon": {}, "animal": {"1": {"kills": 2}}, "daily_missions": {}}"""), 12.5, 3600, null, null);
        var theirs = await Send(community, "POST", "api.test", Replay.STATISTICS_TOTAL, tokenA, "user_id=8");
        Assert.Equal((200, "state"), (theirs.Status, theirs.Source));
        Eq("""{"duration": 3600, "distance": 12.5, "data": {"collectables": {}, "weapon": {}, "animal": {"1": {"kills": 2}}, "daily_missions": {}}}""", JsonNode.Parse(theirs.Body));
        // an unknown user id and the recorded player's are not served
        foreach (var id in new[] { 7, 99 })
            Assert.Equal("empty", (await Send(community, "POST", "api.test", Replay.STATISTICS_TOTAL, tokenB, $"user_id={id}")).Source);
    }

    [Fact]
    public void TestANewAccountsConnectHasItsOwnAccountAndEmptyStatistics()
    {
        var connect = JsonNode.Parse(state.ConnectReply(8))!;
        Eq("""["second", 8]""", T(connect["account"]!["handle"], connect["account"]!["user_id"]));
        Eq("""{}""", connect["user_stats"]!["user_stats"]!["weapon"]);
        Eq("[]", connect["user_stats"]!["achievement_stats"]);
    }

    static JsonNode Ids(JsonNode list) => new JsonArray([.. list.AsArray().Select(e => e!["id"]!.DeepClone())]);

    [Fact]
    public async Task TestFriendRequestsAreSentAcceptedDeclinedAndRemoved()
    {
        state.CreateAccount(10, "Third");
        state.CreateAccount(11, "alpha");
        var (tokenC, tokenD) = (state.Store.CreateToken(10), state.Store.CreateToken(11));
        Task<JsonNode> Friends(string token) => Api(Replay.ME_FRIENDS, token);
        async Task<string> Call(string path, string token, long id) => Encoding.ASCII.GetString((await Send(community, "POST", "api.test", path, token, $"friend_user_id={id}")).Body);

        // 8 asks 10: a request on both sides, counted by the poll and Me/me of 10
        Assert.Equal("true", await Call(Replay.FRIEND_ADD, tokenB, 10));
        Assert.Equal("true", await Call(Replay.FRIEND_ADD, tokenB, 10));  // asking again changes nothing
        Eq("""{"friends": [], "friendCount": 0, "requests": [], "requestCount": 0, "pending": [10]}""", await Friends(tokenB));
        var asked = await Friends(tokenC);
        Eq("""[1, [8], []]""", T(asked["requestCount"], Ids(asked["requests"]!), asked["pending"]));
        var entry = asked["requests"]![0]!;
        Eq("""[8, "second", 0, "second", 0, false]""", T(entry["id"], entry["handle"], entry["fullbody_avatar"], entry["hostname"], entry["hunterscore"], entry["online"]));
        Assert.EndsWith(".png", (string)entry["avatar"]!);
        Eq("[1, 1]", T((await Api(Replay.POLL, tokenC))["friend_requests"], (await Api(Replay.ME, tokenC))["friend_requests"]));

        // 10 accepts with the current build's call (game web host, lower case, JSON body)
        var accept = await community.SendAsync("POST", "game_web.test", "/v1/friend/add?locale=en_us", [("Content-Type", "application/json")],
                                               Encoding.UTF8.GetBytes($$"""{"oauth_access_token": "{{tokenC}}", "friend_user_id": 8}"""));
        Assert.Equal("true", Encoding.ASCII.GetString(accept.Body));
        foreach (var token in new[] { tokenB, tokenC })
        {
            var f = await Friends(token);
            Eq($"[1, [{(token == tokenB ? 10 : 8)}], 0, [], []]", T(f["friendCount"], Ids(f["friends"]!), f["requestCount"], f["requests"], f["pending"]));
        }
        Eq("0", (await Api(Replay.POLL, tokenC))["friend_requests"]);

        // 11 asks 8, 8 declines; then 8 asks 11 and 11 accepts
        Assert.Equal("true", await Call(Replay.FRIEND_ADD, tokenD, 8));
        Assert.Equal("true", await Call(Replay.FRIEND_REMOVE, tokenB, 11));
        Eq("""[[10], [], []]""", T(Ids((await Friends(tokenB))["friends"]!), (await Friends(tokenB))["requests"], (await Friends(tokenD))["pending"]));
        Assert.Equal("true", await Call(Replay.FRIEND_ADD, tokenB, 11));
        Assert.Equal("true", await Call(Replay.FRIEND_ADD, tokenD, 8));
        Eq("[11, 10]", Ids((await Friends(tokenB))["friends"]!));  // by handle, any case: alpha, Third

        // any player's list without a token, paged; its errors
        async Task<string> List(string body) => Encoding.ASCII.GetString((await Send(community, "POST", "api.test", Replay.FRIENDS_LIST, null, body)).Body);
        var all = JsonNode.Parse(await List("user_id=8&offset=0&limit=0"))!;
        Eq("[2, [11, 10]]", T(all["total"], Ids(all["friends"]!)));
        Eq(all, JsonNode.Parse(await List("user_id=8")));
        var page = JsonNode.Parse(await List("user_id=8&offset=1&limit=1"))!;
        Eq("[2, [10]]", T(page["total"], Ids(page["friends"]!)));
        Eq("""{"total": 2, "friends": []}""", JsonNode.Parse(await List("user_id=8&offset=5&limit=20")));
        Assert.Equal("""{"errorCode": 400}""", await List("offset=0"));
        foreach (var body in new[] { "user_id=", "user_id=0", "user_id=-3", "user_id=99", "user_id=7" })
            Assert.Equal("null", await List(body));
        foreach (var body in new[] { "user_id=x", "user_id=8&offset=x", "user_id=8&limit=x" })
            Assert.Equal("""{"error": 1, "code": "handlerError"}""", await List(body));

        // 8 removes 10: gone on both sides; an own, unknown or the recorded player's id is refused
        Assert.Equal("true", await Call(Replay.FRIEND_REMOVE, tokenB, 10));
        Eq("[]", (await Friends(tokenC))["friends"]);
        Eq("""{"total": 0, "friends": []}""", JsonNode.Parse(await List("user_id=10")));
        Eq("[11]", Ids((await Friends(tokenB))["friends"]!));
        foreach (var id in new[] { 8, 99, 7 })
            Assert.Equal("""{"errorCode": 400}""", await Call(Replay.FRIEND_ADD, tokenB, id));

        state.Store.DeleteAccount(11);  // its edges go with it
        state.CreateAccount(11, "again");
        Eq("[]", (await Friends(tokenB))["friends"]);
    }

    [Fact]
    public async Task TestARestoredFriendListCountsOnceTheFriendHasAnAccountHere()
    {
        state.CreateAccount(10, "third");
        var zip = TestZip.Own()
            .Reply("User_friendslist__user_id=1001__offset=0__limit=0.json", "/v1/User/friendslist", """{"total": 2, "friends": [{"id": 8}, {"id": 4242}]}""",
                   ("user_id", "1001"), ("offset", "0"), ("limit", "0"))
            .Reply("Me_friends.json", "/v1/Me/friends", """{"friends": [{"id": 8}, {"id": 4242}], "requests": [{"id": 4343}], "pending": [10]}""");
        Restorer.Import(state, zip.Write(tmp), null, tmp);
        var token = state.Store.CreateToken(1001);

        var mine = await Api(Replay.ME_FRIENDS, token);
        Eq("[1, [8], 0, [10]]", T(mine["friendCount"], Ids(mine["friends"]!), mine["requestCount"], mine["pending"]));
        Eq("[1001]", Ids((await Api(Replay.ME_FRIENDS, tokenB))["friends"]!));  // mutual: 8's own list never held it
        Eq("[1001]", Ids((await Api(Replay.ME_FRIENDS, state.Store.CreateToken(10)))["requests"]!));

        state.CreateAccount(4242, "later");
        state.CreateAccount(4343, "asker");
        mine = await Api(Replay.ME_FRIENDS, token);
        Eq("[[4242, 8], [4343]]", T(Ids(mine["friends"]!), Ids(mine["requests"]!)));  // later, second
        Eq("[4242, 8]", Ids(JsonNode.Parse((await Send(community, "POST", "api.test", Replay.FRIENDS_LIST, null, "user_id=1001")).Body)!["friends"]!));

        // 4343's own restore holds 1001: its request (from 1001's backup) becomes a friendship, whichever restore came first
        var asker = TestZip.Own(uid: "4343", handle: "asker")
            .Reply("User_friendslist__user_id=4343.json", "/v1/User/friendslist", """{"total": 1, "friends": [{"id": 1001}]}""", ("user_id", "4343"));
        Restorer.Import(state, asker.Write(tmp, "asker.zip"), null, tmp);
        mine = await Api(Replay.ME_FRIENDS, token);
        Eq("[[4343, 4242, 8], []]", T(Ids(mine["friends"]!), mine["requests"]));  // asker, later, second
    }
}
