// Server playtime (the hunts made here, apart from the restored total) and the hunting statistics built on it: gm$ per hour per reserve and per species.
// Everything comes from `expeditions`, `kills`/`harvests` and `wallet_log`, so every hunt already played counts at once (docs/server.md#server-playtime-and-hunting-statistics).
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The day the server opened, 2026-11-12 00:00 UTC (Unix seconds): the default of settings.json `playtime_since`.</summary>
    public const long DefaultPlaytimeSince = 1794441600;

    /// <summary>settings.json `playtime_since` (Unix seconds): a hunt that started before it is no server playtime (an operator's test).</summary>
    public double PlaytimeSince { get; set; } = DefaultPlaytimeSince;

    /// <summary>A figure of the public hunting statistics is built from at least this many players, so no one player can be singled out.</summary>
    public const int HuntingMinPlayers = 5;

    /// <summary>How long the hunting statistics are kept before they are computed again.</summary>
    public static readonly TimeSpan HuntingTtl = TimeSpan.FromHours(1);

    const string RewardPrefix = "CSS Reward (Animal Id: ";

    /// <summary>The gm$ rewards that are not a harvest's, by their wallet summary (the operator's view lists them apart).</summary>
    static readonly (string Kind, string Summary)[] OtherRewards =
        [("mission", "gm$ reward"), ("community", "gm$ community reward"), ("daily", "Reward for Daily Mission"), ("dog", "Dog level reward.")];

    sealed record ServerHunt(long Id, long? Reserve, double Seconds, bool NoHeartbeat);

    /// <summary>The account's hunts made here since <see cref="PlaytimeSince"/>. A hunt's seconds: a closed one from its start to its end, an open one from its start to its last heartbeat (so far: the stale close
    /// ends a hunt that stopped beating, and one that never got a heartbeat is never closed by it, so "until now" would grow for ever); a hunt with no heartbeat after its start counts 0 rather than a guess.</summary>
    List<ServerHunt> ServerHunts(Db con) =>
        [.. con.Rows("SELECT id, reserve, start_ts, end_ts, last_seen FROM expeditions WHERE account_id = $a AND start_ts >= ?", PlaytimeSince).Select(r =>
        {
            var start = Convert.ToDouble(r[2]);
            var open = r[3] is DBNull;
            var seen = r[4] is not DBNull;
            var seconds = open ? seen ? Convert.ToDouble(r[4]) - start : 0 : seen ? Convert.ToDouble(r[3]) - start : 0;
            return new ServerHunt((long)r[0], r[1] is long reserve ? reserve : null, Math.Max(0, seconds), !open && !seen);
        })];

    /// <summary>The seconds the account hunted on this server since <see cref="PlaytimeSince"/> (`huntSecondsHere`); the open hunt up to its last heartbeat.</summary>
    public long HuntSecondsHere(Db con) => (long)ServerHunts(con).Sum(h => h.Seconds);

    sealed class Cell { public double Seconds, Gm; public long Harvests; }

    /// <summary>One account's hunting figures: its server playtime, per reserve the playtime, harvest gm$ and harvests of the hunts there, per species and reserve the harvests and their gm$, and the other gm$ rewards by kind.</summary>
    sealed record AccountHunting(long Account, string Handle, double Seconds, Dictionary<long, Cell> Reserves, Dictionary<(long Species, long Reserve), Cell> Species, Dictionary<string, double> OtherGm);

    sealed record HuntingFigures(double At, List<AccountHunting> Players);

    readonly object huntingGate = new();
    HuntingFigures? huntingKept;

    /// <summary>The figures of every account that hunted here (a banned account and the recorded player's are left out), computed at most once in <see cref="HuntingTtl"/>.
    /// ponytail: scans every account on its own connection, as <see cref="ComputeBoards"/>; once an hour that is fine, store the per-hunt figures at the hunt's close if it gets slow.</summary>
    HuntingFigures KeptHunting()
    {
        lock (huntingGate)
        {
            var now = Clock();
            if (huntingKept is { } kept && now >= kept.At && now - kept.At < HuntingTtl.TotalSeconds) return kept;
            List<(long Id, string Handle)> accounts;
            using (var con = Store.Open())
                accounts = [.. con.Rows("SELECT id, handle FROM accounts WHERE banned = 0 AND id IS NOT ? ORDER BY id", LocalAccount).Select(r => ((long)r[0], (string)r[1]))];
            var players = new List<AccountHunting>();
            var noHeartbeat = 0;
            foreach (var (id, handle) in accounts)
            {
                using var con = Con(id);
                var hunting = AccountFigures(con, id, handle, ref noHeartbeat);
                if (hunting.Reserves.Count > 0 || hunting.OtherGm.Count > 0 || hunting.Seconds > 0) players.Add(hunting);
            }
            Console.WriteLine($"hunting statistics: {noHeartbeat} hunts closed without a heartbeat count 0 s");
            return huntingKept = new HuntingFigures(now, players);
        }
    }

    AccountHunting AccountFigures(Db con, long account, string handle, ref int noHeartbeat)
    {
        var hunts = ServerHunts(con).ToDictionary(h => h.Id);
        var onReserve = new Dictionary<long, Cell>();
        var seconds = 0.0;
        foreach (var h in hunts.Values)
        {
            seconds += h.Seconds;
            if (h.NoHeartbeat) noHeartbeat++;
            if (h.Reserve is { } r) (onReserve.TryGetValue(r, out var c) ? c : onReserve[r] = new Cell()).Seconds += h.Seconds;
        }
        var paid = new Dictionary<long, double>();  // gm$ paid by kill
        var other = new Dictionary<string, double>();
        foreach (var w in con.Rows("SELECT summary, amount, ts FROM wallet_log WHERE account_id = $a AND currency = 'gm' AND amount > 0"))
        {
            var (summary, amount) = ((string)w[0], Convert.ToDouble(w[1]));
            if (summary.StartsWith(RewardPrefix, StringComparison.Ordinal) && summary.EndsWith(')') && long.TryParse(summary.AsSpan(RewardPrefix.Length, summary.Length - RewardPrefix.Length - 1), out var kill))
                paid[kill] = paid.GetValueOrDefault(kill) + amount;
            else if (Convert.ToDouble(w[2]) >= PlaytimeSince && OtherRewards.FirstOrDefault(o => o.Summary == summary).Kind is { } kind)
                other[kind] = other.GetValueOrDefault(kind) + amount;
        }
        var harvested = new Dictionary<(long, long), Cell>();
        foreach (var k in con.Rows("SELECT k.kill_id, k.species, json_extract(h.body, '$.expedition_id') FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id WHERE k.account_id = $a"))
        {
            if (k[2] is not long hunt || !hunts.TryGetValue(hunt, out var found) || found.Reserve is not { } reserve || k[1] is not long sp) continue;
            var gm = paid.GetValueOrDefault((long)k[0]);
            var byReserve = onReserve[reserve];
            byReserve.Gm += gm;
            byReserve.Harvests++;
            var cell = harvested.TryGetValue((sp, reserve), out var c) ? c : harvested[(sp, reserve)] = new Cell();
            cell.Gm += gm;
            cell.Harvests++;
        }
        return new AccountHunting(account, handle, seconds, onReserve, harvested, other);
    }

    static double Per(double amount, double by) => by > 0 ? Math.Round(amount / by, 2) : 0;

    static JsonObject Rates(JsonObject row, double seconds, double gm, long harvests)
    {
        row["hours"] = Math.Round(seconds / 3600, 1);
        row["gmPerHour"] = Per(gm * 3600, seconds);
        row["gmPerHarvest"] = Per(gm, harvests);
        row["harvestsPerHour"] = Per(harvests * 3600.0, seconds);
        return row;
    }

    /// <summary>`GET /stats/hunting`: server-wide figures per reserve (playtime, gm$ per hour, gm$ per harvest, harvests per hour) and per species (the same, `hours` the playtime on the reserves where the
    /// species was harvested, by the players who harvested it there; `bestReserve` the reserve where the species paid most gm$ per hour, from reserves of at least <see cref="HuntingMinPlayers"/> players, else null), `since` the day counted from.
    /// Never a name, handle or rank; a row built from fewer than <see cref="HuntingMinPlayers"/> players is left out. Harvest gm$ only, as paid (a time-limited bonus included).</summary>
    public JsonObject HuntingStatistics()
    {
        var players = KeptHunting().Players;
        var reserveRows = players.SelectMany(p => p.Reserves.Where(r => r.Value.Seconds > 0).Select(r => (Reserve: r.Key, r.Value)))
            .GroupBy(x => x.Reserve).Where(g => g.Count() >= HuntingMinPlayers)
            .Select(g => (Name: ReserveName(JsonValue.Create(g.Key)), Row: Rates(new JsonObject { ["reserve"] = ReserveName(JsonValue.Create(g.Key)), ["players"] = g.Count() },
                g.Sum(x => x.Value.Seconds), g.Sum(x => x.Value.Gm), g.Sum(x => x.Value.Harvests))))
            .OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => (JsonNode?)x.Row);
        // a name is one id (the typical and the non-typical deer are listed apart): one cell per player and reserve, the reserve's playtime counted once
        var cells = players.SelectMany(p => p.Species.Select(s => (p.Account, Name: BoardName(s.Key.Species), s.Key.Reserve, s.Value, Seconds: p.Reserves[s.Key.Reserve].Seconds)))
            .GroupBy(c => (c.Name, c.Account, c.Reserve)).Select(g => (g.Key.Name, g.Key.Account, g.Key.Reserve, Harvests: g.Sum(c => c.Value.Harvests), Gm: g.Sum(c => c.Value.Gm), g.First().Seconds));
        var speciesRows = cells.GroupBy(c => c.Name).Where(g => g.Select(c => c.Account).Distinct().Count() >= HuntingMinPlayers)
            .Select(g =>
            {
                var best = g.GroupBy(c => c.Reserve).Where(r => r.Count() >= HuntingMinPlayers && r.Sum(c => c.Seconds) > 0).MaxBy(r => r.Sum(c => c.Gm) / r.Sum(c => c.Seconds));
                var row = Rates(new JsonObject { ["species"] = g.Key, ["players"] = g.Select(c => c.Account).Distinct().Count() }, g.Sum(c => c.Seconds), g.Sum(c => c.Gm), g.Sum(c => c.Harvests));
                row["bestReserve"] = best is null ? null : ReserveName(JsonValue.Create(best.Key));
                return (g.Key, Row: row);
            })
            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode?)x.Row);
        return new JsonObject { ["reserves"] = new JsonArray([.. reserveRows]), ["species"] = new JsonArray([.. speciesRows]), ["since"] = DateTimeOffset.FromUnixTimeSeconds((long)PlaytimeSince).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") };
    }

    /// <summary>The operator's view, `GET /admin/hunting`: every player's figures with the name, most playtime first: `huntSecondsHere`, `reserves` (the hunts' playtime, harvest gm$ and harvests, gm$ per hour), `species` (harvests and their gm$ by species and reserve),
    /// `otherGm` (the mission, community, daily and dog rewards, apart from the harvests'). Kept like <see cref="HuntingStatistics"/>, so up to <see cref="HuntingTtl"/> old.</summary>
    public JsonObject HuntingAdmin() => new()
    {
        ["since"] = DateTimeOffset.FromUnixTimeSeconds((long)PlaytimeSince).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        ["players"] = new JsonArray([.. KeptHunting().Players.OrderByDescending(p => p.Seconds).ThenBy(p => p.Account).Select(p => (JsonNode?)new JsonObject
        {
            ["account"] = p.Account, ["handle"] = p.Handle, ["huntSecondsHere"] = (long)p.Seconds,
            ["reserves"] = new JsonArray([.. p.Reserves.OrderBy(r => r.Key).Select(r => (JsonNode?)Rates(new JsonObject { ["reserve"] = ReserveName(JsonValue.Create(r.Key)), ["gm"] = r.Value.Gm, ["harvests"] = r.Value.Harvests }, r.Value.Seconds, r.Value.Gm, r.Value.Harvests))]),
            ["species"] = new JsonArray([.. p.Species.OrderBy(s => s.Key.Species).ThenBy(s => s.Key.Reserve).Select(s => (JsonNode?)new JsonObject
            {
                ["species"] = BoardName(s.Key.Species), ["reserve"] = ReserveName(JsonValue.Create(s.Key.Reserve)), ["harvests"] = s.Value.Harvests, ["gm"] = s.Value.Gm,
            })]),
            ["otherGm"] = new JsonObject([.. p.OtherGm.OrderBy(o => o.Key).Select(o => KeyValuePair.Create(o.Key, (JsonNode?)o.Value))]),
        })]),
    };
}
