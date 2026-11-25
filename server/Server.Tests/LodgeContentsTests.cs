using System.IO.Compression;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The lodges of a section save (`lodge_contents`): restored as the account's own rows, and filled afterwards for an account that restored one before it was read.</summary>
public class LodgeContentsTests : StateTestBase
{
    const string Mounted = """{"slot_id": 1, "trophy_id": 9, "trophy_details": {"species": 5, "score": 200.5, "gender": 1}}""";
    const string Contents = """{"61": {"id": 61, "lodge_type_id": 780, "animal_slots": [""" + Mounted + """, {"slot_id": 2}], "weapon_slots": [{"slot_id": 1, "weapon_id": 3}]}, "62": {"id": 62, "lodge_type_id": 790, "animal_slots": [""" + Mounted + """]}}""";

    public LodgeContentsTests() => state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);

    string Save()
    {
        var path = Path.Combine(tmp, "section-save.zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, string body) { using var w = new StreamWriter(z.CreateEntry("tool_public_sample/" + name).Open()); w.Write(body); }
        Put("json_profile.json", """{"id":1001,"handle":"Sample","hostname":"sample","hunterscore":1}""");
        Put("json_trophies.json", """{"total":1,"trophies":[{"id":99,"trophy_id":3,"ts":1600000000}]}""");
        Put("json_expeditions.json", """{"has_more":false,"expeditions":[{"id":9100,"kills":1,"start_ts":1700000100}]}""");
        Put("json_hunt_details.json", """{"9100":{"stats":null,"kills":[],"expedition":{"id":9100,"reserve":2}}}""");
        Put("json_gallery.json", """{"total":0,"photos":[]}""");
        Put("json_v2_lodges.json", """[{"id":61,"lodge_type_id":780,"name":"Lodge"},{"id":62,"lodge_type_id":790,"name":"Cabin"}]""");
        Put("json_lodge_contents.json", Contents);
        return path;
    }

    long Rows(long account, string table, string source)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE account_id = $a AND source = ?", source)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASectionSaveRestoresItsLodgesAsTheAccountsOwnRowsAndKeepsTheMountedAnimals(bool onAClaim)  // a claim's account takes the save as a merge
    {
        var uploads = new Uploads(state, tmp) { MinFreeBytes = 0 };
        var player = state.Store.NextServerId();
        state.CreateAccount(player, "player", "76561190000000001");
        if (onAClaim)
            using (var con = state.Store.Open(player)) PlayerRows.AddAudit(con, "archive:1001", "public", "claim");
        var account = onAClaim ? player : 1001;
        var id = uploads.Start(player)!;
        uploads.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(Save()))).GetAwaiter().GetResult();
        Assert.Equal("review", uploads.Finish(player, id).GetAwaiter().GetResult().Kind);
        long row;
        using (var con = state.Store.Open(player)) row = (long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!;
        Assert.Equal("restored", uploads.Approve(row).Kind);

        Assert.Equal([2L, 2L], [Rows(account, "lodges", "/game/user/lodges"), Rows(account, "lodges", "/game/user/lodge")]);
        var lodge = Reply(state.LodgeReply(account, 61)!);  // the account's own row: `GET /game/user/<uid>/lodges/<id>` answers it, no recording is asked
        Assert.Equal(9L, (long)lodge["animal_slots"]![0]!["trophy_id"]!);
        Assert.Equal(3L, (long)lodge["weapon_slots"]![0]!["weapon_id"]!);
        Assert.NotNull(state.LodgeReply(account, 62));
        Assert.Null(state.LodgeReply(account, 63));
        using var after = state.Store.Open(account);
        Assert.Contains("9", after.Rows("SELECT id FROM trophies WHERE account_id = $a AND source = '/game/user/trophies'").Select(r => (string)r[0]));  // the animal the lodge mounts has a trophy row of its own
    }

    [Fact]
    public void TheBackfillFillsAListedLodgeFromTheSavedExtraOnceAndAFirstRunWritesNothing()
    {
        const long account = 7;
        state.CreateAccount(account, "backfill");
        using (var con = state.Store.Open(account))
        {
            var saved = JsonNode.Parse(Contents)!.AsObject();
            saved["64"] = JsonNode.Parse("""{"id": 64, "animal_slots": [{"slot_id": 5}]}""");
            PlayerRows.SetExtra(con, "json_lodge_contents.json", saved);
            foreach (var lodge in new[] { 61, 62, 63, 64 })
                PlayerRows.AddLodge(con, "/game/user/lodges", lodge.ToString(), JsonNode.Parse($$"""{"id": {{lodge}}, "name": "listed"}"""));
            PlayerRows.AddLodge(con, "/game/user/lodge", "64", JsonNode.Parse("""{"id": 64, "name": "kept", "animal_slots": []}"""));  // a lodge that has contents is never touched
        }
        Assert.Null(state.LodgeReply(account, 61));

        var dry = state.BackfillLodgeContents(apply: false);
        Assert.Equal(["account 7 lodge 61: would add, 3 slots, 1 trophy rows", "account 7 lodge 62: would add, 1 slots, 0 trophy rows"], dry);
        Assert.Equal([1L, 0L], [Rows(account, "lodges", "/game/user/lodge"), Rows(account, "trophies", "/game/user/trophies")]);  // nothing written
        Assert.Null(state.LodgeReply(account, 61));

        Assert.Equal(["account 7 lodge 61: added, 3 slots, 1 trophy rows", "account 7 lodge 62: added, 1 slots, 0 trophy rows"], state.BackfillLodgeContents(apply: true));
        Assert.Equal([3L, 1L], [Rows(account, "lodges", "/game/user/lodge"), Rows(account, "trophies", "/game/user/trophies")]);
        Assert.Equal(9L, (long)Reply(state.LodgeReply(account, 61)!)["animal_slots"]![0]!["trophy_id"]!);
        Assert.Equal("kept", (string)Reply(state.LodgeReply(account, 64)!)["name"]!);
        Assert.Null(state.LodgeReply(account, 63));  // listed, but the save holds no contents for it

        Assert.Empty(state.BackfillLodgeContents(apply: true));  // idempotent
        Assert.Equal([3L, 1L], [Rows(account, "lodges", "/game/user/lodge"), Rows(account, "trophies", "/game/user/trophies")]);
    }
}
