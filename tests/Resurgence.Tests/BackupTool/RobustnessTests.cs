using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The v0.3 behaviours of the engine on the fake host. Every test has its own temp folder.</summary>
public class RobustnessTests
{
    static readonly string Tok = new('a', 40);

    static async Task<Engine> Run(Fake f, TempDir t, string[] groups, bool resume = true, CancellationToken? ct = null, Action<Engine>? tune = null)
    {
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        tune?.Invoke(e);
        await e.Run(t.Path, "", () => Tok, new HashSet<string>(groups), resume, false, ct ?? TestContext.Current.CancellationToken);
        return e;
    }

    static string[] Folders(TempDir t) => [.. Directory.GetDirectories(t.Path).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    static JsonNode Manifest(string dir) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!;

    /// <summary>F1: a finished backup is never filled up later. The next run is a new snapshot, even with resume on, so a hunt played since is saved.</summary>
    [Fact]
    public async Task FinishedBackupIsFollowedByANewSnapshot()
    {
        using var t = new TempDir("backup-test-v03-");
        var f1 = new Fake(Tok);
        await Run(f1, t, ["hunts"]);
        var first = Path.Combine(t.Path, "backup-7");
        Assert.True(Manifest(first)["completed_utc"] != null, "a whole run writes completed_utc");

        var f2 = new Fake(Tok) { ExpeditionList = """{"total":2,"expeditions":[{"id":43},{"id":42}]}""" };
        await Run(f2, t, ["hunts"]);
        var folders = Folders(t);
        Assert.True(folders.Length == 2 && folders[0] == "backup-7" && folders[1].StartsWith("backup-7-"), $"a new folder beside the first ({string.Join(",", folders)})");
        var second = Path.Combine(t.Path, folders[1]);
        Assert.True(File.Exists(Path.Combine(second, "Public_user_expedition__user_id=7__expedition_id=43.json")) && f2.Seen.Count(p => p == "/v1/Public_user/expedition") == 2, "the new hunt is saved and the old one asked again, nothing reused");
        Assert.True(!File.Exists(Path.Combine(first, "Public_user_expedition__user_id=7__expedition_id=43.json")), "the first backup is left as it was");

        var f3 = new Fake(Tok);
        await Run(f3, t, ["hunts"]);
        Assert.True(Folders(t).Length == 3, "a third run is a third snapshot, also within the same second");
    }

    /// <summary>F1: an unfinished run (cancelled, or with requests that failed) is continued by the next run with resume on.</summary>
    [Fact]
    public async Task UnfinishedBackupIsContinuedNotRestarted()
    {
        using var t = new TempDir("backup-test-v03-");
        using var cts = new CancellationTokenSource();
        const string six = """{"total":6,"expeditions":[{"id":1},{"id":2},{"id":3},{"id":4},{"id":5},{"id":6}]}""";
        var f1 = new Fake(Tok) { DelayMs = 10, Cts = cts, CancelAt = n => n == 5, ExpeditionList = six };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f1, t, ["hunts"], ct: cts.Token));
        Assert.True(Manifest(Path.Combine(t.Path, "backup-7"))["completed_utc"] == null, "a cancelled run is not finished");

        var f2 = new Fake(Tok) { ExpeditionList = six };
        f2.Down.Add("/v1/Public_user/expedition");
        var e2 = await Run(f2, t, ["hunts"]);
        Assert.True(Folders(t).Length == 1 && e2.Skipped > 0 && e2.Failed > 0, $"the same folder, files skipped, requests failed ({Folders(t).Length} folders, {e2.Skipped} skipped, {e2.Failed} failed)");
        Assert.True(Manifest(Path.Combine(t.Path, "backup-7"))["completed_utc"] == null, "a run with failed requests is not finished either");

        var e3 = await Run(new Fake(Tok) { ExpeditionList = six }, t, ["hunts"]);
        Assert.True(Folders(t).Length == 1 && e3.Failed == 0 && Manifest(Path.Combine(t.Path, "backup-7"))["completed_utc"] != null, "the retry completes the same backup");
    }

    static string Kills(params (int Id, double? Score, string? Photo)[] k) => "{\"kills\":[" + string.Join(",", k.Select(x =>
        "{\"id\":" + x.Id + ",\"kill\":{\"score\":" + (x.Score?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + (x.Photo == null ? "" : ",\"photo\":\"" + x.Photo + "\"") + "}}")) + "]}";

    /// <summary>F3: the picture of every kill that has one is saved, scored or not; a score sheet only for a scored kill.</summary>
    [Fact]
    public async Task KillPhotosAreSavedForUnscoredKillsToo()
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok);
        f.HuntReplies["42"] = Kills((900, 1.5, "https://photo.test/a.jpg"), (901, null, "https://photo.test/b.jpg"), (902, null, null));
        var e = await Run(f, t, ["kills"]);
        var dir = Path.Combine(t.Path, "backup-7");
        Assert.True(f.Seen.Contains("/a.jpg") && f.Seen.Contains("/b.jpg") && f.Seen.Count(p => p == "/user/x/score/") == 1 && e.Failed == 0, "both pictures fetched, one sheet (the scored kill), 0 failed");
        Assert.True(Directory.GetFiles(Path.Combine(dir, "kills")).Length == 3 && File.Exists(Path.Combine(dir, "kills", "score_900.html")) && !File.Exists(Path.Combine(dir, "kills", "score_901.html")), "kills/ holds the sheet and the two pictures");
    }

    /// <summary>F5: the hunts are read newest first, whatever order the list has, so the kill sheets of the latest hunts come first.</summary>
    [Fact]
    public async Task KillSheetsAreFetchedNewestHuntFirst()
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok) { ExpeditionList = """{"total":3,"expeditions":[{"id":41,"start_ts":100},{"id":43,"start_ts":300},{"id":42,"start_ts":200}]}""" };
        foreach (var (hunt, kill) in new[] { ("41", 4101), ("42", 4201), ("43", 4301) }) f.HuntReplies[hunt] = Kills((kill, 1.5, null));
        await Run(f, t, ["kills"]);
        var sheets = f.Urls.Where(u => u.StartsWith("/user/x/score/")).Select(u => u.Split("id=")[1].Split('&')[0]).ToArray();
        Assert.True(sheets.SequenceEqual(["4301", "4201", "4101"]), $"sheets newest hunt first ({string.Join(",", sheets)})");
    }

    /// <summary>F5: the time left follows the pace of the samples it is given, not an average; nothing is promised on too little data.</summary>
    [Fact]
    public void TimeLeftFollowsThePaceBetweenTwoSamples()
    {
        Assert.True(Engine.SecondsLeft(0, 0, 60, 60, 660) == 600, "1 request/s, 600 to go: 600 s");
        Assert.True(Engine.SecondsLeft(60, 60, 120, 66, 666) == 6000, "the last minute ran at 0.1 request/s: 6000 s, whatever the start was like");
        Assert.True(Engine.SecondsLeft(0, 5, 4, 9, 100) == null && Engine.SecondsLeft(0, 5, 30, 5, 100) == null && Engine.SecondsLeft(0, 0, 30, 100, 100) == null, "under 5 s, no progress, or finished: no estimate");
    }

    /// <summary>F6: a host lane that gave up on N requests in a row ends the run with a clear exception; what was saved is kept.</summary>
    [Fact]
    public async Task HostThatKeepsFailingEndsTheRunAfterTheBudget()
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok) { DeadHost = "apiv2.test" };
        var ex = await Assert.ThrowsAsync<HostSilentException>(() => Run(f, t, ["profile"], tune: e => (e.GiveUpLimit, e.BackoffMs) = (3, 5)));
        Assert.True(ex.Message.Contains("apiv2.test") && ex.Message.Contains('3'), "the message names the host and the count");
        Assert.True(f.Starts["apiv2.test"].Count == 5 + 2, $"one back-off cycle (5 attempts), then one attempt each until the 3rd give-up ({f.Starts["apiv2.test"].Count} attempts)");
        Assert.True(File.Exists(Path.Combine(t.Path, "backup-7", "manifest.json")) && Manifest(Path.Combine(t.Path, "backup-7"))["completed_utc"] == null, "the manifest of what was saved is written, the run is not finished");
    }

    /// <summary>F6: an answer between failures starts the count again, so a flaky host is not cut off.</summary>
    [Fact]
    public async Task AnswerResetsTheFailureCount()
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok);
        f.Down.Add("/game/user/7/profile");
        f.Down.Add("/game/user/7/ranks");
        var e = await Run(f, t, ["profile"], tune: e => (e.GiveUpLimit, e.BackoffMs) = (3, 0));
        Assert.True(e.Failed == 2, "two requests failed in a row, below the budget of 3, and the run went on");
    }

    /// <summary>F6: a 429 or 503 with Retry-After is waited out as the server asked (capped); other waits are the plain back-off.</summary>
    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RetryAfterIsHonouredAndCapped(int status)
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok) { Throttle = ("/v1/Skill/list", 1, status, 1) };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var e = await Run(f, t, ["profile"], tune: e => e.BackoffMs = 0);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900) && e.Failed == 0 && f.Seen.Count(p => p == "/v1/Skill/list") == 2, $"waited the second the server asked for, then the request went through ({clock.ElapsedMilliseconds} ms)");

        using var t2 = new TempDir("backup-test-v03-");
        var f2 = new Fake(Tok) { Throttle = ("/v1/Skill/list", 1, status, 3600) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // a cap that does not work would wait an hour
        var e2 = await Run(f2, t2, ["profile"], ct: cts.Token, tune: e => (e.BackoffMs, e.RetryAfterCapMs) = (0, 200));
        Assert.True(e2.Failed == 0 && f2.Seen.Count(p => p == "/v1/Skill/list") == 2, "an hour asked for, 200 ms waited (the cap)");
    }

    /// <summary>F7a: a JWT that was refused (401) is replaced and the request is made once more.</summary>
    [Fact]
    public async Task RefusedJwtIsReplacedAndTheRequestRepeatedOnce()
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok) { Throttle = ("/game/user/loadout", 1, 401, 0) };
        var e = await Run(f, t, ["inventory"]);
        Assert.True(e.Failed == 0 && f.Seen.Count(p => p == "/game/user/loadout") == 2 && f.Seen.Count(p => p == "/v1/User/jwt") == 2, $"loadout asked twice, a second JWT fetched ({f.Seen.Count(p => p == "/v1/User/jwt")}), 0 failed");
        Assert.True(File.Exists(Path.Combine(t.Path, "backup-7", "game_user_loadout.json")), "the repeated request was saved");

        using var t2 = new TempDir("backup-test-v03-");
        var f2 = new Fake(Tok) { Throttle = ("/game/user/loadout", 5, 401, 0) };
        var e2 = await Run(f2, t2, ["inventory"]);
        Assert.True(e2.Failed == 1 && f2.Seen.Count(p => p == "/game/user/loadout") == 2, "a JWT that is refused again is not tried forever: one retry, then failed");
    }

    /// <summary>F7b: the engine asks for the current token before each request that needs it. A new token replaces the refused one and clears the stale mark.</summary>
    [Fact]
    public async Task NewTokenFromTheGameIsUsedFromThenOn()
    {
        using var t = new TempDir("backup-test-v03-");
        var old = Tok;
        var fresh = new string('b', 40);
        var current = old;
        var f = new Fake(Tok);
        f.CancelAt = n => { if (n == 6) { f.Revoked = old; current = fresh; } return false; }; // from the 6th request on the old token is refused and the game has a new one
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "", () => current, new HashSet<string> { "account" }, false, false, TestContext.Current.CancellationToken);
        var auth = f.Forms.Where(x => x.Contains("oauth_access_token")).ToList();
        Assert.True(auth.Count(x => x.Contains(fresh)) >= 3 && !e.TokenStale, $"requests after the change carry the new token ({auth.Count(x => x.Contains(fresh))}) and the stale mark is cleared");
        Assert.True(e.Failed <= 2, $"only the fake's deliberate token leak and the one request that met the revoked token failed ({e.Failed}: {string.Join("; ", Manifest(Path.Combine(t.Path, "backup-7"))["files"]!.AsArray().Where(x => x!["error"] != null).Select(x => x!["path"] + " " + x["error"]))})");
    }

    static readonly byte[] Html = System.Text.Encoding.UTF8.GetBytes("<html><body>Sign in to the proxy</body></html>");

    /// <summary>F8: a reply that is not what the request asked for (a proxy page for JSON or a picture, no table in a sheet, cut-off JSON) is not saved and is counted as "not data".</summary>
    [Fact]
    public async Task RepliesThatAreNotDataAreNotSaved()
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok);
        f.Raw["/v1/Skill/list"] = Html; // an API reply that is a web page
        f.Raw["/v1/Statistics/total"] = "{\"cut\":"u8.ToArray(); // cut off
        f.Raw["/v1/Challenge/get_user_challenges"] = "null"u8.ToArray(); // a legal JSON value
        f.Raw["/a.jpg"] = Html; // a picture that is a web page
        f.Raw["/user/x/score/"] = "<html>login</html>"u8.ToArray(); // a sheet without its table
        f.HuntReplies["42"] = Kills((900, 1.5, "https://photo.test/a.jpg"));
        var e = await Run(f, t, ["profile", "kills"]);
        var dir = Path.Combine(t.Path, "backup-7");
        Assert.True(!File.Exists(Path.Combine(dir, "Skill_list.json")) && !File.Exists(Path.Combine(dir, "Statistics_total.json")) && !Directory.Exists(Path.Combine(dir, "kills")), "none of the four is saved");
        Assert.True(Directory.GetFiles(dir, "Challenge_get_user_challenges*.json").Length == 1, "null is JSON and is saved");
        var bad = Manifest(dir)["files"]!.AsArray().Where(x => x!["error"]?.ToString().Contains("not data") == true).Count();
        Assert.True(bad == 4 && e.FailureSummary.Contains("unexpected reply"), $"4 failed as not data ({bad}) and the summary says so ({e.FailureSummary})");
    }

    /// <summary>F8: pictures of the four known formats are saved, anything else is not.</summary>
    [Fact]
    public async Task PicturesNeedAnImageHeader()
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok);
        f.Raw["/p.jpg"] = [255, 216, 255, 224, 0];
        f.Raw["/p.png"] = [137, 80, 78, 71, 13, 10, 26, 10];
        f.Raw["/p.gif"] = "GIF89a"u8.ToArray();
        f.Raw["/p.webp"] = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        f.Raw["/p.bmp"] = "BM\0\0\0\0\0\0"u8.ToArray();
        f.HuntReplies["42"] = Kills((1, null, "https://photo.test/p.jpg"), (2, null, "https://photo.test/p.png"), (3, null, "https://photo.test/p.gif"), (4, null, "https://photo.test/p.webp"), (5, null, "https://photo.test/p.bmp"));
        var e = await Run(f, t, ["kills"]);
        Assert.True(Directory.GetFiles(Path.Combine(t.Path, "backup-7", "kills")).Length == 4 && e.Failed == 1, $"jpeg, png, gif and webp saved, bmp is not ({e.Failed} failed)");
    }

    /// <summary>F10: a picture or sheet that is 404 is "dead on the server": in the manifest with that error, counted on its own, no failure and no reason to run again; the resumed run does not ask for it again.</summary>
    [Fact]
    public async Task PermanentlyMissingPictureIsDeadNotFailed()
    {
        using var t = new TempDir("backup-test-v03-");
        var f = new Fake(Tok);
        f.Down.Add("/v1/Skill/list"); // a real failure beside it: the run stays unfinished and resume goes on
        f.HuntReplies["42"] = Kills((900, 1.5, "https://photo.test/gone.jpg"), (901, null, "https://photo.test/k.jpg"));
        f.NotFound.Add("/gone.jpg");
        f.NotFound.Add("/user/x/score/");
        var e = await Run(f, t, ["kills", "profile"]);
        var dir = Path.Combine(t.Path, "backup-7");
        Assert.True(e.Dead == 2 && e.Failed == 1, $"2 dead (picture and sheet), only the real failure counted ({e.Dead} dead, {e.Failed} failed)");
        var deadEntries = Manifest(dir)["files"]!.AsArray().Where(x => x!["error"]?.ToString() == "dead on the server (404)").ToList();
        Assert.True(deadEntries.Count == 2 && deadEntries.All(x => x!["status"]!.GetValue<int>() == 404) && e.FailureSummary.Contains("Profile"), "both in the manifest with status 404, the summary names the profile only");

        f.Down.Clear();
        f.Seen.Clear();
        var e2 = await Run(f, t, ["kills", "profile"]);
        Assert.True(!f.Seen.Contains("/gone.jpg") && !f.Seen.Contains("/user/x/score/") && e2.Dead == 2 && e2.Failed == 0, "the resumed run does not ask for them again, still reports 2 dead, and now has 0 failed");
    }
}
