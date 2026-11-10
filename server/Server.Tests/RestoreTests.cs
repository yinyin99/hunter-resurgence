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
    public void TestALocalServerKeepsTheRankOfTheRestoredProfile()
    {
        var local = new State(Path.Combine(tmp, "local.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: true);
        Restorer.Import(local, Zip(TestZip.Own()), local.LocalAccount, tmp);
        // the backup's rank, not the place by hunterscore (the only account would be 1)
        Eq("3", local.MeReply(local.LocalAccount, new JsonObject())!["rank"]);
    }

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
        // hunterscore: the computed value, the zip's statistics hold no ethical kills
        Eq("""{"handle": "sample", "user_id": 1001, "account_id": 1001, "hostname": "sample", "gender": 1, "hunterscore": 0, "membership": 0, "avatar_url": "https://avatars.example/a.png", "face": 1000000, "facebook_enabled": false, "share_on_facebook": false}""",
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

    [Fact]
    public void TestRestoredHuntsFollowTheServerHuntsAndAreNotCounted()
    {
        Restorer.Import(state, Zip(TestZip.Own().Hunts()), null, tmp);
        var none = "{\"total\": 0, \"expeditions\": []}"u8.ToArray();
        JsonNode Page(string form) => JsonNode.Parse(state.ExpeditionListReply(1001, none, Encoding.UTF8.GetBytes(form)))!;
        string Ids(JsonNode p) => string.Join(",", p["expeditions"]!.AsArray().Select(e => e!["id"]!.ToString()));
        Assert.Equal(("9003,9002", "3", true), (Ids(Page("limit=2&offset=0")), Page("limit=2&offset=0")["total"]!.ToString(), (bool)Page("limit=2&offset=0")["has_more"]!));
        Assert.Equal("9001", Ids(Page("limit=2&offset=2")));
        var statistics = JsonNode.Parse(state.StatisticsReply(1001, "user_id=1001"u8.ToArray()))!.ToJsonString();
        // a server hunt comes first; `total` counts both
        state.NoteLaunch(1001, "start=100&reserve=2"u8.ToArray());
        var all = Page("limit=10&offset=0");
        Assert.Equal(("4", 4), (all["total"]!.ToString(), all["expeditions"]!.AsArray().Count));
        Assert.Equal("9003,9002,9001", string.Join(",", all["expeditions"]!.AsArray().Skip(1).Select(e => e!["id"]!.ToString())));
        // a restored hunt by id answers the saved reply; an unsaved id is the recorded reply; without an id the server hunt is the latest
        byte[] Hunt(string form) => state.ExpeditionReply(1001, "{\"replayed\": 1}"u8.ToArray(), Encoding.UTF8.GetBytes(form));
        Eq("9001", JsonNode.Parse(Hunt("expedition_id=9001"))!["expedition"]!["id"]);
        Assert.Equal("{\"replayed\": 1}"u8.ToArray(), Hunt("expedition_id=9002"));
        Assert.NotEqual("9003", JsonNode.Parse(Hunt(""))!["expedition"]!["id"]!.ToString());
        Assert.Equal(statistics, JsonNode.Parse(state.StatisticsReply(1001, "user_id=1001"u8.ToArray()))!.ToJsonString());
    }

    [Fact]
    public void TestARestoredHuntIsAPublicHuntAndItsKillHasNoSheet()
    {
        var zip = TestZip.Own()
            .Reply("Expedition_list__user_id=1001__offset=0__limit=2.json", "/v1/Expedition/list",
                   """{"total":1,"expeditions":[{"id":9100,"kills":1,"collectables":2,"start_ts":1700000100,"end_ts":1700003700,"reserve":2,"location_id":101}]}""", ("offset", "0"), ("limit", "2"))
            .Reply("Public_user_expedition__user_id=1001__expedition_id=9100.json", "/v1/Public_user/expedition", """
                {"stats":{"duration":3600,"distance":4000,"data":{"achievements":[],"weapon":{"5":{"10":{"hits":1,"misses":0,"kills":1,"distance":82000}}},"animal":{"1":{"kills":1}}}},
                 "kills":[{"id":777,"speciesName":"Test Deer","weight":64350,"kill":{"score":120.5,"confirmTs":1700000500},"hits":[{"distance":82000,"weapon_id":5,"ammo_id":10,"organ":1}]}],
                 "expedition":{"id":9100,"reserve":2,"location_id":101,"start_ts":1700000100,"end_ts":1700003700}}
                """, ("user_id", "1001"), ("expedition_id", "9100"));
        Restorer.Import(state, Zip(zip), null, tmp);

        var row = state.PublicHunts(1001, 1)["rows"]!.AsArray().Single()!;
        Eq("""[9100, 1700000100, 3600, "Test Reserve", "#101", 2, 1]""", T(row["id"], row["start"], row["seconds"], row["reserve"], row["lodge"], row["collectables"], row["kills"]));
        var hunt = state.PublicHunt(1001, 9100)!;
        Eq("""[1700003700, 4.0, 3600, "Test Rifle", "Test Bullet", 1, "Test Deer", 1]""", T(hunt["end"], hunt["distanceKm"], hunt["seconds"], hunt["weapons"]![0]!["weapon"], hunt["weapons"]![0]!["ammo"],
            hunt["weapons"]![0]!["shots"], hunt["animals"]![0]!["name"], hunt["animals"]![0]!["harvested"]));
        var kill = hunt["kills"]!.AsArray().Single()!;
        Eq("""[777, "Test Deer", "Test Rifle", "Test Bullet", 82.0, 120.5, 64.35, 1700000500, false]""", T(kill["id"], kill["species"], kill["weapon"], kill["ammo"], kill["distanceM"], kill["score"], kill["weightKg"], kill["when"], kill["sheet"]));
        Assert.Null(state.PublicSheet(1001, 777));  // until the saved kill page is unpacked
        Assert.Null(state.PublicHunt(1001, 9101));
    }

    JsonArray Gallery(long account) => JsonNode.Parse(state.GalleryListReply(account, []))!["photos"]!.AsArray();

    static string Link(JsonNode? url) => new Uri((string)url!).AbsolutePath;

    [Fact]
    public void TestTheGalleryPicturesOfAZipAreListedAtTheServersLinksWithTheirSavedLabels()
    {
        var result = Restorer.Import(state, Zip(TestZip.Own().Pictures()), null, tmp);
        Assert.Contains("pictures stored: gallery 2, kill photos and score pages 2, avatar and background pictures 1; 1 gallery entry without a picture in the zip", result.Report);
        var gallery = Gallery(1001);
        Eq("""[2, 2, "120.5", "Test Deer", 3, "", ""]""", T(JsonNode.Parse(state.GalleryListReply(1001, []))!["total"], gallery[0]!["type"], gallery[0]!["label"], gallery[0]!["animal"],
            gallery[1]!["type"], gallery[1]!["label"], gallery[1]!["animal"]));  // newest first, as saved
        Assert.Matches(@"^/photos/1001/\d+-2\.jpg$", Link(gallery[0]!["url"]));
        Assert.Equal(TestZip.Jpeg("b"), File.ReadAllBytes(state.PhotoFile(Link(gallery[0]!["url"]))!));
        Assert.Equal(TestZip.Jpeg("a"), File.ReadAllBytes(state.PhotoFile(Link(gallery[1]!["url"]))!));
        Assert.Equal(gallery[0]!["url"]!.ToString(), gallery[0]!["thumbnail"]!.ToString());  // the tool saved no thumbnail
        Assert.Equal(TestZip.Jpeg("b"), File.ReadAllBytes(state.PhotoFile(Link(state.PublicGallery(1001, 1, "https://web.test")["rows"]![0]!["url"]))!));
        using var zip = new ZipArchive(new MemoryStream(state.GalleryZip(1001)));
        Assert.Equal(2, zip.Entries.Count);
    }

    [Fact]
    public void TestARestoredKillIsLinkedToItsSavedPictureAndScoreSheetPage()
    {
        Restorer.Import(state, Zip(TestZip.Own().Pictures()), null, tmp);
        var kills = JsonNode.Parse(state.ExpeditionReply(1001, "null"u8.ToArray(), "expedition_id=9100"u8.ToArray()))!["kills"]!.AsArray();
        Assert.Equal("/photos/1001/kills_photos.example_k_777.jpg", Link(kills[0]!["kill"]!["photo"]));
        Assert.Equal(TestZip.Jpeg("kill"), File.ReadAllBytes(state.PhotoFile(Link(kills[0]!["kill"]!["photo"]))!));
        Assert.Equal("https://photos.example/k/778.jpg", (string)kills[1]!["kill"]!["photo"]!);  // its picture is not in the zip: as saved
        var hunt = state.PublicHunt(1001, 9100)!["kills"]!.AsArray();
        Eq("[true, false]", T(hunt[0]!["sheet"], hunt[1]!["sheet"]));
        Assert.Equal("<div>saved sheet</div>", Encoding.UTF8.GetString(state.SavedScorePage(1001, 777)!));
        Assert.Null(state.SavedScorePage(1001, 778));
        Assert.Null(state.ScorePageReply(1001, 777));  // no kill made here: the game route answers with the saved page
        var sheet = state.PublicSheet(1001, 777)!;
        Eq("""["antler", "Test Deer", "Test Rifle", 82.0, 1, 120.5, 64.35, 1.5, 88, "Test Reserve (10, 30)", 9100]""",
           T(sheet["kind"], sheet["species"], sheet["weapon"], sheet["distanceM"], sheet["shots"], sheet["score"], sheet["weightKg"], sheet["woundTime"], sheet["integrity"], sheet["shotLocation"], sheet["expeditionId"]));
        Assert.Empty(sheet["rows"]!.AsArray());
        Assert.Null(state.PublicSheet(1001, 778));
    }

    [Fact]
    public void TestRestoringAgainReplacesTheUnpackedPicturesAndKeepsThePhotosTakenHere()
    {
        Restorer.Import(state, Zip(TestZip.Own().Pictures()), null, tmp);
        var dir = Path.Combine(tmp, "photos", "1001");
        Assert.Equal(5, Directory.GetFiles(dir).Length);  // two gallery pictures, a kill photo, a score page, an avatar
        using (var con = state.Store.Open(1001))
            con.Exec("INSERT INTO photos (id, account_id, photo_type, file) VALUES (900, $a, 2, '900-2.jpg')");  // a photo taken here
        File.WriteAllBytes(Path.Combine(dir, "900-2.jpg"), TestZip.Jpeg("mine"));
        var other = TestZip.Own("\"ems\":1.0,\"gms\":2.0")
            .Gallery("""[{"id":60,"type":2,"url":"https://photos.example/p/d.jpg","thumbnail":"","label":"","animal":""}]""").Media("photos/photos.example_p_d.jpg", TestZip.Jpeg("d"));
        Restorer.Import(state, Zip(other, "again.zip"), null, tmp);
        var gallery = Gallery(1001);
        Assert.Equal(2, gallery.Count);  // the new picture, then the one taken here
        Assert.Equal(TestZip.Jpeg("d"), File.ReadAllBytes(state.PhotoFile(Link(gallery[0]!["url"]))!));
        Assert.Equal(TestZip.Jpeg("mine"), File.ReadAllBytes(state.PhotoFile("/photos/1001/900-2.jpg")!));
        Assert.Equal(gallery.Select(g => Path.GetFileName(Link(g!["url"]))).Order(), Directory.GetFiles(dir).Select(Path.GetFileName).Order());  // no file of the first zip is left
        Assert.Null(state.SavedScorePage(1001, 777));
        using var check = state.Store.Open(1001);
        Assert.Equal(1L, check.Scalar("SELECT COUNT(*) FROM photos WHERE account_id = $a AND saved IS NOT NULL"));
    }

    [Fact]
    public void TestADatabaseOfSchema9GetsTheSavedColumnOfThePhotos()
    {
        var file = Path.Combine(tmp, "old9.db");
        using (var con = new Store(file).Open())
        {
            con.Exec("ALTER TABLE photos DROP COLUMN saved");
            con.Exec("DROP TABLE wallet_log");
            con.Exec("PRAGMA user_version = 9");
        }
        using var migrated = new Store(file).Open();
        Assert.Equal(1L, migrated.Scalar("SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name = 'saved'"));
        Assert.Equal((long)Store.Version, migrated.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void TestWithoutAServerHuntTheNewestRestoredHuntIsTheLatest()
    {
        Restorer.Import(state, Zip(TestZip.Own().Hunts()), null, tmp);
        Eq("9003", JsonNode.Parse(state.ExpeditionReply(1001, "{}"u8.ToArray()))!["expedition"]!["id"]);
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
        // hunterscore: the computed value, the zip's statistics hold no ethical kills
        Eq("""[1001, "sample", 0, 1, 7, 12.5, 3000.25, 0, "freeplay"]""", T(me["id"], me["handle"], me["hunterscore"], me["rank"], me["user_group"], me["ems"], me["gms"],
            me["unread_messages"], me["membershipType"]));
        Eq("""{"best": 9}""", me["records"]);
        Eq("[]", me["worlditems"]);
        Assert.DoesNotContain("example.invalid", Encoding.UTF8.GetString(reply.Body));
        Assert.DoesNotContain("Person", Encoding.UTF8.GetString(reply.Body));
        // the account that restored nothing gets a new account's profile, not the recorded player's; 1001's computed 0 does not rank
        // above it (equal hunterscores: the lower id first)
        var other = JsonNode.Parse((await Post(Replay.ME, token7)).Body)!;
        Eq("""{}""", other["records"]);
        Eq("1", other["rank"]);
    }

    [Fact]
    public async Task TestTheRecordedAccountsHunterscoreDoesNotCountInTheRank()
    {
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 5000) WHERE id = ?", state.LocalAccount);
        Eq("1", JsonNode.Parse((await Post(Replay.ME, token1001)).Body)!["rank"]);
    }

    [Fact]
    public async Task TestStatisticsAndAchievementsOfTheOwnUserIdAreRestoredAndOthersReplayed()
    {
        var stats = JsonNode.Parse((await Post(Replay.STATISTICS_TOTAL, token1001, "&user_id=1001")).Body)!;
        Eq("""{"duration": 3600, "distance": 12.5, "data": {"collectables": {}, "weapon": {}, "animal": {"5": {"kills": 2}}, "daily_missions": {}}}""", stats);
        var achievements = await Post(Replay.ACHIEVEMENT_STATS, token1001, "&user_id=1001");
        Assert.Equal("state", achievements.Source);
        Assert.Equal(2, JsonNode.Parse(achievements.Body)!.AsArray().Count);
        // another player's user id gets the empty reply, never the recorded one; an account without a restore has a new account's
        // statistics (its server hunts only) and achievements (none earned yet)
        Assert.Equal("empty", (await Post(Replay.STATISTICS_TOTAL, token1001, "&user_id=555")).Source);
        var fresh = await Post(Replay.STATISTICS_TOTAL, token7, "&user_id=7");
        Assert.Equal("state", fresh.Source);
        Eq("""{"duration": 0, "distance": 0, "data": {"collectables": {}, "weapon": {}, "animal": {}, "daily_missions": {}}}""", JsonNode.Parse(fresh.Body));
        var none = await Post(Replay.ACHIEVEMENT_STATS, token7, "&user_id=7");
        Assert.Equal(("state", "[]"), (none.Source, Encoding.UTF8.GetString(none.Body)));
    }
}
