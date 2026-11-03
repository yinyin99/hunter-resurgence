using System.Net;
using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>Paged lists against a server that ignores the offset and answers the same full page again: the run must end, ask for nothing twice, and not call the list whole.</summary>
public class PagingTests
{
    static readonly string Tok = new('a', 40);

    static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    static string Rows(int first, int count) => string.Join(',', Enumerable.Range(first, count).Select(i => $$"""{"id":{{i}}}"""));

    static int Offset(string form) => int.Parse(System.Text.RegularExpressions.Regex.Match(form, @"offset=(\d+)").Groups[1].Value);

    /// <summary>Runs the groups and fails the test when more than 200 requests are asked: a loop that never ends is cut by the cancel, not by a timeout.</summary>
    static Task<(Engine E, string Dir)> Run(Fake f, TempDir t, bool own, params string[] groups) => Run(f, t, own, false, groups);

    static async Task<(Engine E, string Dir)> Run(Fake f, TempDir t, bool own, bool resume, params string[] groups)
    {
        using var cts = new CancellationTokenSource();
        var asked = 0;
        Func<string, string, HttpResponseMessage?> custom = f.Custom ?? ((_, _) => null);
        f.Custom = (p, form) => { if (Interlocked.Increment(ref asked) > 200) cts.Cancel(); return custom(p, form); };
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        try { await e.Run(t.Path, own ? "" : "7", own ? () => Tok : null, new HashSet<string>(groups), resume, false, cts.Token); }
        catch (OperationCanceledException) { Assert.Fail("the paging never ended"); }
        return (e, Path.Combine(t.Path, "backup-7"));
    }

    const string TrophyFile = "Trophy_list__user_id=7__offset=0__limit=100000.json";

    [Theory]
    [InlineData(600, true, 2)] // an exact multiple of the page size: two pages, no third
    [InlineData(600, false, 3)] // no total: the empty third page ends the list
    [InlineData(300, true, 1)]
    public async Task TrophyListOfAnExactMultipleIsSavedWhole(int count, bool withTotal, int pages)
    {
        using var t = new TempDir("backup-test-paging-");
        var f = new Fake(Tok) { Custom = (p, form) => p != "/v1/Trophy/list" ? null : Reply($$"""{"trophies":[{{Rows(Offset(form), Math.Max(0, Math.Min(300, count - Offset(form))))}}]{{(withTotal ? $",\"total\":{count}" : "")}}}""") };
        var (e, dir) = await Run(f, t, false, "trophies");
        var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, TrophyFile)))!["trophies"]!.AsArray().Select(x => x!["id"]!.GetValue<int>());
        Assert.True(e.Failed == 0 && rows.SequenceEqual(Enumerable.Range(0, count)), $"{count} rows once each, in order, nothing failed ({e.Failed})");
        Assert.Equal(pages, f.Forms.Count(x => x.Contains("limit=300")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrophyListThatIgnoresTheOffsetIsNotSavedAsWhole(bool withTotal)
    {
        using var t = new TempDir("backup-test-paging-");
        var f = new Fake(Tok) { Custom = (p, _) => p != "/v1/Trophy/list" ? null : Reply($$"""{"trophies":[{{Rows(0, 300)}}]{{(withTotal ? ",\"total\":650" : "")}}}""") };
        var (e, dir) = await Run(f, t, false, "trophies");
        Assert.True(e.Failed == 1 && !e.Whole && !File.Exists(Path.Combine(dir, TrophyFile)), "300 of an unknown number of trophies is a failed request, not a list");
    }

    [Fact]
    public async Task GalleryThatIgnoresTheOffsetSavesEachPhotoOnce()
    {
        using var t = new TempDir("backup-test-paging-");
        var photos = string.Join(',', Enumerable.Range(0, 24).Select(i => $$"""{"url":"https://photo.test/p{{i}}.jpg"}"""));
        var f = new Fake(Tok) { Custom = (p, _) => p == "/v1/Gallery/list" ? Reply($$"""{"photos":[{{photos}}]}""") : null };
        var (e, dir) = await Run(f, t, false, "photos");
        Assert.True(Enumerable.Range(0, 24).All(i => f.Seen.Count(p => p == $"/p{i}.jpg") == 1) && e.Failed == 1 && !e.Whole, $"24 photos, each asked once, and the repeated page is reported ({e.Failed} failed)");

        // the repeated page is a failed request in the manifest, not a file: a later run (here with a server that pages properly) asks for it again and finishes
        const string repeated = "Gallery_list__user_id=7__offset=24__limit=24.json";
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Single(x => x!["path"]!.ToString() == repeated)!;
        Assert.True(entry["error"] != null && entry["sha256"] == null && !File.Exists(Path.Combine(dir, repeated)), "listed as failed, no file");
        var fixedServer = new Fake(Tok) { Custom = (p, form) => p == "/v1/Gallery/list" ? Reply(Offset(form) == 0 ? $$"""{"photos":[{{photos}}]}""" : """{"photos":[{"url":"https://photo.test/p24.jpg"}]}""") : null };
        var (again, _) = await Run(fixedServer, t, false, true, "photos");
        Assert.True(fixedServer.Forms.Any(x => x.Contains("offset=24")) && File.Exists(Path.Combine(dir, "photos", "photo.test_p24.jpg")) && again.Failed == 0 && again.Whole, "the page is asked again, the backup finishes");
    }

    [Fact]
    public async Task HuntListThatIgnoresTheOffsetAsksEachHuntOnce()
    {
        using var t = new TempDir("backup-test-paging-");
        var list = $$"""{"total":51,"expeditions":[{{string.Join(',', Enumerable.Range(1, 50).Select(i => $$"""{"id":{{i}},"start_ts":{{i}}}"""))}}]}""";
        var f = new Fake(Tok) { ExpeditionList = list };
        var (e, dir) = await Run(f, t, false, "hunts");
        Assert.True(f.Seen.Count(p => p == "/v1/Public_user/expedition") == 50 && e.HuntsListFailed && e.Failed == 1 && !e.Whole, "50 hunts asked once each, and the cut list is flagged");
    }

    [Fact]
    public async Task WalletThatIgnoresTheOffsetStopsAtTheRepeat()
    {
        using var t = new TempDir("backup-test-paging-");
        var rows = $"[{Rows(0, 50)}]";
        var f = new Fake(Tok) { Custom = (p, _) => p switch { "/v1/Wallet/transactions" => Reply(rows), "/v1/Mission/missions" => Reply("[]"), _ => null } }; // the fake's default missions reply carries the token
        var (e, dir) = await Run(f, t, true, "account");
        Assert.True(f.Seen.Count(p => p == "/v1/Wallet/transactions") == 2 && e.Failed == 1 && !e.Whole, "the first page and the repeat, then stop, reported");
    }

    [Fact]
    public async Task WalletPagesOf50And50And7AreAllAsked()
    {
        using var t = new TempDir("backup-test-paging-");
        var f = new Fake(Tok) { Custom = (p, form) => p switch { "/v1/Wallet/transactions" => Reply($"[{Rows(Offset(form), Math.Max(0, Math.Min(50, 107 - Offset(form))))}]"), "/v1/Mission/missions" => Reply("[]"), _ => null } };
        var (e, dir) = await Run(f, t, true, "account");
        var offsets = f.Forms.Zip(f.Seen).Where(x => x.Second == "/v1/Wallet/transactions").Select(x => Offset(x.First));
        Assert.Equal([0, 50, 100], offsets);
        Assert.True(Directory.GetFiles(dir, "Wallet_transactions__*").Length == 3 && e.Failed == 0, $"three files, nothing failed ({e.Failed})");
    }

    [Fact]
    public async Task GalleryPagesOf24And24And5AreAllSaved()
    {
        using var t = new TempDir("backup-test-paging-");
        static string Page(int off) => $$"""{"photos":[{{string.Join(',', Enumerable.Range(off, Math.Max(0, Math.Min(24, 53 - off))).Select(i => $$"""{"url":"https://photo.test/p{{i}}.jpg"}"""))}}]}""";
        var f = new Fake(Tok) { Custom = (p, form) => p == "/v1/Gallery/list" ? Reply(Page(Offset(form))) : null };
        var (e, dir) = await Run(f, t, false, "photos");
        Assert.True(Enumerable.Range(0, 53).All(i => f.Seen.Count(p => p == $"/p{i}.jpg") == 1) && Directory.GetFiles(Path.Combine(dir, "photos")).Length == 53 && e.Failed == 0 && e.Whole, "53 pictures, each once, the backup is whole");
    }
}
