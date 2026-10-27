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
}
