// The cleanup of the harvests a multiplayer game credited to the player who harvested the animal and not to the one who shot it last (#857): the operator's
// `admin mp-harvests`. docs/server.md, "Multiplayer". The live rule is <see cref="CreditOf"/>; the games are not kept after they end, so here the other accounts' stored events stand in for the game.
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The hits of another account that can be a harvest's last hit: the ones of the ten minutes before the harvest.</summary>
    const double MpHitWindow = 600;

    sealed record MpHarvest(long Account, long Kill, long? Species, double Individual, string Event3, long EventId, double Ts, long OwnLast, long Reserve, long Hunt, double Start, double End);

    sealed record MpHit(long Account, long Id, double Ts, double Individual, long? Species);

    /// <summary>`Net`: the hunt's events hold a network event (29, the game's port and network id), which only a game of several players sends.</summary>
    sealed record MpHunt(long Id, long? Reserve, double Start, double End, bool Net);

    static double? Dbl(object o) => o is long l ? (double?)l : o is double d ? d : null;

    /// <summary>Admin: move the stored harvests of multiplayer games to the player who shot the animal last. A harvest of an account is moved when another account's event 1 on the same `individual`
    /// (of the same species, when the hit names it) came in the ten minutes before it, in a hunt of the same reserve that overlapped the harvester's and was still running at the harvest (both hunts sent a network event, 29, so were games of several players), after the
    /// harvester's own last hit on it. The first harvest of such an animal moves; a later one, or one of an animal its shooter harvested himself, stays (counted in the total).
    /// A moved harvest takes its page (taxidermy or skull bought with it too), its kill row and its events to the shooter, under a new kill id of the shooter's, and the trophy mounted in the harvester's lodge is taken down.
    /// Its score is judged again on the shooter's hits. **The gm$ are not moved**: what the harvester was paid stays his and the shooter gets none (a wallet row of 0 keeps `admin rescore` from paying it).
    /// Missions and challenges are checked again for the shooter (the confirmation of #718), the harvester's missions by the rollback of <see cref="RollbackMissions"/>, the hunterscore of both is made again, and a record
    /// per account is kept in `mission_rollbacks`. One line per moved harvest (kill, species, from, to, gm$), then a total. Without `apply` nothing is written.
    /// ponytail: a challenge the harvester completed with the harvest stays completed (a completed challenge keeps no event); the shooter's challenges see the shooter's latest events as the harvest's hunt.</summary>
    public IEnumerable<string> MoveMpHarvests(bool apply)
    {
        List<long> accounts;
        using (var all = Store.Open())
            accounts = all.Rows("SELECT id FROM accounts ORDER BY id").Select(r => (long)r[0]).ToList();
        var (harvests, hits, hunts, e3s) = (new List<MpHarvest>(), new List<MpHit>(), new Dictionary<long, List<MpHunt>>(), new List<(long Account, double Individual, long Id)>());
        foreach (var account in accounts)
        {
            using var con = Con(account);
            var network = con.Rows("SELECT id FROM events WHERE account_id = $a AND event = 29").Select(r => (long)r[0]).ToList();
            var rows = con.Rows("SELECT id, reserve, start_ts, COALESCE(end_ts, COALESCE(last_seen, start_ts) + ?), COALESCE(first_event, 0) FROM expeditions WHERE account_id = $a ORDER BY id", HEARTBEAT_TIMEOUT);
            var mine = hunts[account] = [.. rows.Select((r, i) => new MpHunt((long)r[0], r[1] as long?, Convert.ToDouble(r[2]), Convert.ToDouble(r[3]),  // a hunt's events lie between its first event and the next hunt's
                network.Any(n => n > (long)r[4] && (i + 1 == rows.Count || n < (long)rows[i + 1][4]))))];
            hits.AddRange(con.Rows("SELECT id, ts, individual, json_extract(body, '$.species') FROM events WHERE account_id = $a AND event = 1 AND ts IS NOT NULL AND individual IS NOT NULL")
                .Where(r => Dbl(r[2]) is not null).Select(r => new MpHit(account, (long)r[0], Convert.ToDouble(r[1]), Dbl(r[2])!.Value, Dbl(r[3]) is { } s ? (long?)s : null)));
            e3s.AddRange(con.Rows("SELECT id, individual FROM events WHERE account_id = $a AND event = 3 AND individual IS NOT NULL")
                .Where(r => Dbl(r[1]) is not null).Select(r => (account, Dbl(r[1])!.Value, (long)r[0])));
            foreach (var k in con.Rows("SELECT k.kill_id, k.species, k.harvest, h.body FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id WHERE k.account_id = $a ORDER BY k.kill_id"))
            {
                JsonObject e3, page;
                try { (e3, page) = (Parse(k[2]), Parse(k[3])); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or InvalidCastException) { continue; }
                if (con.Rows("SELECT id, ts FROM events WHERE account_id = $a AND event = 3 AND body = ? ORDER BY id DESC LIMIT 1", k[2]).FirstOrDefault() is not { } ev || Dbl(ev[1]) is not { } ts
                    || !Py.IsNum(e3["individual"]) || !Py.IsNum(page["expedition_id"]) || mine.Find(u => u.Id == Py.L(page["expedition_id"])) is not { Reserve: { } reserve } hunt)
                    continue;
                var eventId = (long)ev[0];
                var own = con.Scalar("SELECT MAX(id) FROM events WHERE account_id = $a AND event = 1 AND individual = ?1 AND id < ?2 AND id > COALESCE("
                                     + "(SELECT MAX(id) FROM events WHERE account_id = $a AND event = 7 AND individual = ?1 AND id < ?2), 0)", e3["individual"], eventId) is long last ? last : 0L;
                harvests.Add(new(account, (long)k[0], k[1] as long?, Py.D(e3["individual"]), (string)k[2], eventId, ts, own, reserve, hunt.Id, hunt.Start, hunt.End));
            }
        }
        var (byAnimal, harvested) = (hits.ToLookup(x => x.Individual), e3s.ToLookup(x => (x.Account, x.Individual), x => x.Id));
        var (taken, moved, kept, gmsStay) = (new HashSet<(long, long)>(), 0, 0, 0L);
        var (records, touched) = (new Dictionary<long, JsonObject>(), new SortedSet<long>());
        foreach (var h in harvests.OrderBy(h => h.EventId))
        {
            // the newest hit of another account that was in a running hunt of the reserve, after the harvester's own last hit
            if (!hunts[h.Account].Any(u => u.Id == h.Hunt && u.Net))
                continue;  // not a game of several players
            var found = byAnimal[h.Individual].Where(x => x.Account != h.Account && x.Id > h.OwnLast && x.Id < h.EventId && x.Ts >= h.Ts - MpHitWindow && x.Ts <= h.Ts && (x.Species is null || h.Species is null || x.Species == h.Species % 100 + 1))
                .Select(x => (Hit: x, Hunt: hunts[x.Account].FirstOrDefault(u => u.Net && u.Reserve == h.Reserve && u.Start <= x.Ts && u.End >= h.Ts - HEARTBEAT_TIMEOUT && u.Start <= h.End && u.End >= h.Start)))
                .Where(x => x.Hunt is not null).OrderByDescending(x => x.Hit.Id).FirstOrDefault();
            if (found.Hunt is not { } shooterHunt)
                continue;
            var shooter = found.Hit.Account;
            if (harvested[(shooter, h.Individual)].Any(id => id > found.Hit.Id && id != h.EventId) || !taken.Add((shooter, found.Hit.Id)))
            {
                kept++;  // the shooter harvested it himself, or an earlier harvest of it moved to him
                continue;
            }
            long gms;
            using (var con = Con(h.Account))
                gms = Convert.ToInt64(con.Scalar("SELECT COALESCE(SUM(amount), 0) FROM wallet_log WHERE account_id = $a AND summary = ?", $"CSS Reward (Animal Id: {h.Kill})"));
            var line = $"kill {h.Kill}, species {h.Species}, from account {h.Account} to account {shooter}, gm$ {gms} stay with {h.Account}";
            moved++;
            gmsStay += gms;
            if (apply)
            {
                var kill = MoveMpHarvest(h, shooter, shooterHunt.Id);
                foreach (var (account, key) in new[] { (h.Account, "moved"), (shooter, "received") })
                {
                    var record = records.TryGetValue(account, out var r) ? r : records[account] = new JsonObject();
                    (record[key] as JsonArray ?? (JsonArray)(record[key] = new JsonArray())).Add(new JsonObject { ["kill"] = h.Kill, ["species"] = h.Species, ["from"] = h.Account, ["to"] = shooter, ["newKill"] = kill, ["gms"] = gms });
                    touched.Add(account);
                }
                line = "moved " + line + $" (kill {kill} of account {shooter})";
            }
            else
                line = "would move " + line;
            yield return line;
        }
        if (apply)
        {
            foreach (var account in records.Where(r => r.Value["moved"] is not null).Select(r => r.Key))  // the harvesters' progress that no event meets any more is reopened, as by `missions-rollback`
                foreach (var line in RollbackMissions(true, account))
                    yield return line;
            foreach (var account in touched)
            {
                using var con = Con(account);
                UpdateHunterscore(con, account);
                con.Exec("INSERT INTO mission_rollbacks (account_id, ts, record) VALUES ($a, ?, ?)", (long)Clock(), Py.Dumps(records[account]));
            }
            if (moved > 0)
                DropBoards();
        }
        yield return $"{(apply ? "moved" : "would move")} {moved} harvests (gm$ {gmsStay} stay with the harvesters, no wallet changes); kept {kept} second harvests"
            + (apply ? "" : " (dry run: nothing written, --apply writes)");
    }

    /// <summary>One harvest of <see cref="MoveMpHarvests"/>: its rows go from the harvester's account to the shooter's (the harvester's side in one transaction, then the shooter's), the shooter's kill id returned.</summary>
    long MoveMpHarvest(MpHarvest h, long to, long toHunt)
    {
        long kill;
        using (var theirs = Con(to))
            kill = NextKillId(theirs);
        using (var mine = Con(h.Account))
        {
            mine.Begin();
            var page = Parse(mine.Scalar("SELECT body FROM harvests WHERE account_id = $a AND kill_id = ?", h.Kill)!);
            page["expedition_id"] = toHunt;
            var e22 = mine.Scalar("SELECT MAX(id) FROM events WHERE account_id = $a AND event = 22 AND individual = ?1 AND id < ?2 AND id > COALESCE("
                                  + "(SELECT MAX(id) FROM events WHERE account_id = $a AND event = 7 AND individual = ?1 AND id < ?2), 0)", h.Individual, h.EventId) as long?;
            mine.Exec("UPDATE kills SET account_id = ?1, kill_id = ?2 WHERE account_id = $a AND kill_id = ?3", to, kill, h.Kill);
            mine.Exec("UPDATE harvests SET account_id = ?1, kill_id = ?2, body = ?3 WHERE account_id = $a AND kill_id = ?4", to, kill, Py.Dumps(page), h.Kill);
            mine.Exec("UPDATE events SET account_id = ?1 WHERE account_id = $a AND id IN (?2, ?3)", to, h.EventId, e22 ?? h.EventId);  // the trophy measurement goes with the harvest
            foreach (var l in mine.Rows("SELECT n, node FROM lodges WHERE account_id = $a"))  // a mounted trophy is taken down
                if (JsonNode.Parse((string)l[1]) is JsonObject node && node["animal_slots"] is JsonArray slots
                    && slots.Where(s => s is JsonObject o && Py.Str(o["trophy_id"]) == h.Kill.ToString()).ToList() is { Count: > 0 } mounted)
                {
                    foreach (var slot in mounted)
                        slots.Remove(slot);
                    mine.Exec("UPDATE lodges SET node = ? WHERE account_id = $a AND n = ?", Py.Dumps(node), l[0]);
                }
            mine.Exec("UPDATE expeditions SET stats = NULL WHERE account_id = $a AND id = ?", h.Hunt);
            mine.Commit();
        }
        using (var theirs = Con(to))
        {
            theirs.Begin();
            var e3 = Parse(h.Event3);
            var (_, disqualified, score, type, _) = ScoreKill(theirs, e3, h.Event3, Trophy, AmmoCheck);
            var page = Parse(theirs.Scalar("SELECT body FROM harvests WHERE account_id = $a AND kill_id = ?", kill)!);
            if (disqualified)
            {
                page["score"] = 0;  // a hit of the shooter's with an ammunition not permitted on the species
                page["score_type"] = "";
            }
            else if (Py.Truthy(score))
            {
                page["score"] = C(score);
                page["score_type"] = type ?? "";
            }
            theirs.Exec("UPDATE harvests SET body = ? WHERE account_id = $a AND kill_id = ?", Py.Dumps(page), kill);
            var balance = theirs.Scalar("SELECT amount FROM wallet WHERE account_id = $a AND currency = 'gm'");
            theirs.Exec("INSERT INTO wallet_log (account_id, ts, currency, amount, after, summary, item_ids) VALUES ($a, ?, 'gm', 0, ?, ?, '[]')",
                        (long)Clock(), balance is { } b && Dbl(b) is { } d ? d : 0.0, $"CSS Reward (Animal Id: {kill})");  // paid to the harvester: a 0 row, so that `rescore` does not pay it again
            theirs.Exec("UPDATE expeditions SET stats = NULL WHERE account_id = $a AND id = ?", toHunt);
            ChallengeEvent(theirs, e3);
            if (missions.Count > 0)
                ConfirmAccount(theirs);
            theirs.Commit();
        }
        return kill;
    }
}
