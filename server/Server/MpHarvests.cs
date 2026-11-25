// The cleanup of the harvests a multiplayer game credited to the player who harvested the animal and not to the one who shot it last (#857): the operator's
// `admin mp-harvests`. docs/server.md, "Multiplayer". The live rule is <see cref="CreditOf"/>; the games are not kept after they end, so here the other accounts' stored events stand in for the game.
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The hits of another account that can be a harvest's last hit: the ones of the ten minutes before the harvest.</summary>
    const double MpHitWindow = 600;

    /// <summary>The fields of the spawn event (7) every player's game sends alike for one animal (its position and population key are the player's own): two records of one animal agree on the ones both carry.</summary>
    static readonly string[] SameAnimal = ["species", "gender", "texture", "weight_grams", "population", "special", "feature"];

    sealed record MpHarvest(long Account, long Kill, long? Species, double Individual, string Event3, JsonObject Spawn, long EventId, double Ts, long OwnLast, long Reserve, long Hunt, double Start, double End);

    sealed record MpHit(long Account, long Id, double Ts, double Individual, long? Species);

    /// <summary>`Net`: the hunt's events hold a network event (29, the game's port and network id), which only a game of several players sends.</summary>
    sealed record MpHunt(long Id, long? Reserve, double Start, double End, bool Net);

    static double? Dbl(object o) => o is long l ? (double?)l : o is double d ? d : null;

    /// <summary>Whether two spawn records are of one animal: both exist and every field of <see cref="SameAnimal"/> they both carry is equal.</summary>
    static bool SameSpawn(JsonObject a, JsonObject b) => a.Count > 0 && b.Count > 0 && SameAnimal.All(k => !(a.ContainsKey(k) && b.ContainsKey(k)) || Py.Eq(a[k], b[k]));

    /// <summary>Admin: move the stored harvests of multiplayer games to the player who shot the animal last. A harvest of an account is moved when another account's event 1 on the same `individual`
    /// (a hit names its animal's species as the catalogue's number plus one; a variant, 100 and up, counts as its typical species) came in the ten minutes before it, in a hunt of the same reserve that overlapped
    /// the harvester's and was still running at the harvest, after the harvester's own last hit on it, both hunts holding a network event (29: only a game of several players sends it), and both players' spawn records of the
    /// animal (event 7) agree (<see cref="SameAnimal"/>: an `individual` number is reused by other animals, which differ). The first harvest of such an animal moves; a later one, or one of an animal its shooter harvested himself,
    /// stays (counted in the total). A move is one transaction of both accounts: the page (taxidermy or skull bought with it too), the kill row and the events go to the shooter under a new kill id of his, a trophy mounted in the
    /// harvester's lodge is taken down, the score is judged again on the shooter's hits, the harvester's missions lose only the objectives that harvest had finished and no other event meets, the shooter's missions and
    /// challenges are checked again (the confirmation of #718), the hunterscore of both is made again, and a record per account is kept in `mission_rollbacks`. A failed move is undone whole and counted.
    /// **The gm$ are not moved**: what the harvester was paid stays his and the shooter gets none (a wallet row of 0 keeps `admin rescore` from paying it). One line per moved harvest (kill, species, from, to, gm$) with a
    /// line under it for each mission objective reopened and each mission activated to stay unlocked, then a total. Without `apply` nothing is written.
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
                harvests.Add(new(account, (long)k[0], k[1] as long?, Py.D(e3["individual"]), (string)k[2], KillEvents(con, e3["individual"], (string)k[2]).E7, eventId, ts, own, reserve, hunt.Id, hunt.Start, hunt.End));
            }
        }
        var (byAnimal, harvested) = (hits.ToLookup(x => x.Individual), e3s.ToLookup(x => (x.Account, x.Individual), x => x.Id));
        var (taken, moved, kept, failed, reopenedTotal, gmsStay) = (new HashSet<(long, long)>(), 0, 0, 0, 0, 0L);
        var gone = accounts.ToDictionary(a => a, _ => new HashSet<long>());  // the harvest events that moved away from each account so far
        foreach (var h in harvests.OrderBy(h => h.EventId))
        {
            if (!hunts[h.Account].Any(u => u.Id == h.Hunt && u.Net))
                continue;  // not a game of several players
            // the newest hit of another account in a running hunt of the reserve (a game), after the harvester's own last hit, on an animal both players' records agree is the same
            var found = byAnimal[h.Individual].Where(x => x.Account != h.Account && x.Id > h.OwnLast && x.Id < h.EventId && x.Ts >= h.Ts - MpHitWindow && x.Ts <= h.Ts && (x.Species is null || h.Species is null || x.Species == h.Species % 100 + 1))
                .Select(x => (Hit: x, Hunt: hunts[x.Account].FirstOrDefault(u => u.Net && u.Reserve == h.Reserve && u.Start <= x.Ts && u.End >= h.Ts - HEARTBEAT_TIMEOUT && u.Start <= h.End && u.End >= h.Start)))
                .Where(x => x.Hunt is not null).OrderByDescending(x => x.Hit.Id).FirstOrDefault(x => SameSpawn(SpawnOf(x.Hit), h.Spawn));
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
            List<(long Mission, long Was, List<long> Objectives)> reopened;
            List<long> activated;
            long newKill = 0;
            string? failure = null;
            try
            {
                if (apply)
                    (newKill, reopened, activated) = MoveMpHarvest(h, shooter, shooterHunt.Id, gms, gone[h.Account]);
                else
                    (reopened, activated) = MpReopened(h, gone[h.Account]);
            }
            catch (Exception ex)
            {
                (failure, reopened, activated) = (ex.Message, [], []);
            }
            if (failure is not null)
            {
                failed++;
                yield return $"failed to move {line}: {failure} (nothing of it written)";
                continue;
            }
            gone[h.Account].Add(h.EventId);
            moved++;
            gmsStay += gms;
            yield return (apply ? "moved " : "would move ") + line + (apply ? $" (kill {newKill} of account {shooter})" : "");
            foreach (var (mission, was, objectives) in reopened)
            {
                reopenedTotal += objectives.Count;
                yield return $"  {(apply ? "reopened" : "would reopen")} account {h.Account}: mission {mission} ({(was == 3 ? "completed, active again" : "active")}) objectives {string.Join(", ", objectives)}";
            }
            if (activated.Count > 0)
                yield return $"  {(apply ? "activated" : "would activate")} account {h.Account}, so that nobody is locked out: missions {string.Join(", ", activated)}";
        }
        if (apply && moved > 0)
            DropBoards();
        yield return $"{(apply ? "moved" : "would move")} {moved} harvests (gm$ {gmsStay} stay with the harvesters, none are moved), {(apply ? "reopened" : "would reopen")} {reopenedTotal} mission objectives; kept {kept} second harvests"
            + (failed > 0 ? $"; {failed} failed and undone" : "") + (apply ? "" : " (dry run: nothing written, --apply writes)");
    }

    /// <summary>The shooter's own spawn record of the animal a hit was made on: the latest event 7 of its individual before it ({} when there is none).</summary>
    JsonObject SpawnOf(MpHit hit)
    {
        using var con = Con(hit.Account);
        return con.Rows("SELECT body FROM events WHERE account_id = $a AND event = 7 AND individual = ? AND id < ? ORDER BY id DESC LIMIT 1", hit.Individual, hit.Id).FirstOrDefault() is { } row ? Parse(row[0]) : new JsonObject();
    }

    /// <summary>The mission objectives <see cref="MoveMpHarvests"/> would reopen for the harvester of `h`, read only.</summary>
    (List<(long Mission, long Was, List<long> Objectives)> Reopened, List<long> Activated) MpReopened(MpHarvest h, HashSet<long> gone)
    {
        if (missions.Count == 0)
            return ([], []);
        using var con = Con(h.Account);
        var (reopened, activated, _) = RollbackAccount(con, false, h.EventId, gone);
        return (reopened, activated);
    }

    /// <summary>One harvest of <see cref="MoveMpHarvests"/>, in one transaction that spans both accounts (<see cref="Db.Sharing"/>): the harvester's mission rollback for what the harvest finished, its page, kill row and events
    /// to the shooter's account under a new kill id (the number is chosen by <see cref="NextKillId"/>, which also avoids the one the harvester's gm$ row names), the mounted trophy taken down, the score judged again on the
    /// shooter's hits, the 0 gm$ row, the shooter's missions and challenges, both hunterscores and a record per account. Any failure rolls all of it back. The shooter's kill id and the objectives reopened.</summary>
    (long Kill, List<(long Mission, long Was, List<long> Objectives)> Reopened, List<long> Activated) MoveMpHarvest(MpHarvest h, long to, long toHunt, long gms, HashSet<long> gone)
    {
        using var con = Con(h.Account);
        con.Begin();
        var theirs = con.Sharing(to);
        var (reopened, activated, _) = missions.Count == 0 ? ([], [], 0) : RollbackAccount(con, true, h.EventId, gone);
        var kill = NextKillId(theirs);
        var page = Parse(con.Scalar("SELECT body FROM harvests WHERE account_id = $a AND kill_id = ?", h.Kill)!);
        page["expedition_id"] = toHunt;
        var e22 = con.Scalar("SELECT MAX(id) FROM events WHERE account_id = $a AND event = 22 AND individual = ?1 AND id < ?2 AND id > COALESCE("
                             + "(SELECT MAX(id) FROM events WHERE account_id = $a AND event = 7 AND individual = ?1 AND id < ?2), 0)", h.Individual, h.EventId) as long?;
        con.Exec("UPDATE kills SET account_id = ?1, kill_id = ?2 WHERE account_id = $a AND kill_id = ?3", to, kill, h.Kill);
        con.Exec("UPDATE harvests SET account_id = ?1, kill_id = ?2, body = ?3 WHERE account_id = $a AND kill_id = ?4", to, kill, Py.Dumps(page), h.Kill);
        con.Exec("UPDATE events SET account_id = ?1 WHERE account_id = $a AND id IN (?2, ?3)", to, h.EventId, e22 ?? h.EventId);  // the trophy measurement goes with the harvest
        foreach (var l in con.Rows("SELECT n, node FROM lodges WHERE account_id = $a"))  // a mounted trophy is taken down
            if (JsonNode.Parse((string)l[1]) is JsonObject node && node["animal_slots"] is JsonArray slots
                && slots.Where(s => s is JsonObject o && Py.Str(o["trophy_id"]) == h.Kill.ToString()).ToList() is { Count: > 0 } mounted)
            {
                foreach (var slot in mounted)
                    slots.Remove(slot);
                con.Exec("UPDATE lodges SET node = ? WHERE account_id = $a AND n = ?", Py.Dumps(node), l[0]);
            }
        con.Exec("UPDATE expeditions SET stats = NULL WHERE account_id = $a AND id = ?", h.Hunt);
        var e3 = Parse(h.Event3);
        var (_, disqualified, score, type, _) = ScoreKill(theirs, e3, h.Event3, Trophy, AmmoCheck);
        page = Parse(theirs.Scalar("SELECT body FROM harvests WHERE account_id = $a AND kill_id = ?", kill)!);
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
        HunterscoreIn(con, h.Account, true);
        HunterscoreIn(theirs, to, true);
        var record = new JsonObject { ["mpHarvest"] = new JsonObject { ["kill"] = h.Kill, ["species"] = h.Species, ["from"] = h.Account, ["to"] = to, ["newKill"] = kill, ["gms"] = gms } };
        theirs.Exec("INSERT INTO mission_rollbacks (account_id, ts, record) VALUES ($a, ?, ?)", (long)Clock(), Py.Dumps(record));
        record["reopened"] = new JsonArray([.. reopened.Select(r => (JsonNode?)new JsonObject { ["mission"] = r.Mission, ["was"] = r.Was == 3 ? "completed" : "active", ["objectives"] = List(r.Objectives) })]);
        record["activated"] = List(activated);
        con.Exec("INSERT INTO mission_rollbacks (account_id, ts, record) VALUES ($a, ?, ?)", (long)Clock(), Py.Dumps(record));
        con.Commit();
        return (kill, reopened, activated);
    }
}
