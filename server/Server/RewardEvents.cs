// The events log (`events_log`) and the one-time backfill of the base of the rewards paid before it existed (docs/server.md#reward-bases-and-events).
// A payment's `wallet_log` row keeps its base (without any bonus) and the event that changed it; the event is a row here: a gm$ or mission bonus the operator
// turned on, or a period without a money bonus (a seasonal event) the operator added with `admin event add`.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class RewardEvents
{
    public const string GmBonus = "gm_bonus", MissionBonus = "mission_bonus", Seasonal = "seasonal";
    /// <summary>`applies_to` of an event whose multiplier is on harvest gm$ (`CSS Reward` payments).</summary>
    public const string HarvestGm = "harvest_gm";

    const string HarvestPayment = "CSS Reward (Animal Id: %";

    /// <summary>One event of a backfill file (or an `admin event add`): a period, and for a bonus its multiplier and what it multiplies.</summary>
    public sealed record EventSpec(string Name, string Kind, string AppliesTo, double? Multiplier, long Start, long End);

    /// <summary>The id of the event: the one that exists, or a new row. A bonus (a multiplier) is the same event when its kind, what it multiplies, its multiplier and its end are the same
    /// (whatever its name or start: the server's start that read it and the operator's backfill meet in one row); any other event when its name, kind and period are.</summary>
    public static long Record(Db con, string name, string kind, long start, long end, double? multiplier = null, string appliesTo = "")
    {
        var found = multiplier is { } m
            ? con.Scalar("SELECT id FROM events_log WHERE kind = ? AND applies_to = ? AND multiplier = ? AND end_ts = ?", kind, appliesTo, m, end)
            : con.Scalar("SELECT id FROM events_log WHERE name = ? AND kind = ? AND start_ts = ? AND end_ts = ? AND multiplier IS NULL", name, kind, start, end);
        return found is long id ? id
            : con.Insert("INSERT INTO events_log (name, kind, start_ts, end_ts, multiplier, applies_to) VALUES (?, ?, ?, ?, ?, ?)", name, kind, start, end, multiplier, appliesTo);
    }

    public static string Iso(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>An ISO time (no zone means UTC) as Unix seconds; null when it is not one.</summary>
    public static long? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeSeconds() : null;

    /// <summary>`admin event add`: the event is written (an identical one is not written twice); returns its id.</summary>
    public static long Add(Store store, string name, string kind, long start, long end)
    {
        using var con = store.Open();
        return Record(con, name, kind, start, end);
    }

    /// <summary>`admin event list`: one line per event, oldest first.</summary>
    public static List<string> List(Store store)
    {
        using var con = store.Open();
        return [.. con.Rows("SELECT id, name, kind, start_ts, end_ts, multiplier, applies_to FROM events_log ORDER BY start_ts, id").Select(r =>
            $"{r[0]} | {r[1]} | {r[2]} | {Iso((long)r[3])} .. {Iso((long)r[4])} | {(r[5] is double m ? "x" + m.ToString(CultureInfo.InvariantCulture) : "no multiplier")}{((string)r[6] is { Length: > 0 } a ? " on " + a : "")}")];
    }

    /// <summary>The events of a backfill file: a JSON array of `{"name", "start", "end"}` (ISO times, no zone means UTC) with, for a bonus, `"multiplier"` and `"applies_to": "harvest_gm"`
    /// (the only bonus that has payments to backfill), and an optional `"kind"` (default `gm_bonus` for a bonus, else `seasonal`).</summary>
    public static List<EventSpec> ParseSpecs(string json)
    {
        JsonArray list;
        try { list = JsonNode.Parse(json) as JsonArray ?? throw new InvalidDataException("the events file is a JSON array"); }
        catch (System.Text.Json.JsonException e) { throw new InvalidDataException("the events file is not JSON: " + e.Message); }
        return [.. list.Select(n =>
        {
            var o = n as JsonObject ?? throw new InvalidDataException("every event is a JSON object");
            var name = (o["name"] as JsonValue)?.TryGetValue<string>(out var s) == true && s.Trim() is { Length: > 0 } t ? t : throw new InvalidDataException("event: name is text");
            long? start = ParseTime((o["start"] as JsonValue)?.TryGetValue<string>(out var a) == true ? a : null), end = ParseTime((o["end"] as JsonValue)?.TryGetValue<string>(out var b) == true ? b : null);
            if (start is null || end is null || end <= start) throw new InvalidDataException($"event {name}: start and end are ISO times, end after start");
            double? multiplier = null;
            if (o["multiplier"] is { } mult)
                multiplier = mult is JsonValue v && v.TryGetValue<double>(out var d) && d > 0 && double.IsFinite(d) ? d : throw new InvalidDataException($"event {name}: multiplier is a number above 0");
            var appliesTo = (o["applies_to"] as JsonValue)?.TryGetValue<string>(out var at) == true ? at : "";
            if (multiplier is not null && appliesTo != HarvestGm) throw new InvalidDataException($"event {name}: a bonus needs \"applies_to\": \"{HarvestGm}\" (the only bonus with payments to backfill)");
            if (multiplier is null && appliesTo != "") throw new InvalidDataException($"event {name}: applies_to is for a bonus; give it a multiplier");
            var kind = (o["kind"] as JsonValue)?.TryGetValue<string>(out var k) == true && k.Trim() is { Length: > 0 } kk ? kk : multiplier is null ? Seasonal : GmBonus;
            return new EventSpec(name, kind, appliesTo, multiplier, start.Value, end.Value);
        })];
    }

    /// <summary>The seconds after a harvest's event that its payment still counts as the heartbeat's own: the payment is written in the heartbeat that carries the event.</summary>
    const long OwnPayment = 600;

    /// <summary>True when the payment `summary` made at `paidAt` came more than <see cref="OwnPayment"/> after the server received the kill's harvest event (a rescore pays a missed harvest later);
    /// false when it came with it, or when the harvest's event has no time (a row from before schema 14), which cannot tell.</summary>
    static bool PaidLong(Db con, string summary, long paidAt)
    {
        if (!long.TryParse(summary.AsSpan("CSS Reward (Animal Id: ".Length).TrimEnd(')'), out var kill)) return false;
        var times = con.Rows("SELECT e.ts FROM kills k JOIN events e ON e.account_id = $a AND e.event = 3 AND e.individual = k.individual WHERE k.account_id = $a AND k.kill_id = ? AND e.ts IS NOT NULL", kill);
        return times.Count > 0 && !times.Any(t => paidAt - Convert.ToDouble(t[0]) <= OwnPayment && paidAt - Convert.ToDouble(t[0]) >= -OwnPayment);
    }

    /// <summary>`admin backfill-rewards`: writes the events, then gives every payment that has no base (a row from before the log: base NULL) one. A harvest payment
    /// inside a bonus event's period has base = paid / multiplier (rounded as the payment was) and that event; every other payment has base = amount, no event. A payment made since
    /// the log exists already has its base, so it is never touched, and a second run changes nothing. A harvest payment inside a period that came long after its harvest (<see cref="PaidLong"/>:
    /// a rescore pays a missed harvest without the bonus) is reported and not divided. Without `apply` it writes nothing; one line per event, per such payment, and a total.</summary>
    public static List<string> Backfill(Store store, IReadOnlyList<EventSpec> specs, bool apply)
    {
        var lines = new List<string>();
        var ids = new Dictionary<EventSpec, long>();
        using (var con = store.Open())
            foreach (var s in specs)
            {
                if (apply)
                {
                    ids[s] = Record(con, s.Name, s.Kind, s.Start, s.End, s.Multiplier, s.AppliesTo);
                    con.Exec("UPDATE events_log SET name = ?, start_ts = ? WHERE id = ?", s.Name, s.Start, ids[s]);  // the operator's name and start win over a server start's
                }
                lines.Add($"event {s.Name}: {s.Kind}{(s.Multiplier is { } m ? " x" + m.ToString(CultureInfo.InvariantCulture) : "")}, {Iso(s.Start)} .. {Iso(s.End)}, {(apply ? "written" : "would be written")}");
            }
        var tally = specs.Where(x => x.Multiplier is not null).ToDictionary(x => x, _ => (Payments: 0L, Paid: 0.0, Base: 0.0));
        long plain = 0;
        List<long> accounts;
        using (var con = store.Open())
            accounts = [.. con.Rows("SELECT id FROM accounts ORDER BY id").Select(r => (long)r[0])];
        foreach (var account in accounts)
        {
            using var con = store.Open(account);
            if (apply) con.Begin();
            var open = (long)con.Scalar("SELECT COUNT(*) FROM wallet_log WHERE account_id = $a AND base IS NULL")!;  // before the events take theirs
            var claimed = new HashSet<long>();
            foreach (var s in specs.Where(x => x.Multiplier is not null))
                foreach (var r in con.Rows("SELECT n, amount, ts, summary FROM wallet_log WHERE account_id = $a AND currency = 'gm' AND amount > 0 AND base IS NULL AND summary LIKE ? AND ts >= ? AND ts < ?",
                                           HarvestPayment, s.Start, s.End))
                {
                    var (n, paid) = ((long)r[0], Convert.ToDouble(r[1]));
                    if (claimed.Contains(n)) continue;
                    if (PaidLong(con, (string)r[3], (long)r[2]))
                    {
                        // not the heartbeat's own payment: a rescore paid it later, without the bonus. It is not divided; it stays a payment with base = amount
                        lines.Add($"account {account}: {(string)r[3]} paid {paid.ToString(CultureInfo.InvariantCulture)} at {Iso((long)r[2])} long after its harvest (a rescore?): not divided, base = amount");
                        continue;
                    }
                    claimed.Add(n);
                    var baseAmount = (double)Py.RoundInt(paid / s.Multiplier!.Value);
                    tally[s] = (tally[s].Payments + 1, tally[s].Paid + paid, tally[s].Base + baseAmount);
                    if (apply) con.Exec("UPDATE wallet_log SET base = ?, bonus_event = ? WHERE account_id = $a AND n = ?", baseAmount, ids[s], n);
                }
            plain += open - claimed.Count;
            if (apply)
            {
                con.Exec("UPDATE wallet_log SET base = amount WHERE account_id = $a AND base IS NULL");
                con.Commit();
            }
        }
        foreach (var (s, t) in tally)
            lines.Add($"{s.Name}: {t.Payments} harvest payments, paid {t.Paid.ToString(CultureInfo.InvariantCulture)}, base {t.Base.ToString(CultureInfo.InvariantCulture)}");
        lines.Add($"every other payment: {plain} get base = amount");
        return lines;
    }
}
