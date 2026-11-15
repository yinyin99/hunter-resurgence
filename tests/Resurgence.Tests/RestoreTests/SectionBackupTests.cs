using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Resurgence.Restore;

namespace Resurgence.Tests.RestoreTests;

/// <summary>A zip in the layout of another community tool: a folder with index.json and one json_&lt;section&gt;.json per API section. Every id and value is made up.</summary>
sealed class SectionZip
{
    public string Prefix = "backup_1001/";
    public JsonObject Index = new() { ["captured_at"] = "2026-01-02T03:04:05.000Z", ["user_id"] = "1001", ["handle"] = "sample", ["source"] = "made up", ["note"] = "made up" };
    readonly List<(string Name, byte[] Body)> sections = [];
    /// <summary>Entries written as they are: not in the index, or outside the folder.</summary>
    public readonly List<(string Name, byte[] Body)> Raw = [];

    public SectionZip Section(string name, string json) => Section(name, Encoding.UTF8.GetBytes(json));

    public SectionZip Section(string name, byte[] body)
    {
        sections.Add(($"json_{name}.json", body));
        return this;
    }

    /// <summary>The index.json text as it is, when a test needs one no JsonObject can hold.</summary>
    public string? IndexText;

    public byte[] Build()
    {
        Index["files"] ??= new JsonArray([.. sections.Select(s => (JsonNode?)s.Name)]);
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string name, byte[] body) { using var s = z.CreateEntry(name).Open(); s.Write(body); }
            Put(Prefix + "index.json", Encoding.UTF8.GetBytes(IndexText ?? Index.ToJsonString()));
            foreach (var (name, body) in sections) Put(Prefix + name, body);
            foreach (var (name, body) in Raw) Put(name, body);
        }
        return ms.ToArray();
    }

    public Restored Read(Limits? limits = null) => Importer.Read(new MemoryStream(Build()), limits: limits);

    public string Refused(Limits? limits = null) => Assert.Throws<RestoreException>(() => Read(limits)).Message;
}

/// <summary>Restore layer A on a zip of another tool's layout: the same restore as our own zip, the trust rule, what is ignored and what is refused.</summary>
public class SectionBackupTests
{
    const string Uid = "1001";
    static string Zeros(int n, string v) => string.Join(',', Enumerable.Repeat(v, n));

    const string Me = """
        {"id":1001,"handle":"sample","hostname":"sample","gender":1,"hunterscore":1234,"avatar":"https://avatars.example/a.png","membershipType":"freeplay",
         "uuid":"00000000-0000-0000-0000-000000000001","locale":"en_US","timezone":"UTC","country":"XX","memberSince":1500000000,"user_group":7,"rank":3,
         "ems":12.5,"gms":3000.25,"worlditems":[]}
        """;
    const string Row = """{"id":9001,"slot":1,"units":3,"metadata":{},"location":null}""";
    const string Dict = $$$"""
        {"500":[{{{Row}}}],
         "610":[{"id":9101,"slot":23,"units":0,"metadata":{},"location":{"pos":{"x":3144.645,"y":-0.999,"z":10.0005},"dir":{"x":0.5,"y":0,"z":-0.5},"reserve":2}}]}
        """;
    const string InventoryList = $$"""[{"item":{"id":500},"own":[{{Row}}]}]""";
    static readonly string Skills = $$"""{"weapon":{"300":4.75,"200":10.2},"tracking":[{{Zeros(59, "1.5")}}],"spotting":[{{Zeros(59, "2")}}]}""";
    const string Stats = """{"duration":3600,"distance":12.5,"data":{"collectables":{},"weapon":{},"animal":{"5":{"kills":2}},"daily_missions":{}}}""";
    const string Achievements = """[{"id":12,"category_id":1,"group_id":2,"value":3,"triggers":{"1":1600000000,"3":1600000100}},{"id":4,"category_id":1,"group_id":1,"value":0,"triggers":[]}]""";
    const string Trophies = """{"total":1,"trophies":[{"id":81,"trophy_id":3,"ts":1600000000}]}""";
    const string Missions = """{"active":[2],"completed":[1],"available":[],"states":{"1":{"state":2,"objectives":null}}}""";
    const string Expeditions = """{"has_more":false,"expeditions":[{"id":9100,"kills":1,"start_ts":1700000100}]}""";
    const string Hunt = """{"stats":null,"kills":[],"expedition":{"id":9100,"reserve":2}}""";
    const string Gallery = """{"total":1,"photos":[{"id":52,"type":2,"url":"https://photos.example/p/b.jpg","thumbnail":"https://photos.example/p/b-t.jpg","label":"120.5","animal":"Test Deer"}]}""";
    const string Wallet = """[{"id":1,"created":1700000000,"type":{"id":"C","name":"Credit"},"amount":5,"qty":1,"preBalance":0,"postBalance":5,"summary":"made up","mission":"","items":[]}]""";
    const string Friends = """{"friends":[{"id":2002,"handle":"friend"}],"total":1}""";

    /// <summary>The sections the tool's zip holds that have an endpoint, as one zip of this layout.</summary>
    static SectionZip Sections() => new SectionZip()
        .Section("me", Me).Section("inventory", InventoryList).Section("inventory_dict", Dict).Section("skills", Skills).Section("statistics", Stats)
        .Section("achievements", Achievements).Section("trophies", Trophies).Section("missions", Missions).Section("expeditions", Expeditions)
        .Section("hunt_details", $$"""{"9100":{{Hunt}}}""").Section("gallery", Gallery).Section("friendslist", Friends)
        .Section("wallet_transactions", Wallet).Section("wallet_spent", """{"spent":5,"silver_spent":0}""").Section("leaderboard", """{"status":"done","data":[]}""")
        .Section("challenges", """{"15":{"id":1,"challenge_id":15,"completed":true}}""").Section("chat_conversations", """[{"user_id":2002,"read":1}]""")
        .Section("daily_missions_calendar", """{"calendar":[],"is_frozen":false}""").Section("v2_profile", """{"hunterscore":1234,"earned_achievements":1}""")
        .Section("v2_ranks", """[{"id":1,"category_id":1,"value":5}]""").Section("v2_lodges", """[{"id":61,"name":"Lodge"}]""").Section("v2_backgrounds", """[{"id":1,"active":true}]""");

    /// <summary>The same replies as a zip of our own format: the file names and params are the backup tool's.</summary>
    static SyntheticZip Ours()
    {
        var z = new SyntheticZip();
        z.Manifest["format"] = 2;
        z.Manifest["mode"] = "own";
        z.Manifest["handle"] = "sample";
        z.Reply("/v1/Me/me", Me).Reply("/v1/Inventory/list", InventoryList, ("user_id", Uid)).Reply("/v1/Inventory/dict", Dict, ("user_id", Uid))
            .Reply("/v1/Skill/list", Skills, ("user_id", Uid)).Reply("/v1/Statistics/total", Stats, ("user_id", Uid)).Reply("/v1/Achievement/stats", Achievements, ("user_id", Uid))
            .Reply("/v1/Trophy/list", Trophies, ("user_id", Uid), ("offset", "0"), ("limit", "100000")).Reply("/v1/Mission/missions", Missions)
            .Reply("/v1/Expedition/list", Expeditions, ("user_id", Uid), ("limit", "50"), ("offset", "0"))
            .Reply("/v1/Public_user/expedition", Hunt, ("user_id", Uid), ("expedition_id", "9100"))
            .Reply("/v1/Gallery/list", Gallery, ("user_id", Uid), ("offset", "0"), ("limit", "24")).Reply("/v1/User/friendslist", Friends, ("user_id", Uid), ("offset", "0"), ("limit", "0"))
            .Reply("/v1/Wallet/transactions", Wallet, ("lang", "en"), ("offset", "0")).Reply("/v1/Wallet/spent", """{"spent":5,"silver_spent":0}""")
            .Reply("/v1/Leaderboard/personal", """{"status":"done","data":[]}""", ("user_id", Uid), ("season_no", "0"))
            .Reply("/v1/Challenge/get_user_challenges", """{"15":{"id":1,"challenge_id":15,"completed":true}}""", ("user_id", Uid))
            .Reply("/v1/Chat/conversations", """[{"user_id":2002,"read":1}]""").Reply("/v1/Daily_Mission/get_daily_missions_calendar", """{"calendar":[],"is_frozen":false}""")
            .Reply($"/game/user/{Uid}/profile", """{"hunterscore":1234,"earned_achievements":1}""").Reply($"/game/user/{Uid}/ranks", """[{"id":1,"category_id":1,"value":5}]""")
            .Reply($"/game/user/{Uid}/lodges", """[{"id":61,"name":"Lodge"}]""").Reply("/game/user/backgrounds", """[{"id":1,"active":true}]""");
        return z;
    }

    static string Json(object o) => JsonSerializer.Serialize(o, Importer.Json);

    [Fact]
    public void ASectionFolderRestoresLikeOurOwnZip()
    {
        var ours = Ours().Read();
        var foreign = Sections().Read();

        // everything but who the zip says it is (the manifest's Steam id, claim hash and tool version have no counterpart): the same model, the same extras (so the same file names), the same report lines
        Assert.Equal(Json(ours.Model with { Identity = foreign.Model.Identity }), Json(foreign.Model));
        Assert.Equal(ours.Report.Restored, foreign.Report.Restored);
        Assert.Equal(ours.Report.Unsupported, foreign.Report.Unsupported);
        Assert.Equal(ours.Report.Missing, foreign.Report.Missing);
        Assert.Equal(ours.Report.Warnings, foreign.Report.Warnings);
        Assert.Empty(foreign.Report.Rejected);
        Assert.Empty(foreign.Report.Failed);

        var m = foreign.Model;
        Assert.Equal(2, m.Items.Count);
        Assert.Equal(12.5, m.Wallet!.Em);
        Assert.Equal(2, m.Skills!.Weapon.Count);
        Assert.Equal(2, m.Achievements.Count);
        Assert.Single(m.Trophies);
        Assert.Single(m.Lodges);
        Assert.NotNull(m.Stats);
        Assert.Contains("Mission_missions.json", m.Extras.Keys);
        Assert.Contains("Public_user_expedition__user_id=1001__expedition_id=9100.json", m.Extras.Keys);
        Assert.Contains("Gallery_list__user_id=1001__offset=0__limit=24.json", m.Extras.Keys);
        Assert.Contains("Wallet_transactions__lang=en__offset=0.json", m.Extras.Keys);
        Assert.Equal(("1001", "sample", "my-account", 0, "unknown"), (m.Identity.Uid, m.Identity.Handle, m.Identity.Mode, m.Identity.Format, foreign.Report.Complete));
    }

    /// <summary>The trust rule: the format decides the parser, never the trust. A zip without our manifest has no Steam id and no claim hash, whatever its index says.</summary>
    [Fact]
    public void AZipOfAnotherToolNeverCarriesASteamIdOrAClaimHash()
    {
        var z = Sections();
        z.Index["steam_id64"] = "76561190000000000";
        z.Index["claim_hash"] = new string('a', 64);
        z.Index["mode"] = "public";
        var r = z.Read();
        Assert.Null(r.Model.Identity.SteamId64);
        Assert.Null(r.Model.Identity.ClaimHash);
        Assert.Null(r.Model.Account.SteamId);
        Assert.Null(r.Model.Identity.ToolVersion);
        Assert.Equal("my-account", r.Model.Identity.Mode);
    }

    [Fact]
    public void SectionsWithoutAnEndpointAreKeptAndWhatIsOutsideTheFolderIsIgnored()
    {
        var z = Sections().Section("observed", """{"me/me":{"a":1}}""").Section("competitions_list", "[]")
            .Section("gallery_all", """{"photos":["https://photos.example/p/b.jpg"]}""");
        z.Raw.Add(("backup_1001_converted/json_account.json", """{"made":"up"}"""u8.ToArray()));
        z.Raw.Add(("backup_1001_converted/README.txt", "made up"u8.ToArray()));
        var r = z.Read();
        Assert.Empty(r.Report.Rejected);
        Assert.Empty(r.Report.Missing.Where(x => x.Contains("index.json")));
        Assert.Equal("""{"me/me":{"a":1}}""", r.Model.Extras["json_observed.json"]!.ToJsonString());
        Assert.Contains("json_competitions_list.json", r.Model.Extras.Keys);
        Assert.Equal("""{"photos":["https://photos.example/p/b.jpg"]}""", r.Model.Extras["Gallery_list_all.json"]!.ToJsonString());
        Assert.Contains("unknown endpoint: 2 file(s), kept in Extras", r.Report.Unsupported);
        Assert.Contains("/v1/Gallery/list_all: 1 file(s), kept in Extras", r.Report.Unsupported);
        Assert.DoesNotContain(r.Model.Extras.Keys, k => k.Contains("converted") || k == "json_account.json");
    }

    [Fact]
    public void TheFolderMayBeAtTheRootOfTheZip()
    {
        var z = Sections();
        z.Prefix = "";
        Assert.Equal(2, z.Read().Model.Items.Count);
    }

    /// <summary>Another tool writes the same layout under its own section names: `trophy_list` is Trophy/list, `trophies` the own trophies and harvests (an array),
    /// `lodges` and one `lodge_&lt;id&gt;` per lodge, `loadouts`, `user_challenges`, `leaderboard_personal`, `ranks`.</summary>
    [Fact]
    public void TheOtherToolsSectionNamesRestoreTrophiesLodgesAndLoadouts()
    {
        var r = new SectionZip()
            .Section("me", Me).Section("inventory_dict", Dict)
            .Section("trophy_list", Trophies).Section("trophies", """[{"id":82,"harvest_id":7}]""")
            .Section("lodges", """[{"id":61,"name":"Lodge"},{"id":62,"name":"Cabin"}]""").Section("lodge_61", """{"animal_slots":[]}""")
            .Section("loadouts", """[{"id":5,"name":"Deer","items":[{"item_id":500,"slot_id":11}]}]""")
            .Section("user_challenges", """{"15":{"id":1,"challenge_id":15,"completed":true}}""")
            .Section("leaderboard_personal", """{"status":"done","data":[]}""").Section("ranks", """[{"id":1,"category_id":1,"value":5}]""")
            .Read();
        var m = r.Model;
        Assert.Equal(["81", "82"], m.Trophies.Select(t => t.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["61", "61", "62"], m.Lodges.Select(l => l.Id).Order(StringComparer.Ordinal));  // the list's two rows and lodge 61's own reply
        Assert.Contains("game_user_loadout.json", m.Extras.Keys);
        Assert.Contains("Challenge_get_user_challenges__user_id=1001.json", m.Extras.Keys);
        Assert.Contains("Leaderboard_personal__user_id=1001__season_no=0.json", m.Extras.Keys);
        Assert.DoesNotContain(r.Report.Missing, x => x.StartsWith("lodges", StringComparison.Ordinal) || x.StartsWith("trophies", StringComparison.Ordinal) || x.StartsWith("loadout", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Report.Warnings, x => x.Contains("Trophy/list", StringComparison.Ordinal));
    }

    /// <summary>The first tool names the saved loadouts `v2_loadout`, like its other `v2_` sections (`v2_trophies`, `v2_lodges`): they are the loadouts, not an unknown section.</summary>
    [Fact]
    public void TheV2LoadoutSectionIsTheSavedLoadouts()
    {
        var r = new SectionZip().Section("me", Me).Section("v2_loadout", """[{"id":5,"name":"Deer","items":[{"item_id":500,"slot_id":11}]}]""").Read();
        Assert.Contains("game_user_loadout.json", r.Model.Extras.Keys);
        Assert.DoesNotContain("json_v2_loadout.json", r.Model.Extras.Keys);
        Assert.DoesNotContain(r.Report.Missing, x => x.StartsWith("loadout", StringComparison.Ordinal));
    }

    [Fact]
    public void FilesThatBreakTheIndexAreRejectedOrMissing()
    {
        // hunt_details holds a hunt and two keys that are no hunt id
        var z = new SectionZip().Section("me", Me).Section("poll", "not json").Section("hunt_details", $$"""{"abc":{},"09100":{},"9100":{{Hunt}}}""");
        z.Raw.Add(("backup_1001/json_extra.json", "{}"u8.ToArray()));
        z.Raw.Add(("backup_1001/README.txt", "made up"u8.ToArray()));
        z.Index["files"] = new JsonArray("json_me.json", "json_poll.json", "json_hunt_details.json", "json_not_in_zip.json");
        var r = z.Read();
        Assert.Equal(
        [
            "README.txt: not a section file",
            "json_extra.json: not listed in index.json",
            "json_hunt_details.json: a key that is not a hunt id",
            "json_hunt_details.json: a key that is not a hunt id",
            "json_poll.json: not JSON, nested deeper than 64 or with a duplicate key",
        ], r.Report.Rejected.Order(StringComparer.Ordinal).ToList());
        Assert.Contains("json_not_in_zip.json (listed in index.json, not in the zip)", r.Report.Missing);
        Assert.Contains("Public_user_expedition__user_id=1001__expedition_id=9100.json", r.Model.Extras.Keys);
        Assert.DoesNotContain("Public_user_expedition__user_id=1001__expedition_id=09100.json", r.Model.Extras.Keys);
        Assert.Equal("sample", r.Model.Identity.Handle);
    }

    [Fact]
    public void AZipThatMatchesNoFormatIsRefused()
    {
        using var none = new MemoryStream();
        using (var zip = new ZipArchive(none, ZipArchiveMode.Create, true))
            foreach (var name in new[] { "random.json", "backup_1001/json_me.json", "backup_1001/readme.txt" })
                using (var s = zip.CreateEntry(name).Open()) s.Write("{}"u8);
        Assert.Equal("not a backup zip", Assert.Throws<RestoreException>(() => Importer.Read(new MemoryStream(none.ToArray()))).Message);

        var noSection = new SectionZip();  // an index and no section file
        Assert.Equal("not a backup zip", noSection.Refused());

        var twice = Sections();
        twice.Raw.Add(("backup_2002/index.json", """{"user_id":"2002","files":[]}"""u8.ToArray()));
        twice.Raw.Add(("backup_2002/json_me.json", "{}"u8.ToArray()));
        Assert.Equal("more than one folder with an index.json: a backup holds one", twice.Refused());
    }

    [Fact]
    public void TheIndexNamesAnIdAndTheIdentityReplyMustAgree()
    {
        var noUid = Sections();
        noUid.Index.Remove("user_id");
        Assert.Equal("index.json has no uid", noUid.Refused());
        var text = Sections();
        text.Index["user_id"] = "abc";
        Assert.Equal("index.json uid is not digits only", text.Refused());
        var big = Sections();
        big.Index["user_id"] = "99999999999999999999";
        Assert.Equal("index.json uid is too large for an id", big.Refused());
        var other = new SectionZip().Section("me", Me.Replace("\"id\":1001", "\"id\":2002"));
        Assert.Contains("2002", other.Refused());
        Assert.Equal("index.json is not a JSON object (or nested too deep, or has a duplicate key)", new SectionZip { IndexText = "[]" }.Section("me", Me).Refused());
    }

    [Fact]
    public void TheCapsOfOurOwnZipApplyToo()
    {
        var slip = Sections();
        slip.Raw.Add(("backup_1001/../evil.json", "{}"u8.ToArray()));
        Assert.Contains("unsafe entry name", slip.Refused());
        Assert.Contains("too many entries", Sections().Refused(new Limits(Entries: 5)));
        Assert.Contains("JSON entry over 10 bytes: json_achievements.json", Sections().Refused(new Limits(JsonEntryBytes: 10)));
        Assert.Contains("zip content over 2000 bytes", Sections().Refused(new Limits(TotalBytes: 2000)));
        Assert.Contains("JSON content over 1000 bytes", Sections().Refused(new Limits(JsonTotalBytes: 1000)));
        Assert.Contains("index.json is over 20 bytes", Sections().Refused(new Limits(ManifestBytes: 20)));
        Assert.Contains("compressed more than 100:1: backup_1001/json_zeros.json", Sections().Section("zeros", new byte[3 << 20]).Refused());
        var deep = Sections().Section("poll", new string('[', 70) + new string(']', 70)).Read();
        Assert.Contains("json_poll.json: not JSON, nested deeper than 64 or with a duplicate key", deep.Report.Rejected);
    }

    /// <summary>A zip of tens of thousands of folders, each with an index.json (within the entry cap): refused at once, not after a scan per folder.</summary>
    [Fact]
    public void ManyFoldersAreRefusedQuickly()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            for (var i = 0; i < 30_000; i++)
            {
                zip.CreateEntry($"f{i}/index.json");
                zip.CreateEntry($"f{i}/x");
            }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("not a backup zip", Assert.Throws<RestoreException>(() => Importer.Read(new MemoryStream(ms.ToArray()))).Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
    }
}
