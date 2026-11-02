using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Restore;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Restore layer B: a zip's model becomes the rows of one account, and the client is answered from them.</summary>
public class RestoreTests : StateTestBase
{
    static readonly string[] ProfileTables = ["items", "wallet", "skills", "profile", "statistics", "achievements", "trophies", "lodges", "extras"];

    public RestoreTests()
    {
        var app = APP;
        // item 500 counts units, 610 is not in the catalogue at all
        app["items"]!.AsArray().Add(O("""
            {"id": 500, "name": "Test Ammo", "description": "", "define": "TEST_AMMO", "type": 2, "group": 1, "cat": 1, "client_type": 1, "image": "ammo",
             "variationname": null, "units": {"count": 50}, "fit": {}, "inventory": {"slots": [0, 1], "capacity": 0, "weight": 10}}
            """));
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false);
    }

    string Zip(TestZip zip, string name = "backup.zip") => zip.Write(tmp, name);

    /// <summary>Every row of the account's profile tables, as text.</summary>
    string Snapshot(long account)
    {
        using var con = state.Store.Open(account);
        return string.Join("\n", ProfileTables.SelectMany(t =>
            con.Rows($"SELECT * FROM {t} WHERE account_id = $a").Select(r => t + ": " + string.Join(" | ", r.Select(v => v.ToString())))));
    }

    long Count(long account, string table)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE account_id = $a")!;
    }

    JsonNode Connect(long account) => JsonNode.Parse(state.ConnectReply(account))!;

    /// <summary>JSON member of a node by name, as a node of its own.</summary>
    static JsonNode? At(JsonNode? n, string key) => n![key]?.DeepClone();

    [Fact]
    public void TestAZipBecomesTheRowsOfANewAccount()
    {
        var result = Restorer.Import(state, Zip(TestZip.Own()), null, tmp);
        Assert.Equal((1001L, true), (result.Account, result.Created));
        Assert.Contains("stored: items 2, wallet 2, skills 120, profile 1, statistics 1, achievements 2, trophies 1, lodges 1,", result.Report);
        Assert.Equal("sample", state.Store.AccountJson(1001)["handle"]!.ToString());
        Eq("""{"em": 12.5, "gm": 3000.25}""", state.Wallet(1001));
        var skills = JsonNode.Parse(state.SkillListReply(1001, "user_id=1001"u8.ToArray()))!;
        Eq("""{"200": 10.2, "300": 4.75}""", skills["weapon"]);
        Eq("[1.5, 2]", T(skills["tracking"]![0], skills["spotting"]![0]));
        using var con = state.Store.Open(1001);
        var items = new JsonObject();
        foreach (var i in State.Items(con))
            items[(string)i!["guid"]!] = i.DeepClone();
        // units only where the catalogue says so; a placed item's position and direction are millimetres and leave the metadata
        Eq("""{"item_id": 500, "slot": 1, "guid": "9001", "metadata": {}, "units": 3}""", items["9001"]);
        Eq("""{"item_id": 610, "slot": 23, "guid": "9101", "metadata": {"dog_name": "Rex"}, "units": null, "position": {"x": 3144645, "y": -999, "z": 10001}, "direction": {"x": 500, "y": 0, "z": -500}}""", items["9101"]);
        Eq("""{"collectables": {}, "weapon": {}, "animal": {"5": {"kills": 2}}, "daily_missions": {}}""",
           JsonNode.Parse((string)con.Scalar("SELECT user_stats FROM statistics WHERE account_id = $a")!));
        Eq("[420, null]", T(Py.FromSql(con.Scalar("SELECT bust_through_score FROM statistics WHERE account_id = $a")),
                            Py.FromSql(con.Scalar("SELECT lane_bandit_score FROM statistics WHERE account_id = $a"))));
        Eq("""{"1": 1600000000, "3": 1600000100}""", JsonNode.Parse((string)con.Scalar("SELECT triggers FROM achievements WHERE account_id = $a AND id = 12")!));
        Eq("""{"id": 81, "trophy_id": 3, "ts": 1600000000}""", JsonNode.Parse((string)con.Scalar("SELECT node FROM trophies WHERE account_id = $a")!));
        Eq("""{"id": 61, "name": "Lodge"}""", JsonNode.Parse((string)con.Scalar("SELECT node FROM lodges WHERE account_id = $a")!));
        Assert.Contains(con.Rows("SELECT path FROM extras WHERE account_id = $a"), r => (string)r[0] == "loadout.json");
        var audit = con.Rows("SELECT zip_sha256, mode FROM restore_audit WHERE account_id = $a");
        Assert.Equal([(result.Sha256, "my-account")], audit.Select(r => ((string)r[0], (string)r[1])));
        Assert.True(File.Exists(Path.Combine(tmp, "restores", "1001", result.Sha256 + ".zip")));  // the zip is kept for a reset
    }

    [Fact]
    public void TestTheSameZipTwiceGivesTheSameStateAndTheOldDatabaseIsCopied()
    {
        var path = Zip(TestZip.Own());
        Restorer.Import(state, path, null, tmp);
        var first = Snapshot(1001);
        Assert.Empty(Directory.GetFiles(tmp, "state.db.bak-*"));  // a new account replaced nothing
        var again = Restorer.Import(state, path, null, tmp);
        Assert.False(again.Created);
        Assert.Equal(first, Snapshot(1001));
        Assert.Single(Directory.GetFiles(tmp, "state.db.bak-*"));
        Assert.Single(Directory.GetFiles(Path.Combine(tmp, "restores", "1001")));
        Assert.Equal(2L, Count(1001, "restore_audit"));
    }

    [Fact]
    public void TestAnotherZipReplacesTheProfileWithoutMerging()
    {
        Restorer.Import(state, Zip(TestZip.Own()), null, tmp);
        Restorer.Import(state, Zip(TestZip.Public(), "public.zip"), 1001, tmp);
        // the public zip has no wallet or skills: new-player values, and nothing of the first zip is left
        Eq("""{"em": 0.0, "gm": 0.0}""", state.Wallet(1001));
        using var con = state.Store.Open(1001);
        Assert.DoesNotContain(con.Rows("SELECT path FROM extras WHERE account_id = $a"), r => (string)r[0] is "loadout.json" or "Me_me.json");
        Assert.Equal(118L, Count(1001, "skills"));
        Assert.Equal(2L, Count(1001, "restore_audit"));
    }

    [Fact]
    public void TestPersonalFieldsAreNotStored()
    {
        var result = Restorer.Import(state, Zip(TestZip.Own()), null, tmp);
        Assert.Contains("personal field(s)", result.Report);
        using var con = state.Store.Open(1001);
        using var shared = state.Store.Open();
        var all = string.Join("\n", shared.Rows("SELECT account FROM accounts").Select(r => (string)r[0]))
                  + string.Join("\n", new[] { "profile", "extras", "trophies", "lodges", "restore_audit" }.SelectMany(t =>
                      con.Rows($"SELECT * FROM {t} WHERE account_id = $a").Select(r => string.Join("|", r))));
        Assert.DoesNotContain("example.invalid", all);
        Assert.DoesNotContain("Person", all);
        Assert.DoesNotContain("Test Street", all);
        Assert.DoesNotContain("\"email\"", all.Replace("\"email\": null", ""));
    }

    [Fact]
    public void TestASecondAccountIsUntouched()
    {
        state.CreateAccount(8, "second");
        var (before7, before8) = (Snapshot(ME), Snapshot(8));
        Restorer.Import(state, Zip(TestZip.Own()), null, tmp);  // creates 1001
        Assert.Equal((before7, before8), (Snapshot(ME), Snapshot(8)));
        Restorer.Import(state, Zip(TestZip.Own("\"ems\":1.0,\"gms\":2.0"), "other.zip"), ME, tmp);  // replaces the profile of 7 only
        Assert.Equal(before8, Snapshot(8));
        Assert.NotEqual(before7, Snapshot(ME));
        Eq("""{"em": 12.5, "gm": 3000.25}""", state.Wallet(1001));
        Eq("""{"em": 1.0, "gm": 2.0}""", state.Wallet(ME));
        Eq("7", state.Store.AccountJson(ME)["user_id"]);  // the account keeps its id
    }

    [Fact]
    public void TestARefusedImportLeavesNothingBehind()
    {
        var path = Zip(TestZip.Own());
        Assert.Contains("does not exist", Assert.Throws<RestoreException>(() => Restorer.Import(state, path, 555, tmp)).Message);
        File.WriteAllText(Path.Combine(tmp, "bad.zip"), "not a zip");
        Assert.Throws<RestoreException>(() => Restorer.Import(state, Path.Combine(tmp, "bad.zip"), null, tmp));
        state.CreateAccount(8, "sample");  // the zip's handle is another account's
        Assert.Contains("handle", Assert.Throws<RestoreException>(() => Restorer.Import(state, path, null, tmp)).Message);
        Assert.False(state.Store.AccountExists(1001));
        Assert.False(Directory.Exists(Path.Combine(tmp, "restores", "1001")));
    }

    [Fact]
    public void TestAnExistingAccountKeepsItsHandleWhenTheZipsIsTaken()
    {
        state.CreateAccount(8, "sample");
        Restorer.Import(state, Zip(TestZip.Own()), ME, tmp);
        Assert.Equal("hunter", (string)state.Store.AccountJson(ME)["handle"]!);
        Restorer.Import(state, Zip(TestZip.Own(), "again.zip"), 8, tmp);  // its own handle: nothing to settle
        Assert.Equal("sample", (string)state.Store.AccountJson(8)["handle"]!);
    }

    [Fact]
    public void TestConnectAnswersFromTheRestoredRowsAndNotFromTheRecordedPlayer()
    {
        Restorer.Import(state, Zip(TestZip.Own()), null, tmp);
        var reply = Connect(1001);
        Eq("""{"em": 12.5, "gm": 3000.25}""", reply["wallet"]);
        var skills = reply["skills"]!;
        Eq("""{"200": 10, "300": 4}""", skills["weapon"]);
        Eq("[59, 59, 1, 2]", T(skills["tracking"]!.AsArray().Count, skills["spotting"]!.AsArray().Count, skills["tracking"]![0], skills["spotting"]![0]));
        Eq("""["9001", "9101"]""", new JsonArray(reply["inventory"]!["items"]!.AsArray().Select(i => At(i, "guid")).ToArray()));
        Eq("""{"x": 3144645, "y": -999, "z": 10001}""", reply["inventory"]!["items"]![1]!["position"]);
        Eq("""{"handle": "sample", "user_id": 1001, "account_id": 1001, "hostname": "sample", "gender": 1, "hunterscore": 1234, "membership": 0, "avatar_url": "https://avatars.example/a.png", "face": 1000000, "facebook_enabled": false, "share_on_facebook": false}""",
           reply["account"]);
        var stats = reply["user_stats"]!;
        Eq("""{"collectables": {}, "weapon": {}, "animal": {"5": {"kills": 2}}, "daily_missions": {}, "distance": 12.5, "duration": 3600}""", stats["user_stats"]);
        Eq("[420, null, 0]", T(stats["bust_through_score"], stats["lane_bandit_score"], stats["first_time"]));
        Eq("""[{"category_id": 1, "group_id": 1, "id": 4, "value": 0}, {"category_id": 1, "group_id": 2, "id": 12, "triggers": {"1": 1600000000, "3": 1600000100}, "value": 3}]""", stats["achievement_stats"]);
        Eq("""{"locale": "en_US", "facebook_id": null, "has_password": 0, "share_on_facebook": 0, "timezone": "UTC", "id": 1001, "user_id": 1001, "uuid": "00000000-0000-0000-0000-000000000001", "country": "XX", "account_state": 0, "steam_id": "", "user_group": 7, "email_verified": 0, "email": null}""",
           reply["account_info"]);
        Assert.DoesNotContain("example.invalid", reply.ToJsonString());
        // an account that restored nothing has its own account and empty statistics, not the recorded player's
        Assert.Equal("hunter", Connect(ME)["account"]!["handle"]!.ToString());
        Eq("""{"collectables": {}, "weapon": {}, "animal": {}, "daily_missions": {}, "distance": 0, "duration": 0}""", Connect(ME)["user_stats"]!["user_stats"]);
    }

    [Fact]
    public void TestConnectOfAPublicZipHasNoWalletOrSkillsOfAnotherPlayer()
    {
        Restorer.Import(state, Zip(TestZip.Public()), null, tmp);
        var reply = Connect(1001);
        Eq("""{"em": 0.0, "gm": 0.0}""", reply["wallet"]);
        Assert.Equal(59, reply["skills"]!["tracking"]!.AsArray().Count);
        Assert.Empty(reply["skills"]!["weapon"]!.AsObject());
        Assert.Equal("user-1001", reply["account"]!["handle"]!.ToString());
    }
}

/// <summary>The restored account's profile reads over HTTP: Me/me, Statistics/total and Achievement/stats, with the tokens of two accounts.</summary>
public class RestoredRequestTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    static readonly (string, string)[] FormHeader = [("Content-Type", "application/x-www-form-urlencoded")];
    string token7 = "", token1001 = "";
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        Restorer.Import(state, TestZip.Own().Write(tmp), null, tmp);
        (token7, token1001) = (state.Store.CreateToken(ME), state.Store.CreateToken(1001));
        var table = new Replay.HarTable();
        table.Entries[new("POST", "api.test", Replay.ME, null)] = new Replay.Hit(2, "", 200, [("Content-Type", "application/json")],
            """{"id": 7, "handle": "hunter", "ems": 1.0, "gms": 2.0, "unread_messages": 0, "records": {"recorded": 1}, "worlditems": [{"recorded": 1}], "rank": 99}"""u8.ToArray());
        foreach (var path in new[] { Replay.STATISTICS_TOTAL, Replay.ACHIEVEMENT_STATS })
            table.Entries[new("POST", "api.test", path, null)] = new Replay.Hit(2, "", 200, [("Content-Type", "application/json")], """{"recorded": 1}"""u8.ToArray());
        server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    Task<Reply> Post(string path, string token, string extra = "") =>
        server.SendAsync("POST", "api.test", path, FormHeader, Encoding.ASCII.GetBytes($"oauth_access_token={token}{extra}"));

    [Fact]
    public async Task TestMeMeIsTheRestoredProfileWithTheWalletOfTheState()
    {
        var reply = await Post(Replay.ME, token1001);
        Assert.Equal((200, "state"), (reply.Status, reply.Source));
        var me = JsonNode.Parse(reply.Body)!;
        Eq("""[1001, "sample", 1234, 1, 7, 12.5, 3000.25, 0, "freeplay"]""", T(me["id"], me["handle"], me["hunterscore"], me["rank"], me["user_group"], me["ems"], me["gms"],
            me["unread_messages"], me["membershipType"]));
        Eq("""{"best": 9}""", me["records"]);
        Eq("[]", me["worlditems"]);
        Assert.DoesNotContain("example.invalid", Encoding.UTF8.GetString(reply.Body));
        Assert.DoesNotContain("Person", Encoding.UTF8.GetString(reply.Body));
        // the account that restored nothing gets a new account's profile, not the recorded player's, and ranks below 1001
        var other = JsonNode.Parse((await Post(Replay.ME, token7)).Body)!;
        Eq("""{}""", other["records"]);
        Eq("2", other["rank"]);
    }

    [Fact]
    public async Task TestStatisticsAndAchievementsOfTheOwnUserIdAreRestoredAndOthersReplayed()
    {
        var stats = JsonNode.Parse((await Post(Replay.STATISTICS_TOTAL, token1001, "&user_id=1001")).Body)!;
        Eq("""{"duration": 3600, "distance": 12.5, "data": {"collectables": {}, "weapon": {}, "animal": {"5": {"kills": 2}}, "daily_missions": {}}}""", stats);
        var achievements = await Post(Replay.ACHIEVEMENT_STATS, token1001, "&user_id=1001");
        Assert.Equal("state", achievements.Source);
        Assert.Equal(2, JsonNode.Parse(achievements.Body)!.AsArray().Count);
        // another player's user id, and an account without a restore, get the empty reply, never the recorded one
        Assert.Equal("empty", (await Post(Replay.STATISTICS_TOTAL, token1001, "&user_id=555")).Source);
        Assert.Equal("empty", (await Post(Replay.STATISTICS_TOTAL, token7, "&user_id=7")).Source);
        Assert.Equal("empty", (await Post(Replay.ACHIEVEMENT_STATS, token7, "&user_id=7")).Source);
    }
}
