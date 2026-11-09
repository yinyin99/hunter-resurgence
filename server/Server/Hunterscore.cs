// The hunterscore of a community server's account (docs/server.md#the-hunterscore): the rank rows of the facts file over the
// account's statistics (the restored ones plus this server's closed hunts, stored when each closes).
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The hunterscore of these counts: per rank row, min(cap, value), summed. userStats is the Statistics/total `data`
    /// (`animal.<species>.ethical_kills`, `weapon.<item>.<ammo>.ethical_kills`, `collectables.<type>.collected`).</summary>
    public static long Score(Facts facts, JsonObject userStats)
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
        return facts.Ranks.Sum(r => Math.Min(facts.RankCap, r.Category switch
        {
            "animal" => animal.GetValueOrDefault(r.Ids[0]),
            "weapon" => r.Ids.Sum(weapon.GetValueOrDefault),
            _ => r.Ids.Sum(collectables.GetValueOrDefault),
        }));
    }

    /// <summary>Store the statistics (<see cref="HuntStats"/>) of every closed expedition of the account that has none yet; call
    /// it inside a transaction. Its events end where the next expedition's begin; its kills are its harvests (event 3): each
    /// counts for its species and for the ammo of its last hit, ethical when no hit used an ammo not permitted on its species.
    /// A kill whose rows cannot be read back (a number that is not finite) is skipped and logged; a hunt with such an event
    /// counts nothing (logged), stored as empty so it is not read again.</summary>
    void StoreHuntStats(Db con, long account)
    {
        foreach (var e in con.Rows("SELECT id, start_ts, end_ts, first_event, distance, (SELECT MIN(n.first_event) FROM expeditions n"
                                   + " WHERE n.account_id = $a AND n.id > e.id) FROM expeditions e"
                                   + " WHERE account_id = $a AND end_ts IS NOT NULL AND stats IS NULL"))
        {
            var kills = new List<(JsonNode?, List<JsonObject>, bool)>();
            foreach (var k in con.Rows("SELECT k.individual, k.harvest FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                                       + " WHERE k.account_id = $a AND json_extract(h.body, '$.expedition_id') = ? ORDER BY k.kill_id DESC", e[0]))
                try
                {
                    var sp = Parse(k[1])["extended_species"];
                    var hits = KillHits(con, Py.FromSql(k[0]), (string)k[1]);
                    kills.Add((sp, hits, Ethical(sp, hits)));
                }
                catch (Exception ex) when (ex is JsonException or InvalidCastException or OverflowException or InvalidOperationException)
                {
                    Console.WriteLine($"statistics of account {account}: a kill is not counted: {ex.Message}");
                }
            var distance = Py.FromSql(e[4]);
            JsonObject stats;
            try
            {
                stats = HuntStats(con, Py.FromSql(e[3]), e[5] is long until ? until : long.MaxValue, kills,
                                  (long)(Convert.ToDouble(e[2]) - Convert.ToDouble(e[1])), Py.Truthy(distance) ? distance : 0);
            }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or OverflowException or InvalidOperationException)
            {
                Console.WriteLine($"statistics of account {account}: a hunt is not counted: {ex.Message}");
                stats = new JsonObject { ["duration"] = 0, ["distance"] = 0, ["data"] = new JsonObject { ["achievements"] = new JsonArray() } };
            }
            con.Exec("UPDATE expeditions SET stats = ? WHERE account_id = $a AND id = ?", Py.Dumps(stats), e[0]);
        }
    }

    /// <summary>The account's statistics (`data` of Statistics/total); only its closed hunts when the restored ones cannot be read back (logged).</summary>
    static JsonObject StoredStats(Db con, long account)
    {
        try
        {
            return (JsonObject)RestoredReads.StatisticsTotal(con)["data"]!;
        }
        catch (Exception e) when (e is JsonException or InvalidCastException or InvalidOperationException)
        {
            Console.WriteLine($"hunterscore of account {account}: the restored statistics are not counted: {e.Message}");
            var hunts = new JsonObject();
            RestoredReads.AddHunts(con, hunts);
            return hunts;
        }
    }

    /// <summary>Store the statistics of the account's newly closed hunts (<see cref="StoreHuntStats"/>), then recompute its
    /// hunterscore and store it in its connect `account` and, for a restored account, its Me/me profile. Every close path, a
    /// restore and every start call it, outside a transaction. The hunterscore is computed on a community server only; the
    /// recorded account (LocalAccount) keeps the recorded player's value. Always the computed value: what cannot be read counts
    /// 0, so a close, a restore or the start never fails and a backup's own value never stays.
    /// ponytail: sums every stored hunt of the account at every close; keep a running total if that gets slow.</summary>
    public void UpdateHunterscore(Db con, long account)
    {
        con.Begin();
        StoreHuntStats(con, account);
        if (Online && account != LocalAccount)
        {
            var score = Score(facts, StoredStats(con, account));
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', ?) WHERE id = ?", score, account);
            con.Exec("UPDATE profile SET body = json_set(body, '$.hunterscore', ?) WHERE account_id = $a", score);
        }
        con.Commit();
    }
}
