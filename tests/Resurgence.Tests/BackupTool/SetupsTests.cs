using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The map-setups group: placed equipment read last, written as map-setups.json. Fake host, own temp folder per test.</summary>
public class SetupsTests
{
    static readonly string Tok = new('a', 40);
    static readonly HashSet<string> Setups = ["setups"];
    static readonly HashSet<string> Everything = [.. Hosts.AllGroups, "setups"];

    const string Catalogue = """
        {"season":3,
         "items":[{"id":11,"define":"TENT02","name":"Test Tent"},{"id":12,"define":"TREE_STAND01","name":"Test Stand"},{"id":13,"define":"BEAR_BAIT_BARREL","name":"Test Barrel"},
                  {"id":14,"define":"DEPLOYABLE_HUNTING_TOWER_01","name":"Test Tower"},{"id":354,"define":"CLIMBING_GEAR_01","name":"Test Climbing Gear"}],
         "reserves":[{"id":1,"name":"Reserve One"},{"id":11,"name":"Reserve Eleven"}]}
        """;

    /// <summary>A location: positions as text, so a test can check that they come back digit for digit.</summary>
    static string At(string x, string y, string z, int? reserve = 1) =>
        $$"""{"pos":{"x":{{x}},"y":{{y}},"z":{{z}}},"dir":{"x":0.5,"y":0,"z":-0.5}{{(reserve is { } r ? $",\"reserve\":{r}" : "")}}}""";

    static string Row(string guid, string? location, int slot = 23, string units = "0", string metadata = "{}") =>
        $$"""{"id":"{{guid}}","slot":{{slot}},"units":{{units}},"metadata":{{metadata}},"location":{{location ?? "null"}}}""";

    static string Dict(params (int Item, string Row)[] rows) =>
        "{" + string.Join(",", rows.GroupBy(r => r.Item).Select(g => $"\"{g.Key}\":[{string.Join(",", g.Select(r => r.Row))}]")) + "}";

    static string ListOf(params (int Item, string Define, string Row)[] rows) =>
        "[" + string.Join(",", rows.GroupBy(r => (r.Item, r.Define)).Select(g => $$"""{"item":{"id":{{g.Key.Item}},"define":"{{g.Key.Define}}","name":"n"},"own":[{{string.Join(",", g.Select(r => r.Row))}}]}""")) + "]";

    static string Worlditems(params (string Guid, string Pos, int Reserve)[] w) =>
        "[" + string.Join(",", w.Select(x => $$"""{"id":"{{x.Guid}}","item_id":1,"define":"D","pos":{{x.Pos}},"reserve":{{x.Reserve}},"metadata":{} }""")) + "]";

    static string P(string x, string y, string z) => $$"""{"x":{{x}},"y":{{y}},"z":{{z}}}""";

    static JsonNode Read(string dir, string file = "map-setups.json") => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, file)))!;

    static Task<string> RunOwn(Fake f, TempDir t, bool resume, ISet<string> groups, bool zip = false, CancellationToken ct = default) =>
        Hosts.Make(f, Path.Combine(t.Path, "claim.key")).Run(t.Path, "", Tok, groups, resume, zip, ct);

    // the world as the first tests use it: 7 rows, 6 placed on two maps, one picked up that still has a location
    static (string Dict, string List, string Me) World()
    {
        var rows = new (int, string, string)[]
        {
            (11, "TENT02", Row("t1", At("-13077.100", "412.5", "3034.25000"))),
            (11, "TENT02", Row("t2", At("-13000.5", "410", "3100"))),
            (12, "TREE_STAND01", Row("s1", At("-12900.25", "415", "3200.75"), metadata: """{"tree_shape_key":123,"tree_shape_position_x":-12900250}""")),
            (13, "BEAR_BAIT_BARREL", Row("f1", At("2000.5", "500", "-700.125", 11), metadata: """{"reserve":10,"bait_amount":5}""")),
            (14, "DEPLOYABLE_HUNTING_TOWER_01", Row("w1", At("2100", "505", "-710", 11))),
            (354, "CLIMBING_GEAR_01", Row("c1", At("2200", "520", "-720", 11), units: "20")),
            (11, "TENT02", Row("x0", At("1", "2", "3"), slot: 0)),
        };
        return (Dict([.. rows.Select(r => (r.Item1, r.Item3))]), ListOf(rows), Worlditems(
            ("t1", P("-13077.100", "412.5", "3034.25000"), 1), ("t2", P("-13000.5", "410", "3100"), 1), ("s1", P("-12900.25", "415", "3200.75"), 1),
            ("f1", P("2000.5", "500", "-700.125"), 11), ("w1", P("2100", "505", "-710"), 11), ("c1", P("2200", "520", "-720"), 11)));
    }

    static Fake FakeWorld()
    {
        var (d, l, m) = World();
        return new Fake(Tok) { InventoryDict = d, InventoryList = l, WorldItems = m, Catalogue = Catalogue };
    }

    /// <summary>Case 1: the derived file has the placed items per map, kinds, names, and pos, dir, metadata and units as the server sent them.</summary>
    [Fact]
    public async Task DerivedFileHasThePlacedItemsPerMapVerbatim()
    {
        using var t = new TempDir("backup-test-setups-");
        var f = FakeWorld();
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "", Tok, new HashSet<string> { "profile", "setups" }, false, false, TestContext.Current.CancellationToken);
        var s = Read(Path.Combine(t.Path, "backup-7"));
        Assert.True(s["format"]!.GetValue<int>() == 1 && s["uid"]!.ToString() == "7" && s["tool_version"] != null, "format, uid, tool version");
        Assert.True(s["totals"]!["placed"]!.GetValue<int>() == 6 && s["totals"]!["reserves"]!.GetValue<int>() == 2 && e.SetupItems == 6 && e.SetupReserves == 2 && !e.SetupChecks, "6 placed items on 2 maps, the picked-up row is not one, sources agree");
        var kinds = s["totals"]!["by_kind"]!.AsObject().ToDictionary(k => k.Key, k => k.Value!.GetValue<int>());
        Assert.True(kinds.Count == 5 && kinds["tent"] == 2 && kinds["tree_stand"] == 1 && kinds["feeder"] == 1 && kinds["tower"] == 1 && kinds["climbing"] == 1, "kinds from the defines");
        var maps = s["reserves"]!.AsArray();
        Assert.True(maps.Count == 2 && (int)maps[0]!["reserve"]! == 1 && maps[0]!["name"]!.ToString() == "Reserve One" && (int)maps[0]!["count"]! == 3 && (int)maps[1]!["reserve"]! == 11 && maps[1]!["name"]!.ToString() == "Reserve Eleven", "maps in order with names from the catalogue");
        var one = maps[0]!["items"]!.AsArray();
        Assert.True(one.Select(i => i!["guid"]!.ToString()).SequenceEqual(["t1", "t2", "s1"]) && one[0]!["define"]!.ToString() == "TENT02" && one[0]!["name"]!.ToString() == "Test Tent" && one[0]!["item_id"]!.GetValue<int>() == 11, "items: guid, item id, define, name");
        Assert.True(one[0]!["pos"]!.ToJsonString() == """{"x":-13077.100,"y":412.5,"z":3034.25000}""" && one[0]!["dir"]!.ToJsonString() == """{"x":0.5,"y":0,"z":-0.5}""", "pos and dir exactly as sent, digit for digit");
        Assert.True(one[2]!["metadata"]!.ToJsonString() == """{"tree_shape_key":123,"tree_shape_position_x":-12900250}""" && one[2]!["kind"]!.ToString() == "tree_stand", "metadata exactly as sent");
        var eleven = maps[1]!["items"]!.AsArray();
        var climbing = eleven.Single(i => i!["guid"]!.ToString() == "c1")!;
        Assert.True(climbing["units"]!.GetValue<int>() == 20 && climbing["kind"]!.ToString() == "climbing" && eleven.Single(i => i!["guid"]!.ToString() == "f1")!["metadata"]!["reserve"]!.GetValue<int>() == 10, "climbing gear keeps units 20; the feeder keeps its own 0-based reserve in metadata");
        Assert.True(one[0]!["in"]!.AsArray().Select(x => x!.ToString()).SequenceEqual(["dict", "list", "me"]), "item is listed in all three sources");
        Assert.True(!s["checks"]!.AsObject().ContainsKey("climbing_variant") && s["checks"]!["location_null"]!.AsArray().Count == 0 && s["checks"]!["pos_differs"]!.AsArray().Count == 0, "no climbing_variant constant, nothing found");
        Assert.True(!JsonNode.Parse(File.ReadAllText(Path.Combine(t.Path, "backup-7", "map-setups.json")))!.ToJsonString().Contains("x0"), "the slot-0 row is not in the file");
    }

    /// <summary>Placed rows without a reserve go under reserve null; placed rows without a position are listed in checks.location_null.</summary>
    [Fact]
    public async Task PlacedItemWithoutReserveIsKeptAndWithoutPositionIsListed()
    {
        using var t = new TempDir("backup-test-setups-");
        var rows = new (int, string, string)[]
        {
            (11, "TENT02", Row("n1", At("5", "6", "7", reserve: null))),
            (11, "TENT02", Row("n2", null)),
            (11, "TENT02", Row("r1", At("8", "9", "10"))),
        };
        var f = new Fake(Tok) { InventoryDict = Dict([.. rows.Select(r => (r.Item1, r.Item3))]), InventoryList = ListOf(rows) };
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "hh", null, Setups, false, false, TestContext.Current.CancellationToken);
        var s = Read(Path.Combine(t.Path, "backup-7"));
        var maps = s["reserves"]!.AsArray();
        Assert.True(maps.Count == 2 && (int)maps[0]!["reserve"]! == 1 && maps[1]!["reserve"] == null && (int)maps[1]!["count"]! == 1 && maps[1]!["items"]![0]!["guid"]!.ToString() == "n1", "the row without a reserve is under reserve null, last");
        Assert.True(s["checks"]!["location_null"]!.AsArray().Select(x => x!.ToString()).SequenceEqual(["n2"]) && s["totals"]!["placed"]!.GetValue<int>() == 2 && e.SetupChecks, "the row without a position is in location_null and not counted");
    }

    /// <summary>Case 3: a second run with resume on asks the setup again; the moved and the added tent show. Nothing else is asked again.</summary>
    [Fact]
    public async Task SetupIsReadAgainOnAResumedRun()
    {
        using var t = new TempDir("backup-test-setups-");
        var groups = new HashSet<string> { "inventory", "setups" };
        var rows1 = new (int, string, string)[] { (11, "TENT02", Row("t1", At("100", "1", "100"))), (11, "TENT02", Row("t2", At("200", "1", "200"))) };
        var f1 = new Fake(Tok) { InventoryDict = Dict([.. rows1.Select(r => (r.Item1, r.Item3))]), InventoryList = ListOf(rows1), Catalogue = Catalogue };
        await RunOwn(f1, t, true, groups, ct: TestContext.Current.CancellationToken);
        var dir = Path.Combine(t.Path, "backup-7");
        Assert.True(Read(dir)["totals"]!["placed"]!.GetValue<int>() == 2, "first run: 2 tents");

        var rows2 = new (int, string, string)[] { (11, "TENT02", Row("t1", At("111", "1", "111"))), (11, "TENT02", Row("t2", At("200", "1", "200"))), (11, "TENT02", Row("t3", At("300", "1", "300"))) };
        var d2 = Dict([.. rows2.Select(r => (r.Item1, r.Item3))]);
        Hosts.Unfinish(dir); // a crashed or cancelled run: a finished one is never continued
        var f2 = new Fake(Tok) { InventoryDict = d2, InventoryList = ListOf(rows2), Catalogue = Catalogue };
        var e2 = await RunOwnEngine(f2, t, groups);
        var s = Read(dir);
        var items = s["reserves"]![0]!["items"]!.AsArray();
        Assert.True(items.Count == 3 && items.Single(i => i!["guid"]!.ToString() == "t1")!["pos"]!["x"]!.GetValue<int>() == 111 && items.Any(i => i!["guid"]!.ToString() == "t3"), "second run shows the moved tent and the added one");
        Assert.True(f2.Seen.Count == 3 && f2.Seen.Contains("/v1/Me/me") && f2.Seen.Count(p => p == "/v1/Inventory/dict") == 1 && f2.Seen.Count(p => p == "/v1/Inventory/list") == 1, $"only identity, dict and list were asked ({f2.Seen.Count} requests); every other file was skipped");
        Assert.True(File.ReadAllText(Path.Combine(dir, "Inventory_dict__user_id=7.json")) == d2 && e2.Skipped > 0, "the raw dict file is the new reply");
        var m = Read(dir, "manifest.json")["files"]!.AsArray();
        Assert.True(m.Count(e => e!["path"]!.ToString() == "Inventory_dict__user_id=7.json") == 1, "one manifest entry for the dict, replaced not duplicated");
    }

    static async Task<Engine> RunOwnEngine(Fake f, TempDir t, ISet<string> groups)
    {
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "", Tok, groups, true, false, TestContext.Current.CancellationToken);
        return e;
    }

    /// <summary>Case 4: the sources disagree. The checks name the items, the run has 0 failed requests.</summary>
    [Fact]
    public async Task SourcesThatDisagreeAreNamedInTheChecks()
    {
        using var t = new TempDir("backup-test-setups-");
        var all = new (int, string, string)[] { (11, "TENT02", Row("g1", At("1", "1", "1"))), (11, "TENT02", Row("g2", At("2", "2", "2"))) };
        var f = new Fake(Tok)
        {
            InventoryDict = Dict([.. all.Select(r => (r.Item1, r.Item3))]),
            InventoryList = ListOf(all[0]), // lacks g2
            WorldItems = Worlditems(("g1", P("1", "1", "1"), 1), ("g2", P("2", "2", "2"), 1), ("g9", P("9", "9", "9"), 1)), // has an extra one
        };
        var e = await RunOwnEngine(f, t, Setups);
        var c = Read(Path.Combine(t.Path, "backup-7"))["checks"]!;
        string[] Only(string src) => [.. c["only_in"]![src]!.AsArray().Select(x => x!.ToString())];
        Assert.True(Only("dict").SequenceEqual(["g2"]) && Only("list").Length == 0 && Only("me").SequenceEqual(["g2", "g9"]), "only_in names g2 (not in list) and g9 (only in me)");
        Assert.True(e.Failed == 0 && e.SetupChecks && e.SetupItems == 2, "no failed request, the check flag is set, g9 is not counted as an item");
    }

    /// <summary>Case 4b: a position that differs between sources is named.</summary>
    [Fact]
    public async Task PositionsThatDifferAreNamed()
    {
        using var t = new TempDir("backup-test-setups-");
        var d = new (int, string, string)[] { (11, "TENT02", Row("g1", At("1", "1", "1"))) };
        var l = new (int, string, string)[] { (11, "TENT02", Row("g1", At("1", "1", "2"))) };
        var f = new Fake(Tok) { InventoryDict = Dict((d[0].Item1, d[0].Item3)), InventoryList = ListOf(l) };
        await RunOwnEngine(f, t, Setups);
        var c = Read(Path.Combine(t.Path, "backup-7"))["checks"]!;
        Assert.True(c["pos_differs"]!.AsArray().Select(x => x!.ToString()).SequenceEqual(["g1"]), "pos_differs names g1");
    }

    /// <summary>Case 6: a public profile has no game session: me is skipped, no request carries a token, no game-only request is sent.</summary>
    [Fact]
    public async Task PublicProfileUsesDictAndListOnly()
    {
        using var t = new TempDir("backup-test-setups-");
        var f = FakeWorld();
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "hh", null, Everything, false, false, TestContext.Current.CancellationToken);
        string[] gameOnly = ["/v1/Me/me", "/v1/Me/friends", "/v1/User/jwt", "/game/user/loadout", "/game/user/trophies", "/v1/Chat/conversations", "/v1/Wallet/spent",
            "/v1/Page_content/competition_states", "/v1/Page_content/competition_community_league_states", "/v1/Chat/get_message_settings", "/v1/Recruit/get_recruits", "/v1/Mission/missions",
            "/v1/Daily_Mission/get_daily_missions_calendar", "/v1/Wallet/bank_transactions", "/v1/Wallet/transactions", "/v1/Wallet/silver_transactions", "/game/user/backgrounds"];
        var s = Read(Path.Combine(t.Path, "backup-7"));
        Assert.True(s["sources"]!["me_worlditems"]!.ToString() == "skipped: public profile" && s["sources"]!["dict"]!["rows"]!.GetValue<int>() == 7 && s["sources"]!["list"]!["rows"]!.GetValue<int>() == 7, "me skipped, dict and list read");
        Assert.True(f.WithToken == 0 && !f.Seen.Intersect(gameOnly).Any() && e.Failed == 0 && e.SetupItems == 6 && !e.SetupChecks, "no token, no game-only request, 0 failed, 6 placed items");
        Assert.True(s["reserves"]![0]!["items"]![0]!["in"]!.AsArray().Select(x => x!.ToString()).SequenceEqual(["dict", "list"]), "items are in dict and list");
    }

    /// <summary>Case 7: a dict reply that carries the token is dropped. The file says dict is unavailable and is not built from the older copy.</summary>
    [Fact]
    public async Task LeakingDictReplyIsDroppedAndNotReplacedByAnOlderCopy()
    {
        using var t = new TempDir("backup-test-setups-");
        var a = new (int, string, string)[] { (11, "TENT02", Row("t1", At("10", "1", "10"))) };
        var b = new (int, string, string)[] { (11, "TENT02", Row("t1", At("99", "1", "99"))) };
        await RunOwn(new Fake(Tok) { InventoryDict = Dict((a[0].Item1, a[0].Item3)), InventoryList = ListOf(a) }, t, true, Setups, ct: TestContext.Current.CancellationToken);
        Hosts.Unfinish(Path.Combine(t.Path, "backup-7"));
        var f = new Fake(Tok) { InventoryDict = $$"""{"11":[],"leak":"{{Tok}}"}""", InventoryList = ListOf(b), WorldItems = Worlditems(("t1", P("99", "1", "99"), 1)) };
        var e = await RunOwnEngine(f, t, Setups);
        var dir = Path.Combine(t.Path, "backup-7");
        var s = Read(dir);
        Assert.True(s["sources"]!["dict"]!.ToString() == "unavailable" && s["reserves"]![0]!["items"]![0]!["pos"]!["x"]!.GetValue<int>() == 99, "dict unavailable; the item comes from the fresh list, not the older dict file");
        var entry = Read(dir, "manifest.json")["files"]!.AsArray().Single(x => x!["path"]!.ToString() == "Inventory_dict__user_id=7.json")!;
        Assert.True(e.Failed == 1 && !e.SetupChecks && entry["error"] == null, "one failed request counted, no check flag; the older dict entry stays as it was");
        Assert.True(!File.ReadAllText(Path.Combine(dir, "map-setups.json")).Contains(Tok), "no token in the file");
    }

    /// <summary>Case 8: an empty setup is "none placed"; a failed read is never "none placed".</summary>
    [Fact]
    public async Task EmptySetupIsNoneAndFailedReadsAreNot()
    {
        using var t = new TempDir("backup-test-setups-");
        var e = await RunOwnEngine(new Fake(Tok) { InventoryDict = "{}", InventoryList = "[]" }, t, Setups);
        var s = Read(Path.Combine(t.Path, "backup-7"));
        Assert.True(e.SetupItems == 0 && !e.SetupChecks && s["totals"]!["placed"]!.GetValue<int>() == 0 && s["reserves"]!.AsArray().Count == 0, "{} gives 0 placed and no flag: the window says none placed");

        using var t2 = new TempDir("backup-test-setups-");
        var one = new (int, string, string)[] { (11, "TENT02", Row("t1", At("1", "1", "1"))) };
        var f2 = new Fake(Tok) { InventoryList = ListOf(one), WorldItems = Worlditems(("t1", P("1", "1", "1"), 1)) };
        f2.Down.Add("/v1/Inventory/dict");
        var e2 = await RunOwnEngine(f2, t2, Setups);
        var s2 = Read(Path.Combine(t2.Path, "backup-7"));
        Assert.True(s2["sources"]!["dict"]!.ToString() == "unavailable" && e2.Failed > 0 && !e2.SetupChecks && e2.SetupItems == 1, "dict down: unavailable, counted as failed, built from the list, no check flag (the failure is counted)");

        using var t3 = new TempDir("backup-test-setups-");
        await RunOwn(new Fake(Tok) { InventoryDict = "{}", InventoryList = "[]" }, t3, true, Setups, ct: TestContext.Current.CancellationToken);
        Hosts.Unfinish(Path.Combine(t3.Path, "backup-7"));
        var f3 = new Fake(Tok);
        f3.Down.Add("/v1/Inventory/dict");
        f3.Down.Add("/v1/Inventory/list");
        var e3 = await RunOwnEngine(f3, t3, Setups);
        var dir3 = Path.Combine(t3.Path, "backup-7");
        var entry = Read(dir3, "manifest.json")["files"]!.AsArray().Single(x => x!["path"]!.ToString() == "map-setups.json")!;
        Assert.True(e3.SetupItems == null && !File.Exists(Path.Combine(dir3, "map-setups.json")) && entry["error"] != null && e3.Failed >= 3, "both down: no file (the older one is removed), the file is a failed entry in the manifest");
    }

    /// <summary>Case 9: the file is in the manifest as derived with its sha256 and in the zip; a cancel leaves no partial file.</summary>
    [Fact]
    public async Task FileIsInManifestAndZipAndACancelLeavesNoPartialFile()
    {
        using var t = new TempDir("backup-test-setups-");
        var zip = await RunOwn(FakeWorld(), t, false, Setups, true, TestContext.Current.CancellationToken);
        var dir = Path.Combine(t.Path, "backup-7");
        var entry = Read(dir, "manifest.json")["files"]!.AsArray().Single(x => x!["path"]!.ToString() == "map-setups.json")!;
        Assert.True(entry["derived"]!.GetValue<bool>() && entry["error"] == null && entry["sha256"]!.ToString() == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "map-setups.json")))), "manifest: derived, sha256 matches the file");
        using (var z = ZipFile.OpenRead(zip)) Assert.True(z.Entries.Any(x => x.FullName.Replace('\\', '/').EndsWith("map-setups.json")), "the zip holds map-setups.json");

        using var t2 = new TempDir("backup-test-setups-");
        using var cts = new CancellationTokenSource();
        var f = new Fake(Tok) { DelayMs = 10, Cts = cts, CancelAt = n => n == 2 }; // the cancel comes while the dict is read
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => RunOwn(f, t2, false, Setups, ct: cts.Token));
        var dir2 = Path.Combine(t2.Path, "backup-7");
        Assert.True(ex is OperationCanceledException && !File.Exists(Path.Combine(dir2, "map-setups.json")) && !Directory.EnumerateFiles(dir2, "*.part").Any(), "cancelled: no map-setups.json, no .part file");
    }

    /// <summary>Case 11: the setup reads are the last two requests of a run with every group, after everything else.</summary>
    [Fact]
    public async Task SetupReadsComeLast()
    {
        using var t = new TempDir("backup-test-setups-");
        var f = FakeWorld();
        await RunOwn(f, t, false, Everything, ct: TestContext.Current.CancellationToken);
        Assert.True(f.Seen[^2] == "/v1/Inventory/dict" && f.Seen[^1] == "/v1/Inventory/list", "the last two requests are dict and list");
        Assert.True(f.Seen.Count(p => p == "/v1/Inventory/dict") == 2 && f.Seen.Count(p => p == "/v1/Inventory/list") == 1, "dict was read by the inventory group too, and again at the end");
        var m = Read(Path.Combine(t.Path, "backup-7"), "manifest.json")["files"]!.AsArray();
        Assert.True(m.Count(x => x!["path"]!.ToString() == "Inventory_dict__user_id=7.json") == 1, "one manifest entry for the dict");
    }
}
