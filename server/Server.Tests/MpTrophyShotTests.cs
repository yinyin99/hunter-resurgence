using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;

using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The trophy shot the harvester takes of a harvest that counted for the shooter (#1041) is the shooter's kill's photo: on his kill and in his gallery, and in the harvester's gallery as a link to
/// that kill (`harvest_links`, `photos.kill_id`, `photos.taker`). The players are invented; the mule deer buck scores 168.8186, as in `MpHarvestTests`.</summary>
public class MpTrophyShotTests : StateTestBase
{
    const long HOST = 11, GUEST = 12, THIRD = 13;
    const string Start = "start=101&loctype=LODGE&reserve=2&time=540&loadout_id=0";

    public MpTrophyShotTests()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false) { Clock = () => 1_000_000 };
        foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest") })
            state.CreateAccount(id, handle);
        state.HostGame(HOST, "host-session", Encoding.UTF8.GetBytes($"{Start}&name=Camp&max_players=3"));
        Beat(HOST, O("""{"event": 29, "port": 32303, "guid": "9007199254740993", "color": "c73378"}"""));
        var game = (long)Reply(state.NetworkListReply())[0]!["id"]!;
        Assert.Null(state.JoinGame(GUEST, Encoding.UTF8.GetBytes($"start=101&loctype=LODGE&network_id={game}&loadout_id=0")).Error);
    }

    JsonArray Beat(long who, params JsonNode[] events) => Reply(state.HeartbeatReply(who, Events(events)))["updates"]!.AsArray();

    static JsonNode Spawn(int individual) =>
        O($$"""{"event": 7, "individual": {{individual}}, "species": 1, "gender": 0, "weight_grams": 98120, "texture": 1}""");

    static JsonNode Hit(int individual) =>
        O($$"""{"event": 1, "individual": {{individual}}, "species": 2, "distance_cms": 8250, "organ1": 17, "organ2": 8388610, "weapon_id": 5, "ammunition_id": 10, "animal_state": 3}""");

    /// <summary>What the player who skins the buck sends (the antlers of `MpHarvestTests`): its spawn (unless he sent it with his shot), its trophy measurement and the harvest.</summary>
    static JsonNode[] Skinned(int individual, bool spawn = true)
    {
        var trophy = O($$"""{"event": 22, "individual": {{individual}}, "greatest_spread": 640, "tip_to_tip_spread": 600, "inside_spread_of_main_beams": 560}""");
        trophy["right_antler"] = O("""{"circumference_h1": 125, "circumference_h2": 125, "circumference_h3": 100, "circumference_h4": 100, "points": [{"length": 750, "type": 0}, {"length": 125, "type": 1}, {"length": 500, "type": 2}, {"length": 375, "type": 3}, {"length": 250, "type": 4}]}""");
        trophy["left_antler"] = O("""{"circumference_h1": 150, "circumference_h2": 125, "circumference_h3": 125, "circumference_h4": 100, "points": [{"length": 800, "type": 0}, {"length": 125, "type": 1}, {"length": 450, "type": 2}, {"length": 375, "type": 3}, {"length": 250, "type": 4}]}""");
        var harvest = O($$"""{"event": 3, "individual": {{individual}}, "extended_species": 1, "trophy_integrity": 100000, "antlers_seed": 1}""");
        return spawn ? [Spawn(individual), trophy, harvest] : [trophy, harvest];
    }

    /// <summary>`who`'s game asks for a photo of the animal and uploads it: the picture's path and the upload's query.</summary>
    (string Path, string Query) Upload(long who, int individual, int type = 2)
    {
        var update = Beat(who, O($$"""{"event": 4, "hash": 1, "photo_type": {{type}}, "individual": {{individual}}}""")).Single(u => Py.Eq(u!["update"], 11))!;
        var query = new Uri((string)update["url"]!).Query.TrimStart('?');
        var (status, body) = state.UploadReply(who, query, [1, 2]);
        Assert.Equal(200, status);
        return (new Uri((string)Reply(body)["mediaLink"]!).AbsolutePath, query);
    }

    string Photo(long who, int individual, int type = 2) => Upload(who, individual, type).Path;

    JsonArray Gallery(long account) => JsonNode.Parse(state.GalleryListReply(account, []))!["photos"]!.AsArray();

    JsonArray WebRows(long account) => state.PublicGallery(account, 1, "https://web.test", account)["rows"]!.AsArray();

    JsonNode Kill(long account) => Assert.Single(Reply(state.ExpeditionReply(account, "{}"u8.ToArray()))["kills"]!.AsArray())!;

    [Fact]
    public void TestTheHarvestersTrophyShotOfACreditedHarvestIsTheShootersKillsAndLinkedInTheHarvestersGallery()
    {
        Beat(GUEST, Spawn(460), Hit(460));
        Beat(HOST, Skinned(460));  // the host skins the guest's buck: the kill is the guest's
        var shot = Photo(HOST, 460);
        var camera = Photo(HOST, 460, type: 1);  // a camera photo stays the harvester's
        Assert.StartsWith($"/photos/{GUEST}/", shot);  // stored in the shooter's folder
        Assert.StartsWith($"/photos/{HOST}/", camera);

        // the shooter's kill has the picture, his gallery lists it as the kill's trophy shot
        var kill = Kill(GUEST);
        Assert.EndsWith(shot, (string)kill["kill"]!["photo"]!);
        var his = Assert.Single(Gallery(GUEST))!;
        Assert.EndsWith(shot, (string)his["url"]!);
        Assert.Equal("168.8186", (string?)his["label"]);
        Assert.NotEqual("", (string?)his["animal"]);
        var hisWeb = Assert.Single(WebRows(GUEST))!;
        Assert.Equal(((long?)kill["id"], null, true), ((long?)hisWeb["kill"], (string?)hisWeb["hunter"], hisWeb["share"] is not null));

        // the harvester's gallery lists the same picture as the shooter's kill, by his name, with no share link
        var game = Gallery(HOST);
        Assert.Equal(2, game.Count);  // the linked trophy shot and the harvester's own camera photo
        var linked = game.Single(p => (long)p!["id"]! == (long)his["id"]!)!;
        Assert.Equal(((string?)his["url"], (string?)his["label"], (string?)his["animal"]), ((string?)linked["url"], (string?)linked["label"], (string?)linked["animal"]));
        var web = WebRows(HOST);
        Assert.Equal(2, web.Count);
        var row = web.Single(r => r!["hunter"] is not null)!;
        Assert.Equal(("guest", (long?)kill["id"], "168.8186", null), ((string?)row["hunter"], (long?)row["kill"], (string?)row["label"], row["share"]));
        Assert.StartsWith($"https://web.test/photos/{GUEST}/display-", (string)row["url"]!);
        Assert.NotNull(web.Single(r => r!["hunter"] is null)!["share"]);  // the harvester's own camera photo keeps its share link
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(state.GalleryZip(HOST))))
            Assert.Equal(2, zip.Entries.Count);  // the harvester's zip holds both

        // only the shooter deletes it
        Assert.False(state.DeletePhoto(HOST, (long)his["id"]!));
        Assert.True(state.DeletePhoto(GUEST, (long)his["id"]!));
        Assert.Single(Gallery(HOST));
        Assert.Null(Kill(GUEST)["kill"]!["photo"]);
    }

    [Fact]
    public void TestTheLinkFollowsAnAccountGivenAnotherUserId()
    {
        Beat(GUEST, Spawn(460), Hit(460));
        Beat(HOST, Skinned(460));
        var shot = Photo(HOST, 460);
        using (var con = state.Store.Open(GUEST))
        {
            con.Begin();
            Restorer.Adopt(con, GUEST, 99);
            con.Commit();
        }
        using (var con = state.Store.Open(HOST))
        {
            con.Begin();
            Restorer.Adopt(con, HOST, 98);
            con.Commit();
        }
        Assert.EndsWith(shot[$"/photos/{GUEST}/".Length..], (string)Assert.Single(Gallery(98))!["url"]!);  // the harvester's gallery still finds the shooter's photo
        Assert.Equal("guest", (string?)Assert.Single(WebRows(98))!["hunter"]);
        Beat(98, Spawn(460));  // a new spawn of that individual number is another animal: its trophy shot is the harvester's
        Assert.StartsWith("/photos/98/", Photo(98, 460));
    }

    [Fact]
    public void TestTheFirstTrophyShotUsesTheLinkUpAndItsPictureIsNotReplaced()
    {
        Beat(GUEST, Spawn(460), Hit(460));
        Beat(HOST, Skinned(460));
        var (first, query) = Upload(HOST, 460);
        Assert.StartsWith($"/photos/{GUEST}/", first);
        Assert.Equal(404, state.UploadReply(HOST, query, [9]).Status);  // the shooter's picture is stored once: the harvester cannot replace it
        Assert.StartsWith($"/photos/{HOST}/", Photo(HOST, 460));  // a second trophy shot of the animal is the harvester's own
        Assert.EndsWith(first, (string)Kill(GUEST)["kill"]!["photo"]!);
        Assert.Single(Gallery(GUEST));

        // once the shooter deleted it, the harvester can neither upload it again nor link a new one
        Assert.True(state.DeletePhoto(GUEST, (long)Gallery(GUEST)[0]!["id"]!));
        Assert.Equal(404, state.UploadReply(HOST, query, [9]).Status);
        Assert.StartsWith($"/photos/{HOST}/", Photo(HOST, 460));
        Assert.Empty(Gallery(GUEST));
        Assert.Null(Kill(GUEST)["kill"]!["photo"]);
    }

    [Fact]
    public void TestAnotherPlayerSeesTheLinkWithoutShareAndABannedShootersShotIsHidden()
    {
        state.CreateAccount(THIRD, "third");
        Beat(GUEST, Spawn(460), Hit(460));
        Beat(HOST, Skinned(460));
        Photo(HOST, 460);
        var seen = Assert.Single(state.PublicGallery(HOST, 1, "https://web.test", THIRD)["rows"]!.AsArray())!;
        Assert.Equal(("guest", null), ((string?)seen["hunter"], seen["share"]));
        Assert.Contains($"viewer={THIRD}", (string)seen["url"]!);

        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = ?", GUEST);
        Assert.Empty(Gallery(HOST));  // hidden like a banned account everywhere else
        Assert.Empty(WebRows(HOST));
    }

    [Fact]
    public void TestADatabaseAtTheCurrentVersionWithoutTheLinksGetsThem()
    {
        using (var con = state.Store.Open())
        {
            // `taker` is in a foreign key, which SQLite does not drop: the table, its index and the other column stand for the step
            foreach (var sql in (string[])["DROP INDEX photos_taker", "ALTER TABLE photos DROP COLUMN kill_id", "DROP TABLE harvest_links"])
                con.Exec(sql);
            Assert.Equal((long)Store.Version, con.Scalar("PRAGMA user_version"));  // the version stays at Store.Version
        }
        using var check = new Store(Db).Open();
        Assert.Equal(2L, check.Scalar("SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name IN ('taker', 'kill_id')"));
        Assert.Equal(2L, check.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('harvest_links', 'photos_taker')"));
    }

    [Fact]
    public void TestATrophyShotOfTheHarvestersOwnKillIsUnchanged()
    {
        Beat(GUEST, Spawn(460), Hit(460));
        Beat(HOST, Spawn(460), Hit(460));  // the host shot it last
        Beat(HOST, Skinned(460, spawn: false));
        var shot = Photo(HOST, 460);
        Assert.StartsWith($"/photos/{HOST}/", shot);
        Assert.EndsWith(shot, (string)Kill(HOST)["kill"]!["photo"]!);
        var row = Assert.Single(WebRows(HOST))!;
        Assert.Equal(("168.8186", null), ((string?)row["label"], (string?)row["hunter"]));
        Assert.NotNull(row["share"]);
        Assert.Empty(Gallery(GUEST));
        using var con = state.Store.Open(HOST);
        Assert.Equal(0L, con.Scalar("SELECT COUNT(*) FROM harvest_links WHERE account_id = $a"));
    }
}
