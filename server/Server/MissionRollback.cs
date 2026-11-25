// Missions checked again against the harvests and clues already stored: at the server's start (<see cref="ConfirmMissions"/>: what a version that understands more titles
// finishes late), by the operator (<see cref="RollbackMissions"/>: what the lax rules of an earlier version finished wrongly) and by the daily audit (<see cref="AuditMissions"/>:
// the recent finishes that look suspicious) and by the operator's stuck report (<see cref="StuckMissions"/>: the objectives players attempt and nobody completes). docs/server.md, "Missions".
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>A stored harvest (event 3) or clue (event 12) with its kill, its animal's species and gender, and when the server received it (null before schema 14), ready for the rules; or, for a
    /// confirmation, a place entered (event 101: `Arrival`, no kill).</summary>
    sealed record Recorded(long Id, Kind Kind, Kill Kill, long? Species, long? Gender, double? Ts, Arrival? Arrival = null)
    {
        public bool Meets(Rule r, long after) => Arrival is { } a ? ArrivesAt(r, a) : (r.Species is null || r.Species == Species) && (r.Gender is null || r.Gender == Gender) && State.Meets(r, Kill, after);
    }

    /// <summary>The animal of an event: the latest spawn (event 7) of its individual before it in its hunt (individual ids are reused by the next hunt); {} when there is none.</summary>
    static JsonObject SpawnBefore(Db con, JsonNode? individual, long eventId) =>
        con.Rows("SELECT body FROM events WHERE account_id = $a AND event = 7 AND individual = ?1 AND id < ?2 AND id > COALESCE("
            + "(SELECT first_event FROM expeditions WHERE account_id = $a AND COALESCE(first_event, 0) < ?2 ORDER BY id DESC LIMIT 1), 0) ORDER BY id DESC LIMIT 1", individual, eventId)
            .Select(r => Parse(r[0])).FirstOrDefault() ?? new JsonObject();

    /// <summary>The harvests and the clues stored after event `after`, in order; with `spots`, the spotted animals (event 11) too, which only the stuck report reads; with `arrivals`, the places
    /// entered (event 101), which only the confirmation at the start reads.</summary>
    List<Recorded> RecordedOf(Db con, long after, bool spots = false, bool arrivals = false) =>
        [.. con.Rows("SELECT id, event, body, ts FROM events WHERE account_id = $a AND event IN (3, 12" + (spots ? ", 11" : "") + (arrivals ? ", 101" : "") + ") AND id > ? ORDER BY id", after).Select(r =>
        {
            var (id, e, ts) = ((long)r[0], Parse(r[2]), r[3] as double?);
            if ((long)r[1] == 101)
                return ArrivalOf(con, id, e) is { } arrival ? new Recorded(id, Kind.Arrive, null!, null, null, ts, arrival) : null;  // an arrival has no kill
            if ((long)r[1] is 11 or 12)
            {
                var e7 = SpawnBefore(con, e["individual"], id);
                var (clueSpecies, clueGender) = AnimalOf(e7, e7["species"]);
                return (long)r[1] == 11 ? new Recorded(id, Kind.Spot, SpotOf(con, e, id, e7), clueSpecies, clueGender, ts) : new Recorded(id, Kind.Track, ClueOf(con, id, e, e7), clueSpecies, clueGender, ts);
            }
            var kill = KillOf(con, e, id);
            var (species, gender) = AnimalOf(kill.E7, e["extended_species"]);
            return new Recorded(id, Kind.Harvest, kill, species, gender, ts);
        }).OfType<Recorded>()];

    /// <summary>Is the objective one a stored event can have finished: a harvest or a clue? A spot, a place and a start leave no stored event the rules can be checked on.</summary>
    static bool IsReplayed(MissionDef m, long objective) => m.Objectives.FirstOrDefault(o => o.Id == objective)?.Rule.Kind is Kind.Harvest or Kind.Track;

    /// <summary>The harvest and clue objectives of `m` that `events` meet, in order, each with the event that met it. `start` are the objectives counted as done before the first
    /// (those of other kinds, and the ones a restore brought); `after` is the last event the progress is not owed from.</summary>
    static List<(Objective Objective, long At)> Replay(MissionDef m, IEnumerable<long> start, IEnumerable<Recorded> events, long after)
    {
        var (done, met) = (start.Distinct().ToList(), new List<(Objective, long)>());
        foreach (var h in events)
            if (Meet(m, done, h.Kind, (r, last) => h.Meets(r, last), after) is { } o)
                met.Add((o, h.Id));
        return met;
    }

    /// <summary>The objectives counted as done before the first event of a replay: those of other kinds and the ones a restore brought; for a confirmation also the ones finished
    /// before the events it checks (an event after them did not finish those, and must not be given to them again).</summary>
    static long[] StartOf(MissionDef m, IEnumerable<long> done, Mark mark, bool confirm) =>
        [.. done.Where(d => !IsReplayed(m, d)).Concat(mark.KeptIds).Concat(confirm ? mark.EarlierIds : []).Distinct()];

    /// <summary>The first event of the account's latest hunt (0 before any).</summary>
    static long LatestHunt(Db con) => con.Scalar("SELECT COALESCE(first_event, 0) FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1") as long? ?? 0;

    // ---- confirmed later

    /// <summary>At the server's start: every account's active missions are checked again, their open harvest, clue and arrive objectives against the harvests, the clues and the places entered
    /// since they were activated (those of the latest hunt for a single-hunt mission), in order. An objective an event meets under the rules of this version is finished now, with its rewards,
    /// as if the event had finished it: a title the server did not understand when the harvest or the clue was made, or a place it did not match by name or by coordinates when it was
    /// entered, is met once a version understands it. Returns how many objectives were finished.</summary>
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

    // ponytail: every start checks every open harvest, clue and arrive objective of every active mission again, whether an event met it earlier or not; a per-mission checked mark
    // if the starts get slow; an ordered arrive objective also counts a place entered before the objective before it was finished (the row keeps no event for it)
    int ConfirmAccount(Db con)
    {
        if (con.Scalar("SELECT 1 FROM mission_prefs WHERE account_id = $a") is null)
            return 0;
        var confirmed = 0;
        for (var again = true; again;)  // a completed mission may activate another, whose events are then checked too
        {
            again = false;
            var (rows, marks, hunt) = (MissionRows(con), Marks(con), LatestHunt(con));
            var work = rows.Where(r => r.Value.State == 2 && missions.TryGetValue(r.Key, out var m)
                                       && m.Objectives.Any(o => o.Rule.Kind is Kind.Harvest or Kind.Track or Kind.Arrive && !r.Value.Done.Contains(o.Id))).ToList();
            var events = work.Count == 0 ? new List<Recorded>() : RecordedOf(con, work.Min(r => marks[r.Key].Seen), arrivals: true);
            foreach (var (id, (_, done)) in work)
            {
                var (m, mark) = (missions[id], marks[id]);
                var after = Math.Max(mark.Seen, m.SingleExpedition ? hunt : 0);
                var met = Replay(m, StartOf(m, done, mark, confirm: true), events.Where(h => h.Id > after), after)
                    .Where(x => !done.Contains(x.Objective.Id)).ToList();
                if (met.Count == 0)
                    continue;
                (confirmed, again) = (confirmed + met.Count, true);
                var finished = done.Concat(met.Select(x => x.Objective.Id)).ToList();
                Stamp(con, m, met.Select(x => x.Objective));
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

    /// <summary>The operator's rollback of what the lax rules finished: for every account (or `only`), each finished harvest and clue objective of a mission activated here that the
    /// rules of this version fully understand is checked against the harvests and clues made since its mission was activated (since the account's first event when that is not known,
    /// and, for a single-hunt mission, the single hunt that confirms most of the objectives it holds); one that no event meets is reopened, and a completed mission that loses one becomes active again. An
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

    /// <summary>The rollback of one account. With `moving` (the id of a harvest event that leaves the account, <see cref="MoveMpHarvests"/>; `gone`: the ones that left before it, which a dry run still sees) only the objectives
    /// the stored events meet now and meet no more without that event are reopened: what that harvest finished, not whatever today's rules do not meet.</summary>
    (List<(long Mission, long Was, List<long> Objectives)> Reopened, List<long> Activated, int KeptNotUnderstood) RollbackAccount(Db con, bool apply, long? moving = null, ISet<long>? gone = null)
    {
        var reopened = new List<(long Mission, long Was, List<long> Objectives)>();
        if (con.Scalar("SELECT 1 FROM mission_prefs WHERE account_id = $a") is null)
            return (reopened, [], 0);
        var (rows, marks) = (MissionRows(con), Marks(con));
        var before = Available(rows).Select(m => m.Id).ToList();
        var events = new Lazy<List<Recorded>>(() => [.. RecordedOf(con, 0).Where(h => gone is null || !gone.Contains(h.Id))]);
        var without = new Lazy<List<Recorded>>(() => [.. events.Value.Where(h => h.Id != moving)]);
        var hunts = new Lazy<List<long>>(() => [.. con.Rows("SELECT COALESCE(first_event, 0) FROM expeditions WHERE account_id = $a ORDER BY id").Select(r => (long)r[0])]);
        var notUnderstood = 0;
        foreach (var (id, (state, done)) in rows)
        {
            if (!missions.TryGetValue(id, out var m) || marks[id].Restored)
                continue;
            var mark = marks[id];
            var (held, unreadable, checkable, met) = JudgeMission(m, state, done, mark, events, hunts);
            notUnderstood += unreadable;
            if (checkable.Count == 0)
                continue;
            var drop = checkable.Where(d => !met.Any(x => x.Objective.Id == d)).ToList();
            if (moving is not null)  // what the leaving harvest finished: met with it, not met without it
            {
                var after = JudgeMission(m, state, done, mark, without, hunts).Met;
                drop = checkable.Where(d => met.Any(x => x.Objective.Id == d) && !after.Any(x => x.Objective.Id == d)).ToList();
                met = after;
            }
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

    /// <summary>The objectives a mission row holds as finished: all of a completed mission's, else the ones it lists.</summary>
    static List<long> HeldOf(MissionDef m, long state, List<long> done) => state == 3 ? m.Objectives.Select(o => o.Id).ToList() : done;

    /// <summary>The one judging of a held mission, for the rollback and the audit: the objectives it holds (all of a completed mission), how many of its harvest and clue ones have words the server
    /// does not read, the ones that can be judged, and the objectives the stored events meet under today's rules, each with the event that met it. `met` is empty when nothing can be judged.</summary>
    (List<long> Held, int NotUnderstood, List<long> Checkable, List<(Objective Objective, long At)> Met) JudgeMission(MissionDef m, long state, List<long> done, Mark mark, Lazy<List<Recorded>> events, Lazy<List<long>> hunts)
    {
        var held = HeldOf(m, state, done);
        bool Judged(long d) => !m.Objectives.First(o => o.Id == d).Rule.Unknown;
        var replayedHeld = held.Where(d => IsReplayed(m, d) && !mark.KeptIds.Contains(d)).ToList();
        var notUnderstood = replayedHeld.Count(d => !Judged(d));
        var checkable = replayedHeld.Where(Judged).ToList();
        if (checkable.Count == 0)
            return (held, notUnderstood, checkable, []);
        var mine = events.Value.Where(h => h.Id > (mark.Since ?? 0)).ToList();
        // a single-hunt mission was finished in one hunt: the hunt that confirms most of what the account holds counts (not the one that meets most objectives: an active
        // mission holds what its latest hunt finished, and an earlier hunt that met others would reopen those)
        var runs = m.SingleExpedition ? mine.GroupBy(h => hunts.Value.Count(f => f < h.Id)).Select(g => g.AsEnumerable()).ToList() : [mine];
        var start = StartOf(m, held, mark, confirm: false).Concat(replayedHeld.Where(d => !Judged(d))).ToArray();  // what cannot be judged counts as done: it neither blocks nor takes an event
        var met = runs.Select(r => Replay(m, start, r, mark.Since ?? 0)).OrderByDescending(r => r.Count(x => checkable.Contains(x.Objective.Id))).FirstOrDefault() ?? [];
        return (held, notUnderstood, checkable, met);
    }

    // ---- audited

    static readonly string[] AuditFlags = ["borderline", "unmet", "rule changed", "fast", "shared", "spike"];

    /// <summary>The operator's daily audit, read only: the harvest and clue objectives that every account (or `only`) holds and that the stored events meet under today's rules
    /// (<see cref="JudgeMission"/>, as the rollback judges them) were completed when the server received the event that meets them (`events.ts`); those of the last `hours` that
    /// look suspicious are listed, one line per objective, grouped by flag: `borderline` (the kill's harvest value, distance, weight, score or typical points is within 5% of a
    /// bound of the title; 100% harvest value has nothing above it and is never borderline), `unmet` (no stored event meets it, and its rule is the one it was finished under: a bug; not dated,
    /// so listed when a harvest or clue of the account since the mission's activation is in the window, the only events that finish one), `rule changed` (the same, but the rule it was finished under
    /// is not today's, or was not kept: finished before schema 35; <see cref="Fingerprint"/>), `fast` (less than 2 minutes after the mission's activation, which
    /// is not stored: the first event the server received after it), `shared` (the event met objectives of 3 or more missions), `spike` (one line per mission completed in the
    /// window at least 5 times and more than 3 times its average over the 14 days before; a mission is dated by its latest objective, one with no harvest or clue objective is not).
    /// Then the counts. `discord`: the same as Markdown, cut into messages of at most 2000 characters separated by a line `---8<---`.</summary>
    public IEnumerable<string> AuditMissions(double hours, long? only = null, bool discord = false)
    {
        if (missions.Count == 0)
        {
            yield return "no missions: no recorded catalogue, or a local server";
            yield break;
        }
        var from = Clock() - hours * 3600;
        var before = from - 14 * 86400;
        List<long> accounts;
        using (var all = Store.Open())
            accounts = (only is { } one ? all.Rows("SELECT id FROM accounts WHERE id = ?", one) : all.Rows("SELECT id FROM accounts ORDER BY id")).Select(r => (long)r[0]).ToList();
        var flagged = new List<(string Flag, string Line)>();
        var completions = new List<(MissionDef Mission, long Account, double At)>();
        var (finished, finishers) = (0, 0);
        foreach (var account in accounts)
        {
            using var con = Con(account);  // no transaction: it would hold the live server's write lock
            if (con.Scalar("SELECT ts FROM events WHERE account_id = $a ORDER BY id DESC LIMIT 1") is not double last || last < before
                || con.Scalar("SELECT 1 FROM mission_prefs WHERE account_id = $a") is null)
                continue;
            var handle = Store.HandleOf(account) ?? account.ToString();
            string Line(MissionDef m, Objective o, string what) => $"mission {m.Id} {m.Title} | {o.Title} | {handle} | {what}";
            static string What(Recorded e) => $"{(e.Kind == Kind.Track ? "clue" : "harvest")} event {e.Id}";
            var (rows, marks) = (MissionRows(con), Marks(con));
            var stamped = con.Rows("SELECT mission_id, rules FROM missions WHERE account_id = $a").ToDictionary(r => (long)r[0], r => Parse(r[1]));
            var events = new Lazy<List<Recorded>>(() => RecordedOf(con, 0));
            var byId = new Lazy<Dictionary<long, Recorded>>(() => events.Value.ToDictionary(e => e.Id));
            var hunts = new Lazy<List<long>>(() => [.. con.Rows("SELECT COALESCE(first_event, 0) FROM expeditions WHERE account_id = $a ORDER BY id").Select(r => (long)r[0])]);
            var met = new List<(MissionDef M, Objective O, Recorded E)>();
            foreach (var (id, (state, done)) in rows)
            {
                if (!missions.TryGetValue(id, out var m) || marks[id].Restored)
                    continue;
                var mark = marks[id];
                var (_, _, checkable, judged) = JudgeMission(m, state, done, mark, events, hunts);
                var mine = judged.Where(x => checkable.Contains(x.Objective.Id)).Select(x => (M: m, O: x.Objective, E: byId.Value[x.At])).ToList();
                met.AddRange(mine);
                if (state == 3 && mine.Count > 0 && mine.Count == checkable.Count)
                    completions.Add((m, account, mine.Max(x => x.E.Ts ?? 0)));
                if (mine.Count < checkable.Count && events.Value.Any(e => e.Id > (mark.Since ?? 0) && e.Ts >= from))
                    foreach (var o in m.Objectives.Where(o => checkable.Contains(o.Id) && !mine.Any(x => x.O == o)))
                        flagged.Add(((string?)stamped[id][o.Id.ToString()] == Fingerprint(m, o) ? "unmet" : "rule changed", Line(m, o, "no harvest or clue since its activation meets it")));
                if (mark.Since is { } since && con.Scalar("SELECT ts FROM events WHERE account_id = $a AND id > ? ORDER BY id LIMIT 1", since) is double activated)
                    foreach (var (_, o, e) in mine.Where(x => x.E.Ts >= from && x.E.Ts - activated < 120))
                        flagged.Add(("fast", Line(m, o, $"{What(e)}, {e.Ts - activated:0} s after the activation")));
            }
            var missionsOf = met.GroupBy(x => x.E.Id).ToDictionary(g => g.Key, g => g.Select(x => x.M.Id).Distinct().Count());
            var recent = met.Where(x => x.E.Ts >= from).ToList();
            (finished, finishers) = (finished + recent.Count, finishers + (recent.Count > 0 ? 1 : 0));
            foreach (var (m, o, e) in recent)
            {
                var near = (o.Rule.Tests ?? []).SelectMany(t => Measured(t, e.Kill) is { } v
                    ? new[] { (t.Min, "min"), (t.Max, "max") }.Where(b => double.IsFinite(b.Item1) && Math.Abs(v - b.Item1) <= 0.05 * Math.Abs(b.Item1) && !(t.Check == Check.Value && b.Item1 >= 100000))
                        .Select(b => $"{t.Check.ToString().ToLowerInvariant()} {v:0.##} vs {b.Item2} {b.Item1:0.##}")
                    : []).ToList();
                if (near.Count > 0)
                    flagged.Add(("borderline", Line(m, o, $"{What(e)}: {string.Join("; ", near)}")));
                if (missionsOf[e.Id] >= 3)
                    flagged.Add(("shared", Line(m, o, $"{What(e)} met objectives of {missionsOf[e.Id]} missions")));
            }
        }
        foreach (var g in completions.GroupBy(c => c.Mission))
        {
            var (now, earlier) = (g.Count(c => c.At >= from), g.Count(c => c.At >= before && c.At < from));
            if (now >= 5 && now > 3 * earlier / 14.0 * hours / 24)
                flagged.Add(("spike", $"mission {g.Key.Id} {g.Key.Title} | every objective | {g.Where(c => c.At >= from).Select(c => c.Account).Distinct().Count()} accounts"
                    + $" | {now} completions in {hours:0.##} h vs {earlier / 14.0:0.##} a day over the 14 days before"));
        }
        var header = $"missions audit, the last {hours:0.##} h: {finished} objectives completed by {finishers} accounts";
        // the objectives of earlier rules are many: with --discord, one line with their count
        IEnumerable<string> Lines(string f)
        {
            var of = flagged.Where(x => x.Flag == f).ToList();
            if (discord && f == "rule changed")
                return of.Count > 0 ? new[] { $"{f} | {of.Count} objectives finished under another rule" } : [];
            return of.Select(x => $"{f} | {x.Line}");
        }
        var lines = AuditFlags.SelectMany(Lines);
        var counts = string.Join(", ", AuditFlags.Select(f => $"{f} {flagged.Count(x => x.Flag == f)}"));
        foreach (var line in discord ? DiscordChunks([$"**{Escaped(header)}**", .. lines.Select(l => "- " + Escaped(l)), $"**{Escaped(counts)}**"]) : [header, .. lines, counts])
            yield return line;
    }

    // ---- stuck

    /// <summary>What the stuck report knows of one open objective: the accounts that hold it, the candidates (events that have its kind, species and gender), how many each condition
    /// refused, and the values the refused candidates had.</summary>
    sealed class Stuck(MissionDef mission, Objective objective)
    {
        public readonly MissionDef Mission = mission;
        public readonly Objective Objective = objective;
        public readonly List<string> Holders = [];
        public int Candidates;
        public readonly Dictionary<Check, int> Refused = [];
        public readonly Dictionary<Check, Dictionary<string, int>> Values = [];
    }

    /// <summary>Two rules are the same objective when their words are, whatever the order of their parts (`;`-separated) and the case: an operator's entry may write them in another order.</summary>
    static bool SameRule(Rule a, Rule b) => Key(a).SequenceEqual(Key(b));

    static IEnumerable<string> Key(Rule r) => r.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToLowerInvariant()).Order(StringComparer.Ordinal);

    /// <summary>For the ordered objective `i` of an open mission: the last event that finished one of the objectives before it (they are all held), or null when one cannot be told. A harvest or a clue
    /// is the event today's replay gives it (`judged`); a spot, a place entered or a hunt started is estimated as the first such event that meets it, searched after the one before it when that
    /// is ordered and from the mission's activation otherwise (the row keeps no event for them); a hunt's event is the one stored before its first.
    /// ponytail: the first matching event, which is not the one that finished it when an earlier objective of the mission took that event; the candidates then begin a little early.</summary>
    long? FinishedBefore(Db con, MissionDef m, int i, long since, List<Recorded> events, List<(Objective Objective, long At)> judged)
    {
        var floor = since;
        foreach (var p in m.Objectives[..i])
        {
            var from = p.Rule.Ordered ? floor : since;  // one that is not ordered may have been finished before an earlier one
            long? at = p.Rule.Kind switch
            {
                Kind.Harvest or Kind.Track => judged.Where(x => x.Objective.Id == p.Id).Select(x => (long?)x.At).FirstOrDefault(),
                Kind.Spot => events.FirstOrDefault(e => e.Kind == Kind.Spot && e.Id > from && e.Meets(p.Rule, since))?.Id,
                Kind.Arrive => con.Rows("SELECT id, body FROM events WHERE account_id = $a AND event = 101 AND id > ? ORDER BY id", from).Select(r => (Id: (long)r[0], Arrival: ArrivalOf(con, (long)r[0], Parse(r[1]))))
                    .Where(e => e.Arrival is { } a && ArrivesAt(p.Rule, a)).Select(e => (long?)e.Id).FirstOrDefault(),
                Kind.Start => con.Rows("SELECT first_event, location_id FROM expeditions WHERE account_id = $a AND COALESCE(first_event, 0) >= ? ORDER BY id", from)
                    .Select(r => (At: r[0] as long? ?? 0, Lodge: r[1] as long?))
                    .Where(x => x.Lodge is { } l && lodgeNames.TryGetValue(l, out var names) && names.Any(n => PlaceIn(p.Rule.Text, n))).Select(x => (long?)x.At).FirstOrDefault(),
                _ => null,
            };
            if (at is not { } last)
                return null;
            floor = Math.Max(floor, last);
        }
        return floor;
    }

    /// <summary>What a kill had where a condition looked, for the stuck report: the weapon, the ammunition, a species or a reserve by name, the other ids and numbers as they are.</summary>
    string Seen(Test t, Kill k, long after, Rule rule)
    {
        var (hits, e12) = (k.Hits.Value, k.E12);
        var (first, last) = (hits.FirstOrDefault(), hits.LastOrDefault());
        static string Id(double? v) => v is { } x ? ((long)x).ToString(CultureInfo.InvariantCulture) : "none";
        string Named(Dictionary<long, JsonObject> from, long? id) => id is { } i ? from.TryGetValue(i, out var o) && Py.IsStr(o["name"]) ? (string)o["name"]! : $"#{i}" : "none";
        return t.Check switch
        {
            Check.Weapon => Named(items, (long?)NumOf(last, "weapon_id")),
            Check.Ammo or Check.NotAmmo => Named(items, (long?)NumOf(last, "ammunition_id")),
            Check.State or Check.NotState => Id(NumOf(last, "animal_state")),
            Check.FirstState => Id(NumOf(first, "animal_state")),
            Check.Pose => Id(NumOf(last, "pose") % 16),
            Check.Stand => Id(NumOf(last, "location")),
            Check.Scope or Check.NoScope => Id(NumOf(last, "scope_id")),
            Check.Reserve => k.Reserve is { } r ? reserves.GetValueOrDefault(r) ?? $"#{r}" : "none",
            Check.Place => "elsewhere",
            Check.Coat or Check.NotCoat => Id(NumOf(k.E7, "texture")),
            Check.Clue => t.Min > 1 && ClueIs(t, e12, k.E7) ? $"{k.Clues.Value.Count(c => c.Id > after && ClueIs(t, c.Body, k.E7))} of {t.Min} clues" : ClueKindOf(e12, AnimalOf(k.E7, k.E7["species"]).Species)?.ToString() ?? "unknown",
            Check.Within or Check.Each or Check.SameShot or Check.Different => $"{Sequenced(t, k, after, rule)} of {t.Min}",
            Check.Named => t.Ids is not null ? Id(NumOf(k.E7, "population")) : double.IsInfinity(t.Min) ? Id(NumOf(k.E7, "special")) : "away from the place",
            Check.Permitted => "not permitted",
            Check.OneShot => $"{hits.Count} shots",
            Check.Organ or Check.OrganOnly or Check.OrganAvoid => Id(NumOf(last, "organ2")),
            _ => Measured(t, k) is { } v ? v.ToString("0", CultureInfo.InvariantCulture) : "none",
        };
    }

    /// <summary>The operator's report of objectives players attempt and nobody completes, read only (no transaction, no lock: it runs in the live container). For every harvest, spot or
    /// clue objective the server understands that is open (and, when ordered, next in line) on an active mission of an account (or `only`) that played in the last `days` days, the
    /// candidates are the events of those days that have the objective's kind, species and gender and that could have finished it: after the mission was activated (a single-hunt mission
    /// forgets its progress when a hunt starts: an event of an earlier hunt counts only when a condition refused it), for an ordered one after the event that finished the one before it, and not one that today's rules give to another objective (<see cref="JudgeMission"/>'s
    /// replay: an event finishes one). Each candidate is judged by the objective's own conditions (<see cref="Meets(Test, Kill, long, Rule)"/>, the test the replay is made of), which counts how
    /// many each refused. An objective is listed with at least
    /// `minCandidates` candidates and no completion (finished by any account here, a restore not counted), or when one condition refused at least `minShare` percent of at least `minSample`
    /// candidates; most candidates first, each with its open accounts, its completions, the refused conditions and the values the most refusing one saw most often.
    /// `discord`: Markdown messages of at most 2000 characters separated by a line `---8<---`.</summary>
    public IEnumerable<string> StuckMissions(double days, long? only = null, bool discord = false, int minCandidates = 5, int minSample = 10, double minShare = 90)
    {
        if (missions.Count == 0)
        {
            yield return "no missions: no recorded catalogue, or a local server";
            yield break;
        }
        var from = Clock() - days * 86400;
        List<long> accounts;
        using (var all = Store.Open())
            accounts = all.Rows("SELECT id FROM accounts ORDER BY id").Select(r => (long)r[0]).ToList();
        var completed = new Dictionary<(long Mission, long Objective), int>();
        var open = new Dictionary<(long Mission, long Objective), Stuck>();
        var holders = new HashSet<long>();
        foreach (var account in accounts)
        {
            using var con = Con(account);  // no transaction: it would hold the live server's write lock
            if (con.Scalar("SELECT 1 FROM mission_prefs WHERE account_id = $a") is null)
                continue;
            var (rows, marks, latestHunt) = (MissionRows(con), Marks(con), LatestHunt(con));
            var played = (only is null || only == account) && con.Scalar("SELECT 1 FROM events WHERE account_id = $a AND event IN (3, 11, 12) AND ts >= ?", from) is not null;
            var handle = new Lazy<string>(() => Store.HandleOf(account) ?? account.ToString());
            var events = new Lazy<List<Recorded>>(() => RecordedOf(con, 0, spots: true));
            var hunts = new Lazy<List<long>>(() => [.. con.Rows("SELECT COALESCE(first_event, 0) FROM expeditions WHERE account_id = $a ORDER BY id").Select(r => (long)r[0])]);
            foreach (var (id, (state, done)) in rows)
            {
                if (!missions.TryGetValue(id, out var m) || marks[id].Restored)
                    continue;
                var (mark, held) = (marks[id], HeldOf(m, state, done));
                var kept = mark.KeptIds;
                foreach (var o in m.Objectives.Where(o => held.Contains(o.Id) && !kept.Contains(o.Id)))
                    completed[(id, o.Id)] = completed.GetValueOrDefault((id, o.Id)) + 1;
                if (state != 2 || !played)
                    continue;
                // what today's rules make of the events the mission holds: the event that finished each objective it holds, and what an open one would finish now
                var judged = new Lazy<List<(Objective Objective, long At)>>(() => JudgeMission(m, state, done, mark, events, hunts).Met);
                for (var i = 0; i < m.Objectives.Length; i++)
                {
                    var (o, rule) = (m.Objectives[i], m.Objectives[i].Rule);
                    if (held.Contains(o.Id) || rule.Unknown || rule.Kind is not (Kind.Harvest or Kind.Spot or Kind.Track) || (rule.Ordered && m.Objectives[..i].Any(p => !held.Contains(p.Id)))
                        || m.Objectives[..i].Any(p => SameRule(p.Rule, rule) && !held.Contains(p.Id)))  // an equal one before it that is open takes the event first
                        continue;
                    var since = mark.Since ?? mark.Seen;  // a row of before schema 30 has no activation event: the last one its progress is not owed from
                    var floor = since;
                    if (rule.Ordered)
                    {
                        if (FinishedBefore(con, m, i, since, events.Value, judged.Value) is not { } last)
                            continue;  // an event that finished one before it is not told: the candidates would begin too early
                        floor = last;
                    }
                    if (!open.TryGetValue((id, o.Id), out var s))
                        open[(id, o.Id)] = s = new(m, o);
                    s.Holders.Add(handle.Value);
                    holders.Add(account);
                    // the events that could have finished it: after the mission's activation (a single-hunt mission: an earlier hunt's event only when a condition refused it), when ordered after the one before it finished, and none that
                    // another objective took (an event finishes one objective); the one an open objective is met by now stays: that is the bug the report shows as "no condition refused"
                    var taken = judged.Value.Where(x => x.Objective.Id != o.Id).Select(x => x.At).ToHashSet();
                    var tests = rule.Tests ?? [];
                    foreach (var e in events.Value.Where(e => e.Id > floor && !taken.Contains(e.Id) && e.Ts >= from && e.Kind == rule.Kind && (rule.Species is null || rule.Species == e.Species) && (rule.Gender is null || rule.Gender == e.Gender)
                                                              && tests.Where(t => t.Check == Check.Species).All(t => Meets(t, e.Kill, since))))  // a title naming several species: the species is part of the kind
                    {
                        var refused = tests.Where(t => t.Check != Check.Species && !Meets(t, e.Kill, since, rule)).DistinctBy(t => t.Check).ToList();
                        if (refused.Count == 0 && m.SingleExpedition && e.Id <= latestHunt)
                            continue;  // a single-hunt mission forgets its progress when a hunt starts: what met everything in an earlier hunt was progress, not a refused attempt
                        s.Candidates++;
                        foreach (var t in refused)
                        {
                            s.Refused[t.Check] = s.Refused.GetValueOrDefault(t.Check) + 1;
                            var values = s.Values.TryGetValue(t.Check, out var v) ? v : s.Values[t.Check] = [];
                            var seen = Seen(t, e.Kill, since, rule);
                            values[seen] = values.GetValueOrDefault(seen) + 1;
                        }
                    }
                }
            }
        }
        var listed = open.Values.Where(s => s.Candidates > 0).Select(s => (S: s, Done: completed.GetValueOrDefault((s.Mission.Id, s.Objective.Id)), Top: s.Refused.OrderByDescending(r => r.Value).FirstOrDefault()))
            .Where(x => x.S.Candidates >= minCandidates && x.Done == 0 || x.S.Candidates >= minSample && x.Top.Value * 100.0 >= minShare * x.S.Candidates)
            .OrderByDescending(x => x.S.Candidates).ThenBy(x => x.S.Mission.Id).ThenBy(x => x.S.Objective.Id).ToList();
        var header = $"missions stuck, the last {days:0.##} days: {listed.Count} objectives listed of {open.Values.Count(s => s.Candidates > 0)} with candidates among {open.Count} open on {holders.Count} accounts";
        var lines = listed.Select(x =>
        {
            var (s, done, top) = x;
            var refused = s.Refused.Count == 0 ? "no condition refused" : string.Join(", ", s.Refused.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}/{s.Candidates}"));
            var seen = top.Value == 0 ? "" : $" | {top.Key}: " + string.Join(", ", s.Values[top.Key].OrderByDescending(v => v.Value).Take(3).Select(v => $"{v.Key} ({v.Value})"));
            var who = string.Join(", ", s.Holders.Take(5)) + (s.Holders.Count > 5 ? $" and {s.Holders.Count - 5} more" : "");
            return $"mission {s.Mission.Id} {s.Mission.Title} | {s.Objective.Title} | {s.Candidates} candidates, {done} completed | open for {s.Holders.Count} accounts: {who} | {refused}{seen}";
        });
        foreach (var line in discord ? DiscordChunks([$"**{Escaped(header)}**", .. lines.Select(l => "- " + Escaped(l))]) : [header, .. lines])
            yield return line;
    }

    static readonly Regex Markdown = new(@"[\\*_~`]", RegexOptions.CultureInvariant);

    static string Escaped(string text) => Markdown.Replace(text, @"\$0");

    /// <summary>Whole lines in messages of at most 2000 characters (Discord's limit; a longer line is cut), separated by a line `---8<---`.</summary>
    static IEnumerable<string> DiscordChunks(IEnumerable<string> lines)
    {
        var (chunk, size) = (new List<string>(), -1);
        foreach (var line in lines.Select(l => l.Length > 2000 ? l[..2000] : l))
        {
            if (chunk.Count > 0 && size + 1 + line.Length > 2000)
            {
                yield return string.Join('\n', chunk);
                yield return "---8<---";
                (chunk, size) = ([], -1);
            }
            chunk.Add(line);
            size += 1 + line.Length;
        }
        yield return string.Join('\n', chunk);
    }
}
