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
        await e.Run(t.Path, "", Tok, new HashSet<string>(groups), resume, false, ct ?? TestContext.Current.CancellationToken);
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
}
