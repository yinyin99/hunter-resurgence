// Entry point: serve (default), admin, coverage.
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Resurgence.Server;

const string AdminUsage = """
    Server-side admin: give the player items or currency (state.db in the data folder).

    Usage (repo root): dotnet run --project server/Server -- admin item <item_id> [count] | admin em|gm <amount>
                       dotnet run --project server/Server -- admin spawn <reserve> <animal> <males> [females] [x z radius]
    A running hunt receives them with its next heartbeat (update 10 / update 15); otherwise the next connect carries them.
    Negative amounts take currency away. With the server running, the same is in its admin API (http://localhost/swagger).
    spawn adds a group to spawn.json in the data folder: the next hunt (connect) has it; x/z default to the start.
    """;

Scoring.Load(Replay.LoadCfg(), Replay.Paths.Root);
var cmd = args.Length > 0 ? args[0] : "serve";
switch (cmd)
{
    case "serve": await Serve(); break;
    case "admin": Admin(args[1..]); break;
    case "coverage": await Coverage(); break;
    default: Die($"unknown command {cmd}: serve | admin | coverage"); break;
}
return 0;

static void Die(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
}

static Replay.HarTable Table(out List<string> hars)
{
    hars = Directory.Exists(Replay.Paths.Recordings)
        ? Directory.GetFiles(Replay.Paths.Recordings, "*" + Recordings.Ext).OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal).ToList()
        : [];
    return Replay.BuildTable(hars);
}

static void Admin(string[] a)
{
    if (a.Length > 0 && a[0] == "spawn")
    {
        var n = a.Skip(3).Select(s => int.TryParse(s, out var v) ? (int?)v : null).ToArray();
        int reserve = 0;
        if (a.Length < 4 || !int.TryParse(a[1], out reserve) || n.Any(v => v is null) || n.Length is 3 or 4 or > 5) Die(AdminUsage);
        var spawner = new Spawner(Path.Combine(Replay.Paths.Work, "spawn.json"));
        var req = new Spawner.Request(reserve, a[2], n[0], n.Length > 1 ? n[1] : 0, n.Length > 2 ? n[2] : null, n.Length > 3 ? n[3] : null,
                                      n.Length > 4 ? n[4] : null, null, null, null, null);
        var errors = Spawner.Validate(req);
        if (errors.Count > 0) Die(string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        Console.WriteLine($"spawn entry {spawner.Add(req).Id} added: the next hunt has it");
        return;
    }
    if (a.Length is not (2 or 3) || a[0] is not ("item" or "em" or "gm") || (a[0] != "item" && a.Length == 3)) Die(AdminUsage);
    long itemId = 0, count = 1;
    double amount = 0;
    if (a[0] == "item"
            ? !long.TryParse(a[1], out itemId) || (a.Length == 3 && !long.TryParse(a[2], out count))
            : !double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
        Die(AdminUsage);
    Directory.CreateDirectory(Replay.Paths.Work);
    var state = Replay.LoadState(Replay.LoadCfg(), Table(out _), Replay.Paths.Work);
    if (state == null) { Die("no recorded connect: nothing to seed the player state from"); return; }
    if (a[0] == "item")
    {
        if (count is < 1 or > AdminApi.MaxStacks || !state.GrantItems(itemId, count))
            Die($"item {itemId}: unknown or bad count (1 to {AdminApi.MaxStacks})");
        Console.WriteLine($"granted {count} x {Py.Str(state.ItemInfo(itemId)!["name"])} ({itemId})");
    }
    else
    {
        if (!AdminApi.ValidAmount(amount)) Die($"{a[1]}: not a number from -2^53 to 2^53");
        var balance = state.Give(a[0], amount);
        if (balance == null) Die($"{a[0]} unchanged: balance would drop below 0");
        Console.WriteLine($"{a[0]} balance: {Py.Str(balance)}");
    }
}

// Replay every recorded request on a redirected host through the server (player state off) and list what it can't
// answer faithfully. Hosts are printed as their settings key (api, static, ...), never by name.
static async Task Coverage()
{
    var cfg = Replay.LoadCfg();
    var table = Table(out var hars);
    await using var server = await Loopback.StartAsync(new Replay.Game(table, cfg, null, Replay.Paths.Mirror));
    var hosts = Replay.RedirectedHosts(cfg).ToHashSet();
    hosts.ExceptWith(Replay.SinkHosts(cfg));
    var roles = Replay.HostKeys.ToDictionary(k => ((string)cfg[k]!).ToLowerInvariant(), k => k[..^"_host".Length]);
    roles[((string)cfg["web_host"]!).ToLowerInvariant().Replace("www.", "")] = "web";
    var sources = new OrderedDictionary<string, int>();
    var unanswered = new OrderedDictionary<(string, string, string, long), int>();
    var total = new OrderedDictionary<(string, string, string), int>();
    var differs = new OrderedDictionary<(string, string, string), int>();
    static void Bump<K>(OrderedDictionary<K, int> d, K k) where K : notnull => d[k] = d.GetValueOrDefault(k) + 1;
    foreach (var p in hars)
        foreach (var e in (JsonArray)JsonNode.Parse(File.ReadAllText(p))!["log"]!["entries"]!)
        {
            var req = e!["request"]!;
            var resp = e["response"]!;
            var (host, path, query) = Replay.SplitUrl((string)req["url"]!);
            if (!hosts.Contains(host)) continue;
            var method = ((string)req["method"]!).ToUpperInvariant();
            var post = req["postData"] as JsonObject;
            var r = await server.SendAsync(method, host, query.Length > 0 ? $"{path}?{query}" : path,
                [("Content-Type", (string?)post?["mimeType"] ?? "")], System.Text.Encoding.UTF8.GetBytes((string?)post?["text"] ?? ""));
            Bump(sources, r.Source);
            var status = Py.L(resp["status"]);
            var role = roles.GetValueOrDefault(host, "other");
            if (r.Source is "weak" or "miss") Bump(unanswered, (method, role, path, status));
            else if (status == 200)
            {
                Bump(total, (method, role, path));
                if (!r.Body.AsSpan().SequenceEqual(Replay.ResponseBody(resp["content"]))) Bump(differs, (method, role, path));
            }
        }
    Console.WriteLine("answers:");
    foreach (var (source, n) in sources.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {source} {n}");
    Console.WriteLine("unanswered (weak/miss):");
    foreach (var (key, n) in unanswered.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"{n,5} {key.Item1} {key.Item2}{key.Item3} [recorded {key.Item4}]");
    Console.WriteLine("replies that differ from the recording (volatile data or wrong variant):");
    foreach (var (key, n) in differs.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"{n,5}/{total[key]} {key.Item1} {key.Item2}{key.Item3}");
}

static async Task Serve()
{
    string cert = Path.Combine(Replay.Paths.Work, "leaf.pem"), key = Path.Combine(Replay.Paths.Work, "leaf.key");
    if (!(File.Exists(cert) && File.Exists(key))) Die("no certificate: put leaf.pem and leaf.key in the data folder");
    var cfg = Replay.LoadCfg();
    Directory.CreateDirectory(Replay.Paths.Work);
    var t0 = DateTime.UtcNow;
    var table = Table(out var hars);
    Console.WriteLine($"table: {table.Entries.Keys.Count(k => k.Params is null)} keys from {hars.Count} recordings in {Py.Fixed((DateTime.UtcNow - t0).TotalSeconds, 1)}s");
    Console.WriteLine($"archives: {ArcList.ArchiveIndex((string)cfg["install_dir"]!).Count} entries");
    var state = Replay.LoadState(cfg, table, Replay.Paths.Work);
    Console.WriteLine("player state: " + (state != null ? Path.Combine(Replay.Paths.Work, "state.db") : "off (no recorded connect)"));
    using var pem = X509Certificate2.CreateFromPemFile(cert, key);
    using var tls = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);  // SChannel can't use a PEM load's ephemeral key; disposing deletes its key file
    var app = ServerApp.Build(new ServerOptions(new Replay.Game(table, cfg, state, Replay.Paths.Mirror), hars.Count, Replay.Paths.Work, Console: true), k =>
    {
        // HTTP/1.1 only: ALPN would move the client's embedded browser to h2. TLS versions: the OS default
        k.Listen(IPAddress.Loopback, 443, l => { l.Protocols = HttpProtocols.Http1; l.UseHttps(tls); });
        k.Listen(IPAddress.Loopback, 80, l => l.Protocols = HttpProtocols.Http1);
    });
    Console.WriteLine("listening on 127.0.0.1:443 and :80; admin API and docs: http://localhost/swagger");
    await app.RunAsync();
}
