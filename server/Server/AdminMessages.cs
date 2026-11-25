// The operator's messages to players (docs/server.md#messages): one inbox message from the notice sender (the row a review decision writes),
// the log of what was sent, and the replies players left to that sender.
using Microsoft.Data.Sqlite;

namespace Resurgence.Server;

public static class AdminMessages
{
    public const int MaxLength = 2000;

    /// <summary>One line of the sent log. Account is null for a message to every account (Recipients of them).</summary>
    public sealed record Sent(long Ts, long? Account, string? Handle, long Recipients, string Text);

    /// <summary>A player's message to the notice sender.</summary>
    public sealed record Reply(long Ts, long Account, string Handle, string Text);

    /// <summary>Why a text cannot be sent (empty, or over <see cref="MaxLength"/> characters); null when it can. The text is kept as written.</summary>
    public static string? Refuse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "the text is empty"
        : text.Length > MaxLength ? $"the text is {text.Length} characters: at most {MaxLength}"
        : null;

    /// <summary>The arguments after `admin message`: `--account <id or handle>` or `--all`, `[--yes]` and the text, in any order; null when they are not that.</summary>
    public static (string? Account, bool All, bool Yes, string Text)? ParseSend(string[] a)
    {
        string? account = null, text = null;
        bool all = false, yes = false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] == "--all" && !all) all = true;
            else if (a[i] == "--yes" && !yes) yes = true;
            else if (a[i] == "--account" && account is null && i + 1 < a.Length && !string.IsNullOrWhiteSpace(a[i + 1])) account = a[++i];
            else if (text is null && !a[i].StartsWith("--", StringComparison.Ordinal)) text = a[i];
            else return null;
        return text is not null && all != (account is not null) ? (account, all, yes, text) : null;
    }

    /// <summary>The arguments after `admin messages`: `[--account <id or handle>] [--sent]` in any order; null when they are not that.</summary>
    public static (string? Account, bool Sent)? ParseList(string[] a)
    {
        string? account = null;
        var sent = false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] == "--sent" && !sent) sent = true;
            else if (a[i] == "--account" && account is null && i + 1 < a.Length && !string.IsNullOrWhiteSpace(a[i + 1])) account = a[++i];
            else return null;
        return (account, sent);
    }

    /// <summary>Puts the text in one account's inbox (account null: every account's) as an unread message from the notice sender and logs it.
    /// Returns how many accounts got it. The text is checked (<see cref="Refuse"/>) and the account exists: the caller's.</summary>
    public static int Send(Store store, long? account, string text)
    {
        var now = Store.Now();
        if (account is { } one)
        {
            using var con = store.Open(one);
            con.Begin();
            Uploads.Notify(con, text);
            Log(con, one, 1, text, now);
            con.Commit();
            return 1;
        }
        // the log row first, so a crash half way still shows in `--sent`; its count is corrected at the end
        var ids = store.AccountIds();
        long logId;
        using (var log = store.Open())
        {
            Log(log, null, ids.Count, text, now);
            logId = (long)log.Scalar("SELECT last_insert_rowid()")!;
        }
        var sent = 0;
        foreach (var id in ids)
        {
            using var con = store.Open(id);
            try { Uploads.Notify(con, text); sent++; }
            catch (SqliteException e) when (e.SqliteErrorCode == 19) { }  // the account was deleted since the list was read
        }
        if (sent != ids.Count)
        {
            using var log = store.Open();
            log.Exec("UPDATE admin_messages SET recipients = ? WHERE id = ?", sent, logId);
        }
        return sent;
    }

    static void Log(Db con, long? account, long recipients, string text, long ts) =>
        con.Exec("INSERT INTO admin_messages (ts, account_id, recipients, text) VALUES (?, ?, ?, ?)", ts, account, recipients, text);

    /// <summary>What was sent, newest first; with an account, the messages to it and to every account.</summary>
    public static List<Sent> SentLog(Store store, long? account)
    {
        using var con = store.Open();
        return [.. con.Rows("SELECT m.ts, m.account_id, a.handle, m.recipients, m.text FROM admin_messages m LEFT JOIN accounts a ON a.id = m.account_id "
                            + "WHERE ?1 IS NULL OR m.account_id IS NULL OR m.account_id = ?1 ORDER BY m.id DESC", account)
            .Select(r => new Sent((long)r[0], r[1] as long?, r[2] as string, (long)r[3], (string)r[4]))];
    }

    /// <summary>The messages players left to the notice sender (every `messages` row that does not come from it), newest first; of one account or of all.</summary>
    public static List<Reply> Replies(Store store, long? account)
    {
        // ponytail: one query per account (the table is per player); an index over every account if the player count makes it slow
        List<long> ids = account is { } one ? [one] : store.AccountIds();
        var replies = new List<Reply>();
        foreach (var id in ids)
        {
            using var con = store.Open(id);
            var rows = con.Rows("SELECT ts, content FROM messages WHERE account_id = $a AND sender <> ?", State.SYSTEM_SENDER);
            if (rows.Count == 0) continue;
            var handle = store.HandleOf(id) ?? "";
            replies.AddRange(rows.Select(r => new Reply((long)r[0], id, handle, (string)r[1])));
        }
        return [.. replies.OrderByDescending(r => r.Ts)];
    }
}
