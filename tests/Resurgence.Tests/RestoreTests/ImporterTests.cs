using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Resurgence.Restore;

namespace Resurgence.Tests.RestoreTests;

/// <summary>Restore layer A on synthetic zips: caps, checks, versions, mode and the field rules.</summary>
public class ImporterTests
{
    sealed class Catalogue(params int[] withUnits) : IItemCatalogue
    {
        public bool HasUnits(int itemId) => withUnits.Contains(itemId);
    }

    static string ModelJson(Restored r) => JsonSerializer.Serialize(r.Model, Importer.Json);

    [Fact]
    public void CapsRefuseTheZip()
    {
        var slip = SyntheticZip.Own();
        slip.Raw.Add(("../evil.json", "{}"u8.ToArray()));
        Assert.Contains("unsafe entry name", slip.Refused());
        foreach (var bad in new[] { "/rooted.json", "C:/drive.json", "a\\b.json", "a/../../b.json" })
        {
            var z = SyntheticZip.Own();
            z.Raw.Add((bad, "{}"u8.ToArray()));
            Assert.Contains("unsafe entry name", z.Refused());
        }

        Assert.Contains("too many entries", SyntheticZip.Own().Refused(new Limits(Entries: 5)));
        Assert.Contains("zip content over 2000 bytes", SyntheticZip.Own().Refused(new Limits(TotalBytes: 2000)));
        Assert.Contains("entry over 100 bytes", SyntheticZip.Own().Refused(new Limits(EntryBytes: 100)));
        Assert.Contains("manifest.json is over 200 bytes", SyntheticZip.Own().Refused(new Limits(ManifestBytes: 200)));

        var bomb = SyntheticZip.Own();
        bomb.File("zeros.json", "/v1/Me/friends", new byte[3 << 20]);
        Assert.Contains("compressed more than 100:1: zeros.json", bomb.Refused());
        Assert.Contains("compressed more than", SyntheticZip.Own().Refused(new Limits(MaxRatio: 1, RatioMinBytes: 1)));

        var deepManifest = SyntheticZip.Own(); // the manifest is 4 deep (files, entry, params)
        Assert.Contains("nested too deep", deepManifest.Refused(new Limits(JsonDepth: 3)));
        var deepReply = SyntheticZip.Own().File("Me_friends.json", "/v1/Me/friends", Encoding.UTF8.GetBytes(new string('[', 70) + new string(']', 70)));
        Assert.Contains("Me_friends.json: not JSON or nested deeper than 64", deepReply.Read().Report.Rejected);

        Assert.Equal(new Limits(20_000, 1L << 30, 64 << 20, 32 << 20, 64, 100, 1 << 20, 128L << 20, 32 << 20), Limits.Default);
    }

    [Fact]
    public void ChecksRejectFilesOrTheZip()
    {
        var tampered = SyntheticZip.Own();
        tampered.Entry("Skill_list__user_id=1001.json")["sha256"] = new string('0', 64);
        tampered.Raw.Add(("extra.json", "{}"u8.ToArray()));
        var r = tampered.Read();
        Assert.Equal(["Skill_list__user_id=1001.json: sha256 does not match the manifest", "extra.json: not listed in the manifest"], r.Report.Rejected);
        Assert.Null(r.Model.Skills);
        Assert.Contains("skills (not in the zip)", r.Report.Missing);
        Assert.DoesNotContain("extra.json", r.Model.Extras.Keys);

        var failed = SyntheticZip.Own().Failed("Wallet_spent.json", "/v1/Wallet/spent", "error reply").Read();
        Assert.Equal(["Wallet_spent.json: error reply"], failed.Report.Failed);

        var wrongKey = SyntheticZip.Own();
        wrongKey.ClaimKey = new byte[32];
        Assert.Equal("claim.key does not match the manifest's claim_hash", wrongKey.Refused());

        var badUid = SyntheticZip.Own();
        badUid.Manifest["uid"] = "10a1";
        Assert.Equal("manifest uid is not digits only", badUid.Refused());

        var otherId = SyntheticZip.PublicZip(byHandle: false);
        otherId.Reply("/v1/Me/me", """{"id":1002,"handle":"x"}""");
        Assert.Contains("is not the manifest uid 1001", otherId.Refused());

        var nested = SyntheticZip.Own();
        nested.Prefix = "backup-1001/";
        Assert.Equal(ModelJson(SyntheticZip.Own().Read()), ModelJson(nested.Read()));
        Assert.Equal("not a zip", Assert.Throws<RestoreException>(() => Importer.Read(new MemoryStream("not a zip"u8.ToArray()))).Message);
    }

    [Fact]
    public void VersionsAreReadByFormatNeverByToolVersion()
    {
        var legacy = SyntheticZip.Own(format: null);
        legacy.Manifest.Remove("completed_utc");
        var lr = legacy.Read();
        Assert.Equal(1, lr.Model.Identity.Format);
        Assert.Equal("unknown", lr.Report.Complete);
        var shortHunts = SyntheticZip.Own();
        shortHunts.Manifest["checks"] = JsonNode.Parse("""{"hunts":{"listed":3,"saved":2}}""");
        Assert.Contains("hunts: 2 of the 3 in the hunt list were saved", shortHunts.Read().Report.Missing);
        var oldFlag = SyntheticZip.Own(); // v0.4.0 compared with the list's window count: every listed hunt saved is no gap
        oldFlag.Manifest["checks"] = JsonNode.Parse("""{"hunts":{"total":3,"listed":2,"saved":2}}""");
        Assert.DoesNotContain(oldFlag.Read().Report.Missing, x => x.StartsWith("hunts:"));
        var cutList = SyntheticZip.Own(); // a list page failed: every listed hunt saved is still a gap
        cutList.Manifest["checks"] = JsonNode.Parse("""{"hunts":{"listed":50,"saved":50,"list_failed":true}}""");
        Assert.Contains("hunts: the hunt list did not load completely (50 of the 50 listed were saved)", cutList.Read().Report.Missing);

        var f2 = SyntheticZip.Own().Read();
        Assert.Equal(2, f2.Model.Identity.Format);
        Assert.Equal("yes", f2.Report.Complete);
        var unfinished = SyntheticZip.Own();
        unfinished.Manifest.Remove("completed_utc");
        Assert.Equal("no", unfinished.Read().Report.Complete);

        var future = SyntheticZip.Own();
        future.Manifest["format"] = 99;
        Assert.Equal("made by a newer backup tool (format 99); this reader knows formats up to 2", future.Refused());

        var old = SyntheticZip.Own();
        old.Manifest["tool_version"] = "1.0.0";
        var or = old.Read();
        Assert.Equal("1.0.0", or.Model.Identity.ToolVersion);
        Assert.Equal(ModelJson(f2).Replace("0.0.0-test", "1.0.0"), ModelJson(or));
        Assert.Equal(f2.Report.ToText(), or.Report.ToText());

        var liar = SyntheticZip.Own();
        liar.Manifest["mode"] = "public";
        var lr2 = liar.Read();
        Assert.Equal("my-account", lr2.Model.Identity.Mode);
        Assert.Contains("the manifest says mode \"public\" but the files are my-account: the files are used", lr2.Report.Warnings);
        Assert.DoesNotContain(f2.Report.Warnings, w => w.Contains("manifest says mode"));
    }

    [Fact]
    public void ModeComesFromTheFiles()
    {
        var own = SyntheticZip.Own().Read();
        Assert.Equal(("my-account", "my-account"), (own.Model.Identity.Mode, own.Report.Mode));
        Assert.Equal("76561190000000000", own.Model.Identity.SteamId64);
        var handle = SyntheticZip.PublicZip(byHandle: true).Read();
        Assert.Equal(("public", "public by handle", "sample"), (handle.Model.Identity.Mode, handle.Report.Mode, handle.Model.Profile.Handle));
        var id = SyntheticZip.PublicZip(byHandle: false).Read();
        Assert.Equal(("public", "public by id"), (id.Model.Identity.Mode, id.Report.Mode));
        Assert.Equal((1001L, 1234), (id.Model.Profile.Id, id.Model.Profile.Hunterscore));
    }

    [Fact]
    public void UnitsFollowTheCatalogue()
    {
        var z = SyntheticZip.PublicZip(byHandle: false);
        // 610 a placeable (no count), 700-703 unlimited items the dict says 1 for, 800 climbing gear (a count)
        z.Reply("/v1/Inventory/list", """
            [{"item":{"id":700},"own":[{"id":9201,"slot":1,"units":1,"metadata":{}}]},{"item":{"id":701},"own":[{"id":9202,"slot":1,"units":1,"metadata":{}}]},
             {"item":{"id":702},"own":[{"id":9203,"slot":1,"units":1,"metadata":{}}]},{"item":{"id":703},"own":[{"id":9204,"slot":1,"units":1,"metadata":{}}]},
             {"item":{"id":800},"own":[{"id":9301,"slot":23,"units":25,"metadata":{},"location":{"pos":{"x":1,"y":2,"z":3},"dir":{"x":0,"y":1,"z":0},"reserve":1}}]},
             {"item":{"id":500},"own":[{"id":9001,"slot":1,"units":3,"metadata":{}}]}]
            """, ("user_id", SyntheticZip.Uid));
        var r = z.Read(new Catalogue(500, 800));
        var units = r.Model.Items.ToDictionary(i => i.ItemId, i => i.Units);
        Assert.Equal(new Dictionary<int, int?> { [500] = 3, [610] = null, [700] = null, [701] = null, [702] = null, [703] = null, [800] = 25 }, units);
        Assert.Equal("catalogue", r.Report.UnitsRuleUsed);
        Assert.DoesNotContain(r.Report.Warnings, w => w.Contains("no item catalogue"));

        var zero = z.Read();
        Assert.Equal("zero-only", zero.Report.UnitsRuleUsed);
        Assert.Contains("no item catalogue: units read as null only when 0 (an unlimited item saved with 1 keeps 1)", zero.Report.Warnings);
        Assert.Equal(new int?[] { 3, null, 1, 1, 1, 1, 25 }, zero.Model.Items.Select(i => i.Units));
    }

    [Fact]
    public void PositionsAreDecimalMillimetres()
    {
        var placed = SyntheticZip.PublicZip(byHandle: false).Read().Model.Items.Single(i => i.ItemId == 610);
        Assert.Equal(new Vec3I(3144645, -999, 10001), placed.Position); // a double gives 3144644; 10.0005 rounds half away from zero
        Assert.Equal(new Vec3I(500, 0, -500), placed.Direction);
        Assert.Equal(2, placed.WebReserve);
        Assert.Equal((-1, 3, -3), (Importer.Milli(JsonNode.Parse("-0.0005")), Importer.Milli(JsonNode.Parse("0.0025")), Importer.Milli(JsonNode.Parse("-0.0025"))));

        var z = SyntheticZip.PublicZip(byHandle: false);
        z.Reply("/v1/Inventory/list", """
            [{"item":{"id":620},"own":[{"id":9401,"slot":23,"units":0,"metadata":{"a":1},"location":null},
                                       {"id":9402,"slot":23,"units":0,"metadata":{},"location":{"pos":{"x":1.5,"y":2,"z":3},"dir":null,"reserve":3}}]},
             {"item":{"id":630},"own":[{"id":9501,"slot":1,"units":0,"metadata":{},"location":{"pos":{"x":4,"y":5,"z":6},"dir":{"x":0,"y":0,"z":1},"reserve":4}}]}]
            """, ("user_id", SyntheticZip.Uid));
        var r = z.Read();
        Assert.DoesNotContain(r.Model.Items, i => i.Guid == 9401);
        Assert.Contains("placed item 9401 (item 620) has no location: not restored, kept in Extras", r.Report.Warnings);
        Assert.Equal(9401, r.Model.Extras["placed-without-location"]![0]!["row"]!["id"]!.GetValue<int>());
        var noDir = r.Model.Items.Single(i => i.Guid == 9402);
        Assert.Equal((new Vec3I(1500, 2000, 3000), (Vec3I?)null), (noDir.Position, noDir.Direction));
        Assert.Contains("item 9402 (item 620) has a position but no direction: direction left empty", r.Report.Warnings);
        var pickedUp = r.Model.Items.Single(i => i.Guid == 9501);
        Assert.Equal((new Vec3I(4000, 5000, 6000), 4), (pickedUp.Position!, pickedUp.WebReserve!.Value));
    }

    [Fact]
    public void ListWinsOverDictAndMetadataStaysAsSent()
    {
        var z = SyntheticZip.PublicZip(byHandle: false);
        z.Reply("/v1/Inventory/dict", """
            {"640":[{"id":9601,"slot":23,"units":0,"metadata":{"bait_amount":20,"reserve":1},"location":{"pos":{"x":1,"y":1,"z":1},"dir":{"x":0,"y":0,"z":1},"reserve":2}}],
             "650":[{"id":9701,"slot":1,"units":2,"metadata":{}}]}
            """, ("user_id", SyntheticZip.Uid));
        z.Reply("/v1/Inventory/list", """
            [{"item":{"id":640},"own":[{"id":9601,"slot":23,"units":0,"metadata":{"bait_amount":15,"reserve":1,"penalty_remaining":0},"location":{"pos":{"x":1,"y":1,"z":1},"dir":{"x":0,"y":0,"z":1},"reserve":2}}]}]
            """, ("user_id", SyntheticZip.Uid));
        var r = z.Read();
        var feeder = r.Model.Items.Single(i => i.Guid == 9601);
        Assert.Equal("""{"bait_amount":15,"reserve":1,"penalty_remaining":0}""", feeder.Metadata.ToJsonString());
        Assert.Equal((2, 1), (feeder.WebReserve!.Value, feeder.Metadata["reserve"]!.GetValue<int>())); // web map 2, metadata.reserve = map - 1, unchanged
        Assert.Contains("item 9601: Inventory/list and Inventory/dict differ in metadata: the list is used", r.Report.Warnings);
        Assert.Contains(r.Model.Items, i => i.Guid == 9701); // only in the dict: kept
        Assert.Contains("\"bait_amount\": 15,", ModelJson(r)); // integers stay integers
        Assert.DoesNotContain("15.0", ModelJson(r));
    }

    [Fact]
    public void SkillsWalletAchievementsStats()
    {
        var m = SyntheticZip.Own().Read().Model;
        Assert.Equal(new Dictionary<int, double> { [200] = 10.2, [300] = 4.75 }, m.Skills!.Weapon);
        Assert.Equal([200, 300], m.Skills.Weapon.Keys);
        var floor = m.Skills.Floor();
        Assert.Equal((10.0, 4.0, 1.0, 2.0), (floor.Weapon[200], floor.Weapon[300], floor.Tracking[0], floor.Spotting[58]));
        Assert.Equal((59, 1.5), (m.Skills.Tracking.Length, m.Skills.Tracking[0]));
        Assert.Equal(new Wallet(12.5, 3000.25), m.Wallet);
        Assert.Equal([4, 12], m.Achievements.Select(a => a.Id));
        Assert.Equal((1, 2, 1600000100L), (m.Achievements[1].CategoryId, m.Achievements[1].GroupId, m.Achievements[1].Triggers["3"]!.GetValue<long>()));
        Assert.Empty(m.Achievements[0].Triggers); // [] from the server is an empty map
        Assert.Equal((12.5, 3600.0, 420, (int?)null), (m.Stats!.Distance, m.Stats.Duration, m.Stats.BustThroughScore, m.Stats.LaneBanditScore));
        Assert.Equal(["collectables", "weapon", "animal", "daily_missions"], m.Stats.UserStats.Select(p => p.Key));
        Assert.Equal(["/game/user/harvest", "/game/user/trophies", "/v1/Trophy/list"], m.Trophies.Select(t => t.Source));
        Assert.Equal(["/game/user/lodge", "/game/user/lodges"], m.Lodges.Select(t => t.Source));
    }

    [Fact]
    public void AccountDefaultsAndMembership()
    {
        var r = SyntheticZip.Own().Read();
        Assert.Equal(new AccountDefaults(false, 0, null, null, 0, 0, 0, 0, 1000000, "76561190000000000", 0, 1), r.Model.Account);
        Assert.Equal((0, 1, false), (r.Model.Profile.MembershipInt, r.Model.Profile.UserGroup, r.Report.MembershipInferred));
        Assert.Null(SyntheticZip.PublicZip(byHandle: true).Read().Model.Account.SteamId);

        (int, bool, bool) Membership(string type)
        {
            var z = SyntheticZip.PublicZip(byHandle: false);
            z.Reply("/v1/Me/me", $$"""{"id":1001,"membershipType":"{{type}}","ems":0,"gms":0}""");
            var x = z.Read();
            return (x.Model.Profile.MembershipInt, x.Report.MembershipInferred, x.Report.Warnings.Contains($"unknown membership type \"{type}\": 0 used"));
        }
        Assert.Equal((0, false, false), Membership("freeplay"));
        Assert.Equal((2, false, false), Membership("staff"));
        Assert.Equal((1, true, false), Membership("premium"));
        Assert.Equal((0, true, false), Membership("freebeta"));
        Assert.Equal((0, false, true), Membership("gold"));
    }

    [Fact]
    public void ReportIsCompleteAndDeterministic()
    {
        var pub = SyntheticZip.PublicZip(byHandle: true).Read();
        Assert.Null(pub.Model.Wallet);
        foreach (var part in new[] { "wallet", "missions", "messages", "loadout", "own trophies and harvests" })
            Assert.Contains($"{part} (public zip)", pub.Report.Missing);

        var own = SyntheticZip.Own().Read();
        Assert.Empty(own.Report.Missing);
        Assert.Contains("/v1/Chat/conversation: 1 file(s), kept in Extras", own.Report.Unsupported);
        Assert.Contains("/v1/Expedition/list: 1 file(s), kept in Extras", own.Report.Unsupported);
        Assert.Contains("Me_me.json", own.Model.Extras.Keys); // partly mapped: kept whole, not listed as unsupported
        Assert.DoesNotContain(own.Report.Unsupported, u => u.StartsWith("/v1/Me/me"));
        Assert.DoesNotContain("Skill_list__user_id=1001.json", own.Model.Extras.Keys);

        var again = SyntheticZip.Own().Read();
        Assert.Equal(ModelJson(own), ModelJson(again));
        Assert.Equal(own.Report.ToText(), again.Report.ToText());

        var derived = SyntheticZip.Own().File("map-setups.json", "derived", """{"format":1}"""u8.ToArray(), derived: true).File("messages-index.json", "derived", """{"threads":[]}"""u8.ToArray(), derived: true);
        Assert.Equal(ModelJson(own), ModelJson(derived.Read()));
    }

    [Fact]
    public void NamesMapBackWithoutAManifestEndpoint()
    {
        var minigames = Endpoints.FromName("Page_content_minigames_list__offset=0__limit=1__game_id=1__user_id=7.json")!.Value;
        Assert.Equal("/v1/Page_content/minigames_list", minigames.Endpoint);
        Assert.Equal(new Dictionary<string, string> { ["offset"] = "0", ["limit"] = "1", ["game_id"] = "1", ["user_id"] = "7" }, minigames.Params);
        Assert.Equal("/v1/Me/me", Endpoints.FromName("Me_me.json")?.Endpoint);
        Assert.Equal("/game/user/7/lodges/3", Endpoints.FromName("game_user_7_lodges_3.json")?.Endpoint);
        Assert.Equal("/game/user/harvest/9", Endpoints.FromName("game_user_harvest_9.json")?.Endpoint);
        Assert.Null(Endpoints.FromName("Unknown_thing__a=1.json"));
        Assert.Null(Endpoints.FromName("images/x.png"));

        var z = SyntheticZip.Own();
        foreach (var p in new[] { "Me_me.json", "Skill_list__user_id=1001.json" }) z.Entry(p).Remove("endpoint");
        Assert.Equal(ModelJson(SyntheticZip.Own().Read()), ModelJson(z.Read()));
    }

    [Fact]
    public void UidTooLargeForAnIdIsRefused()
    {
        var largest = SyntheticZip.PublicZip(byHandle: false);
        largest.Manifest["uid"] = long.MaxValue.ToString();
        Assert.Equal(long.MaxValue, largest.Read().Model.Profile.Id);
        var over = SyntheticZip.PublicZip(byHandle: false);
        over.Manifest["uid"] = "9223372036854775808";
        Assert.Equal("manifest uid is too large for an id", over.Refused());
        over.Manifest["uid"] = new string('9', 40);
        Assert.Equal("manifest uid is too large for an id", over.Refused());
    }

    [Fact]
    public void PositionOutOfRangeIsNoLocation()
    {
        Assert.Equal((2_000_000_000, -2_000_000_000, (int?)null, (int?)null),
            (Importer.Milli(JsonNode.Parse("2000000")), Importer.Milli(JsonNode.Parse("-2000000")), Importer.Milli(JsonNode.Parse("2000000.001")), Importer.Milli(JsonNode.Parse("1e27"))));
        var z = SyntheticZip.PublicZip(byHandle: false);
        z.Reply("/v1/Inventory/list", """
            [{"item":{"id":620},"own":[{"id":9401,"slot":23,"units":0,"metadata":{},"location":{"pos":{"x":1e27,"y":0,"z":0},"dir":{"x":0,"y":0,"z":1},"reserve":1}}]},
             {"item":{"id":630},"own":[{"id":9501,"slot":1,"units":0,"metadata":{},"location":{"pos":{"x":1,"y":-7.9e28,"z":0},"dir":{"x":0,"y":0,"z":1},"reserve":1}},
                                       {"id":9502,"slot":1,"units":0,"metadata":{},"location":{"pos":{"x":1,"y":2,"z":3},"dir":{"x":1e27,"y":0,"z":0},"reserve":1}}]}]
            """, ("user_id", SyntheticZip.Uid));
        var r = z.Read();
        Assert.DoesNotContain(r.Model.Items, i => i.Guid == 9401);
        Assert.Contains("item 9401 (item 620) has a position that is not a number within 2000000 on each axis: read as no location", r.Report.Warnings);
        Assert.Contains("placed item 9401 (item 620) has no location: not restored, kept in Extras", r.Report.Warnings);
        var picked = r.Model.Items.Single(i => i.Guid == 9501);
        Assert.Equal(((Vec3I?)null, (Vec3I?)null, (int?)null), (picked.Position, picked.Direction, picked.WebReserve));
        Assert.Contains("item 9501 (item 630) has a position that is not a number within 2000000 on each axis: read as no location", r.Report.Warnings);
        Assert.Null(r.Model.Items.Single(i => i.Guid == 9502).Direction);
    }

    [Fact]
    public void NonFiniteNumbersAreSkipped()
    {
        var z = SyntheticZip.Own();
        z.Reply("/v1/Skill/list", """{"weapon":{"200":10.2,"300":"NaN","301":"Infinity","302":1e400},"tracking":[],"spotting":[]}""", ("user_id", SyntheticZip.Uid));
        z.Reply("/v1/Me/me", """{"id":1001,"handle":"sample","membershipType":"freeplay","ems":"NaN","gms":3}""");
        var r = z.Read();
        Assert.Equal([200], r.Model.Skills!.Weapon.Keys);
        foreach (var k in new[] { "300", "301", "302" })
            Assert.Contains($"skill weapon entry \"{k}\" is not an item id and a number: skipped", r.Report.Warnings);
        Assert.Null(r.Model.Wallet);
        Assert.Contains("wallet (not in the zip)", r.Report.Missing);
        JsonSerializer.Serialize(r.Model, Importer.Json); // NaN or Infinity would throw here
    }

    [Fact]
    public void JsonEntriesHaveTheirOwnCaps()
    {
        var z = SyntheticZip.PublicZip(byHandle: false);
        z.File("images/avatar.png", "/a.png", [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))]);
        var biggestJson = 600; // the largest reply of PublicZip is the skill list, under 600 bytes
        Assert.Contains("pictures and score pages: 1 file(s), not read", z.Read(limits: new Limits(JsonEntryBytes: biggestJson)).Report.Unsupported); // a 5000-byte picture is no JSON entry
        Assert.Equal("JSON entry over 100 bytes: Achievement_stats__user_id=1001.json", z.Refused(new Limits(JsonEntryBytes: 100)));
        Assert.Equal("JSON entry over 100 bytes: Achievement_stats__user_id=1001.json", z.Refused(new Limits(EntryBytes: 100, JsonEntryBytes: 1000))); // the smaller cap applies
        Assert.Equal("JSON content over 1000 bytes", z.Refused(new Limits(JsonTotalBytes: 1000)));
        using var built = new System.IO.Compression.ZipArchive(new MemoryStream(z.Build()));
        long Json(bool manifest) => built.Entries.Where(e => e.FullName.EndsWith(".json") && (e.FullName == "manifest.json") == manifest).Sum(e => e.Length);
        var all = Json(true) + Json(false);
        Assert.Equal($"JSON content over {all - 1} bytes", z.Refused(new Limits(JsonTotalBytes: all - 1))); // the replies alone fit: the manifest counts too
        Assert.Null(Record.Exception(() => z.Read(limits: new Limits(JsonTotalBytes: all))));
        Assert.Equal($"JSON content over {Json(true) - 1} bytes", z.Refused(new Limits(JsonTotalBytes: Json(true) - 1))); // the manifest alone is over
    }

    [Fact]
    public void UnsupportedIsGroupedByPatternAndNoEntryIsOneLine()
    {
        var z = SyntheticZip.PublicZip(byHandle: false);
        z.Reply($"/game/user/{SyntheticZip.Uid}/ranks", """[{"rank":1}]""");
        z.Reply("/v1/Leaderboard/personal", """{"status":"done"}""", ("user_id", SyntheticZip.Uid), ("season_no", "0"));
        z.Reply("/v1/Leaderboard/personal", """{"status":"done"}""", ("user_id", SyntheticZip.Uid), ("season_no", "1"));
        foreach (var (id, status) in new[] { ("11", 200), ("12", 200), ("13", 404), ("14", 404) })
            z.File($"game_user_leaderboards_antlers_single_{id}_personal_1001.json", $"/game/user/leaderboards/antlers/single/{id}/personal/1001",
                Encoding.UTF8.GetBytes(status == 404 ? """{"message":"no entry"}""" : """{"score":5}"""), status: status);
        var r = z.Read();
        Assert.Equal(
        [
            "/game/user/*/ranks: 1 file(s), kept in Extras",
            "/game/user/leaderboards/antlers/single/*/personal/*: 2 file(s), kept in Extras",
            "/v1/Expedition/list: 1 file(s), kept in Extras",
            "/v1/Leaderboard/personal: 2 file(s), kept in Extras",
            "/v1/Public_user/expedition: 1 file(s), kept in Extras",
            "/v1/User/friendslist: 1 file(s), kept in Extras",
            "\"no entry\" answers (status 404): 2 file(s), kept in Extras",
        ], r.Report.Unsupported);
        Assert.Contains("game_user_leaderboards_antlers_single_13_personal_1001.json", r.Model.Extras.Keys); // never dropped
        Assert.Contains("trophies: 1 rows (0 without a harvest)", r.Report.Restored);
    }
}
