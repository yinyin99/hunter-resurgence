// The hunterscore of a community server's account (docs/server.md#the-hunterscore): the rank rows of the facts file over the
// restored statistics plus this server's ethical kills, credited when an expedition closes.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The hunterscore of these counts: per rank row, min(cap, value), summed. userStats is the Statistics/total `data`
    /// (`animal.<species>.ethical_kills`, `weapon.<item>.<ammo>.ethical_kills`, `collectables.<type>.collected`);
    /// kills are this server's ethical kills of closed hunts: (species, weapon item of the last hit, null without a hit).</summary>
    public static long Score(Facts facts, JsonObject userStats, IEnumerable<(long Species, long? Weapon)> kills)
    {
        // a backup's user_stats is stored verbatim: a key that is no id or a counter that is no number counts 0, and a counter
        // counts at most the cap (a row never adds more), so no sum below can overflow
        var cap = facts.RankCap;
        long Count(JsonNode? n) => Py.IsNum(n) ? (long)Math.Clamp(Py.D(n), 0, cap) : 0;
        static IEnumerable<(long Id, JsonObject Entry)> Entries(JsonObject stats, string kind) =>
            (stats[kind] as JsonObject ?? []).Where(kv => kv.Value is JsonObject && long.TryParse(kv.Key, out _))
                .Select(kv => (long.Parse(kv.Key), (JsonObject)kv.Value!));
        static void Add(Dictionary<long, long> d, long id, long n) => d[id] = d.GetValueOrDefault(id) + n;

        Dictionary<long, long> animal = [], weapon = [], collectables = [];
        foreach (var (id, e) in Entries(userStats, "animal"))
            Add(animal, ((id % 100) + 100) % 100, Count(e["ethical_kills"]));  // variants (100+) count for their species
        foreach (var (id, e) in Entries(userStats, "weapon"))
            Add(weapon, id, e.Sum(ammo => ammo.Value is JsonObject a ? Count(a["ethical_kills"]) : 0));
        foreach (var (id, e) in Entries(userStats, "collectables"))
            Add(collectables, id, Count(e["collected"]));
        foreach (var (sp, w) in kills)
        {
            Add(animal, sp, 1);
            if (w is { } item)
                Add(weapon, item, 1);
        }
        return facts.Ranks.Sum(r => Math.Min(facts.RankCap, r.Category switch
        {
            "animal" => animal.GetValueOrDefault(r.Ids[0]),
            "weapon" => r.Ids.Sum(weapon.GetValueOrDefault),
            _ => r.Ids.Sum(collectables.GetValueOrDefault),
        }));
    }

    /// <summary>This server's ethical kills of the account's closed expeditions: (species, weapon item of the last hit). A
    /// kill counts once harvested (event 3); kills of the open expedition wait for its close. The heartbeat stores events as
    /// sent: a kill whose species is no whole number, or whose rows cannot be read back (a number that is not finite), is
    /// skipped and logged; a weapon that is no whole number counts for the species only.</summary>
    List<(long, long?)> ClosedKills(Db con, long account)
    {
        var kills = new List<(long, long?)>();
        foreach (var row in con.Rows(
            "SELECT k.individual, k.harvest FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
            + " WHERE k.account_id = $a AND NOT EXISTS (SELECT 1 FROM expeditions e WHERE e.account_id = $a AND e.end_ts IS NULL"
            + " AND e.id = json_extract(h.body, '$.expedition_id'))"))
        {
            try
            {
                if (!TryKey(Parse(row[1])["extended_species"], out var sp))
                    continue;
                var hits = KillHits(con, Py.FromSql(row[0]), (string)row[1]);
                if (Ethical(sp, hits))
                    kills.Add((((sp % 100) + 100) % 100, hits.Count > 0 && TryKey(hits[^1]["weapon_id"], out var w) ? w : null));
            }
            catch (Exception e) when (e is JsonException or InvalidCastException or OverflowException or InvalidOperationException)
            {
                Console.WriteLine($"hunterscore of account {account}: a kill is not counted: {e.Message}");
            }
        }
        return kills;
    }

    /// <summary>The restored statistics (`data` of Statistics/total); {} when they cannot be read back (logged).</summary>
    static JsonObject StoredStats(Db con, long account)
    {
        try
        {
            return (JsonObject)RestoredReads.StatisticsTotal(con)["data"]!;
        }
        catch (Exception e) when (e is JsonException or InvalidCastException or InvalidOperationException)
        {
            Console.WriteLine($"hunterscore of account {account}: the restored statistics are not counted: {e.Message}");
            return [];
        }
    }

    /// <summary>Recompute the account's hunterscore and store it in its connect `account` and, for a restored account, its Me/me
    /// profile. Community server only; the recorded account (LocalAccount) keeps the recorded player's value. Call it outside a transaction.
    /// Always the computed value: what cannot be read counts 0, so a close, a restore or the start never fails and a backup's own
    /// value never stays.
    /// ponytail: recounts every kill of the account (one KillHits query each) at every close; keep a running total if that gets slow.</summary>
    public void UpdateHunterscore(Db con, long account)
    {
        if (!Online || account == LocalAccount)
            return;
        var score = Score(facts, StoredStats(con, account), ClosedKills(con, account));
        con.Begin();
        con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', ?) WHERE id = ?", score, account);
        con.Exec("UPDATE profile SET body = json_set(body, '$.hunterscore', ?) WHERE account_id = $a", score);
        con.Commit();
    }
}
