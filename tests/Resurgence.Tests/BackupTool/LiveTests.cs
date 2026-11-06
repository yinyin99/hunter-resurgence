using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The real engine against the official servers, for a public profile found at run time (no handle or id in this file).
/// Off unless BACKUP_LIVE_SETTINGS names a filled settings.json: the other tests never touch the network.
/// A server that does not answer skips (an outage is not the tool's fault); a server that answers but whose reply the tool cannot use fails: the tool needs attention.</summary>
public class LiveTests
{
    /// <summary>Errors that are the server's normal answers, not the tool's problem (docs/backup.md): a board still being built, a picture gone for good.</summary>
    static readonly string[] Normal = ["still building", "dead on the server (404)"];

    [Fact]
    public async Task APublicBackupOfALiveProfileIsWhole()
    {
        var path = Environment.GetEnvironmentVariable("BACKUP_LIVE_SETTINGS");
        if (string.IsNullOrEmpty(path)) Assert.Skip("BACKUP_LIVE_SETTINGS is not set: no live check");
        var s = Settings.Parse(File.ReadAllText(path));
        using var all = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        all.CancelAfter(TimeSpan.FromMinutes(25)); // a host that hangs is an outage, and the job must end with a verdict
        var ct = all.Token;
        try { await Check(s, ct); }
        catch (OperationCanceledException) when (all.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Skip("the run did not finish in 25 minutes: a server outage, not a tool problem");
        }
    }

    static async Task Check(Settings s, CancellationToken ct)
    {
        using var http = new HttpClient();
        var reach = await Connection.Run(s, http, ct: ct);
        if (reach.Official != "ok") Assert.Skip($"the official servers are {reach.Official}: a server outage, not a tool problem");

        // any player on a public board will do; the board is one of the reads the tool makes
        HttpResponseMessage board;
        try { board = await http.GetAsync($"https://{s.WebHost}/v1/game/user/leaderboards/antlers/single/all", ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { Assert.Skip("the antler board did not answer: a server outage, not a tool problem"); return; }
        using (board)
        {
            if ((int)board.StatusCode >= 500 || (int)board.StatusCode == 429) Assert.Skip($"the antler board answered {(int)board.StatusCode}: a server outage, not a tool problem");
            Assert.True(board.IsSuccessStatusCode, $"antler board answered {(int)board.StatusCode}: the v2 API moved again?");
            var handle = (JsonNode.Parse(await board.Content.ReadAsStringAsync(ct)) as JsonArray)?
                .SelectMany(b => b?["records"] as JsonArray ?? []).Select(r => r?["user"]?["handle"]?.ToString())
                .FirstOrDefault(h => !string.IsNullOrEmpty(h) && !h.All(char.IsAsciiDigit)); // all digits would be read as an id
            Assert.True(handle != null, "no player on the antler board: its reply changed shape");
            await Backup(s, handle, ct);
        }
    }

    static async Task Backup(Settings s, string handle, CancellationToken ct)
    {
        using var t = new TempDir("backup-live-");
        var log = new List<string>();
        var e = new Engine(s, new HttpClient(), l => { lock (log) log.Add(l); }, (_, _) => { }, Path.Combine(t.Path, "claim.key")) { GiveUpLimit = 5 };
        try { await e.Run(t.Path, handle, null, new HashSet<string> { "profile", "inventory", "trophies", "friends", "setups" }, false, false, ct); }
        catch (InvalidOperationException) when (Directory.GetDirectories(t.Path, "backup-*").Length == 0 && log.Any(GaveUpForNoAnswer))
        {
            // the folder is made right after the lookup, so none means the lookup failed; a refusal (403, 407) or any other lookup failure is the tool's and propagates
            Assert.Skip("the account lookup got no answer after its retries: a server outage, not a tool problem");
        }

        var dir = Directory.GetDirectories(t.Path, "backup-*").Single();
        var uid = Path.GetFileName(dir)["backup-".Length..];
        var files = (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"] as JsonArray)!;
        var errors = files.Where(f => f?["error"] is { } err && !Normal.Contains(err.ToString())).Select(f => f!).ToList();
        // a reply of another shape, a repeated page, a refusal (403, 407): the tool's to look at, so they fail. Two errors also follow an outage
        // (a trophy page or an inventory read that got no answer): they count as an outage only when the run had another one, else they mean the reply changed
        var outage = errors.Any(f => NoAnswer(f["error"]!.ToString()));
        bool Excused(JsonNode f) => f["error"]!.ToString() is var m && (NoAnswer(m) || outage && FollowOn.Contains(m));
        // paths and errors only: no host, no id in the output
        var failed = errors.Where(f => !Excused(f)).Select(f => $"{Local(f["endpoint"]!.ToString()).Replace(uid, "<uid>")}: {f["error"]} ({f["status"]})").ToList();
        Assert.True(failed.Count == 0, $"{failed.Count} request(s) failed while the server answered:\n{string.Join("\n", failed.Take(30))}");
        if (errors.Count > 0 || e.SilentHosts.Length > 0) Assert.Skip($"{errors.Count} request(s) got no usable answer and {e.SilentHosts.Length} host(s) went silent: a server outage, not a tool problem");
        foreach (var name in new[] { $"game_user_{uid}_profile.json", $"game_user_{uid}_ranks.json", $"Inventory_dict__user_id={uid}.json", $"Inventory_list__user_id={uid}.json", "map-setups.json" })
            Assert.True(File.Exists(Path.Combine(dir, name)), $"{name.Replace(uid, "<uid>")} was not saved");
    }

    static string Local(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.PathAndQuery : endpoint;

    /// <summary>What the engine writes when a server stopped answering: the last try's HTTP 5xx or 429, the exception, or a host already given up on.</summary>
    static bool NoAnswer(string err) => err == "host not answering, not tried" || NoAnswerRe.IsMatch(err);
    static readonly System.Text.RegularExpressions.Regex NoAnswerRe = new(@"^(HTTP (5\d\d|429)|(HttpRequest|TaskCanceled|IO)Exception: )");
    /// <summary>The lookup's give-up line in the engine's log ("! giving up on path: err", the only kind the lookup, the first request, can write) whose err is no answer.</summary>
    static bool GaveUpForNoAnswer(string line) => line.StartsWith("! giving up on ") && NoAnswer(line[(line.IndexOf(": ", 15) + 2)..]);

    /// <summary>Errors the engine writes after a read that got no answer; alone they mean the reply's shape changed.</summary>
    static readonly string[] FollowOn = ["a page of the list gave no trophies", "no inventory reply to build it from"];
}
