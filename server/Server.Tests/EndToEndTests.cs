using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Spec #244 end to end, over HTTP: a player restores a made-up backup (upload, then the operator's approve), launches, connects,
/// hunts and harvests an animal, disconnects; the new kill then shows in the hunterscore, the statistics, the trophies and the hunt history,
/// on the game's replies and on the website's `/hunters/&lt;handle&gt;` reads, for the player's own user id and read by a second account.</summary>
public class EndToEndTests : StateTestBase, IAsyncLifetime
{
    const string Steam = "76561190000000000";  // the one TestZip.Own claims
    const long Restored = 1001;  // the user id and handle in that zip
    const string Handle = "sample";
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    static readonly (string, string)[] FormHeader = [("Content-Type", "application/x-www-form-urlencoded")];

    // species 1 and weapon item 5: one kill with each is worth 1 + 1
    static readonly Facts Ranks = Facts.Parse("""
        {"format": 1, "ranks": {"cap": 1000, "rows": [
          {"id": 1, "title": "One", "category": "animal", "species": 1},
          {"id": 3, "title": "Five", "category": "weapon", "items": [5]}]}}
        """);

    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, facts: Ranks);
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    /// <summary>An account the way the first sign-in makes it: its id, its cookie and a game token.</summary>
    (long Id, string Cookie, string Token) SignedIn(string handle, string steam)
    {
        var id = state.Store.NextServerId();
        state.CreateAccount(id, handle, steam);
        return (id, state.Store.CreateWebSession(id, TimeSpan.FromDays(1)), state.Store.CreateToken(id, TimeSpan.FromHours(1)));
    }

    /// <summary>The website server's read of the restored player's area, with the viewer's cookie (the area is public).</summary>
    async Task<JsonNode> Hunters(string area, string cookie) =>
        JsonNode.Parse((await server.SendAsync("GET", "web.test", $"/hunters/{Handle}{area}", [("Cookie", "hr_session=" + cookie)])).Body)!;

    /// <summary>A form call on the game's web API, as the viewer.</summary>
    async Task<JsonNode> Api(string path, string token, string form) =>
        JsonNode.Parse((await server.SendAsync("POST", "api.test", path, FormHeader, Encoding.ASCII.GetBytes($"oauth_access_token={token}&{form}"))).Body)!;

    Task<Reply> Game(string path, string token, byte[] body) => server.SendAsync("POST", "gameserver.test", path, [("X-Hun-Session", token)], body);

    /// <summary>`/game/user/trophies`: the caller's own list.</summary>
    async Task<JsonArray> OwnTrophies(string token) =>
        JsonNode.Parse((await server.SendAsync("GET", "apiv2.test", "/game/user/trophies", [("Authorization", "Bearer " + token)])).Body)!.AsArray();

    [Fact]
    public async Task TestAKillOfARestoredPlayerShowsEverywhereForTheOwnerAndForAnotherPlayer()
    {
        var (player, cookie, token) = SignedIn("player", Steam);
        var (_, otherCookie, otherToken) = SignedIn("second", "76561190000000002");

        // restore: upload, then the operator approves; the account is the backup's now, its token and cookie stay
        var zip = TestZip.Own().Hunts().Reply("game_user_trophies.json", "/game/user/trophies",
            """[{"id": 50, "species": 1, "hidden": false, "timestamp": 1600000000, "weight": 41000}]""");
        var upload = await server.SendAsync("POST", "web.test", "/account/restore",
            [("Content-Type", "application/zip"), ("X-Requested-With", "XMLHttpRequest"), ("Cookie", "hr_session=" + cookie)], File.ReadAllBytes(zip.Write(tmp)));
        Assert.Equal((200, "review"), (upload.Status, (string)JsonNode.Parse(upload.Body)!["status"]!));
        using (var con = state.Store.Open(player))
            Assert.Equal("restored", new Uploads(state, tmp).Approve((long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!).Kind);
        Assert.Equal(Restored, state.Store.ResolveToken(token));
        Eq("0", (await Hunters("", otherCookie))["hunterscore"]);  // the backup's statistics hold no kill of the hunterscore's rows

        // play: launch, connect, harvest a deer with the rifle, disconnect
        var launch = await server.SendAsync("POST", "api.test", Replay.LAUNCH_START, FormHeader,
            Encoding.ASCII.GetBytes($"start=101&reserve=2&loctype=LODGE&oauth_access_token={token}"));
        Assert.Equal("state", launch.Source);
        Assert.Equal("state", (await Game(Replay.CONNECT, token, "{}"u8.ToArray())).Source);
        Assert.Equal("state", (await Game(Replay.HEARTBEAT, token, Events(AccountIsolationTests.Harvest(81, 72270)))).Source);
        Eq("""{"err": 0}""", JsonNode.Parse((await Game(Replay.DISCONNECT, token, """{"distance_travelled": 466}"""u8.ToArray())).Body));

        // read by the player and by a second account: another player's view of the same profile
        foreach (var (viewer, viewerToken) in new[] { (cookie, token), (otherCookie, otherToken) })
        {
            // the hunterscore: the species row and the weapon row
            Eq("2", (await Hunters("", viewer))["hunterscore"]);
            var user = await server.SendAsync("POST", "api.test", Replay.PUBLIC_USER, FormHeader, Encoding.ASCII.GetBytes($"hostname={Handle}"));
            Eq("2", JsonNode.Parse(user.Body)!["hunterscore"]);

            // the statistics: the backup's base and the new kill on top
            var total = (await Api(Replay.STATISTICS_TOTAL, viewerToken, $"user_id={Restored}"))["data"]!;
            Eq("""[2, 1, 1]""", T(total["animal"]!["5"]!["kills"], total["animal"]!["1"]!["kills"], total["weapon"]!["5"]!["10"]!["kills"]));
            var animals = (await Hunters("/statistics", viewer))["animals"]!.AsArray();
            Assert.Equal(1L, (long)animals.Single(a => (string)a!["name"]! == "Test Deer")!["harvested"]!);

            // the hunt history: the hunt made here ahead of the three the backup saved
            var list = await Api(Replay.EXPEDITION_LIST, viewerToken, $"user_id={Restored}&offset=0&limit=10");
            Eq("""[4, 1, 9003]""", T(list["total"], list["expeditions"]![0]!["kills"], list["expeditions"]![1]!["id"]));
            var hunts = await Hunters("/hunts", viewer);
            Eq("""[4, 1, 9003]""", T(hunts["total"], hunts["rows"]![0]!["kills"], hunts["rows"]![1]!["id"]));

            // the trophies: the restored one, then the harvest (Trophy/list of the game lists the restored rows only: the harvest is not in it)
            var trophies = await Hunters("/trophies", viewer);
            Eq("""[2, "Test Deer", "Test Deer"]""", T(trophies["total"], trophies["rows"]![0]!["name"], trophies["rows"]![1]!["name"]));
        }

        // the game's own list is the caller's: the player's two, the second account's none
        Eq("[50, 51]", new JsonArray([.. (await OwnTrophies(token)).Select(t => t!["id"]!.DeepClone())]));
        Assert.Empty(await OwnTrophies(otherToken));
    }
}
