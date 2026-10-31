using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The backup engine against the fake host. Every test has its own temp folder.</summary>
public class EngineTests
{
    // built at run time: a 40-character literal looks like a hex secret to the pre-push check
    static readonly string Tok = new('a', 40);

    static void Prepare(out TempDir t, out string tmp, out string key, out string dir)
    {
        t = new TempDir("backup-test-");
        (tmp, key, dir) = (t.Path, Path.Combine(t.Path, "claim.key"), Path.Combine(t.Path, "backup-7"));
    }

    /// <summary>The files and request count of one complete run: the baseline of the cancel test (the first run of the manifest test).</summary>
    static async Task<(HashSet<string> Files, int Requests)> FullRun()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var f1 = new Fake(tok);
        await Hosts.Make(f1, key).Run(tmp, "", () => tok, groups, true, true, TestContext.Current.CancellationToken);
        var good = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Where(e => e!["error"] == null).ToList();
        return (good.Select(e => e!["path"]!.ToString()).ToHashSet(), f1.Seen.Count);
    }

    [Fact]
    public async Task Manifest()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var at = new DateTimeOffset(2026, 10, 20, 8, 30, 15, TimeSpan.Zero);
        var f1 = new Fake(tok) { DateHeader = at };
        f1.Raw["/v1/Chat/conversations"] = "[]"u8.ToArray(); // an empty list still writes messages-index.json: a derived file
        var res = await Hosts.Make(f1, key).Run(tmp, "", () => tok, groups, true, true, TestContext.Current.CancellationToken);
        Assert.True(res.EndsWith(".zip") && File.Exists(res), "zip written");
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!;
        Assert.True(m["uid"]!.ToString() == "7" && m["handle"]!.ToString() == "hh" && m["tool_version"] != null, "manifest uid, handle, version");
        var files = m["files"]!.AsArray();
        var good = files.Where(e => e!["error"] == null).ToList();
        Assert.True(good.Count > 10 && good.All(e => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, e!["path"]!.ToString())))) == e["sha256"]!.ToString()), "manifest sha256 matches the files");
        Assert.True(files.All(e => !e!["params"]!.AsObject().ContainsKey("oauth_access_token")), "no token in manifest params");
        Assert.True(!File.Exists(Path.Combine(dir, "Mission_missions.json")) && files.Any(e => e!["path"]!.ToString() == "Mission_missions.json" && e["error"] != null), "reply with the token not saved");
        Assert.True(Directory.GetFiles(dir).Where(p => !p.EndsWith("claim.key")).All(p => !File.ReadAllText(p).Contains(tok) && !File.ReadAllText(p).Contains("fake.jwt.value")), "no token or JWT in any file");
        var k = File.ReadAllBytes(Path.Combine(dir, "claim.key"));
        Assert.True(k.Length == 32 && m["claim_hash"]!.ToString() == Convert.ToHexStringLower(SHA256.HashData(k)), "claim hash matches claim.key");
        Assert.True(m["steam_id64"]?.ToString() == "76561197960265828", "own account: Steam id 100 -> steam_id64 76561197960265828");
        Assert.True(good.Any(e => e!["path"]!.ToString() == "Public_user_expedition__user_id=7__expedition_id=42.json") && f1.Seen.Count(p => p == "/v1/User/jwt") == 1, "expedition detail fetched, one JWT");
        var replies = files.Where(e => e!["sha256"] != null && e["derived"] == null).ToList();
        Assert.True(replies.Count > 10 && replies.All(e => e!["date"]?.ToString() == "2026-10-20T08:30:15Z"), "every saved reply has the server's Date header, ISO UTC");
        Assert.True(files.Any(e => e!["derived"] != null) && files.Where(e => e!["error"] != null || e["derived"] != null).All(e => e!["date"] == null), "no date on a failed request or a derived file");

        var f2 = new Fake(tok);
        await Hosts.Make(f2, key).Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken);
        Assert.True(f2.Seen.Count == 2 && f2.Seen.Contains("/v1/Me/me") && f2.Seen.Contains("/v1/Mission/missions"), $"resume asks only identity and the unsaved reply ({f2.Seen.Count} requests)");
        var me2 = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Single(e => e!["path"]!.ToString() == "Me_me.json")!;
        Assert.True(me2["sha256"] != null && !me2.AsObject().ContainsKey("date"), "a reply saved without a Date header has no date key");
        var fz = new Fake(tok);
        var ez = Hosts.Make(fz, key);
        ez.SteamAccount = () => 0;
        await ez.Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject().ContainsKey("steam_id64") == false, "Steam not signed in (0): no steam_id64 in the manifest");

        File.Delete(Path.Combine(dir, "manifest.json")); // crash before the manifest was written
        var f3 = new Fake(tok);
        await Hosts.Make(f3, key).Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken);
        var m3 = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
        var optional = f3.Seen.Where(p => p.Contains("/personal/") || p.StartsWith("/game/user/harvest/")).ToList();
        Assert.True(f3.Seen.Count(p => !optional.Contains(p) && p != "/v1/User/jwt") == 2 && m3.Count(e => e!["error"] == null) == good.Count, "resume re-registers files without a manifest");
        Assert.True(optional.Count == 5 && (int)m3.Single(e => e!["path"]!.ToString() == "game_user_harvest_102.json")!["status"]! == 404, "#76: an optional reply with no status on record is asked again, so its 404 keeps its status");
        Assert.True(m3.All(e => e!["date"] == null), "no Date header (and files re-registered without a manifest): no date key");

        var f4 = new Fake("z");
        var e4 = await Assert.ThrowsAnyAsync<Exception>(() => Hosts.Make(f4, key).Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken));
        Assert.True(e4.Message.Contains("start a hunt"), "errorCode 11 -> start a hunt to refresh");
    }

    [Fact]
    public async Task ManifestSaysFormatModeAndIdentity()
    {
        string tok = Tok;
        async Task<(string Dir, JsonNode M)> Run(TempDir t, string idText, bool resume = false)
        {
            var d = await Hosts.Make(new Fake(tok), Path.Combine(t.Path, "claim.key")).Run(t.Path, idText, idText.Length == 0 ? () => tok : null, new HashSet<string> { "friends" }, resume, false, TestContext.Current.CancellationToken);
            return (d, JsonNode.Parse(File.ReadAllText(Path.Combine(d, "manifest.json")))!);
        }
        static string Said(JsonNode m) => $"{m["format"]} {m["mode"]} {m["identity"]}";
        static void MakeOld(string dir) // what every release up to format 1 wrote: no format, mode or identity, and not finished
        {
            Hosts.Unfinish(dir);
            var p = Path.Combine(dir, "manifest.json");
            var o = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
            foreach (var k in new[] { "format", "mode", "identity" }) o.Remove(k);
            File.WriteAllText(p, o.ToJsonString());
        }

        using var own = new TempDir("backup-test-");
        Assert.Equal("2 own me", Said((await Run(own, "")).M));
        using var byHandle = new TempDir("backup-test-");
        var h = await Run(byHandle, "hh");
        Assert.Equal("2 public handle", Said(h.M));
        using var byId = new TempDir("backup-test-");
        var i = (await Run(byId, "7")).M;
        Assert.True(Said(i) == "2 public id" && !i.AsObject().ContainsKey("handle"), $"public by id: {Said(i)}, no handle");

        using var resumed = new TempDir("backup-test-");
        var first = await Run(resumed, "");
        Hosts.Unfinish(first.Dir);
        var second = await Run(resumed, "7", resume: true);
        Assert.True(second.Dir == first.Dir && Said(second.M) == "2 own me", $"a public run continuing an own folder keeps it own ({Said(second.M)})");

        MakeOld(h.Dir);
        Assert.Equal("2 public handle", Said((await Run(byHandle, "hh", resume: true)).M));
        using var oldOwn = new TempDir("backup-test-");
        var o = await Run(oldOwn, "");
        MakeOld(o.Dir);
        var o2 = await Run(oldOwn, "hh", resume: true);
        Assert.True(o2.Dir == o.Dir && Said(o2.M) == "2 own me", $"an old manifest listing Me_me.json is own ({Said(o2.M)})");
    }

    [Fact]
    public async Task LanesNeverTwoRequestsAtOnceOnAHost()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var f = new Fake(tok) { DelayMs = 15 };
        await Hosts.Make(f, key).Run(tmp, "", () => tok, groups, false, false, TestContext.Current.CancellationToken);
        Assert.True(f.MaxActive["api.test"] == 1, $"api host: never two requests at once ({f.Starts["api.test"].Count} requests)");
        Assert.True(f.MaxActive["apiv2.test"] == 1 && f.Overlap, $"apiv2 lane runs beside the api lane ({f.Starts["apiv2.test"].Count} requests)");
    }

    [Fact]
    public async Task EachHostIsPaced()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var fp = new Fake(tok);
        var ep = Hosts.Make(fp, key);
        ep.PaceMs = 120;
        await ep.Run(tmp, "", () => tok, new HashSet<string> { "profile", "friends" }, false, false, TestContext.Current.CancellationToken);
        var gaps = fp.Starts.Where(h => h.Value.Count > 1).Select(h => h.Value.Zip(h.Value.Skip(1), (a, b) => (b - a).TotalMilliseconds).Min()).ToList();
        Assert.True(gaps.Count == 2 && gaps.All(g => g >= 100), $"each host is paced (smallest gaps {string.Join(" and ", gaps.Select(g => (int)g + " ms"))} for 120 ms)");
    }

    [Fact]
    public async Task CancelEndsTheRunAndResumeCompletesIt()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var full = await FullRun();
        using var cts = new CancellationTokenSource();
        var fc = new Fake(tok) { DelayMs = 10, Cts = cts, CancelAt = n => n == 8, DateHeader = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero) };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Hosts.Make(fc, key).Run(tmp, "", () => tok, groups, true, false, cts.Token));
        Assert.True(ex is OperationCanceledException, "cancel ends the run with OperationCanceledException");
        Assert.True(fc.StartedAtCancel == 8 && fc.Seen.Count == 8, $"no request starts after cancel ({fc.Seen.Count} started)");
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Where(e => e!["error"] == null).Select(e => e!["path"]!.ToString()).ToList();
        Assert.True(saved.Count > 0 && saved.All(p => File.Exists(Path.Combine(dir, p))) && !Directory.EnumerateFiles(dir, "*.part").Any(), $"cancelled run left a manifest of {saved.Count} saved files");

        var fr = new Fake(tok) { DateHeader = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero) };
        await Hosts.Make(fr, key).Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken);
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Where(e => e!["error"] == null).Select(e => e!["path"]!.ToString()).ToHashSet();
        Assert.True(after.SetEquals(full.Files), "resumed run completes the cancelled one");
        Assert.True(fr.Seen.Count < full.Requests, $"resume asked {fr.Seen.Count} requests instead of {full.Requests}");
        var dated = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Where(e => e!["sha256"] != null).ToList();
        static string Expected(string path, List<string> first) => first.Contains(path) && path != "Me_me.json" ? "2026-10-20T08:00:00Z" : "2026-10-21T09:00:00Z"; // the identity reply is asked again by every run
        Assert.True(dated.Count > saved.Count && dated.All(e => e!["date"]?.ToString() == Expected(e["path"]!.ToString(), saved)), "files of the cancelled run keep its date, files of the resumed run get the new one");
    }

    [Fact]
    public async Task SeasonLoopCatalogueAntlerBoardsHarvestAndKills()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var fg = new Fake(tok);
        var eg = Hosts.Make(fg, key);
        await eg.Run(tmp, "", () => tok, new HashSet<string> { "profile", "trophies", "kills", "account" }, false, false, TestContext.Current.CancellationToken);
        var mg = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
        JsonNode? Entry(string path) => mg.FirstOrDefault(e => e!["path"]!.ToString() == path);
        Assert.True(fg.Seen.Count(p => p == "/v1/Leaderboard/personal") == 4 && Entry("Leaderboard_personal__user_id=7__season_no=3.json") is { } && Entry("Leaderboard_personal__user_id=7__season_no=4.json") == null,
        "season loop: Leaderboard/personal for season 0 to the catalogue's season 3");
        Assert.True(Entry("Application_application__lang=en.json") is { } && !File.ReadAllText(Path.Combine(dir, "manifest.json")).Contains("ckey-secret-9") && !mg.Any(e => e!["path"]!.ToString().Contains("ckey")),
        "catalogue saved, consumer key in no file name, param or manifest");
        var personal = mg.Where(e => e!["endpoint"]!.ToString().Contains("/personal/7")).ToList();
        Assert.True(personal.Count == 3 && personal.All(e => e!["error"] == null && (int)e["status"]! == 404), "antler boards: one personal request per board, 404 = saved as empty, not failed");
        Assert.True(Entry("game_user_harvest_101.json") is { } && (int)Entry("game_user_harvest_102.json")!["status"]! == 404 && eg.Failed == 1, "harvest record per own trophy, 404 saved as empty (only the token-leak reply failed)");
        Assert.True(fg.Seen.Count(p => p == "/user/x/score/") == 1 && fg.Seen.Count(p => p == "/k.jpg") == 1 && Entry("kills/score_900.html") is { }, "kills: score page and photo only for the scored kill");
    }

    [Fact]
    public async Task DeadHostBacksOffOnce()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var fd = new Fake(tok) { DeadHost = "apiv2.test" };
        var ed = Hosts.Make(fd, key);
        ed.BackoffMs = 20;
        await ed.Run(tmp, "hh", null, new HashSet<string> { "profile", "trophies" }, false, false, TestContext.Current.CancellationToken);
        Assert.True(ed.Failed > 2 && fd.Starts["apiv2.test"].Count == 5 + (ed.Failed - 1), $"dead host: one back-off cycle, then one attempt per request ({fd.Starts["apiv2.test"].Count} attempts for {ed.Failed} requests)");
        Assert.True(ed.FailureSummary == "Profile: some pages did not answer (server error). Trophies and lodges: some pages did not answer (server error).", "result bar names the groups that did not answer");
    }

    [Fact]
    public async Task PublicProfileSendsNoGameOnlyRequest()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var fu = new Fake(tok);
        var eu = Hosts.Make(fu, key);
        await eu.Run(tmp, "hh", null, groups, false, false, TestContext.Current.CancellationToken);
        string[] gameOnly = ["/v1/Me/me", "/v1/Me/friends", "/v1/User/jwt", "/game/user/loadout", "/game/user/trophies", "/v1/Chat/conversations", "/v1/Wallet/spent",
        "/v1/Page_content/competition_states", "/v1/Page_content/competition_community_league_states", "/v1/Chat/get_message_settings", "/v1/Recruit/get_recruits", "/v1/Mission/missions",
        "/v1/Daily_Mission/get_daily_missions_calendar", "/v1/Wallet/bank_transactions", "/v1/Wallet/transactions", "/v1/Wallet/silver_transactions", "/game/user/backgrounds"];
        var mu = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
        Assert.True(!fu.Seen.Any(p => p.StartsWith("/game/user/harvest/")) && eu.Failed == 0 && mu.All(e => e!["error"] == null) && !fu.Seen.Intersect(gameOnly).Any(), $"public profile: no game-only request, 0 failed ({fu.Seen.Count} requests)");
        Assert.True(!JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject().ContainsKey("steam_id64"), "public profile: never a steam_id64 even when Steam is signed in");
    }

    [Fact]
    public async Task GalleryPhotoOnAnUnconfiguredHostIsNotFetched()
    {
        Prepare(out var t, out var tmp, out var key, out var dir);
        using var _ = t;
        string tok = Tok;
        var groups = Hosts.AllGroups;
        var fq = new Fake(tok);
        var eq = Hosts.Make(fq, key);
        await eq.Run(tmp, "hh", null, new HashSet<string> { "photos" }, false, false, TestContext.Current.CancellationToken);
        var mq = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
        var skipped = mq.Where(e => e!["error"]?.ToString() == "not an https URL on a configured host").ToList();
        Assert.True(eq.Failed == 1 && skipped.Count == 1 && fq.Seen.Count(p => p == "/g1.jpg") == 1 && !fq.Seen.Contains("/g2.jpg"), "gallery photo on an unconfigured host: one failed, one manifest entry with the reason, not fetched");
    }
}
