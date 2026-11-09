// The hunterscore and achievements of a community server's account (docs/server.md#the-hunterscore): the rank rows of the facts
// file and the achievement definitions over the account's statistics (the restored ones plus this server's closed hunts, stored
// when each closes).
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

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
                catch (Exception ex) when (ex is not SqliteException)  // a value a player sent, of any kind; a database error stays an error
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
            catch (Exception ex) when (ex is not SqliteException)
            {
                Console.WriteLine($"statistics of account {account}: a hunt is not counted: {ex.Message}");
                stats = new JsonObject { ["duration"] = 0, ["distance"] = 0, ["data"] = new JsonObject { ["achievements"] = new JsonArray() } };
            }
            con.Exec("UPDATE expeditions SET stats = ? WHERE account_id = $a AND id = ?", Py.Dumps(stats), e[0]);
        }
    }

    /// <summary>The account's statistics (Statistics/total: `duration`, `distance`, `data`); only its closed hunts when the
    /// restored ones cannot be read back (logged).</summary>
    static JsonObject StoredStats(Db con, long account)
    {
        try
        {
            return (JsonObject)RestoredReads.StatisticsTotal(con);
        }
        catch (Exception e) when (e is JsonException or InvalidCastException or InvalidOperationException)
        {
            Console.WriteLine($"hunterscore of account {account}: the restored statistics are not counted: {e.Message}");
            var hunts = new JsonObject();
            var (distance, duration) = RestoredReads.AddHunts(con, hunts);
            return new JsonObject { ["duration"] = duration, ["distance"] = distance, ["data"] = hunts };
        }
    }

    /// <summary>The daily mission kinds of an achievement path by name, as the statistics key them.</summary>
    static readonly Dictionary<string, long> DailyMissions = new() { ["EASY"] = 1, ["MEDIUM"] = 2, ["HARD"] = 3 };

    /// <summary>The counter an achievement's `value` path names in the statistics (Statistics/total): segments split on `.`; the
    /// root `animal`, `weapon`, `collectable` or `daily_missions` is that object of `data`, `distance` the metres x 0.001 (km) and
    /// `duration` x 0.000256 (hours); the segment after the root is a name (a species or item define, a daily mission kind) or an
    /// id; `*` sums every child, `[a,b]` the ones listed; the last segment is the counter. What is missing or no number counts 0.</summary>
    double AchievementValue(JsonObject stats, string path)
    {
        var segments = path.Split('.');
        if (segments[0] is "distance" or "duration")
            return Py.IsNum(stats[segments[0]]) ? Py.D(stats[segments[0]]) * (segments[0] == "distance" ? 0.001 : 0.000256) : 0;
        var names = segments[0] switch { "animal" => speciesIds, "weapon" or "collectable" => itemIds, _ => DailyMissions };
        double Sum(JsonNode? n, int i)
        {
            if (i == segments.Length)
                return Py.IsNum(n) ? Py.D(n) : 0;
            if (n is not JsonObject o)
                return 0;
            var s = segments[i];
            if (s == "*")
                return o.Sum(kv => Sum(kv.Value, i + 1));
            return (s.StartsWith('[') && s.EndsWith(']') ? s[1..^1].Split(',') : [s]).Select(k => k.Trim())
                .Sum(k => Sum(o[i == 1 && names.TryGetValue(k, out var key) ? key.ToString() : k], i + 1));
        }
        return Sum((stats["data"] as JsonObject)?[segments[0] == "collectable" ? "collectables" : segments[0]], 1);
    }

    /// <summary>Award the achievements the account's statistics reach: for every definition of the connect reply's
    /// `achievements` (`value` path, `triggers` thresholds), store the current time for each threshold at most its value
    /// (<see cref="AchievementValue"/>) that has no time yet, and that value, in the account's achievement row (made when the
    /// value is above 0). Times already stored, a restore's too, are kept. Call it inside a transaction.
    /// ponytail: evaluates every definition (a few hundred) at every close; only the touched roots if that gets slow.</summary>
    void AwardAchievements(Db con, JsonObject stats)
    {
        var now = (long)Clock();
        foreach (var def in Arr(G(connect, "achievements", null)).OfType<JsonObject>())
        {
            if (!TryKey(def["id"], out var id) || !Py.IsStr(def["value"]))
                continue;
            var value = AchievementValue(stats, (string)def["value"]!);
            var row = con.Rows("SELECT triggers FROM achievements WHERE account_id = $a AND id = ?", id).FirstOrDefault();
            if (row is null && value <= 0)
                continue;
            var triggers = row is null ? new JsonObject() : Parse(row[0]);
            foreach (var t in Arr(def["triggers"]))
                if (Py.IsNum(t) && Py.D(t) <= value && triggers[Py.Str(t)] is null)
                    triggers[Py.Str(t)] = now;
            PlayerRows.AddAchievement(con, id, TryKey(def["category"], out var c) ? c : 0, TryKey(def["group"], out var g) ? g : 0,
                                      (long)Math.Clamp(Math.Floor(value), 0, 1e15), triggers);
        }
    }

    /// <summary>Store the statistics of the account's newly closed hunts (<see cref="StoreHuntStats"/>), then recompute its
    /// hunterscore and store it in its connect `account` and, for a restored account, its Me/me profile, and award the
    /// achievements its statistics reach (<see cref="AwardAchievements"/>). Every close path, a restore and every start call it,
    /// outside a transaction. The hunterscore and achievements are computed on a community server only; the recorded account
    /// (LocalAccount) keeps the recorded player's values. Always the computed value: what cannot be read counts 0, so a close, a
    /// restore or the start never fails and a backup's own value never stays.
    /// ponytail: sums every stored hunt of the account at every close; keep a running total if that gets slow.</summary>
    public void UpdateHunterscore(Db con, long account)
    {
        con.Begin();
        StoreHuntStats(con, account);
        if (Online && account != LocalAccount)
        {
            var stats = StoredStats(con, account);
            var score = Score(facts, (JsonObject)stats["data"]!);
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', ?) WHERE id = ?", score, account);
            con.Exec("UPDATE profile SET body = json_set(body, '$.hunterscore', ?) WHERE account_id = $a", score);
            AwardAchievements(con, stats);
        }
        con.Commit();
    }
}
