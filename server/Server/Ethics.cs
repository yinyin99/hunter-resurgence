// The stored kills judged again by today's ammunition rule (#582, State.AmmoCheck), and the flags of what that leaves outside the rule (`unethical_kills`): `admin ethics` and `admin ethics list` (docs/server.md).
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>One kill <see cref="JudgeKills"/> changes, would change or reports. Change: "disqualify" (unethical with a stored score: it loses the score and its board rows), "unscored" (unethical, nothing stored to
    /// change: a kill with no score whose hits the rule before this change permitted, reported so the list is complete), "requalify" (ethical, no score: scored and paid as <see cref="Rescore"/> does), "flag" (only
    /// flagged), "unflag" (its flag goes: its cause is gone). Flag: "kept" (unethical and taxidermied or mounted in a lodge, which it keeps) or "undecided" (a kill of a hunt the schema-41 step could not decide,
    /// whose verdict under the rule depends on that: it keeps the verdict it was made under, <see cref="KindOf"/>); null for none. Reason, Ammo, Basis: why it is unethical (<see cref="Unethical"/>; for "undecided"
    /// the ammunition of the hit that one of the two kinds refuses). Score: the score it loses (disqualify), gets (requalify) or has. BoardRows, Top10: its rows on the leaderboards now (every board and season) and
    /// how many are in a top 10. Taxidermied, Lodge and Slot (the lodge mount), Photo (a trophy shot): what it keeps. Gms: the gm$ its kill was paid (kept), for "requalify" the gm$ paid now. IfNormal,
    /// IfPossessed: the two verdicts of an undecided kill.</summary>
    public sealed record JudgedKill(long Account, long Kill, long? Species, string Change, string? Flag, string? Reason, long? Ammo, string? Basis, long? Coat, double Score, int BoardRows, int Top10,
                                    bool Taxidermied, long? Lodge, long? Slot, bool Photo, long Gms, string? IfNormal = null, string? IfPossessed = null)
    {
        public bool Board => BoardRows > 0;
    }

    /// <summary>Admin: judge every stored kill (of every account, or of <paramref name="only"/>) by today's rule, in account and kill order, as its kill page judges it (<see cref="KillEntry"/>). An unethical kill
    /// ends as one disqualified at its harvest: its stored score and score type cleared (so it leaves the boards and the personal bests); its gm$, taxidermy, lodge mount and trophy shot stay, and one that
    /// keeps a taxidermy or a mount is flagged "kept". An ethical kill with no score is scored and paid as <see cref="Rescore"/> does. A kill that keeps the verdict it was made under because its hunt is
    /// undecided is flagged "undecided" when the rule could judge it otherwise. A flag whose cause is gone is removed. The judging reads without a lock; with <paramref name="dryRun"/> false each change is
    /// judged again and written in its own short transaction (<paramref name="beforeWrite"/>, for the tests, runs with the kill's id just before it), so live heartbeats wait for one kill at most. A second
    /// run changes nothing (it prints the "unscored" lines again: they write nothing).</summary>
    public List<JudgedKill> JudgeKills(long? only, bool dryRun, Action<long>? beforeWrite = null)
    {
        List<long> ids;
        long ruleStep;
        using (var all = Store.Open())
        {
            ids = only is { } one ? [one] : all.Rows("SELECT id FROM accounts ORDER BY id").Select(r => (long)r[0]).ToList();
            ruleStep = Convert.ToInt64(all.Scalar("SELECT last_event FROM schema_marks WHERE version = 41") ?? 0L);
        }
        var places = BoardPlaces();
        var result = new List<JudgedKill>();
        foreach (var id in ids)
        {
            using var con = Con(id);
            // one read per account for what every kill needs: the gm$ paid per kill and the lodge mounts
            var paid = con.Rows("SELECT summary, SUM(amount) FROM wallet_log WHERE account_id = $a AND summary LIKE 'CSS Reward (Animal Id: %' GROUP BY summary")
                .ToDictionary(r => (string)r[0], r => Convert.ToInt64(r[1]));
            var mounts = new Dictionary<long, (long Lodge, long? Slot)>();
            foreach (var r in con.Rows("SELECT l.n, CAST(json_extract(s.value, '$.trophy_id') AS INTEGER), json_extract(s.value, '$.slot_id') FROM lodges l, json_each(l.node, '$.animal_slots') s"
                                       + " WHERE l.account_id = $a ORDER BY l.n DESC"))
                if (r[1] is long trophy)
                    mounts[trophy] = ((long)r[0], r[2] as long?);
            var cached = new Known(k => mounts.TryGetValue(k, out var m) ? m : null, k => paid.GetValueOrDefault($"CSS Reward (Animal Id: {k})"), k => paid.ContainsKey($"CSS Reward (Animal Id: {k})"));
            foreach (var killId in con.Rows("SELECT kill_id FROM kills WHERE account_id = $a ORDER BY kill_id").Select(r => (long)r[0]).ToList())
            {
                if (JudgeKill(con, id, killId, places, cached, ruleStep, write: false) is not { } judged)
                    continue;
                if (!dryRun)
                {
                    beforeWrite?.Invoke(killId);
                    // judged again inside the write, with the mounts and payments read again: a heartbeat may have changed the kill (a taxidermy, a mount) since the read
                    con.Begin();
                    judged = JudgeKill(con, id, killId, places, Live(con), ruleStep, write: true);
                    con.Commit();
                    if (judged is null)
                        continue;
                }
                result.Add(judged);
            }
        }
        if (!dryRun && result.Any(r => r.Change is "disqualify" or "requalify"))
            DropBoards();
        return result;
    }

    /// <summary>The lodge mount of a kill, the gm$ its kill was paid and whether a `CSS Reward` row of it exists.</summary>
    sealed record Known(Func<long, (long Lodge, long? Slot)?> Mount, Func<long, long> Gms, Func<long, bool> Paid);

    /// <summary><see cref="Known"/> read from the database at once (inside a write).</summary>
    static Known Live(Db con) => new(k => Mount(con, k),
        k => Convert.ToInt64(con.Scalar("SELECT COALESCE(SUM(amount), 0) FROM wallet_log WHERE account_id = $a AND summary = ?", $"CSS Reward (Animal Id: {k})")),
        k => con.Scalar("SELECT 1 FROM wallet_log WHERE account_id = $a AND summary = ?", $"CSS Reward (Animal Id: {k})") is not null);

    /// <summary>One kill of <see cref="JudgeKills"/>: its line (null when nothing changes and nothing is to report), and with <paramref name="write"/> its change written (the caller's transaction).</summary>
    JudgedKill? JudgeKill(Db con, long id, long killId, Dictionary<(long, long), (int Rows, int Top10)> places, Known known, long ruleStep, bool write)
    {
        if (con.Rows("SELECT k.individual, k.harvest, h.body, u.kind, u.taxidermied, u.lodge, u.slot FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                     + " LEFT JOIN unethical_kills u ON u.account_id = $a AND u.kill_id = k.kill_id WHERE k.account_id = $a AND k.kill_id = ?", killId).FirstOrDefault() is not { } k)
            return null;
        var (harvest, page, flag) = ((string)k[1], Parse(k[2]), k[3] as string);
        var (sp, coat) = (page["species"], page["texture_id"]);
        var species = Py.IsNum(sp) ? Py.L(sp) : (long?)null;
        var coatId = Py.IsNum(coat) ? Py.L(coat) : (long?)null;
        var stored = Num(page["score"]);
        var scored = stored != 0 || Py.Truthy(page["score_type"]);
        var (rows, top10) = places.GetValueOrDefault((id, killId));
        var hunt = HarvestEvent(con, harvest);
        if (KindOf(sp, coat, hunt) is ({ } maybe, "undecided"))
        {
            // the kill keeps the verdict it was made under (its species' list and the event's silver); flagged when the rule, judging it as a normal or as a possessed animal, could say otherwise
            var hits = KillHits(con, Py.FromSql(k[0]), harvest);
            Unethical? Why(HuntRule rule) => hits.Select(h => AmmoCheck(h, sp, coat, rule)?.Why).FirstOrDefault(w => w is not null);
            var (kept, asNormal, asPossessed) = (Why(hunt), Why(new(null)), Why(new(maybe)));
            if (kept is null && (asNormal is not null || asPossessed is not null))
            {
                if (flag == "undecided")
                    return null;
                static string Verdict(Unethical? why) => why is null ? "ethical" : "unethical";
                if (write)
                    con.Exec("INSERT OR REPLACE INTO unethical_kills (account_id, kill_id, kind, species, coat, reason, ammo, basis, if_normal, if_possessed, lost_score, ts) VALUES ($a, ?, 'undecided', ?, ?, 'undecided', ?, 'undecided', ?, ?, 0, ?)",
                             killId, species, coatId, (asNormal ?? asPossessed)!.Ammo, Verdict(asNormal), Verdict(asPossessed), (long)Clock());
                return new(id, killId, species, "flag", "undecided", "undecided", (asNormal ?? asPossessed)!.Ammo, "undecided", coatId, stored, rows, top10, false, null, null, false, 0, Verdict(asNormal), Verdict(asPossessed));
            }
        }
        var e3 = Parse(harvest);
        var (_, _, score, type, css, why) = ScoreKill(con, e3, harvest, Trophy, AmmoCheck);
        if (why is not null)
        {
            var (taxidermied, mount) = (Py.Truthy(page["taxidermized"]), known.Mount(killId));
            var keeps = taxidermied || mount is not null;
            var same = flag == "kept" && (k[4] is 1L) == taxidermied && k[5] as long? == mount?.Lodge && k[6] as long? == mount?.Slot;
            var (writeFlag, dropFlag) = (keeps && !same, !keeps && flag is not null);
            string? change = scored ? "disqualify" : Unscored(con, e3, harvest, page, k[0], ruleStep) ? "unscored" : writeFlag ? "flag" : dropFlag ? "unflag" : null;
            if (change is null)
                return null;
            if (write)
            {
                if (scored)
                {
                    page["score"] = 0;
                    page["score_type"] = "";
                    con.Exec("UPDATE harvests SET body = ? WHERE account_id = $a AND kill_id = ?", Py.Dumps(page), killId);
                }
                if (writeFlag)
                    con.Exec("INSERT OR REPLACE INTO unethical_kills (account_id, kill_id, kind, species, coat, reason, ammo, basis, taxidermied, lodge, slot, lost_score, ts) VALUES ($a, ?, 'kept', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                             killId, species, coatId, why.Reason, why.Ammo, why.Basis, taxidermied ? 1 : 0, mount?.Lodge, mount?.Slot, stored, (long)Clock());
                else if (dropFlag)
                    con.Exec("DELETE FROM unethical_kills WHERE account_id = $a AND kill_id = ?", killId);
            }
            return new(id, killId, species, change, keeps ? "kept" : null, why.Reason, why.Ammo, why.Basis, coatId, stored, rows, top10,
                       taxidermied, mount?.Lodge, mount?.Slot, KillPhoto(con, id, k[0], harvest) is not null, known.Gms(killId));
        }
        var requalify = !scored && Py.Truthy(score);
        if (flag is null && !requalify)
            return null;
        var amount = requalify && !known.Paid(killId) ? Math.Max(0, Py.RoundInt(css)) : 0;
        if (write)
        {
            con.Exec("DELETE FROM unethical_kills WHERE account_id = $a AND kill_id = ?", killId);
            if (requalify)
            {
                page["score"] = C(score);
                page["score_type"] = type ?? "";
                con.Exec("UPDATE harvests SET body = ? WHERE account_id = $a AND kill_id = ?", Py.Dumps(page), killId);
                if (amount > 0 && AddCurrency(con, "gm", amount, $"CSS Reward (Animal Id: {killId})") is { } balance)
                    con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = C(balance), ["type"] = "gm", ["update"] = 15 }));
            }
        }
        return new(id, killId, species, requalify ? "requalify" : "unflag", null, null, null, null, coatId, requalify ? Num(score) : stored, rows, top10, false, null, null, false, amount);
    }

    /// <summary>Whether an unethical kill with no stored score is one this change turned unethical: harvested before the schema-41 step (its harvest's event row at most <paramref name="ruleStep"/>, `schema_marks`;
    /// a later kill was judged by this rule when it was made), a kill that scores nothing (its features or family give none), and whose hits the rule it was judged by then all permitted: in a hunt whose
    /// first connect recorded the event (not set by the step, `event_backfilled`), the event's list for a species it names; otherwise its species' list and an event's silver. A kill that would score has
    /// no score because it was disqualified (or refused) at its harvest already.</summary>
    bool Unscored(Db con, JsonObject e3, string harvest, JsonObject page, object? individual, long ruleStep)
    {
        if (con.Scalar("SELECT MAX(id) FROM events WHERE account_id = $a AND event = 3 AND body = ?", harvest) is not long harvested || harvested > ruleStep)
            return false;
        var hits = KillHits(con, Py.FromSql(individual), harvest);
        var (e7, e22) = KillEvents(con, e3["individual"], harvest);
        if (Py.Truthy(Trophy(e3, e7.Count > 0 ? e7 : PageSpawn(page), e22, hits.Count > 0 ? hits[^1] : null).Score))
            return false;
        var recorded = con.Scalar("SELECT CASE WHEN ?1 <= COALESCE(event_until, ?1) AND event_backfilled IS NULL THEN NULLIF(event, '') END FROM expeditions"
                                  + " WHERE account_id = $a AND COALESCE(first_event, 0) < ?1 ORDER BY id DESC LIMIT 1", harvested) as string;
        if (recorded is not null && facts.Possessing(page["species"], recorded) is not null && facts.EventEthical(recorded) is { } then)
            return hits.All(h => Permits(then.Ammo, h["ammunition_id"]));
        return Allowed(page["species"], null) is not { } list || hits.All(h => Permits(list, h["ammunition_id"]) || facts.EventAmmo(h["ammunition_id"]));
    }

    /// <summary>The leaderboard rows each kill holds now ((account, kill) to rows and top-10 places), over every board (score and range) of every season and all time (<see cref="Boards"/>).</summary>
    Dictionary<(long, long), (int Rows, int Top10)> BoardPlaces()
    {
        var places = new Dictionary<(long, long), (int Rows, int Top10)>();
        foreach (var season in (long[])[0, .. Store.Seasons().Select(s => s.No)])
            foreach (var range in (bool[])[false, true])
                foreach (var row in Boards(range, season).Values.SelectMany(r => r).Where(r => r.Kill > 0))
                {
                    var (n, top) = places.GetValueOrDefault((row.Account, row.Kill));
                    places[(row.Account, row.Kill)] = (n + 1, top + (row.Rank <= 10 ? 1 : 0));
                }
        return places;
    }

    /// <summary>The lodge (`lodges.n`) and slot (`slot_id`) of the account that hold the kill <paramref name="killId"/> (a slot's `trophy_id`); null when none does.</summary>
    static (long Lodge, long? Slot)? Mount(Db con, long killId) =>
        con.Rows("SELECT l.n, json_extract(s.value, '$.slot_id') FROM lodges l, json_each(l.node, '$.animal_slots') s WHERE l.account_id = $a AND CAST(json_extract(s.value, '$.trophy_id') AS INTEGER) = ?"
                 + " ORDER BY l.n LIMIT 1", killId).FirstOrDefault() is { } r ? ((long)r[0], r[1] as long?) : null;

    /// <summary>Whether today's rule disqualifies a stored kill (its individual, its harvest's event 3 JSON and its page), judged as its page and <see cref="KillEntry"/> judge it.</summary>
    bool Disqualified(Db con, object? individual, string harvest, JsonObject page) =>
        !Ethical(page["species"], page["texture_id"], KillHits(con, Py.FromSql(individual), harvest), HarvestEvent(con, harvest));

    /// <summary>One row of `unethical_kills` (<see cref="Store"/>).</summary>
    public sealed record FlaggedKill(long Account, long Kill, string Kind, long? Species, long? Coat, string Reason, long? Ammo, string? Basis, string? IfNormal, string? IfPossessed,
                                     bool Taxidermied, long? Lodge, long? Slot, double LostScore, long Ts);

    /// <summary>Admin: the flagged kills (of every account, or of <paramref name="only"/>) in account and kill order, those of a <paramref name="kind"/> ("kept" or "undecided"), a <paramref name="reason"/>,
    /// a <paramref name="species"/> (a non-typical one counts for its typical species) or flagged at or after <paramref name="since"/> (Unix seconds); null filters nothing.</summary>
    public List<FlaggedKill> FlaggedKills(long? only, string? kind, string? reason, long? species, long? since)
    {
        List<long> ids;
        using (var all = Store.Open())
            ids = only is { } one ? [one] : all.Rows("SELECT id FROM accounts ORDER BY id").Select(r => (long)r[0]).ToList();
        var result = new List<FlaggedKill>();
        foreach (var id in ids)
        {
            using var con = Con(id);
            result.AddRange(con.Rows("SELECT kill_id, kind, species, coat, reason, ammo, basis, if_normal, if_possessed, taxidermied, lodge, slot, lost_score, ts FROM unethical_kills WHERE account_id = $a"
                                     + " AND (?1 IS NULL OR kind = ?1) AND (?2 IS NULL OR reason = ?2) AND (?3 IS NULL OR species = ?3 OR species % 100 = ?3) AND (?4 IS NULL OR ts >= ?4) ORDER BY kill_id",
                                     kind, reason, species, since)
                .Select(r => new FlaggedKill(id, (long)r[0], (string)r[1], r[2] as long?, r[3] as long?, (string)r[4], r[5] as long?, r[6] as string, r[7] as string, r[8] as string,
                                             (long)r[9] == 1, r[10] as long?, r[11] as long?, Convert.ToDouble(r[12]), (long)r[13])));
        }
        return result;
    }
}
