using System.Net;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The fake HTTP host every engine test runs on: no live host is contacted.</summary>
public sealed class Fake : HttpMessageHandler
{
    public readonly List<string> Seen = [];
    public readonly string Token;
    public int DelayMs;
    /// <summary>This host always answers 502.</summary>
    public string? DeadHost;
    /// <summary>Called under the lock when request number n (1-based) starts; true = cancel the run right there.</summary>
    public Func<int, bool>? CancelAt;
    public CancellationTokenSource? Cts;
    public int StartedAtCancel = -1;
    public readonly Dictionary<string, int> Active = [], MaxActive = [];
    public readonly Dictionary<string, List<DateTime>> Starts = [];
    public bool Overlap; // two different hosts had a request in flight at the same time
    /// <summary>Replies of the map-setup reads; null keeps the plain default. WorldItems is the placed-item list of Me/me.</summary>
    public string? InventoryDict, InventoryList, WorldItems, Catalogue, ExpeditionList;
    /// <summary>Replies of Public_user/expedition by expedition id; the others get the plain default.</summary>
    public readonly Dictionary<string, string> HuntReplies = [];
    /// <summary>Path and query of every request, in the order they started.</summary>
    public readonly List<string> Urls = [];
    /// <summary>Paths that always answer 502.</summary>
    public readonly HashSet<string> Down = [];
    /// <summary>Requests whose form carried the session token.</summary>
    public int WithToken;
    /// <summary>The first Times requests of Path answer Status with a Retry-After header of After seconds.</summary>
    public (string Path, int Times, int Status, int After)? Throttle;
    int throttled;
    readonly object gate = new();
    public Fake(string token) => Token = token;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath;
        var host = req.RequestUri.Host;
        var form = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
        lock (gate)
        {
            ct.ThrowIfCancellationRequested(); // a real handler refuses a cancelled request too
            Seen.Add(path);
            Urls.Add(req.RequestUri.PathAndQuery);
            if (form.Contains(Token)) WithToken++;
            Starts.TryAdd(host, []);
            Starts[host].Add(DateTime.UtcNow);
            Active[host] = Active.GetValueOrDefault(host) + 1;
            MaxActive[host] = Math.Max(MaxActive.GetValueOrDefault(host), Active[host]);
            if (Active.Count(a => a.Value > 0) > 1) Overlap = true;
            if (CancelAt?.Invoke(Seen.Count) == true) { Cts!.Cancel(); StartedAtCancel = Seen.Count; }
        }
        try { if (DelayMs > 0) await Task.Delay(DelayMs, CancellationToken.None); }
        finally { lock (gate) Active[host]--; }
        if (path.Contains("/personal/") || path == "/game/user/harvest/102") return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("null") };
        if (Throttle is { } th && path == th.Path && Interlocked.Increment(ref throttled) <= th.Times)
        {
            var r = new HttpResponseMessage((HttpStatusCode)th.Status) { Content = new StringContent("slow down") };
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(th.After));
            return r;
        }
        if (host == DeadHost || Down.Contains(path)) return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("bad gateway") };
        var body = path switch
        {
            "/v1/Me/me" => form.Contains(Token) ? $$"""{"id":7,"handle":"hh","worlditems":{{WorldItems ?? "[]"}}}""" : """{"errorCode":11,"message":"access denied"}""",
            "/v1/Application/application" => Catalogue ?? """{"season":3}""",
            "/v1/Inventory/dict" when InventoryDict != null => InventoryDict,
            "/v1/Inventory/list" when InventoryList != null => InventoryList,
            "/game/user/leaderboards/antlers/single/all" => """[{"id":5},{"id":6}]""",
            "/game/user/leaderboards/antlers/double/all" => """[{"id":9}]""",
            "/game/user/trophies" => """[{"id":101},{"id":102}]""",
            "/v1/Public_user/expedition" when System.Text.RegularExpressions.Regex.Match(form, @"expedition_id=(\d+)") is { Success: true } m && HuntReplies.TryGetValue(m.Groups[1].Value, out var hunt) => hunt,
            "/v1/Public_user/expedition" => """{"kills":[{"id":900,"kill":{"score":1.5,"photo":"https://photo.test/k.jpg"}},{"id":901,"kill":{"score":null}}]}""",
            "/v1/Public_user/getByHostname" => """{"id":7,"handle":"hh"}""",
            "/v1/User/jwt" => """{"token":"fake.jwt.value"}""",
            "/v1/Leaderboard/personal" => """{"status":"done","data":[]}""",
            "/game/user/7/lodges" => """[{"id":3}]""",
            "/v1/Expedition/list" => ExpeditionList ?? """{"total":1,"expeditions":[{"id":42}]}""",
            "/v1/Gallery/list" => """{"photos":[{"url":"https://photo.test/g1.jpg"},{"url":"https://other.test/g2.jpg"}]}""",
            "/v1/Mission/missions" => $$"""{"leak":"{{Token}}"}""", // a reply carrying the token must never be saved
            "/v1/Wallet/transactions" or "/v1/Wallet/silver_transactions" => "[]",
            _ => """{"ok":1}""",
        };
        return new HttpResponseMessage { Content = new StringContent(body) };
    }
}

static class Hosts
{
    /// <summary>Takes the "finished" mark off a backup folder: what a crash or a cancel after the last request would leave. A new run then continues it.</summary>
    public static void Unfinish(string dir)
    {
        var path = System.IO.Path.Combine(dir, "manifest.json");
        var m = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        m.Remove("completed_utc");
        File.WriteAllText(path, m.ToJsonString());
    }

    public static readonly HashSet<string> AllGroups = ["profile", "inventory", "trophies", "hunts", "account", "friends", "messages"];

    public static Engine Make(Fake f, string keyPath) => new(
        new("api.test", "apiv2.test", "web.test", "fakeclient.exe", ["photo.test"], "ckey-secret-9"), new HttpClient(f), _ => { }, (_, _) => { }, keyPath) { PaceMs = 0, BackoffMs = 0, SteamAccount = () => 100 };
}
