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
        if (host == DeadHost) return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("bad gateway") };
        var body = path switch
        {
            "/v1/Me/me" => form.Contains(Token) ? """{"id":7,"handle":"hh","worlditems":[]}""" : """{"errorCode":11,"message":"access denied"}""",
            "/v1/Application/application" => """{"season":3}""",
            "/game/user/leaderboards/antlers/single/all" => """[{"id":5},{"id":6}]""",
            "/game/user/leaderboards/antlers/double/all" => """[{"id":9}]""",
            "/game/user/trophies" => """[{"id":101},{"id":102}]""",
            "/v1/Public_user/expedition" => """{"kills":[{"id":900,"kill":{"score":1.5,"photo":"https://photo.test/k.jpg"}},{"id":901,"kill":{"score":null}}]}""",
            "/v1/Public_user/getByHostname" => """{"id":7,"handle":"hh"}""",
            "/v1/User/jwt" => """{"token":"fake.jwt.value"}""",
            "/v1/Leaderboard/personal" => """{"status":"done","data":[]}""",
            "/game/user/7/lodges" => """[{"id":3}]""",
            "/v1/Expedition/list" => """{"total":1,"expeditions":[{"id":42}]}""",
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
    public static readonly HashSet<string> AllGroups = ["profile", "inventory", "trophies", "hunts", "account", "friends", "messages"];

    public static Engine Make(Fake f, string keyPath) => new(
        new("api.test", "apiv2.test", "web.test", "fakeclient.exe", ["photo.test"], "ckey-secret-9"), new HttpClient(f), _ => { }, (_, _) => { }, keyPath) { PaceMs = 0, BackoffMs = 0, SteamAccount = () => 100 };
}
