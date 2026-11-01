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
         "profile", "statistics", "achievements", "trophies", "lodges", "extras", "restore_audit"];

    /// <summary>PRAGMA user_version of the schema below. A change adds a step to Migrate and raises this.</summary>
    public const int Version = 1;

    const string A = "account_id INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE";

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
            first_event INTEGER, distance INTEGER, last_seen REAL);
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

    public Store(string path)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Migrate();
    }

    /// <summary>A pooled connection for one account (null: only the shared tables, accounts and tokens).</summary>
    public Db Open(long? account = null) => new(connectionString, account);

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
            con.Script($"PRAGMA user_version = {Version}");
        }
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

    /// <summary>The connect reply's `account` object of an account.</summary>
    public JsonObject AccountJson(long id)
    {
        using var con = Open();
        return JsonNode.Parse((string)con.Scalar("SELECT account FROM accounts WHERE id = ?", id)!)!.AsObject();
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
    /// database keeps only its hash, so this is the one time anyone sees it. lifetime null: it never expires.</summary>
    public string CreateToken(long accountId, TimeSpan? lifetime = null)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));  // 40 hex, the shape of the game's session
        var now = Now();
        using var con = Open();
        con.Exec("INSERT INTO tokens (hash, account_id, created_ts, expires_ts) VALUES (?, ?, ?, ?)",
                 Hash(token), accountId, now, lifetime is { } l ? now + (long)l.TotalSeconds : null);
        return token;
    }

    /// <summary>The account a token belongs to; null when it is unknown, expired or its account is banned.</summary>
    public long? ResolveToken(string token)
    {
        using var con = Open();
        return con.Scalar("SELECT t.account_id FROM tokens t JOIN accounts a ON a.id = t.account_id"
                          + " WHERE t.hash = ? AND (t.expires_ts IS NULL OR t.expires_ts > ?) AND a.banned = 0",
                          Hash(token), Now()) as long?;
    }

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
