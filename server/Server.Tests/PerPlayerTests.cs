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
    public async Task TestTwoAccountsSeeOnlyTheirOwnItemsButReadEachOthersHunts()
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
        Assert.Equal(1, (int)(await Api(Replay.EXPEDITION_LIST, tokenA, "user_id=8&limit=10&offset=0"))["total"]!);
        Assert.Null(await Api(Replay.PUBLIC_EXPEDITION, tokenA, "user_id=7"));
        Assert.NotNull(await Api(Replay.PUBLIC_EXPEDITION, tokenB, "user_id=8"));
        Assert.NotNull(await Api(Replay.PUBLIC_EXPEDITION, tokenA, "user_id=8"));  // a hunt is public: any player reads another's (the hunts of another id)
        Assert.Null(await Api(Replay.PUBLIC_EXPEDITION, tokenB, "user_id=7"));  // the recorded player's is nobody's
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
            for (var id = 3; id <= 27; id++)
                PlayerRows.AddTrophy(con, "/v1/Trophy/list", id.ToString(), O($$"""{"id": {{id}}, "name": "cup {{id}}", "competition_name": "Open {{id}}", "competition_id": {{1000 + id}}, "image": "https://static.test/img/trophies/cup_{{id}}.png", "ts": 1600000000, "hidden": false}"""));
            PlayerRows.AddTrophy(con, "/game/user/trophies", "5", O("""{"id": 5, "hidden": false, "species": 1, "timestamp": 1600000000}"""));  // a kill of the lodges: not a competition trophy
            con.Commit();
        }
        state.NoteLaunch(8, "start=100&reserve=2"u8.ToArray());
        state.HeartbeatReply(8, Events(AccountIsolationTests.Harvest(71, 72270)));  // a harvest made here: not a competition trophy either
        // Trophy/list of another account's user id: its rows without the hidden ones; its own owner sees all; a banned or unknown id is the empty reply
        var other = await Api(Replay.TROPHY_LIST, tokenA, "user_id=8&limit=1");
        Eq("""[26, [{"id": 1, "name": "shown"}]]""", T(other["total"], other["trophies"]));
        Eq("27", (await Api(Replay.TROPHY_LIST, tokenB, "user_id=8"))["total"]);
        foreach (var id in new[] { 9, 99 })
            Eq("0", (await Api(Replay.TROPHY_LIST, tokenA, $"user_id={id}"))["total"]);

        async Task<JsonNode> Page(string query) => JsonNode.Parse((await Send(community, "GET", "web.test", "/hunters/second/trophies" + query, null)).Body)!;
        var first = await Page("?page=1");
        Eq("""[1, 2, 26]""", T(first["page"], first["pages"], first["total"]));
        Assert.Equal(24, first["rows"]!.AsArray().Count);
        Eq("""{"name": "shown", "date": "", "competitionId": null, "competition": null, "image": null}""", first["rows"]![0]);  // a row without a competition, a date or a picture
        Eq("""{"name": "cup 3", "date": "2020-09-13", "competitionId": 1003, "competition": "Open 3", "image": "cup_3"}""", first["rows"]![1]);
        var last = await Page("?page=3");  // past the end: the last page
        Eq("""[2, 2]""", T(last["page"], last["rows"]!.AsArray().Count));
        Eq("""{"name": "cup 27", "date": "2020-09-13", "competitionId": 1027, "competition": "Open 27", "image": "cup_27"}""", last["rows"]![1]);
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/hunters/banned/trophies", null)).Status);
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/hunters/nobody/trophies", null)).Status);
    }

    [Fact]
    public async Task TestTheProfileHeaderCarriesTheRestoredBackground()
    {
        async Task<JsonNode?> Background() => JsonNode.Parse((await Send(community, "GET", "web.test", "/hunters/second", null)).Body)!["background"];
        Assert.Null(await Background());  // no restored profile
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            PlayerRows.SetExtra(con, "game_user_8_profile.json", O("""{"background": "exploration_3.png", "ribbon": "moose.png"}"""));
            con.Commit();
        }
        Assert.Equal("exploration_3.png", (string?)await Background());
    }

    [Fact]
    public async Task TestAnyAccountsGalleryIsListedItsOwnerDeletesAPhotoAndAKillLinksItsPhoto()
    {
        state.CreateAccount(9, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = 9");
        state.NoteLaunch(8, "start=100&reserve=2"u8.ToArray());
        state.HeartbeatReply(8, Events(AccountIsolationTests.Harvest(71, 72270)));
        void Photo(long account, string json, bool upload = true, bool thumb = false)
        {
            var update = Reply(state.HeartbeatReply(account, Events(O(json))))["updates"]!.AsArray().Single(u => (int)u!["update"]! == 11)!;  // the trophy score of the harvest may come first
            if (upload) Assert.Equal(200, state.UploadReply(account, new Uri((string)update["url"]!).Query.TrimStart('?'), [1, 2]).Status);
            if (thumb) Assert.Equal(200, state.UploadReply(account, new Uri((string)update["thumb_url"]!).Query.TrimStart('?'), [3]).Status);
        }
        Photo(8, """{"event": 4, "hash": 1, "photo_type": 2, "individual": 71}""", thumb: true);    // 1: the kill's trophy photo
        Photo(8, """{"event": 4, "hash": 2, "photo_type": 14, "individual": 71}""");                // 2: the shot's screenshot, no gallery picture
        Photo(8, """{"event": 4, "hash": 3, "photo_type": 3, "individual": 99}""");                 // 3: no kill of this individual
        Photo(8, """{"event": 4, "hash": 4, "photo_type": 2, "individual": 71}""", upload: false);  // 4: asked, never sent
        Photo(ME, """{"event": 4, "hash": 5, "photo_type": 2}""");                                   // 5: the other player's
        var photos = $"{BASE}/photos/8/";

        // Gallery/list: any account's, newest first, paged; no user_id is the player's own; a banned or unknown id is the empty reply
        var other = await Api(Replay.GALLERY_LIST, tokenA, "user_id=8");
        Eq("[2, [3, 1]]", T(other["total"], Ids(other["photos"]!)));
        Eq($$"""{"id": 3, "type": 3, "url": "{{photos}}3-3.jpg", "thumbnail": "{{photos}}3-3.jpg", "label": "", "animal": ""}""", other["photos"]![0]);  // no thumbnail sent: the picture itself
        Eq($$"""[1, 2, "Test Deer", "{{photos}}1-2.jpg", "{{photos}}1-2-thumb.jpg"]""",
           T(other["photos"]![1]!["id"], other["photos"]![1]!["type"], other["photos"]![1]!["animal"], other["photos"]![1]!["url"], other["photos"]![1]!["thumbnail"]));
        Assert.False(string.IsNullOrEmpty((string?)other["photos"]![1]!["label"]));  // the kill's score
        Eq("[1]", Ids((await Api(Replay.GALLERY_LIST, tokenA, "user_id=8&offset=1&limit=1"))["photos"]!));
        Eq("[2, 2, 1]", T((await Api(Replay.GALLERY_LIST, tokenB, ""))["total"], (await Api(Replay.GALLERY_LIST, tokenB, "user_id=8"))["total"], (await Api(Replay.GALLERY_LIST, tokenA, ""))["total"]));
        Eq("[5]", Ids((await Api(Replay.GALLERY_LIST, tokenA, ""))["photos"]!));
        foreach (var id in new[] { 9, 99 })
            Eq("0", (await Api(Replay.GALLERY_LIST, tokenA, $"user_id={id}"))["total"]);

        // a kill's photo is its shot's screenshot, else its other photo
        JsonNode? KillPhoto() => Reply(state.ExpeditionReply(8, "{}"u8.ToArray()))["kills"]![0]!["kill"]!["photo"];
        Eq($"\"{photos}2-14.jpg\"", KillPhoto());

        // the website's page: 24 a page, a page past the end is the last, no banned or unknown hunter
        async Task<JsonNode> Page(string query) => JsonNode.Parse((await Send(community, "GET", "web.test", "/hunters/second/gallery" + query, null)).Body)!;
        var first = await Page("?page=1");
        Eq("[1, 1, 2, [3, 1]]", T(first["page"], first["pages"], first["total"], Ids(first["rows"]!)));
        Eq($"\"https://web.test/photos/8/3-3.jpg\"", first["rows"]![0]!["url"]);  // the website's own host: the game's is no host a browser reaches
        Eq("[3, 1]", Ids(first["rows"]!));
        Assert.Equal(200, (await Send(community, "GET", "web.test", "/photos/8/3-3.jpg", null)).Status);
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/photos/8/nothing.jpg", null)).Status);
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            for (var n = 0; n < 24; n++)
                con.Exec("INSERT INTO photos (account_id, hash, photo_type, file) VALUES ($a, 9, 2, 'x.jpg')");
            con.Commit();
        }
        Eq("[2, 2, 26, 2]", T((await Page("?page=9"))["page"], (await Page("?page=9"))["pages"], (await Page("?page=1"))["total"], (await Page("?page=2"))["rows"]!.AsArray().Count));
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            con.Exec("DELETE FROM photos WHERE account_id = $a AND file = 'x.jpg'");
            con.Commit();
        }
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/hunters/banned/gallery", null)).Status);
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/hunters/nobody/gallery", null)).Status);

        // Gallery/delete: an own photo only, its row and files
        Eq("false", await Api(Replay.GALLERY_DELETE, tokenA, "photo_id=3"));  // another player's
        Assert.NotNull(state.PhotoFile("/photos/8/3-3.jpg"));
        Eq("true", await Api(Replay.GALLERY_DELETE, tokenB, "photo_id=1"));
        Assert.Null(state.PhotoFile("/photos/8/1-2.jpg"));
        Assert.Null(state.PhotoFile("/photos/8/1-2-thumb.jpg"));
        Eq("false", await Api(Replay.GALLERY_DELETE, tokenB, "photo_id=1"));
        Eq("false", await Api(Replay.GALLERY_DELETE, tokenB, "photo_id=nope"));
        Eq("[3]", Ids((await Api(Replay.GALLERY_LIST, tokenB, "limit=1"))["photos"]!));
        Assert.True(state.DeletePhoto(8, 2));
        Assert.Null(KillPhoto());  // the kill's photos are gone
    }

    [Fact]
    public void TestAnIndividualIdThatComesBackShowsNoEarlierKillAndAnOldPhotoIsLinkedByItsPosition()
    {
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        state.HeartbeatReply(ME, Events(AccountIsolationTests.Harvest(71, 72270)));
        void Photo(string json) => state.UploadReply(ME, new Uri((string)Reply(state.HeartbeatReply(ME, Events(O(json))))["updates"]!.AsArray().Single(u => (int)u!["update"]! == 11)!["url"]!).Query.TrimStart('?'), [1]);
        Photo("""{"event": 4, "hash": 1, "photo_type": 2, "individual": 71}""");  // the first animal's
        Hb(O("""{"event": 7, "individual": 71, "species": 1, "gender": 0, "weight_grams": 5000, "texture": 0}"""));  // the id is used again
        Photo("""{"event": 4, "hash": 2, "photo_type": 2, "individual": 71}""");  // the second animal's: no kill
        var photos = state.GalleryListReply(ME, ""u8.ToArray())!;
        Eq("""[2, ["", "Test Deer"]]""", T(Reply(photos)["total"], new JsonArray(Reply(photos)["photos"]!.AsArray().Select(p => (JsonNode?)p!["animal"]!.DeepClone()).ToArray())));
        Assert.Contains("1-2.jpg", (string)Reply(state.ExpeditionReply(ME, "{}"u8.ToArray()))["kills"]![0]!["kill"]!["photo"]!);
        Assert.DoesNotContain("2-2.jpg", (string)Reply(state.ExpeditionReply(ME, "{}"u8.ToArray()))["kills"]![0]!["kill"]!["photo"]!);
    }

    [Fact]
    public void TestAPhotoOfAnOlderDatabaseGetsItsSpawnFromItsPositionAmongTheEvents()
    {
        var file = Path.Combine(tmp, "old.db");
        using (var con = new Store(file).Open())
        {
            con.Exec("ALTER TABLE photos DROP COLUMN spawn");
            con.Exec("ALTER TABLE harvests DROP COLUMN hidden");
            con.Exec("DROP TABLE loadouts");
            con.Exec("DROP TABLE wallet_log");
            con.Exec("DROP TABLE missions");
            con.Exec("DROP TABLE mission_prefs");
            con.Exec("DROP TABLE daily_missions");
            con.Exec("PRAGMA user_version = 7");
        }
        var old = new Store(file);
        old.CreateAccount(5, "five", new JsonObject());
        using (var con = old.Open(5))
        {
            foreach (var (ev, n) in new[] { (7, 1), (4, 2), (7, 3), (4, 4) })  // the events' ids are 1 to 4: the photos are the 2nd and the 4th
                con.Exec("INSERT INTO events (account_id, event, body) VALUES ($a, ?, '{\"individual\": 3}')", ev);
            con.Exec("INSERT INTO photos (account_id, individual) VALUES ($a, 3)");
            con.Exec("INSERT INTO photos (account_id, individual) VALUES ($a, 3)");
        }
        using (var con = old.Open())  // as it was before the column: the rows above are all there is
        {
            con.Exec("ALTER TABLE photos DROP COLUMN spawn");
            con.Exec("ALTER TABLE harvests DROP COLUMN hidden");
            con.Exec("DROP TABLE loadouts");
            con.Exec("DROP TABLE wallet_log");
            con.Exec("DROP TABLE missions");
            con.Exec("DROP TABLE mission_prefs");
            con.Exec("DROP TABLE daily_missions");
            con.Exec("PRAGMA user_version = 7");
        }
        using (var con = new Store(file).Open(5))
        {
            Eq("[1, 3]", T(con.Rows("SELECT spawn FROM photos WHERE account_id = $a ORDER BY id").Select(r => (JsonNode?)Py.FromSql(r[0])).ToArray()));
        }
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
            PlayerRows.AddAchievement(con, 1, 1, 1, 1, O("""{"1": 1600000000, "2": 1600000100, "3": 0}"""));  // two thresholds earned, one without a time
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
        Eq("""{"background": 3, "ribbon": "gold", "hunterscore": 0, "earned_achievements": 2}""", JsonNode.Parse(v2.Body));
        Assert.Equal(v2.Body, (await Send(community, "GET", "api.test", "/v1/game/user/8/profile", null)).Body);  // the current build's path
        Assert.Null(JsonNode.Parse((await Send(community, "GET", "web.test", "/hunters/second", null)).Body)!["background"]);  // the header sends a name only: not this 3
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
    public async Task TestPersonalBestsAreTheRestoredRepliesWithTheServersKillsInTheAllTimeOnesAndOnTheWebsite()
    {
        static string Best(long distance, string score, long animal) =>
            $$"""{"distance": {{distance}}, "confirm_ts": 1700000000, "weapon_id": 9, "animal_id": {{animal}}, "gender": 0, "texture": 0, "score": "{{score}}", "species": 1}""";
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            PlayerRows.SetExtra(con, "Leaderboard_personal__user_id=8__season_no=0.json",
                O($$"""{"status": "done", "data": [{"specie": 1, "score": {{Best(50000, "60.0000", 900)}}, "distance": {{Best(120000, "40.0000", 901)}}}, {"specie": 77, "score": {{Best(1000, "5.5000", 902)}}, "distance": {{Best(1000, "5.5000", 902)}}}, {"specie": 78, "score": 5, "distance": "far"}, {"specie": {"x": 1}, "score": {"score": "9"} }, {"specie": 79, "score": {"score": "NaN", "distance": "Infinity"} }]}"""));
            PlayerRows.SetExtra(con, "Leaderboard_personal__user_id=8__season_no=1.json", JsonNode.Parse("[1]"));  // a reply that is no object: a backup holds whatever the player sent, like the last row above
            PlayerRows.SetExtra(con, "Leaderboard_personal__user_id=8__season_no=3.json", O($$"""{"status": "done", "data": [{"specie": 1, "score": {{Best(1, "1.0000", 1)}}, "distance": {{Best(1, "1.0000", 1)}}}]}"""));
            con.Commit();
        }
        // no kill yet: the restored replies as saved; another season, and a season that was not saved, likewise
        var before = await Api(Replay.LEADERBOARD_PERSONAL, tokenA, "user_id=8&season_no=0");
        Eq("\"60.0000\"", before["data"]![0]!["score"]!["score"]);
        Eq("\"1.0000\"", (await Api(Replay.LEADERBOARD_PERSONAL, tokenA, "user_id=8&season_no=3"))["data"]![0]!["score"]!["score"]);
        Eq("""{"status": "done", "data": []}""", await Api(Replay.LEADERBOARD_PERSONAL, tokenA, "user_id=8&season_no=2"));
        Eq("""{"status": "done", "data": []}""", await Api(Replay.LEADERBOARD_PERSONAL, tokenA, "user_id=8&season_no=1"));

        state.NoteLaunch(8, "start=100&reserve=2"u8.ToArray());
        state.HeartbeatReply(8, Events(AccountIsolationTests.Harvest(71, 50000)));  // a kill 96.14 m away with the rifle 5
        using (var con = state.Store.Open(8))
            con.Exec("UPDATE harvests SET body = json_set(body, '$.score', 70.25) WHERE account_id = $a");

        // the kill beats the restored score, not the restored range; season 0 only
        var all = await Api(Replay.LEADERBOARD_PERSONAL, tokenA, "user_id=8&season_no=0");
        Eq("\"done\"", all["status"]);
        var row = all["data"]!.AsArray();
        Eq("""[1, 77, 78]""", T(row[0]!["specie"], row[1]!["specie"], row[2]!["specie"]));
        var score = row[0]!["score"]!;
        Eq("""["70.2500", 96140, 5, 1, 1]""", T(score["score"], score["distance"], score["weapon_id"], score["animal_id"], score["species"]));
        Assert.True((long)score["confirm_ts"]! > 1700000000);
        Eq(Best(120000, "40.0000", 901), row[0]!["distance"]);
        Eq(Best(1000, "5.5000", 902), row[1]!["score"]);
        Eq("\"1.0000\"", (await Api(Replay.LEADERBOARD_PERSONAL, tokenA, "user_id=8&season_no=3"))["data"]![0]!["score"]!["score"]);
        // anyone's by user id; an unknown one, and a player's own one that has nothing, are empty
        Eq("\"70.2500\"", (await Api(Replay.LEADERBOARD_PERSONAL, tokenB, "user_id=8&season_no=0"))["data"]![0]!["score"]!["score"]);
        Eq("""{"status": "done", "data": []}""", await Api(Replay.LEADERBOARD_PERSONAL, tokenA, "user_id=99&season_no=0"));
        Eq("""{"status": "done", "data": []}""", await Api(Replay.LEADERBOARD_PERSONAL, tokenA, "user_id=7&season_no=0"));

        var page = JsonNode.Parse((await Send(community, "GET", "web.test", "/hunters/second/best", null)).Body)!["rows"]!.AsArray();
        Eq("""["Test Deer", 70.25, 96.14, "Test Rifle", 1]""", T(page[0]!["species"], page[0]!["score"], page[0]!["range"], page[0]!["weapon"], page[0]!["kill"]));
        Eq("""["#77", 5.5, 1.0, "", 902]""", T(page[1]!["species"], page[1]!["score"], page[1]!["range"], page[1]!["weapon"], page[1]!["kill"]));
        Assert.Equal(1700000000L, (long)page[1]!["ts"]!);
        Eq("""["#79", 0.0, 0.0]""", T(page[2]!["species"], page[2]!["score"], page[2]!["range"]));  // numbers that are not finite count 0
        Assert.Equal(3, page.Count);  // the rows with no record, and the one whose species is no number, are left out
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/hunters/nobody/best", null)).Status);
        Assert.Equal(404, (await Send(community, "GET", "web.test", "/hunters/hunter/best", null)).Status);  // the recorded player's account: no player here owns it
    }

    [Fact]
    public async Task TestAnyAccountsHuntsAreReadWithAnotherPlayersToken()
    {
        state.CreateAccount(9, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = 9");
        state.NoteLaunch(8, "start=101&reserve=2"u8.ToArray());
        state.HeartbeatReply(8, Events(AccountIsolationTests.Harvest(81, 72270)));
        state.Disconnect(8, """{"distance_travelled": 466}"""u8.ToArray());
        const string page = "&limit=10&offset=0";
        // another player's list and hunt by user id (the token is any player's), the player's own as before
        var list = await Api(Replay.EXPEDITION_LIST, tokenA, "user_id=8" + page);
        var id = list["expeditions"]!.AsArray().Single()!["id"]!;
        var hunt = await Api(Replay.PUBLIC_EXPEDITION, tokenA, "user_id=8");
        Assert.Single(hunt["kills"]!.AsArray());
        Eq(id, hunt["expedition"]!["id"]);
        Eq(id, (await Api(Replay.PUBLIC_EXPEDITION, tokenB, "user_id=8&expedition_id=" + Py.Str(id)))["expedition"]!["id"]);
        Assert.Empty((await Api(Replay.EXPEDITION_LIST, tokenA, "user_id=7" + page))["expeditions"]!.AsArray());
        // a banned, an unknown and the recorded player's user id are the empty replies, never the own hunts
        foreach (var other in new[] { 9, 99, 7 })
        {
            Assert.Empty((await Api(Replay.EXPEDITION_LIST, tokenB, $"user_id={other}" + page))["expeditions"]!.AsArray());
            Assert.Equal("empty", (await Send(community, "POST", "api.test", Replay.PUBLIC_EXPEDITION, tokenB, $"user_id={other}")).Source);
        }
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
