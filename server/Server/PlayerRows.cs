// Typed inserts for the rows a restored backup writes. Each takes the Db of one account (Store.Open(account)) and
// belongs inside that caller's transaction; nothing here reads a zip.
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class PlayerRows
{
    /// <summary>Delete the account's profile rows (items, wallet, skills, profile, statistics, achievements, trophies,
    /// lodges, extras) before a restore writes them again. Hunt history (events, expeditions, kills, harvests, photos,
    /// messages, pending) and restore_audit stay.</summary>
    public static void ClearModel(Db con)
    {
        foreach (var table in new[] { "items", "wallet", "skills", "profile", "statistics", "achievements", "trophies", "lodges", "extras" })
            con.Exec($"DELETE FROM {table} WHERE account_id = $a");
    }

    /// <summary>guid comes from the zip; the client sees it as is. metadata carries anything else the item row has.</summary>
    public static void AddItem(Db con, long guid, long itemId, long slot, long? units, JsonObject metadata) =>
        con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, ?, ?, ?, ?, ?)",
                 guid, itemId, slot, Py.Dumps(metadata), units);

    public static void SetWallet(Db con, string currency, double amount) =>
        con.Exec("INSERT OR REPLACE INTO wallet (account_id, currency, amount) VALUES ($a, ?, ?)", currency, amount);

    /// <summary>kind: weapon (key: item id), tracking or spotting (key: species slot); xp as the skills table holds it.</summary>
    public static void SetSkill(Db con, string kind, long key, double xp) =>
        con.Exec("INSERT OR REPLACE INTO skills (account_id, kind, key, xp) VALUES ($a, ?, ?, ?)", kind, key, xp);

    public static void SetProfile(Db con, JsonNode profile) =>
        con.Exec("INSERT OR REPLACE INTO profile (account_id, body) VALUES ($a, ?)", Py.Dumps(profile));

    public static void SetStatistics(Db con, JsonObject userStats, double distance, double duration, long? bustThroughScore, long? laneBanditScore) =>
        con.Exec("INSERT OR REPLACE INTO statistics (account_id, user_stats, distance, duration, bust_through_score, lane_bandit_score)"
                 + " VALUES ($a, ?, ?, ?, ?, ?)", Py.Dumps(userStats), distance, duration, bustThroughScore, laneBanditScore);

    public static void AddAchievement(Db con, long id, long categoryId, long groupId, JsonNode? value, JsonObject triggers) =>
        con.Exec("INSERT OR REPLACE INTO achievements (account_id, id, category_id, group_id, value, triggers)"
                 + " VALUES ($a, ?, ?, ?, ?, ?)", id, categoryId, groupId, Py.Dumps(value), Py.Dumps(triggers));

    /// <summary>A reply row as sent; source: the endpoint it came from, id: its id (not unique: rows are kept as read).</summary>
    public static void AddTrophy(Db con, string source, string id, JsonNode? node) =>
        con.Exec("INSERT INTO trophies (account_id, source, id, node) VALUES ($a, ?, ?, ?)", source, id, Py.Dumps(node));

    public static void AddLodge(Db con, string source, string id, JsonNode? node) =>
        con.Exec("INSERT INTO lodges (account_id, source, id, node) VALUES ($a, ?, ?, ?)", source, id, Py.Dumps(node));

    /// <summary>A saved reply with no typed home, keyed by its path in the zip.</summary>
    public static void SetExtra(Db con, string path, JsonNode? body) =>
        con.Exec("INSERT OR REPLACE INTO extras (account_id, path, body) VALUES ($a, ?, ?)", path, Py.Dumps(body));

    /// <summary>One row per restore of a zip (its SHA-256, hex), kept when the profile rows are replaced.</summary>
    public static void AddAudit(Db con, string zipSha256, string mode, string report) =>
        con.Exec("INSERT INTO restore_audit (account_id, ts, zip_sha256, mode, report) VALUES ($a, ?, ?, ?, ?)",
                 Store.Now(), zipSha256, mode, report);
}
