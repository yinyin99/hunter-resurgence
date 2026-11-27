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
         "profile", "statistics", "achievements", "trophies", "lodges", "extras", "loadouts", "restore_audit", "uploads", "upload_results", "wallet_log",
         "missions", "mission_prefs", "mission_rollbacks", "challenges", "daily_missions", "own_limit_removals", "unethical_kills", "harvest_links", "purchases"];

    /// <summary>PRAGMA user_version of the schema below. A change adds a step to Migrate and raises this.</summary>
    public const int Version = 46;

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
            id INTEGER PRIMARY KEY AUTOINCREMENT, {A}, ts INTEGER NOT NULL, zip_sha256 TEXT NOT NULL, user_id INTEGER, note TEXT NOT NULL);
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

    // the account's missions: a row per active (state 2) or completed (3) mission, with the ids of its finished objectives in the
    // order they were finished (JSON); a mission without a row is not started. Ids may have no definition (a restored account's).
    // mission_prefs is the page's switches and favourite mission packs (JSON), one row per account once it has used missions.
    // since: the last event before the mission was activated here (NULL: not known, a row from before schema 30); seen: the events after it are the ones the mission's
    // progress is checked against (State.Mark); kept: the finished objectives a restore brought or a harvest of another player's kill finished (JSON; State.HarvesterEvent), `*` for a mission completed before the restore; earlier: the
    // objectives finished before `seen` (JSON): no harvest after it finished them; rules (schema 35): the fingerprint of the rule each finished objective was finished under
    // (JSON object: objective id to fingerprint, `{}` for none; State.Fingerprint), which the audit compares with today's rule.
    const string NoRules = "{}";  // the `rules` of a mission row that has finished no objective since schema 35 (a raw interpolated string has no literal braces)

    const string Missions = $"""
        CREATE TABLE missions (
            {A}, mission_id INTEGER NOT NULL, state INTEGER NOT NULL, objectives TEXT NOT NULL,
            since INTEGER, seen INTEGER NOT NULL DEFAULT 0, kept TEXT NOT NULL DEFAULT '[]', earlier TEXT NOT NULL DEFAULT '[]', rules TEXT NOT NULL DEFAULT '{NoRules}',
            PRIMARY KEY (account_id, mission_id)) WITHOUT ROWID;
        CREATE TABLE mission_prefs (account_id INTEGER PRIMARY KEY REFERENCES accounts (id) ON DELETE CASCADE, body TEXT NOT NULL);
        {MissionRollbacks}
        """;

    // what a rollback of the missions (State.RollbackMissions) reopened for an account: JSON with the missions and objectives, and the missions it kept unlocked
    const string MissionRollbacks = $"""
        CREATE TABLE IF NOT EXISTS mission_rollbacks (id INTEGER PRIMARY KEY, {A}, ts INTEGER NOT NULL, record TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS mission_rollbacks_account ON mission_rollbacks (account_id, id);
        """;

    // the copies of an item the pass at start removed from an account because it owned more than its limit (State.EnforceOwnLimits): the removed row whole (guid, item, slot, metadata, units), `ts` unix seconds;
    // own_limit_pass (shared, not per player) has a row once the pass has run: it runs once, so a later start never removes a copy
    const string OwnLimitRemovals = $"""
        CREATE TABLE IF NOT EXISTS own_limit_removals (id INTEGER PRIMARY KEY, {A}, ts INTEGER NOT NULL, guid INTEGER NOT NULL, item_id INTEGER NOT NULL, slot INTEGER NOT NULL, metadata TEXT NOT NULL, units INTEGER);
        CREATE INDEX IF NOT EXISTS own_limit_removals_account ON own_limit_removals (account_id, id);
        CREATE TABLE IF NOT EXISTS own_limit_pass (done_ts INTEGER NOT NULL);
        """;

    // the account's daily missions: one JSON row (frozen, the day of the streak and its date, the finished objectives, the broken streak, the days completed here)
    const string DailyMissions = """
        CREATE TABLE daily_missions (account_id INTEGER PRIMARY KEY REFERENCES accounts (id) ON DELETE CASCADE, body TEXT NOT NULL);
        """;

    // the account's completed challenges: a challenge without a row is not completed. `id` is the row number the reply shows; `objectives` is the definition's text
    // as it was when the challenge was completed, with `completed` added; the hash is the definition's.
    const string Challenges = $"""
        CREATE TABLE IF NOT EXISTS challenges (
            id INTEGER PRIMARY KEY, {A}, challenge_id INTEGER NOT NULL, objectives TEXT NOT NULL, objectives_hash TEXT NOT NULL, challenge_date TEXT,
            UNIQUE (account_id, challenge_id));
        """;

    /// <summary>The completed challenges of a restored zip: its saved reply of `Challenge/get_user_challenges` (the extras file of that name; `completed` 0 or 1, false or true),
    /// each completed row as saved. `$a` is the account; the migration reads every account and replaces it with the column.</summary>
    internal const string RestoredChallenges = """
        INSERT OR IGNORE INTO challenges (account_id, challenge_id, objectives, objectives_hash, challenge_date)
        SELECT $a, CAST(j.key AS INTEGER),
               CASE WHEN json_type(j.value, '$.objectives') = 'text' THEN json_extract(j.value, '$.objectives') ELSE '[]' END,
               CASE WHEN json_type(j.value, '$.objectives_hash') = 'text' THEN json_extract(j.value, '$.objectives_hash') ELSE '' END,
               CASE WHEN json_type(j.value, '$.challenge_date') = 'text' THEN json_extract(j.value, '$.challenge_date') END
        FROM extras e, json_each(e.body) j
        WHERE e.account_id = $a AND e.path GLOB 'Challenge_get_user_challenges*.json' AND json_type(e.body) = 'object' AND json_type(j.value) = 'object'
          AND j.key NOT GLOB '*[^0-9]*' AND json_extract(j.value, '$.completed') = 1
        ORDER BY e.path, CAST(j.key AS INTEGER)
        """;

    // an account's loadouts: the id is global (the key of the page's PUT), so it is the table's own rowid; entries is the JSON list of items the page reads
    const string Loadouts = $"""
        CREATE TABLE loadouts (
            id INTEGER PRIMARY KEY, {A}, name TEXT NOT NULL, color INTEGER NOT NULL, entries TEXT NOT NULL);
        CREATE INDEX loadouts_account ON loadouts (account_id, id);
        """;

    /// <summary>The loadouts of a restored zip: its saved reply of `/game/user/loadout` (the extras file `game_user_loadout.json`), in order, under new ids
    /// (the saved ids are global to the game's accounts). A name that is no text, a color that is no whole number and items that are no list are read as
    /// "", 0 and []. `$a` is the account; the migration reads every account and replaces it with the column.</summary>
    internal const string RestoredLoadouts = """
        INSERT INTO loadouts (account_id, name, color, entries)
        SELECT $a,
               CASE WHEN json_type(j.value, '$.name') = 'text' THEN json_extract(j.value, '$.name') ELSE '' END,
               CASE WHEN json_type(j.value, '$.color') = 'integer' THEN json_extract(j.value, '$.color') ELSE 0 END,
               CASE WHEN json_type(j.value, '$.items') = 'array' THEN json_extract(j.value, '$.items') ELSE '[]' END
        FROM extras e, json_each(e.body) j
        WHERE e.account_id = $a AND e.path = 'game_user_loadout.json' AND json_type(e.body) = 'array' AND json_type(j.value) = 'object'
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

    // one row per change of a wallet balance made here: `amount` is signed, `after` the balance after it, `item_ids` the item ids bought (JSON array).
    // `base` is the amount without any bonus and `bonus_event` the events_log row that changed it (null: none); a row from before they existed has base NULL, which reads as `amount` (docs/server.md#reward-bases-and-events)
    const string WalletLog = $"""
        CREATE TABLE wallet_log (
            n INTEGER PRIMARY KEY, {A}, ts INTEGER NOT NULL, currency TEXT NOT NULL, amount REAL NOT NULL, after REAL NOT NULL, summary TEXT NOT NULL, item_ids TEXT NOT NULL,
            base REAL, bonus_event INTEGER);
        CREATE INDEX wallet_log_account ON wallet_log (account_id, currency, n);
        """;

    // the periods that changed what a hunt paid or how it was played: a gm$ or mission bonus (multiplier set) or a seasonal event (none); shared, not per player.
    // kind: gm_bonus | mission_bonus | seasonal | ...; applies_to: what the multiplier multiplies (`harvest_gm`), empty for none; times are Unix seconds, the period is start_ts <= t < end_ts
    const string EventsLog = """
        CREATE TABLE IF NOT EXISTS events_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, kind TEXT NOT NULL, start_ts INTEGER NOT NULL, end_ts INTEGER NOT NULL,
            multiplier REAL, applies_to TEXT NOT NULL DEFAULT '');
        """;

    // the stored kills `admin ethics --apply` (State.JudgeKills) leaves in a state today's ammunition rule (#582, State.AmmoCheck) would not give them, flagged so they can be found later
    // (`admin ethics list`); no player sees the flag. kind kept: an unethical kill (disqualified) that keeps a mount, `taxidermied` (1 or 0) and/or the lodge (`lodges.n`) and `slot` (`slot_id`) that
    // hold it, with its `reason` (silver_on_normal | not_silver_on_possessed | not_permitted), `ammo` (the item id of its first hit outside the list), `basis` (how the animal's kind was decided:
    // coat | species | hunt, State.KindOf) and `lost_score` (the stored score it had: 0 when it had none). kind undecided (reason and basis undecided): a kill of a hunt the schema-41 step could not decide
    // (`expeditions.event_undecided`) whose coat proves nothing, which keeps the verdict it was made under and which the rule could judge otherwise: `if_normal` and `if_possessed` (ethical | unethical).
    // species, coat (its spawn's `texture`) and ts (when it was flagged, Unix seconds) on both
    const string UnethicalKills = $"""
        CREATE TABLE IF NOT EXISTS unethical_kills (
            {A}, kill_id INTEGER NOT NULL, kind TEXT NOT NULL, species INTEGER, coat INTEGER, reason TEXT NOT NULL, ammo INTEGER, basis TEXT, if_normal TEXT, if_possessed TEXT,
            taxidermied INTEGER NOT NULL DEFAULT 0, lodge INTEGER, slot INTEGER, lost_score REAL NOT NULL DEFAULT 0, ts INTEGER NOT NULL,
            PRIMARY KEY (account_id, kill_id));
        """;

    // schema 44 (#1041): a multiplayer harvest State.TryCredit gave the member who shot the animal last (`owner`, its kill `kill_id`), noted on the harvester's side (account_id) by the animal as the
    // harvester's game knows it (`individual`, `spawn` its latest event 7 then), so the trophy shot the harvester takes of it afterwards is that kill's (State.Heartbeat, event 4). Restorer.Adopt moves both ids
    const string HarvestLinks = $"""
        CREATE TABLE IF NOT EXISTS harvest_links (
            id INTEGER PRIMARY KEY, {A}, individual INTEGER, spawn INTEGER,
            owner INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE, kill_id INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS harvest_links_account ON harvest_links (account_id, individual);
        CREATE INDEX IF NOT EXISTS harvest_links_owner ON harvest_links (owner);
        """;

    // schema 45 (#1047): the items a random store sale (settings.json `store_sales`, StoreSales.cs) put on sale in each of its periods and at what percentage, written once per period (State.RecordSalePicks); shared by all players
    const string StoreSalePicks = """
        CREATE TABLE IF NOT EXISTS store_sale_picks (
            sale TEXT NOT NULL, period_start INTEGER NOT NULL, period_end INTEGER NOT NULL, item_id INTEGER NOT NULL, percent REAL NOT NULL,
            PRIMARY KEY (sale, period_start, item_id)) WITHOUT ROWID;
        """;

    // schema 46 (#928): the copies State.WebSell can sell back, one row per item copy (`guid`, `item_id`). A copy a store purchase of one item delivered has the `currency` paid (em or gm) and the whole `paid` price of that copy
    // (the discounted one on a sale); a copy a restore or an archive claim brought has `restored` 1, `currency` gm and `paid` 0: it sells at the catalogue's gm$ price. `ts` is unix seconds. A bundle's items, a grant, a reward and the
    // start kit have no row: no proof of what they cost, so they are not sold back. Deliver drops the row of a guid it reuses; a restore clears the rows with the items (PlayerRows.ClearModel)
    const string Purchases = $"""
        CREATE TABLE IF NOT EXISTS purchases (
            {A}, guid INTEGER NOT NULL, item_id INTEGER NOT NULL, currency TEXT NOT NULL, paid INTEGER NOT NULL, ts INTEGER NOT NULL, restored INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (account_id, guid)) WITHOUT ROWID;
        """;

    // once, when the table is made: the copies an account that restored a backup or claimed an archive holds then are marked restored, except those of an item the account also bought here before the table
    // existed (its wallet_log lists the item id): which copy is which cannot be told, so none of them is. Copies given after the table exists (purchases, grants) are not marked
    const string RestoredBackfill = """
        INSERT OR IGNORE INTO purchases (account_id, guid, item_id, currency, paid, ts, restored)
            SELECT i.account_id, i.guid, i.item_id, 'gm', 0, CAST(strftime('%s', 'now') AS INTEGER), 1 FROM items i
            WHERE EXISTS (SELECT 1 FROM restore_audit r WHERE r.account_id = i.account_id)
              AND NOT EXISTS (SELECT 1 FROM wallet_log w, json_each(w.item_ids) j WHERE w.account_id = i.account_id AND w.amount < 0 AND j.value = i.item_id);
        """;

    // the claims of the operator's archive (docs/server.md#archive-claims): one row per account, ever (state waiting, restored or rejected); several accounts may
    // name the same archived user id (the later ones wait for the operator). claim_lookups counts an account's lookups per UTC day (the day number since 1970)
    // keep_handle: the player chose to keep the account's own name
    const string ClaimTables = """
        CREATE TABLE claims (
            id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL UNIQUE REFERENCES accounts (id) ON DELETE CASCADE, archived_uid INTEGER NOT NULL, handle TEXT NOT NULL,
            hunterscore INTEGER NOT NULL, state TEXT NOT NULL, ts INTEGER NOT NULL, decided_ts INTEGER, note TEXT NOT NULL, keep_handle INTEGER NOT NULL);
        CREATE INDEX claims_uid ON claims (archived_uid);
        CREATE TABLE claim_lookups (
            account_id INTEGER NOT NULL REFERENCES accounts (id) ON DELETE CASCADE, day INTEGER NOT NULL, n INTEGER NOT NULL,
            PRIMARY KEY (account_id, day)) WITHOUT ROWID;
        """;

    // banned_steam: the Steam ids the operator banned, with the time and the reason; not tied to an account row, so a delete of the account does not lift it. A lifted ban keeps its row (lifted_ts)
    // so the review list can say the id was banned before. steam_deletions: when the account of a Steam id was deleted (Steam id and time only), for the claim cooldown after a delete.
    const string SteamTables = """
        CREATE TABLE IF NOT EXISTS banned_steam (
            steam_id64 TEXT PRIMARY KEY, ts INTEGER NOT NULL, reason TEXT NOT NULL, lifted_ts INTEGER) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS steam_deletions (
            steam_id64 TEXT PRIMARY KEY, ts INTEGER NOT NULL) WITHOUT ROWID;
        """;

    // what the operator sent to players (AdminMessages): one row per `admin message`, with the account it went to (NULL: every account, `recipients` of them) and the text;
    // it goes with its account when that is deleted (and moves with it when a restore gives the account another user id: Restorer.Adopt). Not in PlayerTables: a broadcast has no account.
    // A player's reply to the notice sender is not here: it would be a `messages` row of that player with a `sender` other than State.SYSTEM_SENDER (`admin messages` lists those); no client writes one yet.
    const string AdminMessagesTable = """
        CREATE TABLE IF NOT EXISTS admin_messages (
            id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, account_id INTEGER REFERENCES accounts (id) ON DELETE CASCADE, recipients INTEGER NOT NULL, text TEXT NOT NULL);
        """;

    // the host's multiplayer bans (#937, docs/server.md#the-hosts-bans): `host_id` banned `banned_id` from its games (the in-game Ban button, User/ban_player) at `ts`. Like friends, no foreign keys:
    // DeleteAccount drops both directions, Restorer.Adopt moves both, and the reads join accounts.
    const string HostBans = """
        CREATE TABLE IF NOT EXISTS host_bans (
            host_id INTEGER NOT NULL, banned_id INTEGER NOT NULL, ts INTEGER NOT NULL,
            PRIMARY KEY (host_id, banned_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS host_bans_banned ON host_bans (banned_id);
        """;

    // Discord Linked Roles (LinkedRoles.cs, docs/server.md#discord-linked-roles). discord_links: one row per account and per Discord id (both UNIQUE), the user's two OAuth tokens sealed (AES-GCM, the key
    // derived from the secret key file; both null once Discord refused the refresh token), when the access token ends, and the hunterscore last pushed (null: none yet). It goes with its account (and moves
    // with it: Restorer.Adopt). discord_rewards: the reward ledger, one row per Discord id ever paid: `hash` is its keyed hash (never the id), `account_id` the account that was paid (UNIQUE: one reward per
    // account too). The row stays when the account is deleted, with the account cleared, so a recreated account, or another one with that Discord id, is not paid again.
    // Neither is in PlayerTables: a Discord id is looked up across accounts.
    const string DiscordTables = """
        CREATE TABLE IF NOT EXISTS discord_links (
            account_id INTEGER PRIMARY KEY REFERENCES accounts (id) ON DELETE CASCADE, discord_id TEXT NOT NULL UNIQUE,
            access_token TEXT, refresh_token TEXT, expires_ts INTEGER NOT NULL DEFAULT 0, pushed_score INTEGER, pushed_handle TEXT, linked_ts INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS discord_rewards (
            hash TEXT PRIMARY KEY, account_id INTEGER UNIQUE REFERENCES accounts (id) ON DELETE SET NULL) WITHOUT ROWID;
        """;

    // the game's crash and error reports (CrashReports): one row per event id and account, kept 30 days; not in PlayerTables, because the purge
    // runs over every account at once. `frames` is a JSON array of "module+offset" texts, the crashing frame first.
    const string CrashReports = $"""
        CREATE TABLE IF NOT EXISTS crash_reports (
            n INTEGER PRIMARY KEY, {A}, event_id TEXT NOT NULL, ts INTEGER NOT NULL, kind TEXT NOT NULL, module TEXT, frames TEXT NOT NULL, message TEXT NOT NULL, release TEXT,
            UNIQUE (account_id, event_id));
        CREATE INDEX IF NOT EXISTS crash_reports_ts ON crash_reports (ts);
        """;

    // when the taxidermy went live and when the window of the harvests made before it ends (TaxidermyGrace); one row, shared by all players, so not in PlayerTables.
    // A new database has no older harvest: start 0.
    const string TaxidermyGraceTable = "CREATE TABLE IF NOT EXISTS taxidermy_grace (start INTEGER NOT NULL, grace_end INTEGER NOT NULL);";

    // the leaderboard seasons (docs/server.md#leaderboards-and-seasons), shared by all players: `start` and `end` are Unix seconds, both inclusive; the current season has no end.
    // Season 0 (all time) is never stored. Season 1 is everything up to the community server's first day (the restored harvests), 2 starts that day.
    const string SeasonsTable = "CREATE TABLE IF NOT EXISTS seasons (no INTEGER PRIMARY KEY, start INTEGER NOT NULL, end INTEGER);";

    /// <summary>When Season 2 starts: 2026-11-12 00:00:00 UTC, the day after the official servers' last.</summary>
    public static readonly long SecondSeasonStart = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    static readonly string SeasonsSeed = SeasonsTable + $"INSERT OR IGNORE INTO seasons VALUES (1, 0, {SecondSeasonStart - 1}), (2, {SecondSeasonStart}, NULL);";

    // the news posts the feed shows (docs/server.md#news), shared by all players: times are Unix seconds, `picture` the extension of the stored file (data_dir/news/<id>.<ext>), `slot:<name>` for an asset slot of the website and the launcher (docs/images.md), NULL for none, `updated` the version of its link.
    // An id is never reused (it names the picture file and the link). `news_texts` has a row per language, English always.
    const string NewsTables = """
        CREATE TABLE IF NOT EXISTS news (
            id INTEGER PRIMARY KEY AUTOINCREMENT, published INTEGER NOT NULL, until INTEGER, link TEXT, picture TEXT,
            pinned INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0, updated INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS news_texts (
            news_id INTEGER NOT NULL REFERENCES news (id) ON DELETE CASCADE, lang TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL, link_text TEXT,
            PRIMARY KEY (news_id, lang)) WITHOUT ROWID;
        """;

    // the news tables, and the three posts the feed had compiled in while the table has no row. Run at every start and guarded by its own checks only, so it is right whatever other schema steps ran before it, or came later
    static void CreateNews(Db con)
    {
        con.Script(NewsTables);
        if ((long)con.Scalar("SELECT COUNT(*) FROM news")! == 0) NewsSeed.Insert(con);
    }

    // a player's count toward each community mission of an event, and whether the reward was paid; shared by all players, so not in PlayerTables
    const string CommunityProgress = $"""
        CREATE TABLE IF NOT EXISTS community_progress (
            event TEXT NOT NULL, mission_id INTEGER NOT NULL, {A}, count INTEGER NOT NULL, paid INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (event, mission_id, account_id)) WITHOUT ROWID;
        """;

    /// <summary>The first user id the server gives to an account of its own. A user id that comes from a backup is
    /// never this high, so the two never collide; the last one is int.MaxValue (the game's ids are 32-bit).</summary>
    // The highest server id ever handed out: an id in an old alert or log never names another player.
    static readonly string IdCounter = $"CREATE TABLE IF NOT EXISTS id_counter (last INTEGER NOT NULL); INSERT INTO id_counter SELECT COALESCE((SELECT MAX(id) FROM accounts WHERE id >= {FirstServerId}), {FirstServerId - 1}) WHERE NOT EXISTS (SELECT 1 FROM id_counter);";

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
            seasonal_events INTEGER NOT NULL DEFAULT 1,       -- 0: the player opted out of seasonal events
            discord_invited INTEGER NOT NULL DEFAULT 0,       -- 1: the website or the launcher showed the player the Discord invite once
            restore_offered INTEGER NOT NULL DEFAULT 0,       -- 1: the website or the launcher offered the player the restore page once
            start_kit INTEGER NOT NULL DEFAULT 0,             -- the version of the start kit the account has (facts `start.version`; 0: from before the kit): an older one gets the items added since, once
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
        {CrashReports}
        {WalletLog}
        {EventsLog}
        {Missions}
        {Challenges}
        {DailyMissions}
        {ClaimTables}
        {SteamTables}
        {AdminMessagesTable}
        {HostBans}
        {CommunityProgress}
        {Loadouts}
        {OwnLimitRemovals}

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
            {A}, kill_id INTEGER NOT NULL, body TEXT NOT NULL, hidden INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (account_id, kill_id));
        CREATE TABLE expeditions (
            id INTEGER PRIMARY KEY, {A}, reserve INTEGER, location_id INTEGER, start_ts REAL NOT NULL, end_ts REAL,
            first_event INTEGER, distance INTEGER, last_seen REAL,
            stats TEXT,   -- a hunt's statistics (State.HuntStatsOf): stored at its close; an open hunt's counts so far carry "open": true and are stored again by the heartbeats that bring events of it
            start_time INTEGER,   -- the time of day the launch picked, in minutes after midnight (NULL: the recorded one)
            tent INTEGER,         -- the guid of the placed tent the hunt starts at (NULL: it starts at location_id)
            last_x REAL, last_z REAL,   -- the horizontal position (metres) of the hunt's latest heartbeat (NULL: none yet)
            walked REAL,          -- the metres between consecutive heartbeats so far, a jump no one walks or drives left out (NULL: no position came); the hunt's distance when the game's disconnect brings none
            event TEXT,           -- the seasonal event of the hunt, decided by its first connect ('': none; NULL: not decided yet): the fallback of its kills whose coat proves nothing (State.HuntEvent, State.KindOf)
            event_until INTEGER); -- the last event row that event judges: a tutorial started after it (NULL: every row of the hunt). Schema 41 adds event_undecided and event_backfilled (BackfillHuntEvents)
        CREATE INDEX expeditions_account ON expeditions (account_id, end_ts);
        CREATE TABLE events (
            id INTEGER PRIMARY KEY, {A}, event INTEGER, body TEXT NOT NULL, ts REAL,  -- ts: when the server received it (NULL before schema 14)
            individual GENERATED ALWAYS AS (json_extract(body, '$.individual')) VIRTUAL);
        CREATE INDEX events_account ON events (account_id, id);
        CREATE INDEX events_individual ON events (account_id, event, individual, id);
        CREATE TABLE pending (id INTEGER PRIMARY KEY, {A}, body TEXT NOT NULL);
        CREATE INDEX pending_account ON pending (account_id, id);
        CREATE TABLE photos (
            id INTEGER PRIMARY KEY, {A}, hash INTEGER, photo_type INTEGER, shot_id INTEGER, individual INTEGER, file TEXT,
            spawn INTEGER,   -- the event 7 of the animal when the photo was asked for: the kill of that animal is the one the photo shows
            saved TEXT);     -- a picture a restore unpacked: JSON with the label and animal of its saved gallery entry; NULL for a photo taken here
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

    /// <summary>When the taxidermy went live (Unix seconds; 0 for a database made with it) and when the window ends for the harvests made before then.</summary>
    public (long Start, long End) TaxidermyGrace()
    {
        using var con = Open();
        var row = con.Rows("SELECT start, grace_end FROM taxidermy_grace")[0];
        return ((long)row[0], (long)row[1]);
    }

    /// <summary>A leaderboard season: `Start` and `End` in Unix seconds, both inclusive; `End` is null for the current one.</summary>
    public sealed record Season(long No, long Start, long? End);

    /// <summary>The seasons, oldest first (season 0, all time, is not stored); the last is the current one.</summary>
    public List<Season> Seasons()
    {
        using var con = Open();
        return [.. con.Rows("SELECT no, start, end FROM seasons ORDER BY no").Select(r => new Season((long)r[0], (long)r[1], r[2] as long?))];
    }

    /// <summary>The number of the season the Unix time falls in: the stored one it lies in; a time before the first starts is in the first, a time after the current one's start in the current.</summary>
    public static long SeasonAt(IReadOnlyList<Season> seasons, long ts) =>
        seasons.LastOrDefault(s => ts >= s.Start)?.No ?? seasons[0].No;

    /// <summary>Close the current season at `now - 1` and open the next number at `now`; the new season, or null when `now` is not after the current one's start (an empty season).</summary>
    public Season? StartSeason(long now)
    {
        var current = Seasons().Last();  // one server process (ADR 0001): nothing starts a season between this read and the write
        if (now <= current.Start) return null;
        using var con = Open();
        con.Begin();
        con.Exec("UPDATE seasons SET end = ? WHERE no = ?", now - 1, current.No);
        con.Exec("INSERT INTO seasons VALUES (?, ?, NULL)", current.No + 1, now);
        con.Commit();
        return new(current.No + 1, now, null);
    }

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
            con.Script(IdCounter);
            con.Script(TaxidermyGraceTable + "INSERT INTO taxidermy_grace VALUES (0, 0);");
            con.Script(SeasonsSeed);
        }
        else
        {
            if (version < 2) con.Script(WebSessions);
            if (version < 3) con.Script(Uploads);
            if (version < 4) con.Script(UploadResults);
            if (version < 5) con.Script("ALTER TABLE expeditions ADD COLUMN stats TEXT");
            if (version < 6) con.Script(Friends);
            if (version < 7) con.Script("UPDATE expeditions SET stats = NULL WHERE end_ts IS NOT NULL");  // closed hunts count their pickups: stored again at the start
            if (version < 8 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name = 'spawn'")! == 0)
            {
                // the n-th photo of an account came with its n-th event 4 (no photo was deleted before this version)
                con.Script("ALTER TABLE photos ADD COLUMN spawn INTEGER");
                con.Script("UPDATE photos SET spawn = (SELECT MAX(s.id) FROM events s WHERE s.account_id = photos.account_id AND s.event = 7 AND s.individual = photos.individual"
                           + " AND s.id < (SELECT e.id FROM events e WHERE e.account_id = photos.account_id AND e.event = 4"
                           + " AND (SELECT COUNT(*) FROM events f WHERE f.account_id = e.account_id AND f.event = 4 AND f.id < e.id)"
                           + " = (SELECT COUNT(*) FROM photos q WHERE q.account_id = photos.account_id AND q.id < photos.id)))");
            }
            if (version < 9) con.Script("ALTER TABLE harvests ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0");
            if (version < 10 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name = 'saved'")! == 0)
                con.Script("ALTER TABLE photos ADD COLUMN saved TEXT");
            if (version < 11) con.Script(WalletLog);
            if (version < 12)
            {
                con.Script(Loadouts);
                con.Script(RestoredLoadouts.Replace("$a", "e.account_id"));
            }
            if (version < 13) con.Script(Missions);
            if (version < 14)
            {
                if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('events') WHERE name = 'ts'")! == 0)
                    con.Script("ALTER TABLE events ADD COLUMN ts REAL");
                con.Script(Challenges);
                con.Script(RestoredChallenges.Replace("$a", "e.account_id"));
            }
            if (version < 15) con.Script(DailyMissions);
            if (version < 16 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('expeditions') WHERE name = 'start_time'")! == 0)
                con.Script("ALTER TABLE expeditions ADD COLUMN start_time INTEGER");
            if (version < 17 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'seasonal_events'")! == 0)
                con.Script("ALTER TABLE accounts ADD COLUMN seasonal_events INTEGER NOT NULL DEFAULT 1");
            if (version < 18) con.Script(ClaimTables);
            if (version < 19) con.Script(CommunityProgress);
            if (version < 20 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'discord_invited'")! == 0)
                con.Script("ALTER TABLE accounts ADD COLUMN discord_invited INTEGER NOT NULL DEFAULT 0");
            if (version < 21 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('expeditions') WHERE name = 'tent'")! == 0)
                con.Script("ALTER TABLE expeditions ADD COLUMN tent INTEGER");
            if (version < 22 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'start_kit'")! == 0)
                con.Script("ALTER TABLE accounts ADD COLUMN start_kit INTEGER NOT NULL DEFAULT 0");
            if (version < 23 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'restore_offered'")! == 0)
                con.Script("ALTER TABLE accounts ADD COLUMN restore_offered INTEGER NOT NULL DEFAULT 0");
            if (version < 24)
            {
                // ids are never reused (approve and reject delete the row): an id in an old alert must not name another player's zip.
                // The counter starts at 100 at least, above the small ids that earlier alerts and logs already showed.
                con.Script($"""
                    CREATE TABLE uploads_new (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, {A}, ts INTEGER NOT NULL, zip_sha256 TEXT NOT NULL, user_id INTEGER, note TEXT NOT NULL);
                    INSERT INTO uploads_new SELECT id, account_id, ts, zip_sha256, user_id, note FROM uploads;
                    DROP TABLE uploads;
                    ALTER TABLE uploads_new RENAME TO uploads;
                    CREATE INDEX uploads_account ON uploads (account_id, id);
                    DELETE FROM sqlite_sequence WHERE name = 'uploads';
                    INSERT INTO sqlite_sequence (name, seq) VALUES ('uploads', MAX(100, (SELECT COALESCE(MAX(id), 0) FROM uploads)));
                    """);
            }
            // The counter starts at the highest server id now in use: ids freed before this step (adopted or deleted) may be handed out once more.
            if (version < 25) con.Script(IdCounter);
            if (version < 26)
            {
                // claim ids are never reused (deleting the account deletes its claim): a c<id> in an old alert must not name another player's claim. Floor 100 as for uploads.
                con.Script("""
                    CREATE TABLE claims_new (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL UNIQUE REFERENCES accounts (id) ON DELETE CASCADE, archived_uid INTEGER NOT NULL, handle TEXT NOT NULL,
                        hunterscore INTEGER NOT NULL, state TEXT NOT NULL, ts INTEGER NOT NULL, decided_ts INTEGER, note TEXT NOT NULL, keep_handle INTEGER NOT NULL);
                    INSERT INTO claims_new SELECT id, account_id, archived_uid, handle, hunterscore, state, ts, decided_ts, note, keep_handle FROM claims;
                    DROP TABLE claims;
                    ALTER TABLE claims_new RENAME TO claims;
                    CREATE INDEX claims_uid ON claims (archived_uid);
                    DELETE FROM sqlite_sequence WHERE name = 'claims';
                    INSERT INTO sqlite_sequence (name, seq) VALUES ('claims', MAX(100, (SELECT COALESCE(MAX(id), 0) FROM claims)));
                    """);
            }
            if (version < 27) con.Script(CrashReports);
            if (version < 28)  // the first start with the taxidermy: every harvest made until now has a window of 72 hours from now
                con.Script(TaxidermyGraceTable + $"INSERT INTO taxidermy_grace SELECT {Now()}, {Now() + State.TaxidermyHours * 3600L} WHERE NOT EXISTS (SELECT 1 FROM taxidermy_grace);");
            if (version < 29) con.Script(SeasonsSeed);
            if (version < 30)
            {
                con.Script(MissionRollbacks);
                if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('missions') WHERE name = 'since'")! == 0)
                    con.Script("""
                        ALTER TABLE missions ADD COLUMN since INTEGER;
                        ALTER TABLE missions ADD COLUMN seen INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE missions ADD COLUMN kept TEXT NOT NULL DEFAULT '[]';
                        ALTER TABLE missions ADD COLUMN earlier TEXT NOT NULL DEFAULT '[]';
                        -- the rows are older than the events they will be checked against: only the events from now on are theirs, and what an active mission has finished is earlier
                        UPDATE missions SET seen = COALESCE((SELECT MAX(id) FROM events WHERE events.account_id = missions.account_id), 0);
                        UPDATE missions SET earlier = objectives WHERE state = 2;
                        -- what a restore brought stays as it was: a mission completed before it, and the finished objectives of an active one
                        UPDATE missions SET kept = '*' WHERE state = 3 AND EXISTS (SELECT 1 FROM extras e, json_each(e.body, '$.completed') c
                            WHERE e.account_id = missions.account_id AND e.path = 'Mission_missions.json' AND json_valid(e.body) AND c.value = missions.mission_id);
                        UPDATE missions SET kept = COALESCE((SELECT json_extract(e.body, '$.states."' || missions.mission_id || '".objectives') FROM extras e
                            WHERE e.account_id = missions.account_id AND e.path = 'Mission_missions.json' AND json_valid(e.body)
                            AND EXISTS (SELECT 1 FROM json_each(e.body, '$.active') a WHERE a.value = missions.mission_id)), '[]') WHERE kept = '[]';
                        """);
            }
            if (version < 31 && (long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('expeditions') WHERE name = 'walked'")! == 0)
                con.Script("ALTER TABLE expeditions ADD COLUMN last_x REAL; ALTER TABLE expeditions ADD COLUMN last_z REAL; ALTER TABLE expeditions ADD COLUMN walked REAL");
            if (version < 32) con.Script(SteamTables);
            // every start, guarded by its own checks and not by the version: a database made by another branch's schema number (merge or deploy in any order) gets the table and the columns too
            con.Script(EventsLog);
            if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('wallet_log') WHERE name = 'base'")! == 0)
                con.Script("ALTER TABLE wallet_log ADD COLUMN base REAL; ALTER TABLE wallet_log ADD COLUMN bonus_event INTEGER");
            // the rule of each finished objective, as it read when the objective was finished (a JSON object: objective id to fingerprint); the objectives finished before have none.
            // Guarded by the column alone, not by the version, so it is right whatever order the schema steps are merged and deployed in.
            if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('missions') WHERE name = 'rules'")! == 0)
                con.Script($"ALTER TABLE missions ADD COLUMN rules TEXT NOT NULL DEFAULT '{NoRules}'");
        }
        // every start, whatever the version: schema steps of the batch may be deployed in any order, so this one is not gated on a number (IF NOT EXISTS makes it idempotent)
        con.Script(AdminMessagesTable);
        CreateNews(con);  // schema 36, and not a `version <` step: see CreateNews
        // schema 37, guarded by the column and not by the version (every start): 1 when the player chose imperial units (yards, pounds) in the game's settings
        if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = 'use_imperial'")! == 0)
            con.Script("ALTER TABLE accounts ADD COLUMN use_imperial INTEGER NOT NULL DEFAULT 0");
        // schema 38, every start and guarded by its own IF NOT EXISTS (not by the version): what the pass at start removed, and the mark that it has run
        con.Script(OwnLimitRemovals);
        // schema 39, guarded by the columns and not by the version (every start): the seasonal event a hunt's kills are judged by; the hunts before it are decided with none ('')
        // so their kills keep their judgement
        if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('expeditions') WHERE name = 'event'")! == 0)
            con.Script("ALTER TABLE expeditions ADD COLUMN event TEXT; UPDATE expeditions SET event = ''");
        if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('expeditions') WHERE name = 'event_until'")! == 0)
            con.Script("ALTER TABLE expeditions ADD COLUMN event_until INTEGER");
        con.Script(DiscordTables);  // schema 40, every start and guarded by its own IF NOT EXISTS (not by the version), like the steps above
        // schema 41 (#582), guarded by the table and not by the version (every start; it runs once): the kills are judged per animal from now on. The closed hunts recorded without an event ('', every hunt
        // from before schema 39 among them) get the event their own sightings prove (marked `event_backfilled`, so a hunt whose event its first connect recorded can be told apart), or are marked undecided
        // (BackfillHuntEvents); the stored statistics of the closed hunts with a kill are cleared, so the start-up loop (State, UpdateHunterscore) counts their ethical kills and the hunterscores again by the
        // rule; `schema_marks` keeps when the step ran and the last event row before it (the kills made before the rule, State.JudgeKills); then the table of the flagged kills. No score, no wallet row and
        // no other table is written
        if ((long)con.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'unethical_kills'")! == 0)
        {
            foreach (var column in (string[])["event_undecided TEXT", "event_backfilled INTEGER"])
                if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('expeditions') WHERE name = ?", column.Split(' ')[0])! == 0)
                    con.Script($"ALTER TABLE expeditions ADD COLUMN {column}");
            BackfillHuntEvents(con, Facts.Default);
            con.Script("UPDATE expeditions SET stats = NULL WHERE end_ts IS NOT NULL AND stats IS NOT NULL"
                       + " AND id IN (SELECT json_extract(h.body, '$.expedition_id') FROM harvests h WHERE h.account_id = expeditions.account_id)");
            con.Script($"""
                CREATE TABLE IF NOT EXISTS schema_marks (version INTEGER PRIMARY KEY, ts INTEGER NOT NULL, last_event INTEGER NOT NULL);
                INSERT OR REPLACE INTO schema_marks VALUES (41, {Now()}, (SELECT COALESCE(MAX(id), 0) FROM events));
                """);
            con.Script(UnethicalKills);
        }
        // schema 43 (#969), guarded by the columns and not by the version (every start): how often a banned Steam id tried to sign in (BanAttempt)
        foreach (var column in (string[])["last_attempt INTEGER", "attempts INTEGER NOT NULL DEFAULT 0"])
            if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('banned_steam') WHERE name = ?", column.Split(' ')[0])! == 0)
                con.Script($"ALTER TABLE banned_steam ADD COLUMN {column}");
        con.Script(HostBans);  // schema 42 (#937), every start and guarded by its own IF NOT EXISTS (not by the version), like the steps above
        // schema 44 (#1041), every start and guarded by the table and the columns (not by the version): a trophy shot of another member's kill is that kill's photo (`photos.kill_id`, in the shooter's
        // account), and `taker` is the account whose game took and uploads it (its gallery links it)
        con.Script(HarvestLinks);
        foreach (var column in (string[])["taker INTEGER REFERENCES accounts (id) ON DELETE SET NULL", "kill_id INTEGER"])
            if ((long)con.Scalar("SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name = ?", column.Split(' ')[0])! == 0)
                con.Script($"ALTER TABLE photos ADD COLUMN {column}");
        con.Script("CREATE INDEX IF NOT EXISTS photos_taker ON photos (taker) WHERE taker IS NOT NULL");
        con.Script(StoreSalePicks);  // schema 45 (#1047), every start and guarded by its own IF NOT EXISTS (not by the version), like the steps above
        if ((long)con.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'purchases'")! == 0)  // schema 46 (#928), every start and guarded by the table (not by the version), like the steps above
        {
            con.Script(Purchases);
            con.Script(RestoredBackfill);
        }
        // every start: the edges that name the old user id of a restored zip or claim are moved to the account that holds it (a claim or restore made before the edges were resolved left them so);
        // driven by those ids, not by a scan of the friends table; no schema step, it is idempotent
        foreach (var row in con.Rows("SELECT user_id FROM restored_ids UNION SELECT archived_uid FROM claims WHERE state = 'restored'"))
            ReconnectFriends(con, (long)row[0]);
        // every start: a feeder refilled before the refill rule existed gets its fields from its refill events (Feeders.Backfill); it only touches a feeder whose last refill is newer than its start time, so no schema step and no version
        foreach (var row in con.Rows("SELECT id FROM accounts"))
            Feeders.Backfill(con.Sharing((long)row[0]));
        if (version < Version)
            con.Script($"PRAGMA user_version = {Version}");
        con.Commit();
    }

    /// <summary>Schema 41 (#582): a closed hunt recorded without an event ('') gets the event of <paramref name="facts"/> its sightings (event 7, from its first event to the next hunt's, a tutorial after
    /// `event_until` left out) prove (<see cref="Facts.Proves"/>: a creature or a possessed coat), when at least one does and none proves a normal animal or another event; otherwise it stays ''.
    /// One with no deciding sighting at all, on a reserve the event has content on and not ended before the first hunt that has the event (after this backfill) started, gets `event_undecided`: the
    /// event it may have had (`admin ethics` flags its kills whose verdict depends on it). One range of event rows per hunt.</summary>
    static void BackfillHuntEvents(Db con, Facts facts)
    {
        var accounts = con.Rows("SELECT id FROM accounts").Select(r => (long)r[0]).ToList();
        var nothing = new List<(long Account, long Hunt, long Reserve, double End)>();
        foreach (var account in accounts)
        {
            var mine = con.Sharing(account);
            foreach (var hunt in mine.Rows("SELECT e.id, e.first_event, COALESCE(e.event_until, ?1), COALESCE((SELECT MIN(n.first_event) FROM expeditions n WHERE n.account_id = $a AND n.id > e.id), ?1),"
                                           + " COALESCE(e.reserve - 1, e.location_id / 100, -1), e.end_ts"
                                           + " FROM expeditions e WHERE e.account_id = $a AND e.event = '' AND e.end_ts IS NOT NULL AND e.first_event IS NOT NULL", long.MaxValue).ToList())
            {
                string? proved = null;
                bool decided = false, mixed = false;
                foreach (var s in mine.Rows("SELECT json_extract(body, '$.species'), json_extract(body, '$.texture') FROM events WHERE account_id = $a AND event = 7 AND id > ? AND id <= ? AND id <= ?",
                                            hunt[1], hunt[2], hunt[3]))
                    if (facts.Proves(Py.FromSql(s[0]), Py.FromSql(s[1]), out _) is { } p)
                    {
                        decided = true;
                        mixed |= p == "" || proved is not null && proved != p;
                        proved ??= p == "" ? null : p;
                    }
                if (proved is not null && !mixed)
                    mine.Exec("UPDATE expeditions SET event = ?, event_backfilled = 1 WHERE account_id = $a AND id = ?", proved, hunt[0]);
                else if (!decided)
                    nothing.Add((account, (long)hunt[0], (long)hunt[4], Convert.ToDouble(hunt[5])));
            }
        }
        foreach (var name in facts.EventNames)
        {
            var first = accounts.Select(a => con.Sharing(a).Scalar("SELECT MIN(start_ts) FROM expeditions WHERE account_id = $a AND event = ?", name)).OfType<double>().DefaultIfEmpty(double.MaxValue).Min();
            foreach (var (account, hunt, reserve, end) in nothing.Where(h => h.End >= first && facts.Event(name, (int)h.Reserve) is not null))
                con.Sharing(account).Exec("UPDATE expeditions SET event_undecided = ? WHERE account_id = $a AND id = ? AND event_undecided IS NULL", name, hunt);
        }
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
        con.Begin();
        var last = (long)con.Scalar("SELECT last FROM id_counter")!;
        var next = Math.Max(last, (con.Scalar("SELECT MAX(id) FROM accounts WHERE id >= ?", FirstServerId) as long?) ?? 0) + 1;
        if (next > int.MaxValue) throw new InvalidOperationException("the server's user ids are used up");
        con.Exec("UPDATE id_counter SET last = ?", next);  // an insert that then fails burns the number: fine
        con.Commit();
        return next;
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

    /// <summary>Whether the account plays seasonal events: yes unless it opted out (<see cref="SetPlaysEvents"/>).</summary>
    public bool PlaysEvents(long id)
    {
        using var con = Open();
        return con.Scalar("SELECT seasonal_events FROM accounts WHERE id = ?", id) is not 0L;
    }

    public void SetPlaysEvents(long id, bool plays)
    {
        using var con = Open();
        con.Exec("UPDATE accounts SET seasonal_events = ? WHERE id = ?", plays ? 1 : 0, id);
    }

    /// <summary>Whether the account chose imperial units (yards, pounds) for the game and the pages; metric unless it did (<see cref="SetUsesImperial"/>).</summary>
    public bool UsesImperial(long id)
    {
        using var con = Open();
        return con.Scalar("SELECT use_imperial FROM accounts WHERE id = ?", id) is 1L;
    }

    /// <summary>Whether the account has linked a Discord account (<see cref="LinkedRoles"/>): the website and the launcher stop offering the link then.</summary>
    public bool DiscordLinked(long id)
    {
        using var con = Open();
        return con.Scalar("SELECT 1 FROM discord_links WHERE account_id = ?", id) is not null;
    }

    public void SetUsesImperial(long id, bool imperial)
    {
        using var con = Open();
        con.Exec("UPDATE accounts SET use_imperial = ? WHERE id = ?", imperial ? 1 : 0, id);
    }

    /// <summary>True the first time it is asked for the account, false after: the one-time Discord invite of the website and the launcher.</summary>
    public bool NoteDiscordInvite(long id)
    {
        using var con = Open();
        return con.Exec("UPDATE accounts SET discord_invited = 1 WHERE id = ? AND discord_invited = 0", id) > 0;
    }

    /// <summary>True the first time it is asked for the account, false after or when the account restored a backup zip or has an upload (an archive claim never blocks it): the one-time restore offer of the website and the launcher.</summary>
    public bool NoteRestoreOffer(long id)
    {
        using var con = Open(id);
        return con.Exec("UPDATE accounts SET restore_offered = 1 WHERE id = ? AND restore_offered = 0", id) > 0
            && con.Scalar("SELECT 1 WHERE EXISTS (SELECT 1 FROM restore_audit WHERE account_id = $a AND zip_sha256 NOT LIKE 'archive:%' AND mode <> 'merge') OR EXISTS (SELECT 1 FROM uploads WHERE account_id = $a)") is null;
    }

    /// <summary>Delete an account and, by the foreign keys, everything of it: every per-player row, its tokens, its cookie
    /// sessions, its Steam id and handle, its Discord link and tokens; and its friend edges both ways. Files are the caller's. Its Steam id and the time `at` (default now)
    /// stay in `steam_deletions` for the claim cooldown; and its keyed Discord hash stays in the reward ledger (`discord_rewards`), the account cleared. Nothing else of the account does.</summary>
    public void DeleteAccount(long id, long? at = null)
    {
        using var con = Open();
        con.Begin();
        con.Exec("INSERT OR REPLACE INTO steam_deletions (steam_id64, ts) SELECT steam_id64, ?2 FROM accounts WHERE id = ?1 AND steam_id64 IS NOT NULL", id, at ?? Now());
        con.Exec("DELETE FROM friends WHERE account_id = ?1 OR other_id = ?1", id);
        con.Exec("DELETE FROM host_bans WHERE host_id = ?1 OR banned_id = ?1", id);
        con.Exec("DELETE FROM accounts WHERE id = ?", id);
        con.Commit();
    }

    /// <summary>When the account this Steam id had was last deleted (Unix seconds); null when none was, or the record is older than the claim cooldown and was purged.</summary>
    public long? DeletedAt(string steamId64)
    {
        using var con = Open();
        return con.Scalar("SELECT ts FROM steam_deletions WHERE steam_id64 = ?", steamId64) as long?;
    }

    // ---- bans by Steam id

    /// <summary>Is this Steam id banned now: no sign-in with it makes or opens an account.</summary>
    public bool IsBanned(string steamId64) => BanReason(steamId64) is not null;

    /// <summary>The operator's reason for the Steam id's ban ("" when he gave none), null when it is not banned now. The banned player reads it on the sign-in problem page.</summary>
    public string? BanReason(string steamId64)
    {
        using var con = Open();
        return con.Scalar("SELECT COALESCE(reason, '') FROM banned_steam WHERE steam_id64 = ? AND lifted_ts IS NULL", steamId64) as string;
    }

    /// <summary>Ban the Steam id (a ban again replaces the time and the reason) and set `banned` on its current account, if any; returns that account.</summary>
    public long? Ban(string steamId64, string reason, long at)
    {
        using var con = Open();
        con.Begin();
        con.Exec("INSERT INTO banned_steam (steam_id64, ts, reason, lifted_ts) VALUES (?1, ?2, ?3, NULL) ON CONFLICT (steam_id64) DO UPDATE SET ts = ?2, reason = ?3, lifted_ts = NULL", steamId64, at, reason);
        con.Exec("UPDATE accounts SET banned = 1 WHERE steam_id64 = ?", steamId64);
        var account = con.Scalar("SELECT id FROM accounts WHERE steam_id64 = ?", steamId64) as long?;
        con.Commit();
        return account;
    }

    /// <summary>Lift the ban of the Steam id and of its current account, if any; the row stays with its lift time. False: it was not banned.</summary>
    public bool Unban(string steamId64, long at)
    {
        using var con = Open();
        con.Begin();
        var lifted = con.Exec("UPDATE banned_steam SET lifted_ts = ?2 WHERE steam_id64 = ?1 AND lifted_ts IS NULL", steamId64, at) > 0;
        lifted |= con.Exec("UPDATE accounts SET banned = 0 WHERE steam_id64 = ? AND banned = 1", steamId64) > 0;
        con.Commit();
        return lifted;
    }

    /// <summary>When the Steam id was banned and, if it was lifted, when; null when it never was.</summary>
    public (long Ts, long? Lifted)? BanOf(string steamId64)
    {
        using var con = Open();
        var rows = con.Rows("SELECT ts, lifted_ts FROM banned_steam WHERE steam_id64 = ?", steamId64).ToList();
        return rows.Count == 0 ? null : ((long)rows[0][0], rows[0][1] as long?);
    }

    /// <summary>One sign-in attempt of a Steam id that is banned now: counts it on the ban row and returns the count with the time of the attempt before (null: the first). Null: no ban row (an account flagged banned by hand).</summary>
    public (long Attempts, long? Previous)? BanAttempt(string steamId64, long at)
    {
        using var con = Open();
        con.Begin();
        var rows = con.Rows("SELECT attempts, last_attempt FROM banned_steam WHERE steam_id64 = ? AND lifted_ts IS NULL", steamId64).ToList();
        if (rows.Count > 0) con.Exec("UPDATE banned_steam SET attempts = attempts + 1, last_attempt = ?2 WHERE steam_id64 = ?1", steamId64, at);
        con.Commit();
        return rows.Count == 0 ? null : ((long)rows[0][0]! + 1, rows[0][1] as long?);
    }

    /// <summary>The Steam ids banned now that tried to sign in: Steam id, attempts, time of the last, most recent first.</summary>
    public List<(string Steam, long Attempts, long Last)> BanAttempts()
    {
        using var con = Open();
        return [.. con.Rows("SELECT steam_id64, attempts, last_attempt FROM banned_steam WHERE lifted_ts IS NULL AND attempts > 0 ORDER BY last_attempt DESC, steam_id64")
            .Select(r => ((string)r[0]!, (long)r[1]!, (long)r[2]!))];
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

    // ---- the host's multiplayer bans (the host_bans table)

    /// <summary>`host` bans `banned` from its games; a second ban keeps the first one's time.</summary>
    public void BanFromGames(long host, long banned)
    {
        using var con = Open();
        con.Exec("INSERT OR IGNORE INTO host_bans (host_id, banned_id, ts) VALUES (?, ?, ?)", host, banned, Now());
    }

    /// <summary>`host` lifts its ban of `banned`; false when there was none.</summary>
    public bool UnbanFromGames(long host, long banned)
    {
        using var con = Open();
        return con.Exec("DELETE FROM host_bans WHERE host_id = ? AND banned_id = ?", host, banned) > 0;
    }

    /// <summary>The hosts that banned the player from their games.</summary>
    public HashSet<long> BannedBy(long banned)
    {
        using var con = Open();
        return [.. con.Rows("SELECT host_id FROM host_bans WHERE banned_id = ?", banned).Select(r => (long)r[0])];
    }

    /// <summary>The players `host` banned that still have an account: user id and ban time, the latest first.</summary>
    public List<(long Account, long Ts)> GameBansOf(long host)
    {
        using var con = Open();
        return [.. con.Rows("SELECT b.banned_id, b.ts FROM host_bans b JOIN accounts a ON a.id = b.banned_id WHERE b.host_id = ? ORDER BY b.ts DESC, b.banned_id", host).Select(r => ((long)r[0], (long)r[1]))];
    }

    /// <summary>The account that now holds the profile of the old user id `uid` (inside the caller's transaction): the account with that id (it kept its own, or took the
    /// zip's), else the account a zip of that user id was restored onto (`restored_ids`; it proves more than the archive), else the account of the first restored claim
    /// of that archived user id. Null while none does.</summary>
    public static long? Holder(Db con, long uid) =>
        (con.Scalar("SELECT id FROM accounts WHERE id = ?", uid) as long?)
        ?? (con.Scalar("SELECT account_id FROM restored_ids WHERE user_id = ?", uid) as long?)
        ?? (con.Scalar("SELECT account_id FROM claims WHERE archived_uid = ? AND state = 'restored' ORDER BY id LIMIT 1", uid) as long?);

    /// <summary>A restore's edge (inside its transaction), each end as the account that holds that user id now (<see cref="Holder"/>; an id nobody holds yet stays as it is, and
    /// <see cref="ReconnectFriends"/> moves it later): a `friend` edge replaces a request of the same direction; any other edge the pair has already stays.</summary>
    public static void RestoreFriendEdge(Db con, long id, long other, string state)
    {
        (id, other) = (Holder(con, id) ?? id, Holder(con, other) ?? other);
        if (id > 0 && other > 0 && id != other)
            con.Exec("INSERT INTO friends (account_id, other_id, state) VALUES (?, ?, ?)"
                     + " ON CONFLICT (account_id, other_id) DO UPDATE SET state = excluded.state WHERE excluded.state = 'friend'", id, other, state);
    }

    /// <summary>The edges that still name the old user id `uid`, which no account has, name the account that holds its profile now, if one does (inside the caller's transaction):
    /// an edge saved before that profile was claimed or restored here connects once it is. Idempotent.</summary>
    public static void ReconnectFriends(Db con, long uid)
    {
        if (Holder(con, uid) is not { } holder || holder == uid) return;
        foreach (var e in con.Rows("SELECT account_id, other_id, state FROM friends WHERE account_id = ?1 OR other_id = ?1", uid))
        {
            con.Exec("DELETE FROM friends WHERE account_id = ? AND other_id = ?", e[0], e[1]);  // the old edge goes, so a friendship removed here is not made again by the next start
            RestoreFriendEdge(con, (long)e[0], (long)e[1], (string)e[2]);
        }
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

    /// <summary>Rows of the hunterscore board (the order of Rank) from `offset`, at most `limit`.</summary>
    public List<(long Rank, long Id, string Handle, long Hunterscore)> Board(long? excluded, int offset, int limit)
    {
        using var con = Open();
        return con.Rows("SELECT id, handle, COALESCE(json_extract(account, '$.hunterscore'), 0) AS hs FROM accounts"
                        + " WHERE banned = 0 AND id IS NOT ? ORDER BY hs DESC, id LIMIT ? OFFSET ?", excluded, limit, offset)
            .Select((r, i) => (offset + i + 1L, (long)r[0], (string)r[1], Convert.ToInt64(r[2]))).ToList();
    }

    /// <summary>The account's `avatar_url`; null when it has none.</summary>
    public string? AvatarUrl(long id)
    {
        using var con = Open();
        return con.Scalar("SELECT json_extract(account, '$.avatar_url') FROM accounts WHERE id = ?", id) as string;
    }

    /// <summary>The user id of the archived profile the account claimed and had restored; null for none.</summary>
    public long? ArchivedUid(long id)
    {
        using var con = Open();
        return con.Scalar("SELECT archived_uid FROM claims WHERE account_id = ? AND state = 'restored'", id) as long?;
    }

    /// <summary>The id of every account.</summary>
    public List<long> AccountIds()
    {
        using var con = Open();
        return [.. con.Rows("SELECT id FROM accounts").Select(r => (long)r[0])];
    }

    /// <summary>The `avatar_url` of every account that has one, by user id.</summary>
    public List<(long Id, string Url)> AvatarUrls()
    {
        using var con = Open();
        return [.. con.Rows("SELECT id, json_extract(account, '$.avatar_url') FROM accounts WHERE json_extract(account, '$.avatar_url') IS NOT NULL").Select(r => ((long)r[0], (string)r[1]))];
    }

    /// <summary>Point the account's picture at `url`: its `avatar_url` and the `avatar` of its restored profile. A `face` also sets the account's `face` and its `avatar` and `avatar_id` (the last two where it has them: the `picture`, else the face), as a restore does; a `gender` sets the account's `gender`.</summary>
    public void SetAvatar(long id, string url, long? face, long? gender = null, long? picture = null)
    {
        using (var con = Open())
        {
            con.Exec("UPDATE accounts SET account = json_set(account, '$.avatar_url', ?) WHERE id = ?", url, id);
            if (face is { } f)
                con.Exec("UPDATE accounts SET account = json_replace(json_set(account, '$.face', ?1), '$.avatar', ?3, '$.avatar_id', ?3) WHERE id = ?2", f, id, picture ?? f);
            if (gender is { } g)
                con.Exec("UPDATE accounts SET account = json_set(account, '$.gender', ?) WHERE id = ?", g, id);
        }
        using var mine = Open(id);
        mine.Exec("UPDATE profile SET body = json_set(body, '$.avatar', ?) WHERE account_id = $a", url);
        if (gender is { } g2)
            mine.Exec("UPDATE profile SET body = json_set(body, '$.gender', ?) WHERE account_id = $a", g2);
    }

    /// <summary>Every account's `avatar_url`, `face` and `gender` (null where it has none), by user id.</summary>
    public List<(long Id, string? Url, long? Face, long? Gender)> FacesAndGenders()
    {
        using var con = Open();
        return [.. con.Rows("SELECT id, json_extract(account, '$.avatar_url'), json_extract(account, '$.face'), json_extract(account, '$.gender') FROM accounts").Select(r => ((long)r[0], r[1] as string, r[2] as long?, r[3] as long?))];
    }

    /// <summary>Set the account's `face` and `gender` (and the restored profile's `gender`).</summary>
    public void SetFaceAndGender(long id, long face, long gender)
    {
        using (var con = Open())
            con.Exec("UPDATE accounts SET account = json_set(account, '$.face', ?, '$.gender', ?) WHERE id = ?", face, gender, id);
        using var mine = Open(id);
        mine.Exec("UPDATE profile SET body = json_set(body, '$.gender', ?) WHERE account_id = $a", gender);
    }

    /// <summary>Set the account's `gender` when it differs and the account never restored anything (no restored id, no restore audit, no claim); true when it changed.</summary>
    public bool FixGenderUnlessRestored(long id, long gender)
    {
        using var con = Open();
        if (con.Scalar("SELECT 1 FROM accounts WHERE id = ? AND json_extract(account, '$.gender') IS NOT ?", id, gender) is null
            || con.Scalar("SELECT 1 FROM restored_ids WHERE account_id = ? UNION ALL SELECT 1 FROM claims WHERE account_id = ?", id, id) is not null) return false;
        using (var mine = Open(id))
            if (mine.Scalar("SELECT 1 FROM restore_audit WHERE account_id = $a") is not null) return false;
        return con.Exec("UPDATE accounts SET account = json_set(account, '$.gender', ?) WHERE id = ?", gender, id) > 0;
    }

    /// <summary>The minigames' boards by kind: the game id of heartbeat event 17 (<see cref="RestoredReads.BestScore"/>).</summary>
    public static readonly IReadOnlyDictionary<string, long> GameIds =
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<string, long> { ["lanebandit"] = 1, ["bustthrough"] = 0 });

    /// <summary>A minigame's whole board (a kind of <see cref="GameIds"/>): the accounts with a score above 0, highest first, equal scores in account id order; the caller pages it.
    /// ponytail: statistics is a per-player table, so each account is read on its own connection (Db refuses a cross-account read); two queries per account
    /// on every page view, fine for the accounts of a community server. Copy the scores to the accounts row if that gets slow.</summary>
    public List<(long Id, string Handle, long Score)> GameBoard(string kind, long? excluded)
    {
        var game = GameIds[kind];
        List<(long Id, string Handle)> accounts;
        using (var con = Open())
            accounts = [.. con.Rows("SELECT id, handle FROM accounts WHERE banned = 0 AND id IS NOT ? ORDER BY id", excluded).Select(r => ((long)r[0], (string)r[1]))];
        var scored = new List<(long Id, string Handle, long Score)>();
        foreach (var (id, handle) in accounts)
        {
            using var con = Open(id);
            if (RestoredReads.BestScore(con, game) is { } score and > 0)
                scored.Add((id, handle, score));
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

    /// <summary>The accounts whose handle contains the text (any case), the exact handle first, then by handle, at most `limit`; never `self`, a banned one or `excluded`.</summary>
    public List<long> SearchHandles(string text, long self, long? excluded, int limit)
    {
        using var con = Open();
        return [.. con.Rows("SELECT id FROM accounts WHERE banned = 0 AND id <> ?1 AND id IS NOT ?2 AND instr(lower(handle), lower(?3)) > 0 ORDER BY lower(handle) = lower(?3) DESC, handle COLLATE NOCASE LIMIT ?4",
                            self, excluded, text, limit).Select(r => (long)r[0])];
    }

    /// <summary>Add an account and, in the same transaction, let seed fill its rows (the server's starting data, or an
    /// importer's). id is the game's user id; account is the connect reply's `account` object. Throws when the id,
    /// the handle or the Steam id is taken. startKit is the version of the start kit the seed gives (`accounts.start_kit`).</summary>
    public void CreateAccount(long id, string handle, JsonObject account, string? steamId64 = null, Action<Db>? seed = null, long startKit = 1)
    {
        using var con = Open(id);
        con.Begin();
        con.Exec("INSERT INTO accounts (id, steam_id64, handle, created_ts, start_kit, account) VALUES (?, ?, ?, ?, ?, ?)",
                 id, steamId64, handle, Now(), startKit, Py.Dumps(account));
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

    /// <summary>Drop the tokens and cookies that have expired (at startup), and the deletion times older than the claim cooldown (<see cref="PurgeDeletions"/>).</summary>
    public void PurgeExpired()
    {
        using var con = Open();
        var now = Now();
        con.Exec("DELETE FROM tokens WHERE expires_ts <= ?", now);
        con.Exec("DELETE FROM web_sessions WHERE expires_ts <= ?", now);
        PurgeDeletions(now);
    }

    /// <summary>Delete the deletion times older than the claim cooldown: at startup and in the daily purge.</summary>
    public void PurgeDeletions(long now)
    {
        using var con = Open();
        con.Exec("DELETE FROM steam_deletions WHERE ts <= ?", now - Claims.CooldownHours * 3600L);
    }

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
