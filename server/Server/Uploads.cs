// The community upload: a signed-in player's backup zip and what the server does with it (docs/server.md#upload-a-backup-zip).
// A zip whose Steam id is the one the player signed in with restores automatically, once per game user id on this server.
// That match is enough for the restore, not proof of ownership; disputes go to the operator, who can reset the account from the kept zip.
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Resurgence.Restore;

namespace Resurgence.Server;

/// <summary>What an upload came to. Kind: restored, review (kept for the operator, nothing restored), already (this user id
/// was restored before), taken (another account holds the user id), invalid (not a usable backup), busy, too_large.
/// Message is plain words for the player; Report the technical report of a restore.</summary>
public sealed record UploadOutcome(int Status, string Kind, string Message, string? Report = null);

public sealed class Uploads(State state, string dataDir)
{
    /// <summary>The largest zip taken, compressed. A real backup is under 1 MiB, one with pictures a few.</summary>
    public const int MaxZipBytes = 64 << 20;
    /// <summary>The reader's caps for an upload: what the zip may hold uncompressed (the default 1 GiB is for a local PC).</summary>
    public static readonly Limits ZipLimits = new(TotalBytes: 256L << 20);
    /// <summary>Zips one account may have waiting for review.</summary>
    public const int MaxReview = 3;
    /// <summary>Uploads being read at once on the whole server (each is held in memory).</summary>
    const int MaxAtOnce = 4;

    static readonly Regex AdoptableHandle = new(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,31}\z", RegexOptions.ECMAScript);

    readonly ConcurrentDictionary<long, bool> inFlight = new();
    readonly SemaphoreSlim slots = new(MaxAtOnce);
    readonly Lock decide = new();  // one decision at a time: "once per user id" is checked and taken in one step (one server process, ADR 0001)

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

    /// <summary>Is an upload of this account running (a delete waits for it).</summary>
    public bool Busy(long account) => inFlight.ContainsKey(account);

    /// <summary>Apply the rules to the zip (as bytes, already read within the limit) for the signed-in account.</summary>
    public UploadOutcome Accept(long account, byte[] zip)
    {
        Directory.CreateDirectory(Tmp);
        var path = Path.Combine(Tmp, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, zip);
        try
        {
            return Decide(account, path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    UploadOutcome Decide(long account, string path)
    {
        var sha = Restorer.Sha256(path);
        Restored read;
        try
        {
            read = Importer.Read(path, state.Catalogue, ZipLimits);
        }
        catch (RestoreException e)
        {
            return new(400, "invalid", $"This file cannot be read as a backup: {e.Message}. Nothing was changed.");
        }
        catch (InvalidDataException e)
        {
            return new(400, "invalid", $"This file cannot be read as a backup: {e.Message}. Nothing was changed.");
        }
        var model = read.Model;
        var uid = long.Parse(model.Identity.Uid);
        lock (decide)
        {
            if (Store.RestoredBy(uid) is not null)
                return new(409, "already", "A backup of this hunter was already restored on this server, and a backup can be restored once. Nothing was changed. If something went wrong, ask the operator to reset it.");
            var steam = Store.SteamId64(account);
            if (uid < 1 || uid >= Store.FirstServerId)
                return Review(account, sha, path, uid, "its user id is not one this server accepts",
                    "This backup has a user id this server does not accept.");
            if (model.Identity.SteamId64 is null)
                return Review(account, sha, path, uid, "the backup names no Steam account",
                    "This backup does not say which Steam account it belongs to (a public backup, or one from an older backup tool).");
            if (model.Identity.SteamId64 != steam)
                return Review(account, sha, path, uid, "the backup names another Steam account than the signed-in one",
                    "This backup was made for a different Steam account than the one you are signed in with.");
            if (uid != account && Store.AccountExists(uid))
                return new(409, "taken", "Another account on this server already has this backup's user id. Nothing was changed. Ask the operator.");
            if (uid != account && Store.Played(account))
                return Review(account, sha, path, uid, "the account has played, so its user id cannot change",
                    "You have already played with this account, so its user id cannot change to the one in the backup.");

            var zipHandle = model.Profile.Handle ?? model.Identity.Handle;
            var current = (string)Store.AccountJson(account)["handle"]!;
            var adopt = zipHandle is not null && AdoptableHandle.IsMatch(zipHandle) && (Store.AccountByHandle(zipHandle) is not { } holder || holder == account);
            var handle = adopt ? zipHandle! : current;
            try
            {
                var result = Restorer.Apply(state, path, sha, read, account, uid, handle, exists: true, backupDatabase: false, dataDir,
                    con => con.Exec("INSERT INTO restored_ids (user_id, account_id, ts) VALUES (?, ?, ?)", uid, account, Store.Now()));
                return new(200, "restored", Summary(model, handle != current, zipHandle is not null && !adopt), result.Report);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)  // the user id or the handle was taken between the check and the write
            {
                return new(409, "taken", "This backup's user id or name was taken a moment ago. Nothing was changed. Ask the operator.");
            }
        }
    }

    /// <summary>Keep the zip for the operator and write nothing of it into the account.</summary>
    UploadOutcome Review(long account, string sha, string path, long uid, string note, string why)
    {
        using var con = Store.Open(account);
        con.Begin();
        var known = con.Scalar("SELECT 1 FROM uploads WHERE account_id = $a AND zip_sha256 = ?", sha) is not null;
        if (!known)
        {
            if ((long)con.Scalar("SELECT COUNT(*) FROM uploads WHERE account_id = $a")! >= MaxReview)
                return new(429, "busy", $"{why} {MaxReview} backups of yours already wait for review, so this one was not kept. Wait for the operator, or delete your data.");
            var kept = Path.Combine(dataDir, "restores", account.ToString(), sha + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.Copy(path, kept, overwrite: true);
            con.Exec("INSERT INTO uploads (account_id, ts, zip_sha256, user_id, note) VALUES ($a, ?, ?, ?, ?)", Store.Now(), sha, uid, note);
        }
        con.Commit();
        return new(200, "review", $"{why} It was kept, and nothing was restored. The operator will look at it.");
    }

    static string Summary(PlayerModel m, bool handleChanged, bool handleKept)
    {
        var parts = new List<string> { $"{m.Items.Count} items" };
        if (m.Wallet is not null) parts.Add("your gold and silver");
        if (m.Skills is not null) parts.Add("your skills");
        if (m.Stats is not null) parts.Add("your statistics");
        parts.Add($"{m.Achievements.Count} achievements");
        parts.Add($"{m.Trophies.Count} trophies");
        var text = $"Your backup was restored: {string.Join(", ", parts)}. Start the game from the launcher to see them.";
        if (handleChanged) text += " Your name is now the one in the backup.";
        else if (handleKept) text += " Your name was not changed, because the one in the backup is not allowed or belongs to another player.";
        return text;
    }

    /// <summary>What the account page shows: the user id restored (once), and the zips waiting for review.</summary>
    public (long? RestoredUserId, List<(long Ts, string Note)> Review) Status(long account)
    {
        using var con = Store.Open();
        var restored = con.Scalar("SELECT user_id FROM restored_ids WHERE account_id = ?", account) as long?;
        using var mine = Store.Open(account);
        var review = mine.Rows("SELECT ts, note FROM uploads WHERE account_id = $a ORDER BY id").Select(r => ((long)r[0], (string)r[1])).ToList();
        return (restored, review);
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
