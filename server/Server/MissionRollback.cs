// Missions checked again against the harvests already stored: at the server's start (<see cref="ConfirmMissions"/>: what a version that understands more titles
// finishes late) and by the operator (<see cref="RollbackMissions"/>: what the lax rules of an earlier version finished wrongly). docs/server.md, "Missions".
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>A stored harvest (event 3) with its kill and its animal's species and gender, ready for the rules.</summary>
    sealed record Harvest(long Id, Kill Kill, long? Species, long? Gender)
    {
        public bool Meets(Rule r) => (r.Species is null || r.Species == Species) && (r.Gender is null || r.Gender == Gender) && State.Meets(r, Kill);
    }

    List<Harvest> HarvestsOf(Db con, long after) =>
        [.. con.Rows("SELECT id, body FROM events WHERE account_id = $a AND event = 3 AND id > ? ORDER BY id", after).Select(r =>
        {
            var (id, e3) = ((long)r[0], Parse(r[1]));
            var kill = KillOf(con, e3, id);
            var (species, gender) = AnimalOf(kill.E7, e3["extended_species"]);
            return new Harvest(id, kill, species, gender);
        })];

    static bool IsHarvest(MissionDef m, long objective) => m.Objectives.FirstOrDefault(o => o.Id == objective)?.Rule.Kind == Kind.Harvest;

    /// <summary>The harvest objectives of `m` that `harvests` meet, in order, each with the harvest that met it. `start` are the objectives counted as done before the first
    /// (those of other kinds, and the ones a restore brought).</summary>
    static List<(Objective Objective, long At)> Replay(MissionDef m, IEnumerable<long> start, IEnumerable<Harvest> harvests)
    {
        var (done, met) = (start.Distinct().ToList(), new List<(Objective, long)>());
        foreach (var h in harvests)
            if (Meet(m, done, Kind.Harvest, h.Meets) is { } o)
                met.Add((o, h.Id));
        return met;
    }

    /// <summary>The objectives counted as done before the first harvest of a replay: those of other kinds and the ones a restore brought; for a confirmation also the ones finished
    /// before the harvests it checks (a harvest after them did not finish those, and must not be given to them again).</summary>
    static long[] StartOf(MissionDef m, IEnumerable<long> done, Mark mark, bool confirm) =>
        [.. done.Where(d => !IsHarvest(m, d)).Concat(mark.KeptIds).Concat(confirm ? mark.EarlierIds : []).Distinct()];

    /// <summary>The first event of the account's latest hunt (0 before any).</summary>
    static long LatestHunt(Db con) => con.Scalar("SELECT COALESCE(first_event, 0) FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1") as long? ?? 0;

    // ---- confirmed later

    /// <summary>At the server's start: every account's active missions are checked again, their open harvest objectives against the harvests made since they were activated
    /// (those of the latest hunt for a single-hunt mission), in order. An objective a harvest meets under the rules of this version is finished now, with its rewards,
    /// as if the harvest had finished it: a title the server did not understand when the harvest was made is met once a version understands it. Returns how many
    /// objectives were finished.</summary>
    public int ConfirmMissions()
    {
        if (missions.Count == 0)
            return 0;
        List<long> accounts;
        using (var all = Store.Open())
            accounts = all.Rows("SELECT id FROM accounts").Select(r => (long)r[0]).ToList();
        var confirmed = 0;
        foreach (var account in accounts)
        {
            using var con = Con(account);
            con.Begin();
            confirmed += ConfirmAccount(con);
            con.Commit();
        }
        return confirmed;
    }

    // ponytail: every start checks every open harvest objective of every active mission again, whether a harvest met it earlier or not; a per-mission checked mark
    // if the starts get slow
    int ConfirmAccount(Db con)
    {
        if (con.Scalar("SELECT 1 FROM mission_prefs WHERE account_id = $a") is null)
            return 0;
        var confirmed = 0;
        for (var again = true; again;)  // a completed mission may activate another, whose harvests are then checked too
        {
            again = false;
            var (rows, marks, hunt) = (MissionRows(con), Marks(con), LatestHunt(con));
            var work = rows.Where(r => r.Value.State == 2 && missions.TryGetValue(r.Key, out var m)
                                       && m.Objectives.Any(o => o.Rule.Kind == Kind.Harvest && !r.Value.Done.Contains(o.Id))).ToList();
            var harvests = work.Count == 0 ? new List<Harvest>() : HarvestsOf(con, work.Min(r => marks[r.Key].Seen));
            foreach (var (id, (_, done)) in work)
            {
                var (m, mark) = (missions[id], marks[id]);
                var met = Replay(m, StartOf(m, done, mark, confirm: true), harvests.Where(h => h.Id > Math.Max(mark.Seen, m.SingleExpedition ? hunt : 0)))
                    .Where(x => !done.Contains(x.Objective.Id)).ToList();
                if (met.Count == 0)
                    continue;
                (confirmed, again) = (confirmed + met.Count, true);
                var finished = done.Concat(met.Select(x => x.Objective.Id)).ToList();
                void Pending(JsonObject u) => con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(u));
                if (m.Objectives.Any(o => !finished.Contains(o.Id)))
                {
                    con.Exec("UPDATE missions SET objectives = ? WHERE account_id = $a AND mission_id = ?", Py.Dumps(List(finished)), id);
                    foreach (var (o, _) in met)
                        Toast(con, $"Mission \"{m.Title}\", objective \"{o.Title}\" completed.");
                }
                else
                    Complete(con, m, Pending, met[^1].At);
            }
        }
        return confirmed;
    }

    // ---- rolled back

    /// <summary>The operator's rollback of what the lax rules finished: for every account (or `only`), each finished harvest objective of a mission activated here that the
    /// rules of this version fully understand is checked against the harvests made since its mission was activated (since the account's first event when that is not known,
    /// and, for a single-hunt mission, the best single hunt); one that no harvest meets is reopened, and a completed mission that loses one becomes active again. An
    /// objective whose title has words the server does not read cannot be judged: it stays finished (and is counted as kept). The missions
    /// a reopened one had made available are activated, so nobody is locked out. What the account was paid (gm$, em$, items) is not taken back, and the missions a restore
    /// brought are never touched: no event of theirs exists. With `apply` the rows are changed and a record of what was reopened is kept for the account (table
    /// `mission_rollbacks`); without it nothing is written. One line per account with a change, then a total.</summary>
    public IEnumerable<string> RollbackMissions(bool apply, long? only = null)
    {
        if (missions.Count == 0)
        {
            yield return "no missions: no recorded catalogue, or a local server";
            yield break;
        }
        List<long> accounts;
        using (var all = Store.Open())
            accounts = (only is { } one ? all.Rows("SELECT id FROM accounts WHERE id = ?", one) : all.Rows("SELECT id FROM accounts ORDER BY id")).Select(r => (long)r[0]).ToList();
        long objectivesTotal = 0, missionsTotal = 0, notUnderstood = 0;
        foreach (var account in accounts)
        {
            using var con = Con(account);
            con.Begin();
            var (reopened, activated, kept) = RollbackAccount(con, apply);
            notUnderstood += kept;
            if (reopened.Count == 0)
                continue;
            if (apply)
            {
                var record = new JsonObject
                {
                    ["reopened"] = new JsonArray([.. reopened.Select(r => (JsonNode?)new JsonObject { ["mission"] = r.Mission, ["was"] = r.Was == 3 ? "completed" : "active", ["objectives"] = List(r.Objectives) })]),
                    ["activated"] = List(activated),
                };
                con.Exec("INSERT INTO mission_rollbacks (account_id, ts, record) VALUES ($a, ?, ?)", (long)Clock(), Py.Dumps(record));
                con.Commit();
            }
            objectivesTotal += reopened.Sum(r => r.Objectives.Count);
            missionsTotal += reopened.Count;
            yield return $"account {account}: " + string.Join("; ", reopened.Select(r => $"mission {r.Mission} ({(r.Was == 3 ? "completed, active again" : "active")}) objectives {string.Join(", ", r.Objectives)}"))
                + (activated.Count > 0 ? $"; activated to stay unlocked: {string.Join(", ", activated)}" : "");
        }
        yield return $"{(apply ? "reopened" : "would reopen")} {objectivesTotal} objectives in {missionsTotal} missions; kept: not understood {notUnderstood} finished objectives" + (apply ? "" : " (dry run: nothing written, --apply writes)");
    }

    (List<(long Mission, long Was, List<long> Objectives)> Reopened, List<long> Activated, int KeptNotUnderstood) RollbackAccount(Db con, bool apply)
    {
        var reopened = new List<(long Mission, long Was, List<long> Objectives)>();
        if (con.Scalar("SELECT 1 FROM mission_prefs WHERE account_id = $a") is null)
            return (reopened, [], 0);
        var (rows, marks) = (MissionRows(con), Marks(con));
        var before = Available(rows).Select(m => m.Id).ToList();
        var harvests = new Lazy<List<Harvest>>(() => HarvestsOf(con, 0));
        var hunts = new Lazy<List<long>>(() => [.. con.Rows("SELECT COALESCE(first_event, 0) FROM expeditions WHERE account_id = $a ORDER BY id").Select(r => (long)r[0])]);
        var notUnderstood = 0;
        foreach (var (id, (state, done)) in rows)
        {
            if (!missions.TryGetValue(id, out var m) || marks[id].Restored)
                continue;
            var mark = marks[id];
            var held = state == 3 ? m.Objectives.Select(o => o.Id).ToList() : done;
            bool Judged(long d) => m.Objectives.First(o => o.Id == d).Rule is { Kind: Kind.Harvest, Unknown: false };
            var harvestHeld = held.Where(d => IsHarvest(m, d) && !mark.KeptIds.Contains(d)).ToList();
            notUnderstood += harvestHeld.Count(d => !Judged(d));
            var checkable = harvestHeld.Where(Judged).ToList();
            if (checkable.Count == 0)
                continue;
            var mine = harvests.Value.Where(h => h.Id > (mark.Since ?? 0)).ToList();
            // a single-hunt mission was finished in one hunt: the best of its hunts counts
            var runs = m.SingleExpedition ? mine.GroupBy(h => hunts.Value.Count(f => f < h.Id)).Select(g => g.AsEnumerable()).ToList() : [mine];
            var start = StartOf(m, held, mark, confirm: false).Concat(harvestHeld.Where(d => !Judged(d))).ToArray();  // what cannot be judged counts as done: it neither blocks nor takes a harvest
            var met = runs.Select(r => Replay(m, start, r)).OrderByDescending(r => r.Count).FirstOrDefault() ?? [];
            var drop = checkable.Where(d => !met.Any(x => x.Objective.Id == d)).ToList();
            if (drop.Count == 0)
                continue;
            reopened.Add((id, state, drop));
            if (apply)  // a row of an earlier schema: what its replay finishes with a harvest up to `seen` is earlier (a later harvest is not given to it again), what is reopened is not
                con.Exec("UPDATE missions SET state = 2, objectives = ?, earlier = ? WHERE account_id = $a AND mission_id = ?", Py.Dumps(List(held.Where(d => !drop.Contains(d)))),
                         Py.Dumps(List(mark.Since is null ? met.Where(x => x.At <= mark.Seen && held.Contains(x.Objective.Id)).Select(x => x.Objective.Id) : mark.EarlierIds.Except(drop))), id);
        }
        var lost = reopened.Where(r => r.Was == 3).Select(r => r.Mission).ToHashSet();
        var kept = before.Where(b => missions[b].Dependencies.Any(lost.Contains)).ToList();
        if (apply)
            foreach (var b in kept)
                Activate(con, b);
        return (reopened, kept, notUnderstood);
    }
}
