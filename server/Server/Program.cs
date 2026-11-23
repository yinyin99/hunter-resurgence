// Entry point: serve (default), admin, import, review, claim, coverage, backup. Every command reads settings.json: --settings <file>, else the
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

    Usage (repo root): dotnet run --project server/Server -- admin item <item_id> [count] | admin em|gm <amount> [--account <id or handle>]
                       dotnet run --project server/Server -- admin spawn <reserve> <animal> <males> [females] [x z radius]
    --account names another account (user id or handle); without it the local account gets it.
    admin faces [--apply] makes every account's face a 3D face of its body's gender (dry run without --apply: prints the counts).
    admin avatars [--account <id or handle>] [--dry-run] sets the uploaded avatar of every account (or one) whose avatar is the default or a dead upload link, from the avatar host (it must be in picture_link_hosts);
    it prints one line per account: id, handle, set / no uploaded avatar / fetch failed. --dry-run fetches nothing.
    admin rescore [--account <id or handle>] [--kill <id>] [--apply] scores the stored harvests again with today's rules (every account, one, or with --kill, that account's one harvest) and prints one JSON line per harvest whose score changes:
    {account, kill, species, oldScore, newScore, gmsPaid}, then a line per account that changed or would be paid. Without --apply it writes nothing; --kill needs --account (kill ids are per account). gm$ already paid are not taken back.
    admin gallery-details [--account <id or handle>] sets the species, score and kill of every restored gallery picture (or of one account's), and restores the saved profile background and ribbon an account lacks, from the zips the server kept (restores/<account>/): nothing is fetched, added or removed;
    it prints one line per account: id, handle, pictures updated of pictures restored.
    A running hunt receives them with its next heartbeat (update 10 / update 15); otherwise the next connect carries them.
    Negative amounts take currency away. With the server running, the same is in its admin API (http://localhost/swagger).
    spawn adds a group to spawn.json in the data folder: the next hunt (connect) has it; x/z default to the start.
    admin missions-rollback [--apply] [--account <id or handle>] reopens the mission objectives the earlier, laxer rules finished without their conditions: each finished harvest or clue objective whose title the server fully understands is checked again against the harvests and clues
    made since its mission was activated, and one that no harvest or clue meets is reopened (one with words the server does not read stays finished and is counted as `kept: not understood` in the total line) (a completed mission that loses one is active again, and the missions it had made available are activated so nobody is locked out). gm$, em$ and items already paid are not taken back;
    missions completed before a restore are never touched. Without --apply it only prints one line per account with a change and a total, and writes nothing; --apply changes the rows and keeps a record per account (table mission_rollbacks).
    Run it once, with the server stopped, after `backup`.
    admin missions-audit [--hours N] [--account <id or handle>] [--discord] lists the mission objectives completed in the last N hours (default 24) that look suspicious, one line per objective
    (flag | mission | objective | handle | what met it), grouped by flag (borderline, unmet, fast, shared, spike), then the counts. It writes nothing. --discord prints Markdown messages of at most
    2000 characters, separated by a line ---8<---, for a webhook.
    admin missions-stuck [--days N] [--account <id or handle>] [--discord] [--min-candidates N] [--min-sample N] [--min-share P] lists the open harvest, spot and clue objectives the server understands that players of the last N days (default 14)
    attempted and never completed: one line per objective with its candidate events, the accounts that have it open, how many accounts completed it here, how many candidates each condition refused and what the most refusing one saw.
    Listed: at least --min-candidates (5) candidates and no completion, or one condition refusing at least --min-share (90) percent of at least --min-sample (10) candidates. It writes nothing. --discord as for missions-audit.
    """;

const string ImportUsage = """
    Restore a backup zip into an account of state.db in the data folder (the zip's reader is src/Restore).

    Usage (repo root): dotnet run --project server/Server -- import backup <zip> [--account <id>]
                       dotnet run --project server/Server -- import pictures <zip> --account <id>
    Any zip is accepted. The account's profile (items, wallet, skills, profile, statistics, achievements, trophies, lodges) is
    replaced, not merged; hunt history stays; the old database is copied beside it first. Without --account the account is the zip's
    user id (created when it does not exist), on a local server (offline_mode true) the local account. The zip is kept in the data folder.
    import pictures restores only the zip's pictures (gallery, kill photos, score pages, avatar) onto an existing account and leaves everything else as it is.
    It only adds pictures: none of the account's is removed, and one it already has is not added twice. Gallery links the zip holds no file for are read from what the upload kept, or fetched from picture_link_hosts.
    """;

const string ReviewUsage = """
    The backup zips and the archive claims players sent: every one is kept for an operator and never restored automatically.

    Usage (repo root): dotnet run --project server/Server -- review list | review approve <id> [--keep-handle] [--account <account id>] | review reject <id> [--account <account id>] [reason]
    An id is a number for a zip, c<number> for a claim (c12), or r<account>-<8 hex> for a refused zip (r2000000001-1a2b3c4d).
    --account <account id> (zips only): refuse, and change nothing, when the waiting zip belongs to another account: copy it from the alert or from list.
    list shows each kept zip: id, account, the zip's user id and handle, time and note; "steam id matches" is information, not proof.
    A claim line: c<id>, claim, account, archived user id, archived handle, hunterscore, time, note.
    A refused line: r<account>-<8 hex>, refused, account, handle, time, size, reason. Those are zips the reader could not read; they stay until you reject them.
    approve restores a zip by the community rules (once per game user id, never a user id another account holds; an account that has played keeps
    its own user id) and prints the report or the refusal (the refusal is also the zip's note, which the player sees);
    --keep-handle leaves the account its own name instead of the backup's.
    approve c<id> restores the archived profile onto the claiming account (its user id never changes); --keep-handle leaves the account its own name.
    reject deletes the kept zip and its row and keeps the reason (the rest of the line) for the player. A rejected claim stays as a row with its reason.
    Both leave the player a notice in the game.

    review reports lists the problem reports the launcher sent (kept 30 days): reference (p<account>-<8 hex>), account, handle, time, trigger, size.
    review reports <reference> <file> copies that report's zip to <file> (it must not exist yet).
    review diagnostics <account: user id or handle> prints the account's last 30 days: crash reports (time, kind, module, top frames, message), hunts that ended
    without a disconnect, hunts under 60 s, and how many times the game said the connection to the host failed (event 30).
    """;

const string ClaimUsage = """
    Restore an archived public profile onto an account at once: no lookup limit, no review, no confirmation, whatever the hunterscore.

    Usage (repo root): dotnet run --project server/Server -- claim <account: user id or handle> <archived handle or user id> [--keep-handle]
    The account keeps its user id, hunts, kills and photos; its items, skills, achievements, trophies and lodges are replaced.
    --keep-handle leaves the account its own name instead of the archived one. An earlier claim row of the account is replaced.
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
    case "claim": ClaimCommand([.. words.Skip(1)]); break;
    case "coverage": await Coverage(words.Contains("--no-recordings"), words.Contains("--rules")); break;
    case "backup":
        if (words.Count is not (2 or 3)) Die("usage: backup <folder> [keep, default 7]");
        DbBackup.Run(settings.DataDir, words[1], words.Count == 3 ? int.Parse(words[2], CultureInfo.InvariantCulture) : 7);
        break;
    default: Die($"unknown command {cmd}: serve | admin | import | review | claim | coverage | backup"); break;
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
    if (a.Length > 0 && a[0] == "rescore")
    {
        if (AdminApi.ParseRescoreArgs(a[1..]) is not var (name, kill, apply)) { Die(AdminUsage); return; }
        Directory.CreateDirectory(settings.DataDir);
        var loaded = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
        if (loaded == null) { Die("no recorded connect and no connect sample in the install: nothing to seed the player state from"); return; }
        long? only = null;
        if (name is not null && (only = AdminApi.ResolveAccount(loaded, name)) is null) Die($"no account {name}");
        foreach (var r in loaded.Rescore(only, !apply, kill))
        {
            foreach (var h in r.Harvests)
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { account = r.Account, kill = h.Kill, species = h.Species, oldScore = h.OldScore, newScore = h.NewScore, gmsPaid = h.GmsPaid }));
            if (r.Rescored > 0 || r.Paid > 0)
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { account = r.Account, rescored = r.Rescored, paid = r.Paid, gms = r.Gms, applied = apply }));
        }
        Console.WriteLine(apply ? "applied (a running server shows it in its boards within 15 minutes, or at its restart)" : "dry run: nothing written; add --apply to write");
        return;
    }
    if (a.Length > 0 && a[0] == "gallery-details")
    {
        if (a[1..] is not ([] or ["--account", _])) Die(AdminUsage);
        Directory.CreateDirectory(settings.DataDir);
        var loaded = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
        if (loaded == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
        long? only = null;
        if (a.Length == 3 && (only = AdminApi.ResolveAccount(loaded, a[2])) is null) Die($"no account {a[2]}");
        try { foreach (var line in loaded.SweepGalleryDetails(settings.DataDir, only)) Console.WriteLine(line); }
        catch (RestoreException e) { Die($"refused: {e.Message}"); }
        return;
    }
    if (a.Length > 0 && a[0] == "avatars")
    {
        var dry = a.Contains("--dry-run");
        var rest = a[1..].Where(x => x != "--dry-run").ToArray();
        if (rest is not ([] or ["--account", _])) Die(AdminUsage);
        Directory.CreateDirectory(settings.DataDir);
        var loaded = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
        if (loaded == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
        long? only = null;
        if (rest.Length == 2 && (only = AdminApi.ResolveAccount(loaded, rest[1])) is null) Die($"no account {rest[1]}");
        foreach (var line in loaded.SweepAvatars(PictureLinks.FromCfg(settings.Cfg), only, dry).GetAwaiter().GetResult()) Console.WriteLine(line);
        return;
    }
    if (a.Length > 0 && a[0] == "faces")
    {
        if (a[1..].Any(x => x != "--apply")) Die(AdminUsage);
        Directory.CreateDirectory(settings.DataDir);
        var loaded = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
        if (loaded == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
        Console.WriteLine(loaded.FixFaces(a.Contains("--apply")));
        return;
    }
    if (a.Length > 0 && a[0] == "missions-rollback")
    {
        var apply = a.Contains("--apply");
        var rest = a[1..].Where(x => x != "--apply").ToArray();
        if (rest is not ([] or ["--account", _])) Die(AdminUsage);
        Directory.CreateDirectory(settings.DataDir);
        var loaded = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
        if (loaded == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
        long? only = null;
        if (rest.Length == 2 && (only = AdminApi.ResolveAccount(loaded, rest[1])) is null) Die($"no account {rest[1]}");
        foreach (var line in loaded.RollbackMissions(apply, only)) Console.WriteLine(line);
        return;
    }
    if (a.Length > 0 && a[0] == "missions-audit")
    {
        var (hours, discord) = (24.0, false);
        string? whom = null;
        for (var i = 1; i < a.Length; i++)
            if (a[i] == "--discord") discord = true;
            else if (a[i] == "--hours" && i + 1 < a.Length)
            {
                if (!double.TryParse(a[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out hours) || !double.IsFinite(hours) || hours <= 0) Die(AdminUsage);
            }
            else if (a[i] == "--account" && i + 1 < a.Length) whom = a[++i];
            else Die(AdminUsage);
        Directory.CreateDirectory(settings.DataDir);
        var loaded = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
        if (loaded == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
        long? only = null;
        if (whom != null && (only = AdminApi.ResolveAccount(loaded, whom)) is null) Die($"no account {whom}");
        foreach (var line in loaded.AuditMissions(hours, only, discord)) Console.WriteLine(line);
        return;
    }
    if (a.Length > 0 && a[0] == "missions-stuck")
    {
        var (discord, numbers) = (false, new Dictionary<string, double> { ["--days"] = 14, ["--min-candidates"] = 5, ["--min-sample"] = 10, ["--min-share"] = 90 });
        string? whom = null;
        for (var i = 1; i < a.Length; i++)
            if (a[i] == "--discord") discord = true;
            else if (a[i] == "--account" && i + 1 < a.Length) whom = a[++i];
            else if (numbers.ContainsKey(a[i]) && i + 1 < a.Length)
            {
                if (!double.TryParse(a[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number <= 0) Die(AdminUsage);
                numbers[a[i++]] = number;
            }
            else Die(AdminUsage);
        Directory.CreateDirectory(settings.DataDir);
        var loaded = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
        if (loaded == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
        long? only = null;
        if (whom != null && (only = AdminApi.ResolveAccount(loaded, whom)) is null) Die($"no account {whom}");
        foreach (var line in loaded.StuckMissions(numbers["--days"], only, discord, (int)numbers["--min-candidates"], (int)numbers["--min-sample"], numbers["--min-share"])) Console.WriteLine(line);
        return;
    }
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
    string? who = null;
    if (a.Length >= 2 && a[^2] == "--account") { who = a[^1]; a = a[..^2]; }
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
    var account = AdminApi.ResolveAccount(state, who);
    if (account is null) Die($"no account {who}");
    if (a[0] == "item")
    {
        if (count is < 1 or > AdminApi.MaxStacks || !state.GrantItems(account!.Value, itemId, count))
            Die($"item {itemId}: unknown or bad count (1 to {AdminApi.MaxStacks})");
        Console.WriteLine($"granted {count} x {Py.Str(state.ItemInfo(itemId)!["name"])} ({itemId})");
    }
    else
    {
        if (!AdminApi.ValidAmount(amount)) Die($"{a[1]}: not a number from -2^53 to 2^53");
        var balance = state.Give(account!.Value, a[0], amount);
        if (balance == null) Die($"{a[0]} unchanged: balance would drop below 0");
        Console.WriteLine($"{a[0]} balance: {Py.Str(balance)}");
    }
}

void Import(string[] a)
{
    long? account = null;
    if (a is ["pictures", var zip, "--account", var who] && long.TryParse(who, out var target) && target > 0)
    {
        Directory.CreateDirectory(settings.DataDir);
        var loaded = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
        if (loaded == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
        try
        {
            var (report, avatar) = Restorer.ImportPictures(loaded, zip, target, settings.DataDir, PictureLinks.FromCfg(settings.Cfg));
            Console.WriteLine(report);
            Console.WriteLine($"avatar: {avatar}");
        }
        catch (RestoreException e) { Die($"refused: {e.Message}"); }
        catch (IOException e) { Die($"cannot read: {e.Message}"); }
        return;
    }
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
        Console.Write(Restorer.Import(state, a[1], account, settings.DataDir, PictureLinks.FromCfg(settings.Cfg)).Report);
    }
    catch (RestoreException e) { Die($"refused: {e.Message}"); }
    catch (IOException e) { Die($"cannot read: {e.Message}"); }
}

void Review(string[] a)
{
    if (a is ["reports", ..] or ["diagnostics", _])
    {
        ReviewReports(a);
        return;
    }
    long id = 0;
    long? onlyAccount = null;
    if (Array.IndexOf(a, "--account") is var at and >= 0)
    {
        if (at + 1 >= a.Length || !long.TryParse(a[at + 1], out var acc)) { Die(ReviewUsage); return; }
        onlyAccount = acc;
        a = [.. a[..at], .. a[(at + 2)..]];
    }
    var isClaim = false;
    string? refusedId = null;
    var reason = "";
    bool Id(string s)  // 12 is a kept zip, c12 a claim, r2000000001-1a2b3c4d a refused zip
    {
        if (s.Length > 1 && s[0] is 'r' or 'R' && s.Contains('-')) { refusedId = s.ToLowerInvariant(); return true; }
        isClaim = s.Length > 1 && (s[0] is 'c' or 'C');
        return long.TryParse(isClaim ? s[1..] : s, out id);
    }
    if (a is ["reject", var r, .. var rest] && Id(r)) reason = string.Join(' ', rest);
    else if (!(a is ["list"] || (a is ["approve", var n, .. var opt] && opt is [] or ["--keep-handle"] && Id(n)))) Die(ReviewUsage);
    Directory.CreateDirectory(settings.DataDir);
    var state = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
    if (state == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
    var uploads = new Uploads(state, settings.DataDir) { Pictures = PictureLinks.FromCfg(settings.Cfg) };
    var claims = OpenClaims(state);
    long? zipAccount = null;
    if (a[0] != "list" && !isClaim && refusedId is null)
    {
        zipAccount = uploads.AccountOf(id);
        if (onlyAccount is { } want && uploads.WrongAccount(id, want) is { } wrong) Die($"refused: {wrong}");
    }
    else if (onlyAccount is not null) Die("--account is for kept zips, not for list, claims or refused zips");
    switch (a[0])
    {
        case "list":
            var kept = uploads.List();
            foreach (var p in kept)
                Console.WriteLine($"{p.Id}\taccount {p.Account}\tuser id {p.UserId}\thandle {p.Handle ?? "(unreadable)"}\t{DateTimeOffset.FromUnixTimeSeconds(p.Ts):u}\t{(p.SteamMatch ? "steam id matches" : "steam id does not match")}\t{p.Note}");
            var waiting = claims.Waiting();
            var refusedZips = uploads.ListRefused();
            foreach (var z in refusedZips)
                Console.WriteLine($"{z.Id}\trefused\taccount {z.Account}\thandle {z.Handle ?? "(deleted)"}\t{DateTimeOffset.FromUnixTimeSeconds(z.Ts):u}\t{z.Size} bytes\t{z.Reason}");
            foreach (var c in waiting)
                Console.WriteLine($"c{c.Id}\tclaim\taccount {c.Account}\tarchived {c.Uid}\thandle {c.Handle}\thunterscore {c.Hunterscore}\t{DateTimeOffset.FromUnixTimeSeconds(c.Ts):u}\t{c.Note}");
            if (kept.Count + waiting.Count + refusedZips.Count == 0) Console.WriteLine("nothing waits for review");
            break;
        case "approve":
            if (refusedId is not null) Die("a refused zip cannot be approved: reject it to delete it");
            var keep = a is [_, _, "--keep-handle"];
            if (isClaim)
            {
                var claimed = claims.Approve(id, keep);
                if (claimed.Kind != "restored") Die($"refused: {claimed.Message}");
                Console.WriteLine(claimed.Message);
                break;
            }
            var done = uploads.Approve(id, keepHandle: keep);
            if (done.Kind != "restored") Die($"refused: {done.Message}");
            Console.WriteLine($"approved {id}, account {zipAccount}");
            Console.WriteLine(done.Message);
            Console.Write(done.Report);
            break;
        default:
            if (refusedId is not null)
            {
                if (!uploads.RejectRefused(refusedId)) Die($"no refused zip {refusedId}");
                Console.WriteLine($"rejected {refusedId}: the refused zip is deleted");
                break;
            }
            if (isClaim)
            {
                if (!claims.Reject(id, reason)) Die($"no waiting claim c{id}");
                Console.WriteLine($"rejected c{id}: the player has the reason in the inbox");
                break;
            }
            if (!uploads.Reject(id, reason)) Die($"no kept zip {id}");
            Console.WriteLine($"rejected {id}, account {zipAccount}: the zip and its row are deleted");
            break;
    }
}

Claims OpenClaims(State state) => new(state, new Archive((string)settings.Cfg["archive_db"]!), settings.DataDir) { Pictures = PictureLinks.FromCfg(settings.Cfg) };

void ClaimCommand(string[] a)
{
    var keep = a is [_, _, "--keep-handle"];
    if (!(a.Length == 2 || keep)) Die(ClaimUsage);
    Directory.CreateDirectory(settings.DataDir);
    var state = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
    if (state == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
    var account = AdminApi.ResolveAccount(state, a[0]);
    if (account is null) Die($"no account {a[0]}");
    var done = OpenClaims(state).Claim(account!.Value, a[1], useOldName: !keep, confirmOverwrite: true, byOperator: true);
    if (done.Kind != "restored") Die($"refused: {done.Message}");
    Console.WriteLine(done.Message);
}

// Replay every recorded request on a redirected host through the server and list what it can't answer faithfully.
// Default: the recordings answer and the player state is off. --no-recordings: the server as it starts without
// recordings or mirror (player state from the install and the facts file, on a temporary database), so what is listed
// as unanswered still needs a recording. Hosts are printed as their settings key (api, static, ...), never by name.
// Last: the missions the catalogue defines and how many of their objectives the server understands (State.MissionCoverage). --rules adds every objective's rule in words, sorted
// by mission and objective so two runs can be diffed; the warnings where a rule disagrees with its mission's own data end the report, with or without --rules.
async Task Coverage(bool noRecordings, bool rules)
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
        MissionCoverage(cfg, table, rules);
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

void MissionCoverage(JsonObject cfg, Replay.HarTable table, bool rules)
{
    var scratch = Directory.CreateTempSubdirectory("coverage-missions").FullName;
    try
    {
        Console.WriteLine("missions:");
        foreach (var line in Replay.LoadState(cfg, table, scratch)?.MissionCoverage(rules) ?? ["no recorded connect: no catalogue"])
            Console.WriteLine("  " + line);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(scratch, true);
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
    if (state?.OverriddenObjectives is > 0 and var overrides)
        Console.WriteLine($"missions: {overrides} objective rules from {State.MissionRulesFile}");
    if (state is not null && state.ConfirmMissions() is > 0 and var confirmed)
        Console.WriteLine($"missions: {confirmed} objectives confirmed against the harvests and clues made before this start");
    if (state is not null && (bool?)settings.Cfg["offline_mode"] == false) state.BackfillLodges();  // a community server: the lodges its accounts' lodge items lack
    var certs = new List<X509Certificate2>();
    try
    {
        var app = ServerApp.Build(new ServerOptions(new Replay.Game(table, settings.Cfg, state, settings.MirrorDir), hars.Count, settings.DataDir,
            Console: true, settings.TrustedProxies, settings.HostHeader, settings.MaxBodyBytes,
            AdminListeners: settings.AdminListeners), k =>
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
        // the leaderboards are computed in the background at the start and again before they expire: no request waits for a cold board
        if (state is { Online: true })
            app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => state.KeepBoardsWarm(State.BoardRefresh, app.Lifetime.ApplicationStopping)));
        await app.RunAsync();
    }
    finally
    {
        foreach (var c in certs) c.Dispose();  // disposing deletes a loaded key's temporary file
    }
}

void ReviewReports(string[] a)
{
    if (!(a is ["reports"] or ["reports", _, _] or ["diagnostics", _])) Die(ReviewUsage);
    Directory.CreateDirectory(settings.DataDir);
    var state = Replay.LoadState(settings.Cfg, Table(out _), settings.DataDir);
    if (state == null) { Die("no recorded connect: nothing to seed the accounts from"); return; }
    var reports = new ProblemReports(state, settings.DataDir);
    switch (a)
    {
        case ["diagnostics", var who]:
            if (AdminApi.ResolveAccount(state, who) is not { } account) { Die($"no account {who}"); return; }
            foreach (var line in Diagnostics.Of(state.Store, account, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Lines()) Console.WriteLine(line);
            break;
        case ["reports", var reference, var file]:
            if (reports.PathOf(reference) is not { } zip) { Die($"no problem report {reference}"); return; }
            try { File.Copy(zip, file); }
            catch (IOException e) { Die($"cannot write {file}: {e.Message}"); }
            Console.WriteLine($"{reference} saved to {file}");
            break;
        default:
            var all = reports.List();
            foreach (var r in all)
                Console.WriteLine($"{r.Reference}\taccount {r.Account}\thandle {r.Handle ?? "(deleted)"}\t{DateTimeOffset.FromUnixTimeSeconds(r.Ts):u}\t{r.Trigger}\t{r.Size} bytes");
            if (all.Count == 0) Console.WriteLine("no problem reports");
            break;
    }
}
