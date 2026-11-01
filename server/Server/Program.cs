// Entry point: serve (default), admin, import, review, coverage, backup. Every command reads settings.json: --settings <file>, else the
// SERVER_SETTINGS environment variable, else settings.json in the current folder (docs/server.md).
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Resurgence.Restore;
using Microsoft.Data.Sqlite;
using Resurgence.Server;

const string AdminUsage = """
    Server-side admin: give the player items or currency (state.db in the data folder).

    Usage (repo root): dotnet run --project server/Server -- admin item <item_id> [count] | admin em|gm <amount>
                       dotnet run --project server/Server -- admin spawn <reserve> <animal> <males> [females] [x z radius]
    A running hunt receives them with its next heartbeat (update 10 / update 15); otherwise the next connect carries them.
    Negative amounts take currency away. With the server running, the same is in its admin API (http://localhost/swagger).
    spawn adds a group to spawn.json in the data folder: the next hunt (connect) has it; x/z default to the start.
    """;

const string ImportUsage = """
    Restore a backup zip into an account of state.db in the data folder (the zip's reader is src/Restore).

    Usage (repo root): dotnet run --project server/Server -- import backup <zip> [--account <id>]
    Any zip is accepted. The account's profile (items, wallet, skills, profile, statistics, achievements, trophies, lodges) is
    replaced, not merged; hunt history stays; the old database is copied beside it first. Without --account the account is the zip's
    user id (created when it does not exist), on a local server (offline_mode true) the local account. The zip is kept in the data folder.
    """;

const string ReviewUsage = """
    The backup zips players uploaded: every one is kept for an operator and never restored automatically.

    Usage (repo root): dotnet run --project server/Server -- review list | review approve <id> | review reject <id>
    list shows each kept zip: id, account, the zip's user id and handle, time and note; "steam id matches" is information, not proof.
    approve restores it by the community rules (once per game user id, never a user id another account holds, the account's user id
    changes only before it has played) and prints the report or the refusal; reject deletes the kept zip and its row.
    """;

var words = args.ToList();
string? settingsFile = null;
if (words.IndexOf("--settings") is var at and >= 0)
{
    if (at + 1 >= words.Count) Die("--settings needs a file");
    settingsFile = words[at + 1];
    words.RemoveRange(at, 2);
}
settingsFile ??= Environment.GetEnvironmentVariable("SERVER_SETTINGS") ?? "settings.json";
Settings settings = null!;
try { settings = Settings.Load(settingsFile); }
catch (Exception e) when (e is InvalidDataException or IOException) { Die(e.Message); }
Scoring.Load(settings.Cfg, settings.VariantsFile);

var cmd = words.Count > 0 ? words[0] : "serve";
switch (cmd)
{
    case "serve": await Serve(); break;
    case "admin": Admin([.. words.Skip(1)]); break;
    case "import": Import([.. words.Skip(1)]); break;
    case "review": Review([.. words.Skip(1)]); break;
    case "coverage": await Coverage(words.Contains("--no-recordings")); break;
    case "backup":
        if (words.Count is not (2 or 3)) Die("usage: backup <folder> [keep, default 7]");
        Backup(words[1], words.Count == 3 ? int.Parse(words[2], CultureInfo.InvariantCulture) : 7);
        break;
    default: Die($"unknown command {cmd}: serve | admin | import | review | coverage | backup"); break;
}

// A consistent copy of every database of data_dir (VACUUM INTO, not a file copy: ADR 0001) as <name>-<yyyyMMdd>.db in
// <folder>; only the newest <keep> copies of each database stay. Safe while the server runs.
void Backup(string folder, int keep)
{
    Directory.CreateDirectory(folder);
    var day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    foreach (var db in Directory.GetFiles(settings.DataDir, "*.db"))
    {
        var name = Path.GetFileNameWithoutExtension(db);
        var copy = Path.Combine(folder, $"{name}-{day}.db");
        File.Delete(copy);
        using (var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "VACUUM INTO $to";
            cmd.Parameters.AddWithValue("$to", copy);
            cmd.ExecuteNonQuery();
        }
        foreach (var old in Directory.GetFiles(folder, name + "-????????.db").OrderDescending().Skip(keep)) File.Delete(old);
        Console.WriteLine($"backup: {copy}");
    }
}
return 0;

static void Die(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
}

Replay.HarTable Table(out List<string> hars)
{
    hars = Directory.Exists(settings.RecordingsDir)
        ? Directory.GetFiles(settings.RecordingsDir, "*" + Recordings.Ext).OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal).ToList()
        : [];
    return Replay.BuildTable(hars);
}

void Admin(string[] a)
{
    if (a.Length > 0 && a[0] == "spawn")
    {
        var n = a.Skip(3).Select(s => int.TryParse(s, out var v) ? (int?)v : null).ToArray();
        int reserve = 0;
        if (a.Length < 4 || !int.TryParse(a[1], out reserve) || n.Any(v => v is null) || n.Length is 3 or 4 or > 5) Die(AdminUsage);
        var spawner = new Spawner(Path.Combine(settings.DataDir, "spawn.json"));
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
    Directory.CreateDirectory(settings.DataDir);
    var state = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
    if (state == null) { Die("no recorded connect and no connect sample in the install: nothing to seed the player state from"); return; }
    if (a[0] == "item")
    {
        if (count is < 1 or > AdminApi.MaxStacks || !state.GrantItems(state.LocalAccount, itemId, count))
            Die($"item {itemId}: unknown or bad count (1 to {AdminApi.MaxStacks})");
        Console.WriteLine($"granted {count} x {Py.Str(state.ItemInfo(itemId)!["name"])} ({itemId})");
    }
    else
    {
        if (!AdminApi.ValidAmount(amount)) Die($"{a[1]}: not a number from -2^53 to 2^53");
        var balance = state.Give(state.LocalAccount, a[0], amount);
        if (balance == null) Die($"{a[0]} unchanged: balance would drop below 0");
        Console.WriteLine($"{a[0]} balance: {Py.Str(balance)}");
    }
}

void Import(string[] a)
{
    long? account = null;
    if (a.Length is not (2 or 4) || a[0] != "backup" || (a.Length == 4 && (a[2] != "--account" || !long.TryParse(a[3], out var id) || id < 1)))
        Die(ImportUsage);
    if (a.Length == 4) account = long.Parse(a[3]);
    Directory.CreateDirectory(settings.DataDir);
    var state = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
    if (state == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
    if (account is null && (bool?)settings.Cfg["offline_mode"] != false)
        account = state.LocalAccount;  // a local server has one account, the one the client connects as
    try
    {
        Console.Write(Restorer.Import(state, a[1], account, settings.DataDir).Report);
    }
    catch (RestoreException e) { Die($"refused: {e.Message}"); }
    catch (IOException e) { Die($"cannot read: {e.Message}"); }
}

void Review(string[] a)
{
    long id = 0;
    if (!(a is ["list"] || (a is ["approve" or "reject", var n] && long.TryParse(n, out id)))) Die(ReviewUsage);
    Directory.CreateDirectory(settings.DataDir);
    var state = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
    if (state == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
    var uploads = new Uploads(state, settings.DataDir);
    switch (a[0])
    {
        case "list":
            var kept = uploads.List();
            foreach (var p in kept)
                Console.WriteLine($"{p.Id}\taccount {p.Account}\tuser id {p.UserId}\thandle {p.Handle ?? "(unreadable)"}\t{DateTimeOffset.FromUnixTimeSeconds(p.Ts):u}\t{(p.SteamMatch ? "steam id matches" : "steam id does not match")}\t{p.Note}");
            if (kept.Count == 0) Console.WriteLine("nothing waits for review");
            break;
        case "approve":
            var done = uploads.Approve(id);
            if (done.Kind != "restored") Die($"refused: {done.Message}");
            Console.WriteLine(done.Message);
            Console.Write(done.Report);
            break;
        default:
            if (!uploads.Reject(id)) Die($"no kept zip {id}");
            Console.WriteLine($"rejected {id}: the zip and its row are deleted");
            break;
    }
}

// Replay every recorded request on a redirected host through the server and list what it can't answer faithfully.
// Default: the recordings answer and the player state is off. --no-recordings: the server as it starts without
// recordings or mirror (player state from the install and the facts file, on a temporary database), so what is listed
// as unanswered still needs a recording. Hosts are printed as their settings key (api, static, ...), never by name.
async Task Coverage(bool noRecordings)
{
    var cfg = settings.Cfg;
    var table = Table(out var hars);
    var scratch = noRecordings ? Directory.CreateTempSubdirectory("coverage").FullName : null;
    try
    {
        var served = noRecordings ? new Replay.HarTable() : table;
        var state = noRecordings ? Replay.LoadState(cfg, served, scratch!) : null;
        if (noRecordings && state is null) Die("no connect sample in the install: nothing to start the player state from");
        await using var server = await Loopback.StartAsync(new Replay.Game(served, cfg, state, noRecordings ? Path.Combine(scratch!, "mirror") : settings.MirrorDir));
        await ReplayAll(server, cfg, hars);
    }
    finally
    {
        if (scratch is not null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(scratch, true);
        }
    }
}

async Task ReplayAll(Loopback server, JsonObject cfg, List<string> hars)
{
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
            var masked = System.Text.RegularExpressions.Regex.Replace(path, @"[0-9a-fA-F]{16,}|\d{4,}", "<id>");  // tokens and ids of people stay out of the report
            if (!hosts.Contains(host)) continue;
            var method = ((string)req["method"]!).ToUpperInvariant();
            var post = req["postData"] as JsonObject;
            var r = await server.SendAsync(method, host, query.Length > 0 ? $"{path}?{query}" : path,
                [("Content-Type", (string?)post?["mimeType"] ?? "")], System.Text.Encoding.UTF8.GetBytes((string?)post?["text"] ?? ""));
            Bump(sources, r.Source);
            var status = Py.L(resp["status"]);
            var role = roles.GetValueOrDefault(host, "other");
            if (r.Source is "weak" or "miss") Bump(unanswered, (method, role, masked, status));
            else if (status == 200)
            {
                Bump(total, (method, role, masked));
                if (!r.Body.AsSpan().SequenceEqual(Replay.ResponseBody(resp["content"]))) Bump(differs, (method, role, masked));
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

async Task Serve()
{
    foreach (var l in settings.Listeners)
        foreach (var file in new[] { l.Cert, l.Key })
            if (file is not null && !File.Exists(file)) Die($"{file}: not found (listener {l.Address}:{l.Port})");
    Directory.CreateDirectory(settings.DataDir);
    var t0 = DateTime.UtcNow;
    var table = Table(out var hars);
    Console.WriteLine($"table: {table.Entries.Keys.Count(k => k.Params is null)} keys from {hars.Count} recordings in {Py.Fixed((DateTime.UtcNow - t0).TotalSeconds, 1)}s");
    Console.WriteLine($"archives: {ArcList.ArchiveIndex((string)settings.Cfg["install_dir"]!).Count} entries");
    var state = Replay.LoadState(settings.Cfg, table, settings.DataDir);
    Console.WriteLine("player state: " + (state != null ? Path.Combine(settings.DataDir, "state.db") : "off (no recorded connect and no connect sample in the install)"));
    var certs = new List<X509Certificate2>();
    try
    {
        var app = ServerApp.Build(new ServerOptions(new Replay.Game(table, settings.Cfg, state, settings.MirrorDir), hars.Count, settings.DataDir,
            Console: true, settings.TrustedProxies, settings.HostHeader, settings.MaxBodyBytes,
            AdminListeners: [.. settings.Listeners.Where(l => l.Admin).Select(l => (l.Address, l.Port))]), k =>
        {
            foreach (var l in settings.Listeners)
            {
                X509Certificate2? tls = null;
                if (l.Cert is not null)
                {
                    using var pem = X509Certificate2.CreateFromPemFile(l.Cert, l.Key);
                    // SChannel can't use a PEM load's ephemeral key
                    certs.Add(tls = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null));
                }
                // HTTP/1.1 only: ALPN would move the client's embedded browser to h2. TLS versions: the OS default
                k.Listen(l.Address, l.Port, o => { o.Protocols = HttpProtocols.Http1; if (tls != null) o.UseHttps(tls); });
            }
        });
        Console.WriteLine("listening on " + string.Join(", ", settings.Listeners.Select(l => $"{(l.Cert is null ? "http" : "https")}://{l.Address}:{l.Port}{(l.Admin ? " (admin)" : "")}")));
        await app.RunAsync();
    }
    finally
    {
        foreach (var c in certs) c.Dispose();  // disposing deletes a loaded key's temporary file
    }
}
