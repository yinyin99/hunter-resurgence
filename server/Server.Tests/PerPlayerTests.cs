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
        Eq("0", (await Api(Replay.TROPHY_LIST, tokenB, "user_id=7"))["total"]);  // not B's to read
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
    public async Task TestAServerHarvestIsInBothTrophyListsAndARestoredHarvestCanBeTaxidermized()
    {
        using (var con = state.Store.Open(ME))
        {
            con.Begin();
            PlayerRows.AddTrophy(con, "/v1/Trophy/list", "1", O("""{"id": 1, "name": "first"}"""));
            PlayerRows.AddTrophy(con, "/game/user/trophies", "9", O("""{"id": 9, "own": true}"""));
            PlayerRows.AddTrophy(con, "/game/user/harvest", "9", O("""{"id": 9, "taxidermized": false}"""));
            con.Commit();
        }
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Hb(AccountIsolationTests.Harvest(71, 50000));
        string[] fields = ["id", "hidden", "species", "species_define", "score", "score_type", "is_skull", "gender", "weight", "scale", "reserve_id", "timestamp",
                           "texture_id", "base_texture", "fur_texture", "antlers_seed", "antlers_score_percentage", "holed"];
        var page = await Api(Replay.TROPHY_LIST, tokenA, "user_id=7&limit=1&offset=1");
        var row = Assert.Single(page["trophies"]!.AsArray())!.AsObject();
        Assert.Equal((2, 1), ((int)page["total"]!, page["trophies"]!.AsArray().Count));
        Assert.Equal(fields, row.Select(kv => kv.Key));
        Eq("""[1, false, "MULE_DEER", 50000, 0.9, 46658, false]""", T(row["id"], row["hidden"], row["species_define"], row["weight"], row["scale"], row["antlers_seed"], row["is_skull"]));
        var own = JsonNode.Parse((await Send(community, "GET", "apiv2.test", "/game/user/trophies", tokenA)).Body)!.AsArray();
        Eq("[9, 1]", T(own[0]!["id"], own[1]!["id"]));
        Assert.Equal(fields, own[1]!.AsObject().Select(kv => kv.Key));

        Assert.Equal(200, (await Send(community, "POST", "apiv2.test", "/game/user/harvest/9/taxidermize", tokenA)).Status);
        Assert.True((bool)JsonNode.Parse((await Send(community, "GET", "apiv2.test", "/game/user/harvest/9", tokenA)).Body)!["taxidermized"]!);
        Assert.Equal(404, (await Send(community, "POST", "apiv2.test", "/game/user/harvest/10/taxidermize", tokenA)).Status);
    }

    [Fact]
    public void TestANewAccountsConnectHasItsOwnAccountAndEmptyStatistics()
    {
        var connect = JsonNode.Parse(state.ConnectReply(8))!;
        Eq("""["second", 8]""", T(connect["account"]!["handle"], connect["account"]!["user_id"]));
        Eq("""{}""", connect["user_stats"]!["user_stats"]!["weapon"]);
        Eq("[]", connect["user_stats"]!["achievement_stats"]);
    }
}
