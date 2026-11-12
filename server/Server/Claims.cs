// Archive claims: a signed-in player claims the public profile the operator's archive kept for them (docs/server.md#archive-claims, ADR 0008).
// The archived replies are restored onto the claiming account with its own user id (it never changes); below hunterscore 10,000 at once, else the operator decides.
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Resurgence.Restore;

namespace Resurgence.Server;

/// <summary>A lookup of an archived name: Player null when there is none (or no archive); Limited when the account used its lookups of the day;
/// NameFree when the archived handle is a plain name nobody else has, so the account could take it.</summary>
public sealed record LookupResult(ArchivedPlayer? Player, bool Limited, bool NameFree);

/// <summary>What a claim came to. Kind: restored, review (kept for the operator), refused (Message says why), unknown (no such archived name), limit, confirm (the account has played and the overwrite was not confirmed).</summary>
public sealed record ClaimOutcome(int Status, string Kind, string Message);

public sealed class Claims(State state, Archive archive, string dataDir)
{
    /// <summary>Lookups one account may make per UTC day.</summary>
    public const int LookupsPerDay = 5;
    /// <summary>An archived player with this hunterscore or more is restored only by the operator.</summary>
    public const long ReviewScore = 10000;

    /// <summary>Called with the text for the operator when a claim waits for review (the server sets it to the review webhook).</summary>
    public Action<string> Alert { get; init; } = _ => { };

    readonly Lock decide = new();  // one decision at a time (one server process, ADR 0001)

    Store Store => state.Store;

    /// <summary>Is there an archive: without one every lookup is "not found" and the website hides the claim box.</summary>
    public bool Enabled => archive.Enabled;

    bool NameFree(long account, string handle) =>
        Uploads.AdoptableHandle.IsMatch(handle) && (Store.AccountByHandle(handle) is not { } holder || holder == account);

    /// <summary>Count one lookup of the account today; false when the day's lookups are used (the count is a row, so a restart keeps it).</summary>
    bool Spend(long account)
    {
        using var con = Store.Open();
        con.Begin();
        var day = Store.Now() / 86400;
        con.Exec("DELETE FROM claim_lookups WHERE account_id = ? AND day < ?", account, day);
        if (((con.Scalar("SELECT n FROM claim_lookups WHERE account_id = ? AND day = ?", account, day) as long?) ?? 0) >= LookupsPerDay) return false;
        con.Exec("INSERT INTO claim_lookups (account_id, day, n) VALUES (?, ?, 1) ON CONFLICT (account_id, day) DO UPDATE SET n = n + 1", account, day);
        con.Commit();
        return true;
    }

    /// <summary>The archived player named exactly `name` (any case), counted as one of the account's lookups of the day.</summary>
    public LookupResult Lookup(long account, string name)
    {
        if (!archive.Enabled) return new(null, false, false);
        if (!Spend(account)) return new(null, true, false);
        var player = archive.Find(name);
        return new(player, false, player is not null && NameFree(account, player.Handle));
    }

    /// <summary>The archived player named `name`, not counted as a lookup: for the sign-up page, where there is no account yet.</summary>
    public ArchivedPlayer? Peek(string name) => archive.Find(name);

    /// <summary>The account's own claim, null when it made none.</summary>
    public sealed record OwnClaim(string State, string Handle, long Ts);

    public OwnClaim? Of(long account)
    {
        using var con = Store.Open();
        return con.Rows("SELECT state, handle, ts FROM claims WHERE account_id = ?", account).Select(r => new OwnClaim((string)r[0], (string)r[1], (long)r[2])).SingleOrDefault();
    }

    /// <summary>The account claims the archived player named `name` (the operator may give a user id too). One claim per account, ever. `useOldName`: take the archived handle when it is allowed and free.
    /// `byOperator`: no limit, no review, no confirmation; the account's earlier claim row, if any, is replaced.</summary>
    public ClaimOutcome Claim(long account, string name, bool useOldName, bool confirmOverwrite, bool byOperator = false)
    {
        if (!archive.Enabled) return new(404, "unknown", "There is no archive on this server.");
        lock (decide)
        {
            if (!byOperator && Of(account) is not null) return new(409, "refused", "This account has made its one claim already.");
            var player = archive.Find(name)
                         ?? (byOperator && long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? archive.Find(uid) : null);
            if (player is null)  // an unknown name costs a lookup: a claim is not a way to list the archive
                return byOperator || Spend(account) ? new(404, "unknown", "There is no archived player with this name.") : new(429, "limit", "You used your lookups for today. Try again tomorrow.");
            if (byOperator) return Restore(account, player, useOldName);
            if (!confirmOverwrite && Store.Played(account))
                return new(409, "confirm", "This account has played. A claim replaces its items, skills, achievements, trophies and lodges; hunts, kills and photos made here stay. Confirm to go on.");
            if (player.Hunterscore >= ReviewScore) return Review(account, player, useOldName, $"waiting for the operator: hunterscore {ReviewScore} or more");
            using (var con = Store.Open())
                if (con.Scalar("SELECT 1 FROM claims WHERE archived_uid = ? AND account_id <> ?", player.Uid, account) is not null)
                    return Review(account, player, useOldName, "waiting for the operator: already claimed by another account");
            return Restore(account, player, useOldName);
        }
    }

    /// <summary>Keep the claim for the operator: a `waiting` row, a notice for the player and an alert. Nothing is restored.</summary>
    ClaimOutcome Review(long account, ArchivedPlayer p, bool useOldName, string note)
    {
        long id;
        using (var con = Store.Open(account))
        {
            con.Begin();
            id = con.Insert("INSERT INTO claims (account_id, archived_uid, handle, hunterscore, state, ts, note, keep_handle) VALUES (?, ?, ?, ?, 'waiting', ?, ?, ?)",
                            account, p.Uid, p.Handle, p.Hunterscore, Store.Now(), note, useOldName ? 0L : 1L);
            Uploads.Notify(con, "Your claim of an old profile waits for an operator. Nothing was restored yet.");
            con.Commit();
        }
        var handle = Regex.Replace(p.Handle, @"[^A-Za-z0-9_.-]", "");  // the archive's own text: only plain name characters go into the alert
        Alert($"Claim waiting for review: c{id}, account {account}, archived {p.Uid}, handle {(handle.Length > 32 ? handle[..32] : handle)}, hunterscore {p.Hunterscore}, {Alerts.Steam(Store.SteamId64(account))}. Run: review list");
        return new(200, "review", "Received. An operator will check it. Nothing was restored yet.");
    }

    double Balance(long account, string currency)
    {
        using var con = Store.Open(account);
        return con.Scalar("SELECT amount FROM wallet WHERE account_id = $a AND currency = ?", currency) is double d ? d : 0;
    }

    /// <summary>The restore itself: the archived replies replace the account's profile rows, the account keeps its user id and its hunts, kills and photos, and the claim row is written in the same transaction.</summary>
    ClaimOutcome Restore(long account, ArchivedPlayer p, bool useOldName)
    {
        Restored read;
        try
        {
            var replies = archive.Replies(p.Uid);
            if (replies.Count == 0) return new(409, "refused", "The archive holds nothing of this player.");
            read = Importer.FromReplies(p.Uid.ToString(CultureInfo.InvariantCulture), p.Handle, replies, state.Catalogue);
        }
        catch (RestoreException e)
        {
            Console.Error.WriteLine($"claim of account {account}: archived {p.Uid}: {e.Message}");
            return new(409, "refused", "This profile cannot be restored.");
        }
        var current = (string)Store.AccountJson(account)["handle"]!;
        var adopt = useOldName && NameFree(account, p.Handle);
        var handle = adopt ? p.Handle : current;
        var (em, gm) = (Balance(account, "em"), Math.Max(Balance(account, "gm"), Restorer.GmFloor));  // an archived profile has no wallet: the account keeps its own (gm$ at least the floor)
        var m = read.Model;
        var text = $"Your old profile was restored: {m.Items.Count} items, {m.Trophies.Count} trophies and {m.Lodges.Count} lodges, and its skills, statistics and achievements where the archive had them. "
                   + $"Your user id is unchanged and your name is {handle}.";
        if (useOldName && !adopt) text += " The old name was not used: it is not allowed or belongs to another player.";
        try
        {
            Restorer.Apply(state, null, "archive:" + p.Uid.ToString(CultureInfo.InvariantCulture), read, account, account, handle, exists: true, backupDatabase: false, dataDir, con =>
            {
                con.Exec("INSERT INTO claims (account_id, archived_uid, handle, hunterscore, state, ts, decided_ts, note, keep_handle) VALUES (?1, ?2, ?3, ?4, 'restored', ?5, ?5, '', ?6) "
                         + "ON CONFLICT (account_id) DO UPDATE SET archived_uid = ?2, handle = ?3, hunterscore = ?4, state = 'restored', decided_ts = ?5, note = '', keep_handle = ?6",
                         account, p.Uid, p.Handle, p.Hunterscore, Store.Now(), useOldName ? 0L : 1L);
                PlayerRows.SetWallet(con, "em", em);
                PlayerRows.SetWallet(con, "gm", gm);
                Uploads.Notify(con, text);
            });
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)  // the name was taken between the check and the write
        {
            return new(409, "refused", "This name was taken a moment ago. Nothing was changed.");
        }
        return new(200, "restored", text);
    }

    /// <summary>A claim waiting for the operator.</summary>
    public sealed record Pending(long Id, long Account, long Uid, string Handle, long Hunterscore, long Ts, string Note, bool KeepHandle);

    /// <summary>The waiting claims, oldest first (one when `id` is given).</summary>
    public List<Pending> Waiting(long? id = null)
    {
        using var con = Store.Open();
        return con.Rows("SELECT id, account_id, archived_uid, handle, hunterscore, ts, note, keep_handle FROM claims WHERE state = 'waiting' AND (?1 IS NULL OR id = ?1) ORDER BY id", id)
            .Select(r => new Pending((long)r[0], (long)r[1], (long)r[2], (string)r[3], (long)r[4], (long)r[5], (string)r[6], (long)r[7] != 0)).ToList();
    }

    /// <summary>The operator's approve of a waiting claim: the same restore as an automatic one. `keepHandle`: the account keeps its own name whatever the player chose. Status 404: no such waiting claim.</summary>
    public ClaimOutcome Approve(long id, bool keepHandle = false)
    {
        lock (decide)
        {
            if (Waiting(id).SingleOrDefault() is not { } row) return new(404, "unknown", $"No waiting claim c{id}.");
            if (!archive.Enabled) return new(409, "refused", "There is no archive on this server.");
            return Restore(row.Account, new(row.Uid, row.Handle, row.Hunterscore, 0, 0, 0, false, false, false), !keepHandle && !row.KeepHandle);
        }
    }

    /// <summary>The operator's reject: the claim is kept as `rejected` with the reason (may be empty), which the player gets in the inbox. False: no such waiting claim.</summary>
    public bool Reject(long id, string? reason = null)
    {
        lock (decide)
        {
            if (Waiting(id).SingleOrDefault() is not { } row) return false;
            reason = reason?.Trim() ?? "";
            using var con = Store.Open(row.Account);
            con.Begin();
            con.Exec("UPDATE claims SET state = 'rejected', decided_ts = ?, note = ? WHERE id = ?", Store.Now(), reason, id);
            Uploads.Notify(con, reason == "" ? "Your claim of an old profile was rejected by the operator." : $"Your claim of an old profile was rejected by the operator: {reason}");
            con.Commit();
            return true;
        }
    }
}
