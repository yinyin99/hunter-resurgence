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
        var f1 = new Fake(tok);
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

        var f2 = new Fake(tok);
        await Hosts.Make(f2, key).Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken);
        Assert.True(f2.Seen.Count == 2 && f2.Seen.Contains("/v1/Me/me") && f2.Seen.Contains("/v1/Mission/missions"), $"resume asks only identity and the unsaved reply ({f2.Seen.Count} requests)");
        var fz = new Fake(tok);
        var ez = Hosts.Make(fz, key);
        ez.SteamAccount = () => 0;
        await ez.Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject().ContainsKey("steam_id64") == false, "Steam not signed in (0): no steam_id64 in the manifest");

        File.Delete(Path.Combine(dir, "manifest.json")); // crash before the manifest was written
        var f3 = new Fake(tok);
        await Hosts.Make(f3, key).Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken);
        var m3 = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
        Assert.True(f3.Seen.Count == 2 && m3.Count(e => e!["error"] == null) == good.Count, "resume re-registers files without a manifest");

        var f4 = new Fake("z");
        var e4 = await Assert.ThrowsAnyAsync<Exception>(() => Hosts.Make(f4, key).Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken));
        Assert.True(e4.Message.Contains("start a hunt"), "errorCode 11 -> start a hunt to refresh");
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
        var fc = new Fake(tok) { DelayMs = 10, Cts = cts, CancelAt = n => n == 8 };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Hosts.Make(fc, key).Run(tmp, "", () => tok, groups, true, false, cts.Token));
        Assert.True(ex is OperationCanceledException, "cancel ends the run with OperationCanceledException");
        Assert.True(fc.StartedAtCancel == 8 && fc.Seen.Count == 8, $"no request starts after cancel ({fc.Seen.Count} started)");
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Where(e => e!["error"] == null).Select(e => e!["path"]!.ToString()).ToList();
        Assert.True(saved.Count > 0 && saved.All(p => File.Exists(Path.Combine(dir, p))) && !Directory.EnumerateFiles(dir, "*.part").Any(), $"cancelled run left a manifest of {saved.Count} saved files");

        var fr = new Fake(tok);
        await Hosts.Make(fr, key).Run(tmp, "", () => tok, groups, true, false, TestContext.Current.CancellationToken);
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Where(e => e!["error"] == null).Select(e => e!["path"]!.ToString()).ToHashSet();
        Assert.True(after.SetEquals(full.Files), "resumed run completes the cancelled one");
        Assert.True(fr.Seen.Count < full.Requests, $"resume asked {fr.Seen.Count} requests instead of {full.Requests}");
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
