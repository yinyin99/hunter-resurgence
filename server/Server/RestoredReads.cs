// The client's replies built from the rows a restore wrote (Restorer): the profile, statistics and achievements tables.
// An account without a profile row has no restored rows, and these return null: the caller's recorded reply stands. The
// statistics add this server's closed hunts on top of the restored ones.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class RestoredReads
{
    /// <summary>The account's Me/me reply as restored (without wallet, which is its own table); null: nothing restored.</summary>
    public static JsonObject? Me(Db con) =>
        con.Scalar("SELECT body FROM profile WHERE account_id = $a") is string body ? JsonNode.Parse(body)!.AsObject() : null;

    public static bool Has(Db con) => con.Scalar("SELECT 1 FROM profile WHERE account_id = $a") is not null;

    /// <summary>me plus every key of the recorded reply that me lacks, empty of the recorded value's kind: a zip that held
    /// no Me/me (a public one) must not show another player's records.</summary>
    public static JsonObject FillMissing(JsonObject me, JsonObject recorded)
    {
        var reply = (JsonObject)me.DeepClone();
        foreach (var (key, value) in recorded)
            if (!reply.ContainsKey(key))
                reply[key] = value switch
                {
                    JsonObject => new JsonObject(),
                    JsonArray => new JsonArray(),
                    JsonValue v when v.GetValueKind() == JsonValueKind.String => "",
                    JsonValue v when v.GetValueKind() == JsonValueKind.Number => 0,
                    JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => false,
                    _ => null,
                };
        return reply;
    }

    /// <summary>Achievement/stats: one row per achievement, ordered by id. An achievement never earned has no `triggers` member, as the API sends it.</summary>
    public static JsonNode AchievementStats(Db con) => new JsonArray(
        con.Rows("SELECT id, category_id, group_id, value, triggers FROM achievements WHERE account_id = $a ORDER BY id").Select(r =>
        {
            var row = new JsonObject { ["category_id"] = Py.FromSql(r[1]), ["group_id"] = Py.FromSql(r[2]), ["id"] = Py.FromSql(r[0]) };
            if (JsonNode.Parse((string)r[4])!.AsObject() is { Count: > 0 } triggers)
                row["triggers"] = triggers;
            row["value"] = JsonNode.Parse((string)r[3]);
            return (JsonNode?)row;
        }).ToArray());

    /// <summary>The four per-kind statistic objects plus the distance and duration, as the connect reply and Statistics/total carry
    /// them: the restored ones (the base, which play never changes) plus the account's closed hunts (<see cref="AddHunts"/>).</summary>
    static (JsonObject Data, double Distance, double Duration, JsonNode? Bust, JsonNode? Lane) Statistics(Db con)
    {
        var row = con.Rows("SELECT user_stats, distance, duration, bust_through_score, lane_bandit_score FROM statistics WHERE account_id = $a").FirstOrDefault();
        var data = row is null ? new JsonObject() : JsonNode.Parse((string)row[0])!.AsObject();
        if (row is null)  // nothing restored: the empty statistics of a new player; a restored `data` stays as saved (an older reply has no daily_missions)
            foreach (var kind in new[] { "collectables", "weapon", "animal", "daily_missions" })
                data[kind] = new JsonObject();
        var (distance, duration) = AddHunts(con, data);
        return (data, distance + (row is null ? 0 : Convert.ToDouble(row[1])), duration + (row is null ? 0 : Convert.ToDouble(row[2])),
                row is null ? null : Py.FromSql(row[3]), row is null ? null : Py.FromSql(row[4]));
    }

    /// <summary>Add the stored statistics of the account's closed hunts (State.HuntStats) to data's `animal` and `weapon`: counters
    /// add up, a weapon's `distance` (its longest hit) is the largest, a counter of data that is no number is replaced. Returns the
    /// hunts' summed distance and duration. A hunt that cannot be read back (a number that is not finite) is skipped and logged.</summary>
    public static (double Distance, double Duration) AddHunts(Db con, JsonObject data)
    {
        double distance = 0, duration = 0;
        foreach (var r in con.Rows("SELECT stats FROM expeditions WHERE account_id = $a AND stats IS NOT NULL ORDER BY id"))
        {
            JsonObject hunt;
            try
            {
                hunt = JsonNode.Parse((string)r[0])!.AsObject();
            }
            catch (JsonException e)
            {
                Console.WriteLine($"statistics: a hunt is not counted: {e.Message}");
                continue;
            }
            distance += Py.IsNum(hunt["distance"]) ? Py.D(hunt["distance"]) : 0;
            duration += Py.D(hunt["duration"]);
            foreach (var kind in new[] { "animal", "weapon" })
                if (hunt["data"]![kind] is JsonObject counts)
                    Add(data, kind, counts);
        }
        return (distance, duration);
    }

    static void Add(JsonObject into, string key, JsonNode? value)
    {
        if (value is JsonObject o)
        {
            if (into[key] is not JsonObject sub)
                into[key] = sub = new JsonObject();
            foreach (var (k, v) in o)
                Add(sub, k, v);
        }
        else
            into[key] = !Py.IsNum(into[key]) ? value?.DeepClone()
                : Number(key == "distance" ? Math.Max(Py.D(into[key]), Py.D(value)) : Py.D(into[key]) + Py.D(value));
    }

    /// <summary>A whole number as an integer, the way the API sends distance and duration.</summary>
    static JsonNode Number(double d) => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? (long)d : d;

    public static JsonNode StatisticsTotal(Db con)
    {
        var s = Statistics(con);
        return new JsonObject { ["duration"] = Number(s.Duration), ["distance"] = Number(s.Distance), ["data"] = s.Data };
    }

    /// <summary>The connect reply's per-account keys from the restored rows: user_stats (with the achievements' earn times),
    /// account_info (what no reply of the web API holds is the importer's default; no e-mail) and the network handle and
    /// user id. me is the account's restored Me/me; steamId64 the one the server verified, never one a zip claims.</summary>
    public static void Connect(Db con, JsonObject reply, JsonObject me, string? steamId64)
    {
        var s = Statistics(con);
        s.Data["distance"] = Number(s.Distance);
        s.Data["duration"] = Number(s.Duration);
        reply["user_stats"] = new JsonObject
        {
            ["achievement_stats"] = AchievementStats(con), ["bust_through_score"] = s.Bust?.DeepClone(), ["first_time"] = 0,
            ["lane_bandit_score"] = s.Lane?.DeepClone(), ["user_stats"] = s.Data,
        };
        var id = (JsonNode?)me["id"]?.DeepClone();
        reply["account_info"] = new JsonObject
        {
            ["locale"] = me["locale"]?.DeepClone(), ["facebook_id"] = null, ["has_password"] = 0, ["share_on_facebook"] = 0,
            ["timezone"] = me["timezone"]?.DeepClone(), ["id"] = id, ["user_id"] = id?.DeepClone(), ["uuid"] = me["uuid"]?.DeepClone(),
            ["country"] = me["country"]?.DeepClone(), ["account_state"] = 0, ["steam_id"] = steamId64 ?? "",
            ["user_group"] = me["user_group"]?.DeepClone() ?? 1, ["email_verified"] = 0, ["email"] = null,
        };
        if (reply["network"] is JsonObject network)
        {
            network["handle"] = me["handle"]?.DeepClone();
            network["host"] = id?.DeepClone();  // differs by account like the handle and user id: the singleplayer host is the player
            network["user_id"] = id?.DeepClone();
        }
    }
}
