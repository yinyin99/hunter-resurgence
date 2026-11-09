// The one database of a server: schema and migrations (run once, at startup), accounts and tokens.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Resurgence.Server;

/// <summary>The server's database file. Opening a Store migrates it; every connection after that is a pooled one from
/// Open(account), scoped to that account. A local server is the same Store with one account.</summary>
public sealed class Store
{
    /// <summary>Tables with an `account_id` column: Db refuses SQL on them that does not bind the account.</summary>
    public static readonly string[] PlayerTables =
        ["items", "wallet", "skills", "kills", "harvests", "expeditions", "events", "pending", "photos", "messages",
         "profile", "statistics", "achievements", "trophies", "lodges", "extras", "restore_audit", "uploads", "upload_results"];

    /// <summary>PRAGMA user_version of the schema below. A change adds a step to Migrate and raises this.</summary>
    public const int Version = 6;

    const string A = "account_id INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE";

    // the cookie of the website and of the launcher's start page; a separate table, so a cookie is never a game token
    const string WebSessions = """
        CREATE TABLE web_sessions (
            hash TEXT PRIMARY KEY,                           -- SHA-256 of the cookie value, hex
            account_id INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            expires_ts INTEGER NOT NULL);
        """;

    // the zips a community upload kept without restoring them (manual review), and the user ids a restore has taken: one
    // account restores a game user id once, so the id is the key of a table of its own (not per player: it is the check across players)
    const string Uploads = $"""
        CREATE TABLE uploads (
            id INTEGER PRIMARY KEY, {A}, ts INTEGER NOT NULL, zip_sha256 TEXT NOT NULL, user_id INTEGER, note TEXT NOT NULL);
        CREATE INDEX uploads_account ON uploads (account_id, id);
        CREATE TABLE restored_ids (
            user_id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE, ts INTEGER NOT NULL);
        CREATE INDEX restored_ids_account ON restored_ids (account_id);
        """;

    // what the operator decided about a kept zip, for the player: kind restored (user_id, handle, handle_kept) or rejected (user_id, reason)
    const string UploadResults = $"""
        CREATE TABLE upload_results (
            id INTEGER PRIMARY KEY, {A}, ts INTEGER NOT NULL, kind TEXT NOT NULL, user_id INTEGER, handle TEXT, handle_kept INTEGER NOT NULL, reason TEXT NOT NULL);
        CREATE INDEX upload_results_account ON upload_results (account_id, id);
        """;

    // friend edges: account_id asked other_id (`request`), or the two are friends (`friend`: accepted here, or one's restored list
    // holds the other). No foreign keys: a restored edge may name a player with no account here yet; it counts once that account
    // exists (the reads join accounts). DeleteAccount drops both directions.
    const string Friends = """
        CREATE TABLE friends (
            account_id INTEGER NOT NULL, other_id INTEGER NOT NULL, state TEXT NOT NULL,
            PRIMARY KEY (account_id, other_id)) WITHOUT ROWID;
        CREATE INDEX friends_other ON friends (other_id);
        """;

    /// <summary>The first user id the server gives to an account of its own. A user id that comes from a backup is
    /// never this high, so the two never collide; the last one is int.MaxValue (the game's ids are 32-bit).</summary>
    public const long FirstServerId = 2_000_000_000;

    // Ids that come from outside (a restored zip's guid and kill_id) are keyed with the account; the ids the server
    // allocates for the client (expedition, photo) stay rowids and are looked up with the account as well.
    static readonly string Schema = $"""
        CREATE TABLE accounts (
            id INTEGER PRIMARY KEY,                          -- the game's user id
            steam_id64 TEXT UNIQUE,
            handle TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created_ts INTEGER NOT NULL,
            claimed_ts INTEGER,
            banned INTEGER NOT NULL DEFAULT 0,
            account TEXT NOT NULL);                          -- the connect reply's `account` object
        CREATE TABLE tokens (
            hash TEXT PRIMARY KEY,                           -- SHA-256 of the token, hex
            account_id INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            created_ts INTEGER NOT NULL,
            expires_ts INTEGER);
        CREATE INDEX tokens_account ON tokens (account_id);
        {Friends}
        {WebSessions}
        {Uploads}
        {UploadResults}

        CREATE TABLE items (
            {A}, guid INTEGER NOT NULL, item_id INTEGER NOT NULL, slot INTEGER NOT NULL, metadata TEXT NOT NULL, units INTEGER,
            PRIMARY KEY (account_id, guid)) WITHOUT ROWID;
        CREATE TABLE wallet (
            {A}, currency TEXT NOT NULL, amount REAL NOT NULL,
            PRIMARY KEY (account_id, currency)) WITHOUT ROWID;
        CREATE TABLE skills (
            {A}, kind TEXT NOT NULL, key INTEGER NOT NULL, xp REAL NOT NULL,
            PRIMARY KEY (account_id, kind, key)) WITHOUT ROWID;
        CREATE TABLE kills (
            {A}, kill_id INTEGER NOT NULL, individual INTEGER, species INTEGER, harvest TEXT NOT NULL,
            PRIMARY KEY (account_id, kill_id));
        CREATE INDEX kills_individual ON kills (account_id, individual);
        CREATE TABLE harvests (
            {A}, kill_id INTEGER NOT NULL, body TEXT NOT NULL,
            PRIMARY KEY (account_id, kill_id));
        CREATE TABLE expeditions (
            id INTEGER PRIMARY KEY, {A}, reserve INTEGER, location_id INTEGER, start_ts REAL NOT NULL, end_ts REAL,
            first_event INTEGER, distance INTEGER, last_seen REAL,
            stats TEXT);  -- a closed hunt's statistics (State.HuntStats), stored at its close
        CREATE INDEX expeditions_account ON expeditions (account_id, end_ts);
        CREATE TABLE events (
            id INTEGER PRIMARY KEY, {A}, event INTEGER, body TEXT NOT NULL,
            individual GENERATED ALWAYS AS (json_extract(body, '$.individual')) VIRTUAL);
        CREATE INDEX events_account ON events (account_id, id);
        CREATE INDEX events_individual ON events (account_id, event, individual, id);
        CREATE TABLE pending (id INTEGER PRIMARY KEY, {A}, body TEXT NOT NULL);
        CREATE INDEX pending_account ON pending (account_id, id);
        CREATE TABLE photos (
            id INTEGER PRIMARY KEY, {A}, hash INTEGER, photo_type INTEGER, shot_id INTEGER, individual INTEGER, file TEXT);
        CREATE INDEX photos_account ON photos (account_id);
        CREATE TABLE messages (   -- account_id is the recipient
            id INTEGER PRIMARY KEY, {A}, sender INTEGER NOT NULL, content TEXT NOT NULL, ts INTEGER NOT NULL,
            read INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX messages_unread ON messages (account_id, read);
        CREATE INDEX messages_sender ON messages (account_id, sender, id);

        -- what a restored backup zip carries beyond the tables above (PlayerModel); JSON in text columns (ADR 0001)
        CREATE TABLE profile (account_id INTEGER PRIMARY KEY REFERENCES accounts (id) ON DELETE CASCADE, body TEXT NOT NULL);
        CREATE TABLE statistics (
            account_id INTEGER PRIMARY KEY REFERENCES accounts (id) ON DELETE CASCADE,
            user_stats TEXT NOT NULL, distance REAL NOT NULL, duration REAL NOT NULL, bust_through_score INTEGER, lane_bandit_score INTEGER);
        CREATE TABLE achievements (
            {A}, id INTEGER NOT NULL, category_id INTEGER NOT NULL, group_id INTEGER NOT NULL, value TEXT NOT NULL, triggers TEXT NOT NULL,
            PRIMARY KEY (account_id, id));
        CREATE TABLE trophies (n INTEGER PRIMARY KEY, {A}, source TEXT NOT NULL, id TEXT NOT NULL, node TEXT NOT NULL);
        CREATE INDEX trophies_account ON trophies (account_id, source, id);
        CREATE TABLE lodges (n INTEGER PRIMARY KEY, {A}, source TEXT NOT NULL, id TEXT NOT NULL, node TEXT NOT NULL);
        CREATE INDEX lodges_account ON lodges (account_id, source, id);
        CREATE TABLE extras (
            {A}, path TEXT NOT NULL, body TEXT NOT NULL,
            PRIMARY KEY (account_id, path));
        CREATE TABLE restore_audit (
            id INTEGER PRIMARY KEY, {A}, ts INTEGER NOT NULL, zip_sha256 TEXT NOT NULL, mode TEXT NOT NULL, report TEXT NOT NULL);
        CREATE INDEX restore_audit_account ON restore_audit (account_id, ts);
        """;

    readonly string connectionString;

    readonly string path;

    public Store(string path)
    {
        this.path = path;
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Migrate();
    }

    /// <summary>A pooled connection for one account (null: only the shared tables, accounts and tokens).</summary>
    public Db Open(long? account = null) => new(connectionString, account);

    /// <summary>A consistent copy of the whole database beside it (`VACUUM INTO`, file name `&lt;db&gt;.bak-&lt;UTC time&gt;`); returns its path.</summary>
    public string Backup()
    {
        var stamp = $"{path}.bak-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        var target = stamp;
        for (var n = 1; File.Exists(target); n++)
            target = $"{stamp}-{n}";
        using var con = Open();
        con.Script($"VACUUM INTO '{target.Replace("'", "''")}'");
        return target;
    }

    void Migrate()
    {
        using var con = Open();
        con.Script("PRAGMA journal_mode = WAL");  // persistent: set once, with the schema
        con.Begin();
        var version = (long)con.Scalar("PRAGMA user_version")!;
        if (version > Version)
            throw new InvalidOperationException($"the database is from a newer server (schema {version}, this one knows {Version})");
        if (version == 0)
        {
            if ((long)con.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'items'")! > 0)
                throw new InvalidOperationException("the database predates accounts: delete it and start again");
            con.Script(Schema);
        }
        else
        {
            if (version < 2) con.Script(WebSessions);
            if (version < 3) con.Script(Uploads);
            if (version < 4) con.Script(UploadResults);
            if (version < 5) con.Script("ALTER TABLE expeditions ADD COLUMN stats TEXT");
            if (version < 6) con.Script(Friends);
        }
        if (version < Version)
            con.Script($"PRAGMA user_version = {Version}");
        con.Commit();
    }

    // ---- accounts

    public bool AccountExists(long id)
    {
        using var con = Open();
        return con.Scalar("SELECT 1 FROM accounts WHERE id = ?", id) is not null;
    }

    public long? AccountBySteam(string steamId64)
    {
        using var con = Open();
        return con.Scalar("SELECT id FROM accounts WHERE steam_id64 = ?", steamId64) as long?;
    }

    public long? AccountByHandle(string handle)
    {
        using var con = Open();
        return con.Scalar("SELECT id FROM accounts WHERE handle = ?", handle) as long?;
    }

    /// <summary>The next free user id of the server's own range. Callers that then create the account hold one lock, so
    /// two sign-ins never get the same one (one server process, ADR 0001).</summary>
    public long NextServerId()
    {
        using var con = Open();
        var next = ((con.Scalar("SELECT MAX(id) FROM accounts WHERE id >= ?", FirstServerId) as long?) ?? FirstServerId - 1) + 1;
        return next <= int.MaxValue ? next : throw new InvalidOperationException("the server's user ids are used up");
    }

    public string? HandleOf(long id)
    {
        using var con = Open();
        return con.Scalar("SELECT handle FROM accounts WHERE id = ?", id) as string;
    }

    /// <summary>Has the account played: a hunt was opened (`expeditions`) or an event came in (`events`). Only its starting rows otherwise.</summary>
    public bool Played(long id)
    {
        using var con = Open(id);
        return con.Scalar("SELECT 1 FROM expeditions WHERE account_id = $a LIMIT 1") is not null
            || con.Scalar("SELECT 1 FROM events WHERE account_id = $a LIMIT 1") is not null;
    }

    /// <summary>The account that restored a backup of this game user id (once per user id); null when none has.</summary>
    public long? RestoredBy(long userId)
    {
        using var con = Open();
        return con.Scalar("SELECT account_id FROM restored_ids WHERE user_id = ?", userId) as long?;
    }

    /// <summary>Delete an account and, by the foreign keys, everything of it: every per-player row, its tokens, its cookie
    /// sessions, its Steam id and handle; and its friend edges both ways. Files are the caller's.</summary>
    public void DeleteAccount(long id)
    {
        using var con = Open();
        con.Begin();
        con.Exec("DELETE FROM friends WHERE account_id = ?1 OR other_id = ?1", id);
        con.Exec("DELETE FROM accounts WHERE id = ?", id);
        con.Commit();
    }

    // ---- friends (the Friends table)

    // the accounts at the other end of an edge that count: not banned, not `excluded` (the recorded player's), never the account itself
    const string Counted = "SELECT id FROM accounts WHERE banned = 0 AND id IS NOT ?2 AND id <> ?1";
    const string FriendIds = "SELECT other_id FROM friends WHERE account_id = ?1 AND state = 'friend' UNION SELECT account_id FROM friends WHERE other_id = ?1 AND state = 'friend'";

    /// <summary>The account's friends, the accounts that asked it (incoming requests) and those it asked (pending), by user id;
    /// an edge counts only while both accounts exist here.</summary>
    public (List<long> Friends, List<long> Requests, List<long> Pending) FriendsOf(long id, long? excluded)
    {
        using var con = Open();
        List<long> Ids(string sql) => [.. con.Rows($"SELECT * FROM ({sql}) INTERSECT {Counted}", id, excluded).Select(r => (long)r[0])];
        return (Ids(FriendIds),
                Ids($"SELECT account_id FROM friends WHERE other_id = ?1 AND state = 'request' EXCEPT SELECT * FROM ({FriendIds})"),
                Ids($"SELECT other_id FROM friends WHERE account_id = ?1 AND state = 'request' EXCEPT SELECT * FROM ({FriendIds})"));
    }

    /// <summary>Friend/add by `id` of `other`: accepts other's open request (they become friends), else asks other; nothing
    /// when they are friends already.</summary>
    public void AddFriend(long id, long other)
    {
        using var con = Open();
        con.Begin();
        if (con.Scalar($"SELECT 1 FROM ({FriendIds}) WHERE other_id = ?2", id, other) is null)
        {
            var accept = con.Scalar("SELECT 1 FROM friends WHERE account_id = ? AND other_id = ? AND state = 'request'", other, id) is not null;
            con.Exec("INSERT OR REPLACE INTO friends (account_id, other_id, state) VALUES (?, ?, ?)", id, other, accept ? "friend" : "request");
            if (accept) con.Exec("DELETE FROM friends WHERE account_id = ? AND other_id = ?", other, id);
        }
        con.Commit();
    }

    /// <summary>Friend/remove: every edge between the two goes (a declined, cancelled or removed request or friendship).</summary>
    public void RemoveFriend(long id, long other)
    {
        using var con = Open();
        con.Exec("DELETE FROM friends WHERE (account_id = ?1 AND other_id = ?2) OR (account_id = ?2 AND other_id = ?1)", id, other);
    }

    /// <summary>A restore's edge (inside its transaction): a `friend` edge replaces a request of the same direction; any other
    /// edge the pair has already stays.</summary>
    public static void RestoreFriendEdge(Db con, long id, long other, string state)
    {
        if (id > 0 && other > 0 && id != other)
            con.Exec("INSERT INTO friends (account_id, other_id, state) VALUES (?, ?, ?)"
                     + " ON CONFLICT (account_id, other_id) DO UPDATE SET state = excluded.state WHERE excluded.state = 'friend'", id, other, state);
    }

    /// <summary>The Steam id the account signed in with; null when it has none.</summary>
    public string? SteamId64(long id)
    {
        using var con = Open();
        return con.Scalar("SELECT steam_id64 FROM accounts WHERE id = ?", id) as string;
    }

    /// <summary>The connect reply's `account` object of an account.</summary>
    public JsonObject AccountJson(long id)
    {
        using var con = Open();
        return JsonNode.Parse((string)con.Scalar("SELECT account FROM accounts WHERE id = ?", id)!)!.AsObject();
    }

    /// <summary>The account's place on the hunterscore board among the accounts not banned, except `excluded`: 1 for the highest;
    /// equal hunterscores are numbered in account id order (a missing hunterscore is 0).</summary>
    public long Rank(long id, long? excluded = null)
    {
        using var con = Open();
        return (long)con.Scalar("SELECT 1 + COUNT(*) FROM accounts o JOIN accounts a ON a.id = ?1"
                                + " WHERE o.banned = 0 AND o.id IS NOT ?2 AND o.id <> a.id"
                                + " AND (COALESCE(json_extract(o.account, '$.hunterscore'), 0) > COALESCE(json_extract(a.account, '$.hunterscore'), 0)"
                                + " OR (COALESCE(json_extract(o.account, '$.hunterscore'), 0) = COALESCE(json_extract(a.account, '$.hunterscore'), 0) AND o.id < a.id))",
                                id, excluded)!;
    }

    /// <summary>One page of the hunterscore board (the order of Rank) and how many accounts are on it.</summary>
    public (List<(long Rank, string Handle, long Hunterscore)> Rows, long Total) Board(long? excluded, int offset, int limit)
    {
        using var con = Open();
        var rows = con.Rows("SELECT handle, COALESCE(json_extract(account, '$.hunterscore'), 0) AS hs FROM accounts"
                            + " WHERE banned = 0 AND id IS NOT ? ORDER BY hs DESC, id LIMIT ? OFFSET ?", excluded, limit, offset)
            .Select((r, i) => (offset + i + 1L, (string)r[0], Convert.ToInt64(r[1]))).ToList();
        return (rows, (long)con.Scalar("SELECT COUNT(*) FROM accounts WHERE banned = 0 AND id IS NOT ?", excluded)!);
    }

    /// <summary>The minigames' best scores of the restored accounts (statistics, from a backup), by kind.</summary>
    public static readonly IReadOnlyDictionary<string, string> GameColumns =
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<string, string> { ["lanebandit"] = "lane_bandit_score", ["bustthrough"] = "bust_through_score" });

    /// <summary>A minigame's whole board (a kind of <see cref="GameColumns"/>): the accounts with a score above 0, highest first, equal scores in account id order; the caller pages it.
    /// ponytail: statistics is a per-player table, so each account is read on its own connection (Db refuses a cross-account read); one query per account
    /// on every page view, fine for the restored accounts of a community server. Copy the scores to the accounts row if that gets slow.</summary>
    public List<(string Handle, long Score)> GameBoard(string kind, long? excluded)
    {
        var column = GameColumns[kind];  // a fixed name, never the request's
        List<(long Id, string Handle)> accounts;
        using (var con = Open())
            accounts = [.. con.Rows("SELECT id, handle FROM accounts WHERE banned = 0 AND id IS NOT ? ORDER BY id", excluded).Select(r => ((long)r[0], (string)r[1]))];
        var scored = new List<(string Handle, long Score)>();
        foreach (var (id, handle) in accounts)
        {
            using var con = Open(id);
            if (con.Scalar($"SELECT {column} FROM statistics WHERE account_id = $a") is long score && score > 0)
                scored.Add((handle, score));
        }
        return [.. scored.OrderByDescending(s => s.Score)];  // a stable sort: equal scores keep the account id order
    }

    /// <summary>An account on the boards and the profile pages, by user id (a long) or handle (a string, any case); null when
    /// unknown, banned or `excluded`.</summary>
    public long? Listed(object idOrHandle, long? excluded)
    {
        using var con = Open();
        return con.Scalar($"SELECT id FROM accounts WHERE {(idOrHandle is long ? "id" : "handle")} = ? AND banned = 0 AND id IS NOT ?", idOrHandle, excluded) as long?;
    }

    /// <summary>The accounts whose handle contains the text (any case), by handle, at most `limit`; never `self`, a banned one or `excluded`.</summary>
    public List<long> SearchHandles(string text, long self, long? excluded, int limit)
    {
        using var con = Open();
        return [.. con.Rows("SELECT id FROM accounts WHERE banned = 0 AND id <> ?1 AND id IS NOT ?2 AND instr(lower(handle), lower(?3)) > 0 ORDER BY handle COLLATE NOCASE LIMIT ?4",
                            self, excluded, text, limit).Select(r => (long)r[0])];
    }

    /// <summary>Add an account and, in the same transaction, let seed fill its rows (the server's starting data, or an
    /// importer's). id is the game's user id; account is the connect reply's `account` object. Throws when the id,
    /// the handle or the Steam id is taken.</summary>
    public void CreateAccount(long id, string handle, JsonObject account, string? steamId64 = null, Action<Db>? seed = null)
    {
        using var con = Open(id);
        con.Begin();
        con.Exec("INSERT INTO accounts (id, steam_id64, handle, created_ts, account) VALUES (?, ?, ?, ?, ?)",
                 id, steamId64, handle, Now(), Py.Dumps(account));
        seed?.Invoke(con);
        con.Commit();
    }

    // ---- tokens

    /// <summary>A new token for the account (the game's session, an API access token, a JWT: all the same here). The
    /// database keeps only its hash, so this is the one time anyone sees it. lifetime null: it never expires. prefix:
    /// text in front of the random part (a JWT's header and payload).</summary>
    public string CreateToken(long accountId, TimeSpan? lifetime = null, string prefix = "") =>
        AddToken(prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)), accountId, lifetime);  // 40 hex, the shape of the game's session

    /// <summary>Keep a token somebody else built (a signed JWT); returns it.</summary>
    public string AddToken(string token, long accountId, TimeSpan? lifetime = null)
    {
        var now = Now();
        using var con = Open();
        con.Exec("INSERT INTO tokens (hash, account_id, created_ts, expires_ts) VALUES (?, ?, ?, ?)",
                 Hash(token), accountId, now, lifetime is { } l ? now + (long)l.TotalSeconds : null);
        return token;
    }

    /// <summary>The HMAC key of the JWTs (HS256): 32 random bytes in a file beside the database, made on first use. The
    /// client never checks a signature; the server does when a refresh token comes back.</summary>
    public byte[] JwtKey
    {
        get
        {
            if (jwtKey is { } known) return known;
            lock (keyGate)
            {
                var file = path + ".jwtkey";
                if (!File.Exists(file)) File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(32));
                return jwtKey = File.ReadAllBytes(file);
            }
        }
    }

    volatile byte[]? jwtKey;
    readonly Lock keyGate = new();

    /// <summary>The account a token belongs to; null when it is unknown, expired or its account is banned.</summary>
    public long? ResolveToken(string token)
    {
        using var con = Open();
        return con.Scalar("SELECT t.account_id FROM tokens t JOIN accounts a ON a.id = t.account_id"
                          + " WHERE t.hash = ? AND (t.expires_ts IS NULL OR t.expires_ts > ?) AND a.banned = 0",
                          Hash(token), Now()) as long?;
    }

    // ---- web sessions (the cookie of the website and of the start page)

    public string CreateWebSession(long accountId, TimeSpan lifetime)
    {
        var cookie = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        using var con = Open();
        con.Exec("INSERT INTO web_sessions (hash, account_id, expires_ts) VALUES (?, ?, ?)", Hash(cookie), accountId, Now() + (long)lifetime.TotalSeconds);
        return cookie;
    }

    /// <summary>The account of a cookie; null when it is unknown, expired or its account is banned.</summary>
    public long? ResolveWebSession(string cookie)
    {
        using var con = Open();
        return con.Scalar("SELECT s.account_id FROM web_sessions s JOIN accounts a ON a.id = s.account_id"
                          + " WHERE s.hash = ? AND s.expires_ts > ? AND a.banned = 0", Hash(cookie), Now()) as long?;
    }

    public void DeleteWebSession(string cookie)
    {
        using var con = Open();
        con.Exec("DELETE FROM web_sessions WHERE hash = ?", Hash(cookie));
    }

    /// <summary>Drop the tokens and cookies that have expired (at startup).</summary>
    public void PurgeExpired()
    {
        using var con = Open();
        var now = Now();
        con.Exec("DELETE FROM tokens WHERE expires_ts <= ?", now);
        con.Exec("DELETE FROM web_sessions WHERE expires_ts <= ?", now);
    }

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
