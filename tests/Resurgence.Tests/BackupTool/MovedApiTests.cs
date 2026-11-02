using System.Net;
using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The API now lives on the web host: JSON bodies with real numbers, two reads with GET, errors inside "detail", avatars on the avatar host.</summary>
public class MovedApiTests
{
    static readonly string Tok = new('a', 40);

    static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task PublicReadsGoToTheWebHostAsJson()
    {
        using var t = new TempDir("backup-test-");
        var f = new Fake(Tok);
        await Hosts.Make(f, Path.Combine(t.Path, "claim.key")).Run(t.Path, "hh", null, new HashSet<string> { "profile", "trophies", "friends" }, false, false, TestContext.Current.CancellationToken);
        Assert.True(f.Starts.Keys.All(h => h is "web.test" or "apiv2.test"), $"only the web and apiv2 hosts are asked ({string.Join(",", f.Starts.Keys)})");
        Assert.True(f.Bodies.Count > 5 && f.Bodies.All(b => JsonNode.Parse(b) is JsonObject), "every POST body is a JSON object");
        Assert.Contains(f.Bodies, b => b.Contains("\"limit\":300") && b.Contains("\"user_id\":7"));
        Assert.DoesNotContain(f.Bodies, b => b.Contains("\"limit\":\"") || b.Contains("\"offset\":\""));
        Assert.Contains("/v1/Application/application?lang=en", f.Urls);
        Assert.Contains("/v1/Challenge/get_user_challenges?user_id=7", f.Urls);
        Assert.DoesNotContain(f.Urls, u => u.Contains("ckey-secret-9"));
    }

    [Fact]
    public async Task AnErrorInDetailIsNotSaved()
    {
        using var t = new TempDir("backup-test-");
        var f = new Fake(Tok) { Custom = (p, _) => p == "/v1/Trophy/list" ? Reply(HttpStatusCode.OK, """{"detail":{"errorCode":20,"message":"User not found"}}""") : null };
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "7", null, new HashSet<string> { "trophies" }, false, false, TestContext.Current.CancellationToken);
        Assert.True(e.Failed > 0 && !File.Exists(Path.Combine(t.Path, "backup-7", "Trophy_list__user_id=7__offset=0__limit=100000.json")), "the error reply is a failed request, not a file");
    }

    [Fact]
    public async Task A401OnTheSessionMarksItStale()
    {
        using var t = new TempDir("backup-test-");
        var f = new Fake(Tok) { Custom = (p, _) => p == "/v1/Me/me" ? Reply(HttpStatusCode.Unauthorized, """{"detail":{"message":"OAUTH_ACCESS_TOKEN_INVALID"}}""") : null };
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Run(t.Path, "", () => Tok, new HashSet<string> { "account" }, false, false, TestContext.Current.CancellationToken));
        Assert.True(e.TokenStale, "the session is refused: the UI says to start a hunt");
    }

    [Fact]
    public async Task TheAvatarIsFetchedFromTheAvatarHost()
    {
        using var t = new TempDir("backup-test-");
        var f = new Fake(Tok) { Custom = (p, _) => p == "/v1/Public_user/getByHostname" ? Reply(HttpStatusCode.OK, """{"id":7,"handle":"hh","avatar":"https://web.test/media/avatar/uploads/a.jpg"}""") : null };
        await Hosts.Make(f, Path.Combine(t.Path, "claim.key")).Run(t.Path, "hh", null, new HashSet<string> { "profile" }, false, false, TestContext.Current.CancellationToken);
        Assert.True(f.Starts.ContainsKey("avatar.test") && f.Seen.Contains("/uploads/a.jpg") && !f.Seen.Contains("/media/avatar/uploads/a.jpg"), "the picture is asked on the avatar host");
        Assert.True(File.Exists(Path.Combine(t.Path, "backup-7", "images", "avatar.test_uploads_a.jpg")), "saved under the avatar host's name");
    }

    static HttpResponseMessage? Pages(string p, string form, int total, int failFrom = -1)
    {
        if (p != "/v1/Trophy/list") return null;
        var off = int.Parse(System.Text.RegularExpressions.Regex.Match(form, @"offset=(\d+)").Groups[1].Value);
        if (failFrom >= 0 && off >= failFrom) return Reply(HttpStatusCode.OK, """{"detail":{"errorCode":20}}""");
        var rows = string.Join(',', Enumerable.Range(off, Math.Max(0, Math.Min(300, total - off))).Select(i => $$"""{"id":{{i}}}"""));
        return Reply(HttpStatusCode.OK, $$"""{"trophies":[{{rows}}],"total":{{total}}}""");
    }

    [Fact]
    public async Task TrophyPagesMergeIntoOneFile()
    {
        using var t = new TempDir("backup-test-");
        var f = new Fake(Tok) { Custom = (p, form) => Pages(p, form, 650) };
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "7", null, new HashSet<string> { "trophies" }, false, false, TestContext.Current.CancellationToken);
        var dir = Path.Combine(t.Path, "backup-7");
        var file = "Trophy_list__user_id=7__offset=0__limit=100000.json";
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, file)))!;
        Assert.True(j["total"]!.GetValue<int>() == 650 && j["trophies"]!.AsArray().Select(x => x!["id"]!.GetValue<int>()).SequenceEqual(Enumerable.Range(0, 650)), "3 pages = one reply of 650 rows in order");
        Assert.True(f.Forms.Count(x => x.Contains("limit=300")) == 3 && Directory.GetFiles(dir, "Trophy_list*").Length == 1, "three page requests, one file");
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
        Assert.True(e.Failed == 0 && m.Single(x => x!["path"]!.ToString() == file)!["params"]!["limit"]!.ToString() == "100000", "manifest keeps the old params");
    }

    [Fact]
    public async Task ATrophyPageThatFailsLeavesNoFile()
    {
        using var t = new TempDir("backup-test-");
        var f = new Fake(Tok) { Custom = (p, form) => Pages(p, form, 650, 300) };
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "7", null, new HashSet<string> { "trophies" }, false, false, TestContext.Current.CancellationToken);
        Assert.True(e.Failed > 0 && !File.Exists(Path.Combine(t.Path, "backup-7", "Trophy_list__user_id=7__offset=0__limit=100000.json")), "no half list is saved as if whole");
    }

    [Fact]
    public async Task MyAccountAsksForTheSessionBeforeThePublicGroups()
    {
        using var t = new TempDir("backup-test-");
        var f = new Fake(Tok);
        await Hosts.Make(f, Path.Combine(t.Path, "claim.key")).Run(t.Path, "", () => Tok, new HashSet<string> { "profile", "hunts", "account", "trophies", "friends" }, false, false, TestContext.Current.CancellationToken);
        var session = f.Seen.Select((p, i) => (p, i)).Where(x => x.p is "/v1/Mission/missions" or "/v1/Me/friends" or "/v1/Wallet/bank_transactions" or "/v1/game/user/trophies").Select(x => x.i).ToList();
        var pub = f.Seen.Select((p, i) => (p, i)).Where(x => x.p is "/v1/Statistics/total" or "/v1/Expedition/list" or "/v1/Skill/list").Select(x => x.i).ToList();
        Assert.True(session.Count == 4 && pub.Count >= 3 && session.Max() < pub.Min(), $"session calls end at {session.Max()}, public groups start at {pub.Min()}");
    }

    [Fact]
    public async Task ABoardThatStaysQueuedIsAskedThreeTimesAndTheGroupGoesOn()
    {
        using var t = new TempDir("backup-test-");
        var f = new Fake(Tok) { Custom = (p, _) => p == "/v1/Leaderboard/personal" ? Reply(HttpStatusCode.OK, """{"status":"queued"}""") : null };
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "7", null, new HashSet<string> { "profile" }, false, false, TestContext.Current.CancellationToken);
        Assert.True(f.Seen.Count(p => p == "/v1/Leaderboard/personal") == 12 && e.Failed == 4, $"4 boards (all time and 3 seasons) x 3 asks ({f.Seen.Count(p => p == "/v1/Leaderboard/personal")}), each failed once ({e.Failed})");
        Assert.True(File.Exists(Path.Combine(t.Path, "backup-7", "Statistics_total__user_id=7.json")) && File.Exists(Path.Combine(t.Path, "backup-7", "Membership_Bundle_get_active.json")), "the rest of the group was saved");
    }
}
