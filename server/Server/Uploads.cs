// The community upload: a signed-in player's backup zip and what the server does with it (docs/server.md#upload-a-backup-zip).
// Every zip is kept for the operator and never restored automatically: `review approve <id>` runs the rules and the restore.
// A Steam id match is shown to the operator as information, not proof of ownership.
using System.Collections.Concurrent;
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
    /// <summary>Zips one account may have waiting for review.</summary>
    public const int MaxReview = 3;
    /// <summary>Uploads open at once on the whole server (each is a file of uploads-tmp, read from disk at its finish).</summary>
    const int MaxAtOnce = 4;

    /// <summary>The cap of one zip; a test lowers it.</summary>
    public long MaxZip { get; init; } = MaxZipBytes;

    /// <summary>Called with the text for the operator when a zip is kept (the server sets it to the review webhook).</summary>
    public Action<string> Alert { get; init; } = _ => { };

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
        var over = false;
        try
        {
            if (index != u.Next && index != u.Next - 1) return new(409, "invalid", $"Part {index} is out of order: the server expects part {u.Next}.");
            var start = index == u.Next ? u.Length : u.Before;
            await using (var file = new FileStream(u.Path, FileMode.Open, FileAccess.Write))
            {
                file.SetLength(start);
                file.Position = start;
                var buf = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buf)) > 0)
                {
                    if (file.Position + n > MaxZip) { over = true; break; }
                    await file.WriteAsync(buf.AsMemory(0, n));
                }
                if (!over) (u.Before, u.Length, u.Next) = (start, file.Position, index + 1);
            }
            return over ? TooLarge : new(200, "received", $"Part {index} was received.");
        }
        catch (BadHttpRequestException e) when (e.StatusCode == 413)  // a chunked body over the request's limit
        {
            over = true;
            return new(413, "too_large", "This request is too large: a request is at most 90 MiB, and a larger zip is sent in parts.");
        }
        catch (IOException)
        {
            return new(400, "invalid", $"Part {index} did not arrive whole. Send it again.");
        }
        finally
        {
            if (over) Drop(id);
            else
                lock (u)
                {
                    u.Touched = state.Clock();
                    u.Writing = false;
                }
        }
    }

    /// <summary>Apply the rules to the joined zip (read from its file) for the signed-in account; the upload ends whatever the outcome.</summary>
    public UploadOutcome Finish(long account, string id)
    {
        if (Claim(account, id, out var u) is { } no) return no;
        try
        {
            return Decide(account, u.Path);
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

    UploadOutcome Decide(long account, string path)
    {
        var sha = Restorer.Sha256(path);
        Restored read;
        try
        {
            read = Importer.Read(path, state.Catalogue);
        }
        catch (RestoreException e)
        {
            return new(400, "invalid", $"This file cannot be read as a backup: {e.Message}. Nothing was changed.");
        }
        catch (InvalidDataException e)
        {
            return new(400, "invalid", $"This file cannot be read as a backup: {e.Message}. Nothing was changed.");
        }
        var uid = long.Parse(read.Model.Identity.Uid);
        lock (decide)
        {
            // an upload is never restored: only the refusals that are cheap to know now are given at once, the rest waits for the operator
            if (Refusal(account, uid) is { Kind: "already" or "taken" } refused) return refused;
            return Review(account, sha, path, uid, "waiting for the operator", read.Model.Profile.Handle ?? read.Model.Identity.Handle);
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
    UploadOutcome Review(long account, string sha, string path, long uid, string note, string? handle)
    {
        long? keptId = null;
        using (var con = Store.Open(account))
        {
            con.Begin();
            var known = con.Scalar("SELECT 1 FROM uploads WHERE account_id = $a AND zip_sha256 = ?", sha) is not null;
            if (!known)
            {
                if ((long)con.Scalar("SELECT COUNT(*) FROM uploads WHERE account_id = $a")! >= MaxReview)
                    return new(429, "busy", $"{MaxReview} backups of yours already wait for review, so this one was not kept. Wait for the operator, or delete your data.");
                var kept = Path.Combine(dataDir, "restores", account.ToString(), sha + ".zip");
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Copy(path, kept, overwrite: true);
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
                    });
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

    /// <summary>What the operator decided about a kept zip. Kind: restored (UserId, Handle, HandleKept: the account kept its own name) or rejected (UserId, Reason).</summary>
    public sealed record Decision(long Ts, string Kind, long? UserId, string? Handle, bool HandleKept, string Reason);

    /// <summary>What the account page shows: the user id restored (once), the zips waiting for review, and what was decided about the others.</summary>
    public (long? RestoredUserId, List<(long Ts, string Note)> Review, List<Decision> Decisions) Status(long account)
    {
        using var con = Store.Open();
        var restored = con.Scalar("SELECT user_id FROM restored_ids WHERE account_id = ?", account) as long?;
        using var mine = Store.Open(account);
        var review = mine.Rows("SELECT ts, note FROM uploads WHERE account_id = $a ORDER BY id").Select(r => ((long)r[0], (string)r[1])).ToList();
        var decisions = mine.Rows("SELECT ts, kind, user_id, handle, handle_kept, reason FROM upload_results WHERE account_id = $a ORDER BY id")
            .Select(r => new Decision((long)r[0], (string)r[1], r[2] as long?, r[3] as string, (long)r[4] != 0, (string)r[5])).ToList();
        return (restored, review, decisions);
    }

    /// <summary>Delete the account and everything of it: its rows, tokens and sessions (the foreign keys), the kept zips and the photos.</summary>
    public void Delete(long account)
    {
        lock (decide)
        {
            Store.DeleteAccount(account);
            foreach (var dir in new[] { "restores", "photos" })
            {
                var path = Path.Combine(dataDir, dir, account.ToString());
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
    }
}
