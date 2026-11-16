// The community upload: a signed-in player's backup zip and what the server does with it (docs/server.md#upload-a-backup-zip).
// Every zip is kept for the operator and never restored automatically: `review approve <id>` runs the rules and the restore.
// A Steam id match is shown to the operator as information, not proof of ownership.
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Resurgence.Restore;

namespace Resurgence.Server;

/// <summary>What an upload came to. Kind: restored (only an operator's approve), review (kept for the operator, nothing restored), already (this user id
/// was restored before), taken (another account holds the user id), refused (the user id is not accepted), invalid (not a usable backup), busy, too_large.
/// Message is plain words for the player; Report the technical report of a restore.</summary>
public sealed record UploadOutcome(int Status, string Kind, string Message, string? Report = null);

public sealed class Uploads(State state, string dataDir)
{
    /// <summary>The largest zip taken, compressed: the reader's 1 GiB of content (a heavy profile with kill pages and photos holds hundreds of MiB).</summary>
    public const long MaxZipBytes = 1L << 30;
    /// <summary>The largest request body of an upload, one part or a whole zip sent at once: under the 100 MB a proxy in front may allow.</summary>
    public const int MaxPartBytes = 90 << 20;
    /// <summary>An open upload that gets no part for this long is deleted and frees its slots.</summary>
    public const double AbandonedSeconds = 30 * 60;
    /// <summary>The settings `upload_account_files` ... `upload_min_free_bytes` (docs/server.md#upload-a-backup-zip) count the zips stored for the operator:
    /// the ones waiting for review and the refused ones kept under refused/. Restored zips do not count.</summary>
    public const int AccountFilesDefault = 3;
    public const long AccountBytesDefault = 1L << 30, TotalBytesDefault = 5L << 30, MinFreeBytesDefault = 10L << 30;
    public int AccountFiles { get; init; } = AccountFilesDefault;
    public long AccountBytes { get; init; } = AccountBytesDefault;
    public long TotalBytes { get; init; } = TotalBytesDefault;
    public long MinFreeBytes { get; init; } = MinFreeBytesDefault;


    /// <summary>Free bytes of the data disk; a test replaces it.</summary>
    public Func<long> FreeBytes { get; init; } = () => new DriveInfo(Path.GetFullPath(dataDir)).AvailableFreeSpace;
    /// <summary>Uploads open at once on the whole server (each is a file of uploads-tmp, read from disk at its finish).</summary>
    const int MaxAtOnce = 4;

    /// <summary>The cap of one zip; a test lowers it.</summary>
    public long MaxZip { get; init; } = MaxZipBytes;

    /// <summary>Called with the text for the operator when a zip is kept (the server sets it to the review webhook).</summary>
    public Action<string> Alert { get; init; } = _ => { };

    /// <summary>The links of a save's gallery pictures the zip lacks are fetched at upload time and kept with the zip (<see cref="Restorer.PicturesZip"/>); null: none are.</summary>
    public PictureLinks? Pictures { get; init; }

    static readonly UploadOutcome Paused = new(429, "paused", "Uploads are paused for now, try again later.");
    UploadOutcome QuotaFiles => new(429, "quota", $"You have {AccountFiles} backups waiting for the operator. You can upload again once the operator has reviewed them.");
    UploadOutcome QuotaBytes => new(429, "quota", $"Your backups waiting for the operator already use {Size(AccountBytes)}.");

    static string Size(long bytes) => bytes >= 1 << 30 ? $"{bytes >> 30} GB" : $"{bytes >> 20} MB";

    string RefusedDir(long account) => Path.Combine(dataDir, "refused", account.ToString());

    /// <summary>What the account stores for the operator: waiting zips (with their pictures) and refused ones.</summary>
    (int Files, long Bytes) Stored(long account)
    {
        int files = 0;
        long bytes = 0;
        using (var con = Store.Open(account))
            foreach (var r in con.Rows("SELECT zip_sha256 FROM uploads WHERE account_id = $a"))
            {
                files++;
                var kept = Path.Combine(dataDir, "restores", account.ToString(), (string)r[0] + ".zip");
                foreach (var f in new[] { kept, Restorer.PicturesZip(kept) })
                    if (File.Exists(f)) bytes += new FileInfo(f).Length;
            }
        if (Directory.Exists(RefusedDir(account)))
            foreach (var f in Directory.EnumerateFiles(RefusedDir(account), "*.zip"))
            {
                files++;
                bytes += new FileInfo(f).Length;
            }
        return (files, bytes);
    }

    /// <summary>Bytes stored for the operator on the whole server: the accounts that have a restores/ or refused/ folder (not every account).</summary>
    long StoredTotal() =>
        new[] { "restores", "refused" }.SelectMany(d => Directory.Exists(Path.Combine(dataDir, d)) ? Directory.EnumerateDirectories(Path.Combine(dataDir, d)) : [])
            .Select(d => long.TryParse(Path.GetFileName(d), out var a) && Store.AccountExists(a) ? a : (long?)null).Where(a => a is not null).Distinct()
            .Sum(a => Stored(a!.Value).Bytes);

    /// <summary>The refusal when the account or the server cannot store `add` more bytes (a new zip of that size), else null.</summary>
    public UploadOutcome? Full(long account, long add = 1)
    {
        var (files, bytes) = Stored(account);
        if (files >= AccountFiles) return QuotaFiles;
        if (bytes + add > AccountBytes) return QuotaBytes;
        return StoredTotal() + add > TotalBytes || FreeBytes() - add < MinFreeBytes ? Paused : null;
    }

    /// <summary>The most bytes an upload's file may hold (its first `own` bytes are already on disk) and the answer when it crosses that.</summary>
    (long Cap, UploadOutcome Why) Cap(long account, long own) =>
        new[] { (MaxZip, TooLarge), (AccountBytes - Stored(account).Bytes, QuotaBytes), (TotalBytes - StoredTotal(), Paused), (own + FreeBytes() - MinFreeBytes, Paused) }
            .MinBy(x => x.Item1);

    /// <summary>A zip that arrived whole but is no backup: one `refused` row for the player's restore page, the file kept as refused/&lt;account&gt;/&lt;sha&gt;.zip
    /// (the same bytes twice are kept once) with its reason beside it, for the operator, who deletes it (`review reject`), one alert.
    /// A quota keeps no file and sends no alert.</summary>
    UploadOutcome Refuse(long account, string path, string sha, string message)
    {
        string? kept = null;
        var quota = false;
        lock (decide)
        {
            var file = Path.Combine(RefusedDir(account), sha + ".zip");
            if (File.Exists(file)) kept = file;
            else if (Full(account, new FileInfo(path).Length) is null)
            {
                Directory.CreateDirectory(RefusedDir(account));
                File.Move(path, file, overwrite: true);
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                kept = file;
            }
            else quota = true;
            if (kept is not null) File.WriteAllText(Path.ChangeExtension(kept, ".reason"), message);
        }
        using (var con = Store.Open(account))
            con.Exec("INSERT INTO upload_results (account_id, ts, kind, user_id, handle, handle_kept, reason) VALUES ($a, ?, 'refused', NULL, NULL, 0, ?)", Store.Now(), message);
        if (!quota)
            Alert($"Upload refused: account {account}, {Alerts.Name((string)Store.AccountJson(account)["handle"]!)}, {Alerts.Steam(Store.SteamId64(account))}, {message}. Kept: {RefusedId(account, sha)}");
        return new(400, "invalid", message);
    }

    static string RefusedId(long account, string sha) => $"r{account}-{sha[..8]}";

    /// <summary>A refused zip kept for the operator (id `r&lt;account&gt;-&lt;first 8 of its sha&gt;`).</summary>
    public sealed record RefusedZip(string Id, long Account, string? Handle, long Ts, long Size, string Reason);

    /// <summary>The refused zips of every account, oldest first.</summary>
    public List<RefusedZip> ListRefused()
    {
        var root = Path.Combine(dataDir, "refused");
        var list = new List<(DateTime At, RefusedZip Zip)>();
        if (Directory.Exists(root))
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (long.TryParse(Path.GetFileName(dir), out var account))
                    foreach (var f in Directory.EnumerateFiles(dir, "*.zip"))
                    {
                        var info = new FileInfo(f);
                        var why = Path.ChangeExtension(f, ".reason");
                        var handle = Store.AccountExists(account) ? (string)Store.AccountJson(account)["handle"]! : null;
                        list.Add((info.LastWriteTimeUtc, new(RefusedId(account, Path.GetFileNameWithoutExtension(f)), account, handle,
                                  new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), info.Length, File.Exists(why) ? File.ReadAllText(why) : "")));
                    }
        return [.. list.OrderBy(x => x.At).Select(x => x.Zip)];
    }

    /// <summary>The operator's reject of a refused zip: delete it. False: no such id.</summary>
    public bool RejectRefused(string id)
    {
        lock (decide)
            foreach (var z in ListRefused().Where(z => z.Id == id))
            {
                var file = Directory.EnumerateFiles(RefusedDir(z.Account), z.Id[(z.Id.IndexOf('-') + 1)..] + "*.zip").First();
                File.Delete(file);
                File.Delete(Path.ChangeExtension(file, ".reason"));
                return true;
            }
        return false;
    }

    internal static readonly Regex AdoptableHandle = new(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,31}\z", RegexOptions.ECMAScript);

    readonly ConcurrentDictionary<long, bool> inFlight = new();
    readonly SemaphoreSlim slots = new(MaxAtOnce);
    readonly Lock decide = new();  // one decision at a time: "once per user id" is checked and taken in one step (one server process, ADR 0001)

    /// <summary>An upload between its start and its finish: its file, the parts joined so far (Before: the length before the last one) and its slot.
    /// Writing: a part or the finish holds it (under its lock), so nothing else writes, reads or deletes the file meanwhile.</summary>
    sealed class Open(long account, string path, IDisposable slot)
    {
        public readonly long Account = account;
        public readonly string Path = path;
        public readonly IDisposable Slot = slot;
        public int Next;
        public long Before, Length;
        public double Touched;
        public bool Writing;
    }

    readonly ConcurrentDictionary<string, Open> open = new();

    Store Store => state.Store;

    string Tmp => Path.Combine(dataDir, "uploads-tmp");

    /// <summary>Reserve this account's one upload slot (and one of the server's); null when it has one running already or the server is busy.</summary>
    public IDisposable? Begin(long account)
    {
        if (!inFlight.TryAdd(account, true)) return null;
        if (!slots.Wait(0))
        {
            inFlight.TryRemove(account, out _);
            return null;
        }
        return new Slot(this, account);
    }

    sealed class Slot(Uploads owner, long account) : IDisposable
    {
        public void Dispose()
        {
            owner.slots.Release();
            owner.inFlight.TryRemove(account, out _);
        }
    }

    /// <summary>Is an upload of this account running (a delete waits for it; one that waits for its next part is dropped).</summary>
    public bool Busy(long account)
    {
        Sweep(account);
        return inFlight.ContainsKey(account);
    }

    /// <summary>Open an upload for the account: its id, or null when a part of the account's upload is being received or the server is busy.
    /// An upload of the account that waits for its next part is dropped: this one starts over.</summary>
    public string? Start(long account)
    {
        Sweep(account);
        if (Directory.Exists(Tmp))  // left by a server that stopped mid-upload
            foreach (var f in Directory.EnumerateFiles(Tmp))
                if (!open.Values.Any(u => u.Path == f) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddSeconds(-AbandonedSeconds)) File.Delete(f);
        if (Begin(account) is not { } slot) return null;
        Directory.CreateDirectory(Tmp);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Tmp, id + ".zip");
        File.Create(path).Dispose();
        open[id] = new(account, path, slot) { Touched = state.Clock() };
        return id;
    }

    static readonly UploadOutcome Gone = new(404, "invalid", "This upload is not open: it finished, or got no part for too long. Send the zip again.");
    static readonly UploadOutcome Receiving = new(429, "busy", "A part of this upload is still being received.");
    static readonly UploadOutcome TooLarge = new(413, "too_large", "This file is too large: a backup zip is at most 1 GiB.");

    /// <summary>Claim the account's open upload for a part or the finish; an outcome when it cannot be.</summary>
    UploadOutcome? Claim(long account, string id, out Open u)
    {
        Sweep();
        if (!open.TryGetValue(id, out u!) || u.Account != account) return Gone;
        lock (u)
        {
            if (!open.ContainsKey(id)) return Gone;
            if (u.Writing) return Receiving;
            u.Writing = true;
            return null;
        }
    }

    /// <summary>Append part `index` (from 0) of the upload; the last part may be sent again and replaces itself. A zip over the cap ends the upload.</summary>
    public async Task<UploadOutcome> Part(long account, string id, int index, Stream body)
    {
        if (Claim(account, id, out var u) is { } no) return no;
        UploadOutcome? over = null;
        try
        {
            if (index != u.Next && index != u.Next - 1) return new(409, "invalid", $"Part {index} is out of order: the server expects part {u.Next}.");
            var start = index == u.Next ? u.Length : u.Before;
            await using (var file = new FileStream(u.Path, FileMode.Open, FileAccess.Write))
            {
                file.SetLength(start);
                file.Position = start;
                var buf = new byte[81920];
                var (cap, why) = Cap(account, start);
                int n;
                while ((n = await body.ReadAsync(buf)) > 0)
                {
                    if (file.Position + n > cap) { over = why; break; }
                    await file.WriteAsync(buf.AsMemory(0, n));
                }
                if (over is null) (u.Before, u.Length, u.Next) = (start, file.Position, index + 1);
            }
            return over ?? new(200, "received", $"Part {index} was received.");
        }
        catch (BadHttpRequestException e) when (e.StatusCode == 413)  // a chunked body over the request's limit
        {
            over = TooLarge;
            return new(413, "too_large", "This request is too large: a request is at most 90 MiB, and a larger zip is sent in parts.");
        }
        catch (IOException)
        {
            return new(400, "invalid", $"Part {index} did not arrive whole. Send it again.");
        }
        finally
        {
            if (over is not null) Drop(id);
            else
                lock (u)
                {
                    u.Touched = state.Clock();
                    u.Writing = false;
                }
        }
    }

    /// <summary>Apply the rules to the joined zip (read from its file) for the signed-in account; the upload ends whatever the outcome.</summary>
    public async Task<UploadOutcome> Finish(long account, string id)
    {
        if (Claim(account, id, out var u) is { } no) return no;
        try
        {
            return await Decide(account, u.Path);
        }
        finally
        {
            Drop(id);
        }
    }

    /// <summary>End an upload: delete its file, free its slots. Its caller holds it, or it is abandoned.</summary>
    public void Drop(string id)
    {
        if (!open.TryRemove(id, out var u)) return;
        File.Delete(u.Path);
        u.Slot.Dispose();
    }

    /// <summary>Drop the uploads that got no part for <see cref="AbandonedSeconds"/>, and every one of `account`; never one a part is being written to.</summary>
    void Sweep(long? account = null)
    {
        var now = state.Clock();
        foreach (var (id, u) in open)
        {
            lock (u)
            {
                if (u.Writing || (u.Account != account && now - u.Touched <= AbandonedSeconds)) continue;
                u.Writing = true;
            }
            Drop(id);
        }
    }

    static readonly string[] ProgramFiles = [".py", ".exe", ".bat", ".dll"], PictureFiles = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];

    /// <summary>Why a readable zip that holds no backup is none, in words the player can act on, from its entry names alone (cheap); null: no better reason than the reader's.
    /// A backup inside a folder or a zip (nested), a program and no backup data, or only pictures.</summary>
    static string? Diagnose(string path, Exception why)
    {
        if (why.Message != "not a backup zip") return null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName.ToLowerInvariant()).ToList();
            bool Has(string[] ends) => names.Any(n => ends.Any(n.EndsWith));
            if (Has([".zip", "/manifest.json", "/index.json", "/archive_manifest.json"])) return "Your backup is inside another folder or zip: upload the backup zip itself";
            if (Has(ProgramFiles)) return "This zip holds a program, not a backup: upload the file the program made (it ends in _PRIVATE.zip)";
            if (names.Count > 0 && names.All(n => PictureFiles.Any(n.EndsWith))) return "This zip holds only pictures, not a backup";
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
        return null;
    }

    async Task<UploadOutcome> Decide(long account, string path)
    {
        var sha = Restorer.Sha256(path);
        Restored read;
        try
        {
            read = Importer.Read(path, state.Catalogue);
        }
        catch (Exception e) when (e is RestoreException or InvalidDataException)
        {
            return Refuse(account, path, sha, Diagnose(path, e) ?? $"This file cannot be read as a backup: {e.Message}. Nothing was changed.");
        }
        var uid = long.Parse(read.Model.Identity.Uid);
        // an upload is never restored: only the refusals that are cheap to know now are given at once, the rest waits for the operator
        UploadOutcome? Refused()
        {
            lock (decide) return Refusal(account, uid) is { Kind: "already" or "taken" } refused ? refused : null;
        }
        if (Refused() is { } early) return early;
        var known = false;
        using (var con = Store.Open(account))  // the refusals of Review that are cheap to know, before a fetch that may take a minute
            known = con.Scalar("SELECT 1 FROM uploads WHERE account_id = $a AND zip_sha256 = ?", sha) is not null;
        if (!known && Full(account, new FileInfo(path).Length) is { } full) return full;
        // the pictures the zip lists by link and does not hold: fetched now, so that a wait for review does not lose them (not inside the lock: a photo host may be slow); a zip already waiting has its own
        string? links = path + ".pictures";
        try
        {
            if (known || Pictures is null || state.MissingPictures(read) is not { Count: > 0 } missing || await Pictures.Fetch(missing, links) == 0) links = null;
            lock (decide)
                return Refused() ?? Review(account, sha, path, uid, "waiting for the operator", read.Model.Profile.Handle ?? read.Model.Identity.Handle, links);
        }
        finally
        {
            if (links is not null) File.Delete(links);
        }
    }


    /// <summary>The rules of a restore (the operator's approve runs them again): once per game user id, no user id another
    /// account holds. Uploading is the player's consent to replace the profile, so an account that has played is restored too
    /// (Approve keeps its own user id then). Null: allowed.</summary>
    UploadOutcome? Refusal(long account, long uid)
    {
        if (Store.RestoredBy(uid) is not null)
            return new(409, "already", "A backup of this hunter was already restored on this server, and a backup can be restored once. Nothing was changed.");
        if (uid < 1 || uid >= Store.FirstServerId)
            return new(409, "refused", "This backup has a user id this server does not accept.");
        if (uid != account && Store.AccountExists(uid))
            return new(409, "taken", "Another account on this server already has this backup's user id. Nothing was changed. Ask the operator.");
        return null;
    }

    /// <summary>Keep the zip for the operator and write nothing of it into the account.</summary>
    UploadOutcome Review(long account, string sha, string path, long uid, string note, string? handle, string? links)
    {
        long? keptId = null;
        using (var con = Store.Open(account))
        {
            con.Begin();
            var known = con.Scalar("SELECT 1 FROM uploads WHERE account_id = $a AND zip_sha256 = ?", sha) is not null;
            if (!known)
            {
                if (Full(account, new FileInfo(path).Length + (links is null ? 0 : new FileInfo(links).Length)) is { } full)
                    return full;
                var kept = Path.Combine(dataDir, "restores", account.ToString(), sha + ".zip");
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Copy(path, kept, overwrite: true);
                if (links is not null) File.Move(links, Restorer.PicturesZip(kept), overwrite: true);
                con.Exec("INSERT INTO uploads (account_id, ts, zip_sha256, user_id, note) VALUES ($a, ?, ?, ?, ?)", Store.Now(), sha, uid, note);
                keptId = (long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!;
            }
            con.Commit();
        }
        if (keptId is { } n)
        {
            // the handle is the zip's own text: only plain name characters go into the alert
            var name = Regex.Replace(handle ?? "", @"[^A-Za-z0-9_.-]", "");
            Alert($"Backup waiting for review: id {n}, account {account}, backup user id {uid}, handle {(name.Length > 32 ? name[..32] : name)}, {Alerts.Steam(Store.SteamId64(account))}. Run: review list");
        }
        return new(200, "review", "Received. An operator will check it. Nothing was restored yet.");
    }

    /// <summary>A kept zip waiting for the operator. SteamMatch is information only (the zip's Steam id is the account's verified one), not proof.</summary>
    public sealed record Pending(long Id, long Account, long Ts, long? UserId, string? Handle, bool SteamMatch, string Note, string Sha);

    sealed record Row(long Id, long Account, long Ts, long? UserId, string Note, string Sha);

    List<Row> Rows(long? id = null)
    {
        List<long> accounts;
        using (var all = Store.Open())
            accounts = all.Rows("SELECT id FROM accounts ORDER BY id").Select(r => (long)r[0]).ToList();
        var rows = new List<Row>();
        foreach (var a in accounts)
        {
            using var con = Store.Open(a);
            foreach (var r in con.Rows("SELECT id, ts, user_id, note, zip_sha256 FROM uploads WHERE account_id = $a ORDER BY id"))
                if (id is null || (long)r[0] == id)
                    rows.Add(new((long)r[0], a, (long)r[1], r[2] as long?, (string)r[3], (string)r[4]));
        }
        return rows;
    }

    string Kept(Row r) => Path.Combine(dataDir, "restores", r.Account.ToString(), r.Sha + ".zip");

    /// <summary>The kept zips, oldest first, each with what the zip says of itself (handle null when the file cannot be read now).</summary>
    /// <summary>The account a waiting upload belongs to, null: no such id.</summary>
    public long? AccountOf(long id) => Rows(id).SingleOrDefault()?.Account;

    /// <summary>The operator's `--account` check: the refusal when waiting upload `id` belongs to another account than `want`, else null (also for an unknown id: approve and reject say that).</summary>
    public string? WrongAccount(long id, long want) =>
        AccountOf(id) is { } have && have != want ? $"kept zip {id} belongs to account {have}, not {want}. Nothing was changed." : null;

    public List<Pending> List() => Rows().Select(r =>
    {
        PlayerModel? m = null;
        try { m = Importer.Read(Kept(r), state.Catalogue).Model; }
        catch (Exception e) when (e is RestoreException or InvalidDataException or IOException) { }
        var steam = m?.Identity.SteamId64;
        return new Pending(r.Id, r.Account, r.Ts, r.UserId, m is null ? null : m.Profile.Handle ?? m.Identity.Handle,
                           steam is not null && steam == Store.SteamId64(r.Account), r.Note, r.Sha);
    }).ToList();

    /// <summary>The operator's approve: the rules above, then the restore (the kept zip stays, the waiting row goes; the player gets a notice and a result).
    /// `keepHandle`: the account keeps the name it has, the operator's choice. A refusal is kept as the zip's note, and the player gets a notice of it once. Status 404: no such id.</summary>
    public UploadOutcome Approve(long id, bool keepHandle = false)
    {
        lock (decide)
        {
            if (Rows(id).SingleOrDefault() is not { } row) return new(404, "unknown", $"No kept zip {id}.");
            var path = Kept(row);
            Restored read;
            try { read = Importer.Read(path, state.Catalogue); }
            catch (Exception e) when (e is RestoreException or InvalidDataException or IOException)
            {
                return Refused(row, new(400, "invalid", $"The kept zip cannot be read: {e.Message}. Nothing was changed."), "The kept zip cannot be read, so it was not restored.");
            }
            var model = read.Model;
            var uid = long.Parse(model.Identity.Uid);
            var account = row.Account;
            if (Refusal(account, uid) is { } refused) return Refused(row, refused);
            var zipHandle = model.Profile.Handle ?? model.Identity.Handle;
            var finalId = Store.Played(account) ? account : uid;  // an account that has played keeps its id: what it played stays under it and no restored id can clash with the ids handed out here
            var current = (string)Store.AccountJson(account)["handle"]!;
            var adopt = !keepHandle && zipHandle is not null && AdoptableHandle.IsMatch(zipHandle) && (Store.AccountByHandle(zipHandle) is not { } holder || holder == account);
            var handle = adopt ? zipHandle! : current;
            var summary = Summary(model, handle != current, zipHandle is not null && !adopt, keepHandle);
            try
            {
                var result = Restorer.Apply(state, path, row.Sha, read, account, finalId, handle, exists: true, backupDatabase: false, dataDir,
                    con =>
                    {
                        con.Exec("INSERT INTO restored_ids (user_id, account_id, ts) VALUES (?, ?, ?)", uid, account, Store.Now());
                        con.Exec("DELETE FROM uploads WHERE account_id = $a AND id = ?", id);
                        con.Exec("INSERT INTO upload_results (account_id, ts, kind, user_id, handle, handle_kept, reason) VALUES ($a, ?, 'restored', ?, ?, ?, '')",
                                 Store.Now(), finalId, handle, adopt ? 0 : 1);
                        Notify(con, $"{summary} Your user id is {finalId} and your name is {handle}.");
                    }, pictureLinks: Pictures);
                return new(200, "restored", summary, result.Report);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)  // the user id or the handle was taken between the check and the write
            {
                return Refused(row, new(409, "taken", "This backup's user id or name was taken a moment ago. Nothing was changed."));
            }
        }
    }

    /// <summary>A refused approve: the reason becomes the zip's note and the player's notice, unless it is the note already. The player gets the
    /// outcome's words unless `forPlayer` is given (the operator's outcome may hold a technical detail, such as a path).</summary>
    UploadOutcome Refused(Row row, UploadOutcome outcome, string? forPlayer = null)
    {
        var reason = forPlayer ?? outcome.Message;
        if (reason == row.Note) return outcome;
        using var con = Store.Open(row.Account);
        con.Begin();
        con.Exec("UPDATE uploads SET note = ? WHERE account_id = $a AND id = ?", reason, row.Id);
        Notify(con, $"Your backup was not restored: {reason}");
        con.Commit();
        return outcome;
    }

    /// <summary>A message in the account's inbox from the server's notice sender (the thread Chat/conversations lists).</summary>
    internal static void Notify(Db con, string text) =>
        con.Exec("INSERT INTO messages (account_id, sender, content, ts) VALUES ($a, ?, ?, ?)", State.SYSTEM_SENDER, text, Store.Now());

    /// <summary>The operator's reject: delete the kept zip and its row, keep the reason (may be empty) and tell the player. False: no such id.</summary>
    public bool Reject(long id, string? reason = null)
    {
        lock (decide)
        {
            if (Rows(id).SingleOrDefault() is not { } row) return false;
            reason = reason?.Trim() ?? "";
            using (var con = Store.Open(row.Account))
            {
                con.Begin();
                con.Exec("DELETE FROM uploads WHERE account_id = $a AND id = ?", id);
                con.Exec("INSERT INTO upload_results (account_id, ts, kind, user_id, handle, handle_kept, reason) VALUES ($a, ?, 'rejected', ?, NULL, 0, ?)",
                         Store.Now(), row.UserId, reason);
                Notify(con, reason == "" ? "Your backup was rejected by the operator." : $"Your backup was rejected by the operator: {reason}");
                con.Commit();
            }
            File.Delete(Kept(row));
            File.Delete(Restorer.PicturesZip(Kept(row)));
            return true;
        }
    }

    static string Summary(PlayerModel m, bool handleChanged, bool handleKept, bool keptByOperator)
    {
        var parts = new List<string> { $"{m.Items.Count} items" };
        if (m.Wallet is not null) parts.Add("gold and silver");
        if (m.Skills is not null) parts.Add("skills");
        if (m.Stats is not null) parts.Add("statistics");
        parts.Add($"{m.Achievements.Count} achievements");
        parts.Add($"{m.Trophies.Count} trophies");
        var text = $"The backup was restored: {string.Join(", ", parts)}.";
        if (handleChanged) text += " The name is now the one in the backup.";
        else if (handleKept) text += keptByOperator ? " The name was not changed: you keep the one you chose here."
                                                     : " The name was not changed, because the one in the backup is not allowed or belongs to another player.";
        return text;
    }

    /// <summary>What the operator decided about a kept zip. Kind: restored (UserId, Handle, HandleKept: the account kept its own name), rejected (UserId, Reason) or refused (the server could not read the upload: Reason).</summary>
    public sealed record Decision(long Ts, string Kind, long? UserId, string? Handle, bool HandleKept, string Reason);

    /// <summary>A zip waiting for review as the account page shows it. Format: <c>ours</c> (our backup tool's manifest.json, read by the first reader) or <c>other</c> (another tool's, read by another).</summary>
    public sealed record Waiting(long Ts, string Note, string Format);

    /// <summary>What the account page shows: the user id restored (once), the zips waiting for review, what was decided about the others, and whether the account can
    /// store one more zip (Room: under its file and byte limits; the server-wide limits answer an upload with <c>paused</c>).</summary>
    public (long? RestoredUserId, List<Waiting> Review, List<Decision> Decisions, bool Room) Status(long account)
    {
        using var con = Store.Open();
        var restored = con.Scalar("SELECT user_id FROM restored_ids WHERE account_id = ?", account) as long?;
        using var mine = Store.Open(account);
        var review = mine.Rows("SELECT ts, note, zip_sha256 FROM uploads WHERE account_id = $a ORDER BY id").Select(r => new Waiting((long)r[0], (string)r[1], FormatOf(Path.Combine(dataDir, "restores", account.ToString(), (string)r[2] + ".zip")))).ToList();
        var decisions = mine.Rows("SELECT ts, kind, user_id, handle, handle_kept, reason FROM upload_results WHERE account_id = $a ORDER BY id")
            .Select(r => new Decision((long)r[0], (string)r[1], r[2] as long?, r[3] as string, (long)r[4] != 0, (string)r[5])).ToList();
        var (files, bytes) = Stored(account);
        return (restored, review, decisions, files < AccountFiles && bytes < AccountBytes);
    }

    static string FormatOf(string kept)
    {
        try
        {
            return Importer.FormatOf(kept);
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { return "other"; }
    }

    /// <summary>Delete the account and everything of it: its rows, tokens and sessions (the foreign keys), the kept zips and the photos.</summary>
    public void Delete(long account)
    {
        lock (decide)
        {
            Store.DeleteAccount(account);
            foreach (var dir in new[] { "restores", "photos", "refused" })
            {
                var path = Path.Combine(dataDir, dir, account.ToString());
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
    }
}
