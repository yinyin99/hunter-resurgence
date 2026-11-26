// The stored kills judged again by today's ammunition rule (#582, State.AmmoCheck), and the flags of what that leaves outside the rule (`unethical_kills`): `admin ethics` and `admin ethics list` (docs/server.md).
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>One kill <see cref="JudgeKills"/> changes (or would). Change: "disqualify" (unethical, it had a score: it loses the score and its board place), "requalify" (ethical, no score: scored and paid as
    /// <see cref="Rescore"/> does), "flag" (only flagged), "unflag" (its flag goes: its cause is gone). Flag: the flag it gets, "kept" (unethical and taxidermied or mounted in a lodge, which it keeps) or
    /// "undecided" (its verdict depends on a hunt the schema-41 step could not decide: the kill stays as it is); null for none. Reason, Ammo, Basis: why it is unethical (<see cref="Unethical"/>; for
    /// "undecided" the ammunition of the hit that is unethical under one of the two kinds). Score: the score it loses (disqualify) or gets (requalify). Board: it held a place on the boards. Taxidermied,
    /// Lodge and Slot (the lodge mount), Photo (a trophy shot): what it keeps. Gms: the gm$ its kill was paid (kept), for "requalify" the gm$ paid now. IfNormal, IfPossessed: the two verdicts of an undecided kill.</summary>
    public sealed record JudgedKill(long Account, long Kill, long? Species, string Change, string? Flag, string? Reason, long? Ammo, string? Basis, long? Coat, double Score, bool Board,
                                    bool Taxidermied, long? Lodge, long? Slot, bool Photo, long Gms, string? IfNormal = null, string? IfPossessed = null);

    /// <summary>Admin: judge every stored kill (of every account, or of <paramref name="only"/>) by today's rule, in account and kill order, as its page judges it (<see cref="KillEntry"/>). An unethical kill
    /// ends as one disqualified at its harvest: its stored score and score type cleared (so it leaves the boards and the personal bests); its gm$, taxidermy, lodge mount and trophy shot stay, and one that
    /// keeps a taxidermy or a mount is flagged "kept". An ethical kill with no score is scored and paid as <see cref="Rescore"/> does. A kill of a hunt the schema-41 step left undecided
    /// (`expeditions.event_undecided`) whose coat proves nothing and whose verdict differs between a normal and a possessed animal stays as it is and is flagged "undecided". A flag whose cause is gone
    /// is removed. One transaction per account; <paramref name="dryRun"/> writes nothing; a second run finds nothing.</summary>
    public List<JudgedKill> JudgeKills(long? only, bool dryRun)
    {
        List<long> ids;
        using (var all = Store.Open())
            ids = only is { } one ? [one] : all.Rows("SELECT id FROM accounts ORDER BY id").Select(r => (long)r[0]).ToList();
        var result = new List<JudgedKill>();
        foreach (var id in ids)
        {
            using var con = Con(id);
            con.Begin();
            foreach (var k in con.Rows("SELECT k.kill_id, k.individual, k.harvest, h.body, h.hidden, u.kind, e.event_undecided FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                                       + " LEFT JOIN unethical_kills u ON u.account_id = $a AND u.kill_id = k.kill_id LEFT JOIN expeditions e ON e.account_id = $a AND e.id = json_extract(h.body, '$.expedition_id')"
                                       + " WHERE k.account_id = $a ORDER BY k.kill_id"))
            {
                var (killId, harvest, page, flag) = ((long)k[0], (string)k[2], Parse(k[3]), k[5] as string);
                var (sp, coat) = (page["species"], page["texture_id"]);
                var species = Py.IsNum(sp) ? Py.L(sp) : (long?)null;
                var coatId = Py.IsNum(coat) ? Py.L(coat) : (long?)null;
                var stored = Num(page["score"]);
                var scored = stored != 0 || Py.Truthy(page["score_type"]);
                if (k[6] is string maybe && KindOf(sp, coat, null).Basis == "hunt")
                {
                    var hits = KillHits(con, Py.FromSql(k[1]), harvest);
                    var (asNormal, asPossessed) = (hits.Select(h => AmmoCheck(h, sp, coat, null)?.Why).FirstOrDefault(w => w is not null),
                                                   hits.Select(h => AmmoCheck(h, sp, coat, maybe)?.Why).FirstOrDefault(w => w is not null));
                    if ((asNormal is null) != (asPossessed is null))
                    {
                        if (flag == "undecided")
                            continue;
                        static string Verdict(Unethical? why) => why is null ? "ethical" : "unethical";
                        result.Add(new(id, killId, species, "flag", "undecided", "undecided", (asNormal ?? asPossessed)!.Ammo, "hunt", coatId, 0, false, false, null, null, false, 0, Verdict(asNormal), Verdict(asPossessed)));
                        if (!dryRun)
                            con.Exec("INSERT OR REPLACE INTO unethical_kills (account_id, kill_id, kind, species, coat, reason, ammo, basis, if_normal, if_possessed, ts) VALUES ($a, ?, 'undecided', ?, ?, 'undecided', ?, 'hunt', ?, ?, ?)",
                                     killId, species, coatId, (asNormal ?? asPossessed)!.Ammo, Verdict(asNormal), Verdict(asPossessed), (long)Clock());
                        continue;
                    }
                }
                // a harvest credited to its shooter, whose own events have no spawn of the animal, is judged by the spawn its page was made from, as at its harvest
                var (_, _, score, type, css, why) = ScoreKill(con, Parse(harvest), harvest, Trophy, AmmoCheck, PageSpawn(page));
                var reward = $"CSS Reward (Animal Id: {killId})";
                if (why is not null)
                {
                    var (taxidermied, mount) = (Py.Truthy(page["taxidermized"]), Mount(con, killId));
                    var keeps = taxidermied || mount is not null;
                    var change = scored ? "disqualify" : keeps ? flag == "kept" ? null : "flag" : flag is not null ? "unflag" : null;
                    if (change is null)
                        continue;
                    result.Add(new(id, killId, species, change, keeps ? "kept" : null, why.Reason, why.Ammo, why.Basis, coatId, stored, stored > 0 && (long)k[4] == 0,
                                   taxidermied, mount?.Lodge, mount?.Slot, KillPhoto(con, id, k[1], harvest) is not null,
                                   Convert.ToInt64(con.Scalar("SELECT COALESCE(SUM(amount), 0) FROM wallet_log WHERE account_id = $a AND summary = ?", reward))));
                    if (dryRun)
                        continue;
                    if (scored)
                    {
                        page["score"] = 0;
                        page["score_type"] = "";
                        con.Exec("UPDATE harvests SET body = ? WHERE account_id = $a AND kill_id = ?", Py.Dumps(page), killId);
                    }
                    if (keeps)
                        con.Exec("INSERT OR REPLACE INTO unethical_kills (account_id, kill_id, kind, species, coat, reason, ammo, basis, taxidermied, lodge, slot, lost_score, ts) VALUES ($a, ?, 'kept', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                                 killId, species, coatId, why.Reason, why.Ammo, why.Basis, taxidermied ? 1 : 0, mount?.Lodge, mount?.Slot, stored, (long)Clock());
                    else
                        con.Exec("DELETE FROM unethical_kills WHERE account_id = $a AND kill_id = ?", killId);
                    continue;
                }
                var requalify = !scored && Py.Truthy(score);
                if (flag is null && !requalify)
                    continue;
                var amount = requalify && con.Scalar("SELECT 1 FROM wallet_log WHERE account_id = $a AND summary = ?", reward) is null ? Math.Max(0, Py.RoundInt(css)) : 0;
                result.Add(new(id, killId, species, requalify ? "requalify" : "unflag", null, null, null, null, coatId, requalify ? Num(score) : 0, false, false, null, null, false, amount));
                if (dryRun)
                    continue;
                con.Exec("DELETE FROM unethical_kills WHERE account_id = $a AND kill_id = ?", killId);
                if (!requalify)
                    continue;
                page["score"] = C(score);
                page["score_type"] = type ?? "";
                con.Exec("UPDATE harvests SET body = ? WHERE account_id = $a AND kill_id = ?", Py.Dumps(page), killId);
                if (amount > 0 && AddCurrency(con, "gm", amount, reward) is { } balance)
                    con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = C(balance), ["type"] = "gm", ["update"] = 15 }));
            }
            con.Commit();
        }
        if (!dryRun && result.Any(r => r.Change is "disqualify" or "requalify"))
            DropBoards();
        return result;
    }

    /// <summary>The lodge (`lodges.n`) and slot (`slot_id`) of the account that hold the kill <paramref name="killId"/> (a slot's `trophy_id`); null when none does.</summary>
    static (long Lodge, long? Slot)? Mount(Db con, long killId) =>
        con.Rows("SELECT l.n, json_extract(s.value, '$.slot_id') FROM lodges l, json_each(l.node, '$.animal_slots') s WHERE l.account_id = $a AND CAST(json_extract(s.value, '$.trophy_id') AS INTEGER) = ?"
                 + " ORDER BY l.n LIMIT 1", killId).FirstOrDefault() is { } r ? ((long)r[0], r[1] as long?) : null;

    /// <summary>The spawn a harvest page was made from, as far as the page holds it (<see cref="HarvestPage"/>): species, coat, gender and weight.</summary>
    static JsonObject PageSpawn(JsonObject page) => new()
    {
        ["species"] = C(page["species"]), ["texture"] = C(page["texture_id"]), ["gender"] = C(page["gender"]), ["weight_grams"] = C(page["weight"]),
    };

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
