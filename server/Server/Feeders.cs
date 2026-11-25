// Bait feeders: the fields of a feeder's item metadata and how they follow from the time (docs/server.md, "Bait feeders").
using System.Text.Json.Nodes;

namespace Resurgence.Server;

/// <summary>A feeder's metadata keeps what the model needs (`bait_amount_org`, `start_bait_ts`, `delay_start_ts`, `shot_ts`, times in Unix seconds)
/// and the fields the game and the Bait Sites page read (`bait_amount`, `feeder_status`, `bait_attractiveness`, `penalty_remaining`);
/// <see cref="Refresh"/> derives the second kind from the first at the time of every reply. One bait unit goes every 8,640 s (10 a day);
/// a feeder is `Inactive` until 4 h after `delay_start_ts`, then `Active` (attractiveness 100), and `Penalty` for 30 min after `shot_ts`.</summary>
public static class Feeders
{
    public const long UnitSeconds = 8640, WarmupSeconds = 14400, PenaltySeconds = 1800, MaxAmount = 50;

    /// <summary>The keys a refill writes (<see cref="Refill"/>).</summary>
    static readonly string[] Written =
        ["bait_amount", "bait_amount_org", "start_bait_ts", "start_attr_ts", "delay_start_ts", "bait_attractiveness", "bait_attractiveness_org", "feeder_status", "penalty_remaining"];

    static long? Num(JsonObject md, string key) => md[key] is { } n && Py.IsNum(n) ? Py.L(n) : null;

    /// <summary>The bait left at t; null when the row lacks the model's inputs (a feeder never filled).</summary>
    static long? Remaining(JsonObject md, long t) =>
        Num(md, "bait_amount_org") is { } org && Num(md, "start_bait_ts") is { } start ? Math.Max(0, org - Math.Max(0, t - start) / UnitSeconds) : null;

    /// <summary>The bait left at `now`; 0 for a feeder never filled.</summary>
    public static long Left(JsonObject md, double now) => Remaining(md, (long)now) ?? 0;

    /// <summary>Set `bait_amount`, `feeder_status`, `bait_attractiveness` and `penalty_remaining` of a feeder's metadata for the time `now`.
    /// A row without the model's inputs is left as it is.</summary>
    public static void Refresh(JsonObject md, double now)
    {
        var t = (long)now;
        if (Remaining(md, t) is not { } amount)
            return;
        var penalty = Num(md, "shot_ts") is { } shot ? Math.Max(0, (PenaltySeconds - Math.Max(0, t - shot) + 59) / 60) : 0;  // minutes, rounded up
        var status = amount > 0 && penalty > 0 ? "Penalty"
            : amount > 0 && Num(md, "delay_start_ts") is { } delay && t - delay >= WarmupSeconds ? "Active" : "Inactive";
        md["bait_amount"] = amount;
        md["feeder_status"] = status;
        md["bait_attractiveness"] = status == "Active" ? 100 : 0;
        md["penalty_remaining"] = status == "Penalty" ? penalty : 0;
    }

    /// <summary>The feeder gets bait up to `amount` at `now`: the fields to store, or null when `amount` is not above what is left (the game also reports bait eaten).
    /// The amount and both start times restart; the warm-up restarts only for an empty feeder, and a feeder that still has bait keeps its attractiveness.</summary>
    public static JsonObject? Refill(JsonObject md, long amount, double now)
    {
        var t = (long)now;
        var left = Left(md, t);
        if (amount <= left)
            return null;
        var row = (JsonObject)md.DeepClone();
        row["bait_amount_org"] = amount;
        row["start_bait_ts"] = t;
        row["start_attr_ts"] = t;
        row["bait_attractiveness_org"] = left == 0 ? 0 : 100;
        if (left == 0 || Num(md, "delay_start_ts") is null)
            row["delay_start_ts"] = t;
        Refresh(row, t);
        return new JsonObject(Written.Where(row.ContainsKey).Select(k => KeyValuePair.Create(k, row[k]!.DeepClone())));
    }

    /// <summary>Feeders refilled by event 23 before the refill rule existed kept their old fields: for each, the refills newer than its `start_bait_ts` are applied again, in order, at their own times
    /// (a feeder the rule handled has `start_bait_ts` at its last refill, so it is left alone and a second run changes nothing). Runs at every start; `con` is one account's. The number of feeders changed.</summary>
    public static int Backfill(Db con)
    {
        var changed = 0;
        foreach (var guid in con.Rows("SELECT DISTINCT CAST(json_extract(body, '$.item') AS INTEGER) FROM events WHERE account_id = $a AND event = 23 AND body LIKE '%\"bait_amount\"%'"
                                      + " AND json_type(body, '$.metadata.bait_amount') IN ('integer', 'real')"))
        {
            if (con.Rows("SELECT metadata FROM items WHERE account_id = $a AND guid = ?", guid[0]).FirstOrDefault() is not { } row)
                continue;
            var md = JsonNode.Parse((string)row[0])!.AsObject();
            var patch = new JsonObject();
            foreach (var e in con.Rows("SELECT ts, json_extract(body, '$.metadata.bait_amount') FROM events WHERE account_id = $a AND event = 23 AND body LIKE '%\"bait_amount\"%'"
                                       + " AND CAST(json_extract(body, '$.item') AS INTEGER) = ?1 AND json_type(body, '$.metadata.bait_amount') IN ('integer', 'real')"
                                       + " AND ts >= ?2 ORDER BY id", guid[0], (Num(md, "start_bait_ts") ?? 0) + 1))
                if (Refill(md, Math.Clamp(Convert.ToInt64(e[1]), 0, MaxAmount), Convert.ToDouble(e[0])) is { } fields)
                    foreach (var (key, value) in fields)
                    {
                        md[key] = value!.DeepClone();
                        patch[key] = value.DeepClone();
                    }
            if (patch.Count == 0)
                continue;
            con.Exec("UPDATE items SET metadata = json_patch(metadata, ?) WHERE account_id = $a AND guid = ?", Py.Dumps(patch), guid[0]);
            changed++;
        }
        return changed;
    }
}
