using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The real engine against the live official servers, for a public profile found at run time (no handle or id in this file).
/// Off unless BACKUP_LIVE_SETTINGS names a filled settings.json: the other tests never touch the network. A server that does not answer skips
/// (an outage is not the tool's fault); a server that answers but whose reply the tool cannot use fails: the tool needs attention.</summary>
public class LiveTests
{
    [Fact]
    public async Task APublicBackupOfALiveProfileIsWhole()
    {
        var path = Environment.GetEnvironmentVariable("BACKUP_LIVE_SETTINGS");
        if (string.IsNullOrEmpty(path)) Assert.Skip("BACKUP_LIVE_SETTINGS is not set: no live check");
        var s = Settings.Parse(File.ReadAllText(path));
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();

        var reach = await Connection.Run(s, http, ct: ct);
        if (reach.Official != "ok") Assert.Skip($"the official servers are {reach.Official}: a server outage, not a tool problem");

        // any player on a public board will do; the board is one of the reads the tool makes
        using var board = await http.GetAsync($"https://{s.WebHost}/v1/game/user/leaderboards/antlers/single/all", ct);
        Assert.True(board.IsSuccessStatusCode, $"antler board answered {(int)board.StatusCode}: the v2 API moved again?");
        var handle = (JsonNode.Parse(await board.Content.ReadAsStringAsync(ct)) as JsonArray)?
            .SelectMany(b => b?["records"] as JsonArray ?? []).Select(r => r?["user"]?["handle"]?.ToString()).FirstOrDefault(h => !string.IsNullOrEmpty(h));
        Assert.True(handle != null, "no player on the antler board: its reply changed shape");

        using var t = new TempDir("backup-live-");
        var e = new Engine(s, new HttpClient(), _ => { }, (_, _) => { }, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, handle, null, new HashSet<string> { "profile", "inventory", "trophies", "friends", "setups" }, false, false, ct);
        if (e.SilentHosts.Length > 0) Assert.Skip($"{e.SilentHosts.Length} host(s) stopped answering during the run: a server outage, not a tool problem");

        var dir = Directory.GetDirectories(t.Path, "backup-*").Single();
        var files = (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"] as JsonArray)!;
        // "still building" is the server asking to wait (docs/backup.md); anything else failed is the tool's to look at. Paths only: no host in the output.
        var failed = files.Where(f => f?["error"] is { } err && err.ToString() != "still building")
            .Select(f => $"{Local(f!["endpoint"]!.ToString())}: {f["error"]}").ToList();
        Assert.True(failed.Count == 0, $"{failed.Count} request(s) failed while the server answered:\n{string.Join("\n", failed.Take(30))}");
        var uid = Path.GetFileName(dir)["backup-".Length..];
        foreach (var name in new[] { $"game_user_{uid}_profile.json", $"game_user_{uid}_ranks.json", $"Inventory_dict__user_id={uid}.json", $"Inventory_list__user_id={uid}.json", "map-setups.json" })
            Assert.True(File.Exists(Path.Combine(dir, name)), $"{name.Replace(uid, "<uid>")} was not saved");
    }

    static string Local(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.PathAndQuery : endpoint;
}
