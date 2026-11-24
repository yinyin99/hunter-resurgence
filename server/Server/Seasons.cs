// The leaderboards by season (docs/server.md#leaderboards-and-seasons): every hunter's best harvests, sorted per species by score and by range, for one season or for all time (season 0).
// A harvest counts in the season its kill time falls in (the `seasons` table). Season 1 holds the restored harvests (the saved personal bests with their original time, else the restored trophies of a claimed account), Season 2 and
// later this server's own harvests (`harvests`); hidden harvests, refused ones (score 0) and banned accounts are left out. One row per hunter and species on each board.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The first season that holds this server's harvests; an earlier time (a harvest made before the first day) belongs here too, since Season 1 is the restored ones only.</summary>
    public const long FirstServerSeason = 2;

    /// <summary>A kill that may rank: `Rec` is its personal-best record (`{distance` in mm, `confirm_ts`, `weapon_id`, `animal_id`, `gender`, `texture`, `score`, `species}`), `Kill` its id here (0: restored),
    /// `Unsure` a restored rack that stays on the typical board though it may be a non-typical one (<see cref="RestoredBoard"/>).</summary>
    sealed record Cand(long Species, double Score, double Mm, JsonObject Rec, long Ts, long Season, long Kill, bool Unsure = false);

    /// <summary>One place on a board.</summary>
    public sealed record BoardRow(long Rank, long Account, string Handle, double Value, JsonNode? Weapon, long Ts, long Kill, long Animal);

    /// <summary>The kills of an account that may rank, with their seasons: the restored personal bests (the saved reply of season 0: each species' best score and best range, as the game saved them; the original kept
    /// the non-typical racks out of that reply, so it has typical rows only and they stay as saved), else (an archive claim has none) its restored trophies, listed and mounted, by score alone, each on the board of
    /// <see cref="RestoredBoard"/>; and this server's harvests that are not hidden and have a score (they carry their variant).
    /// ponytail: every view reads the hit events of each scored kill (a scan of the account's events each); store the last hit on the harvest when that gets slow.</summary>
    List<Cand> Candidates(Db con, IReadOnlyList<Store.Season> seasons)
    {
        var found = new List<Cand>();
        var saved = con.Rows("SELECT path, body FROM extras WHERE account_id = $a AND path LIKE 'Leaderboard!_personal!_!_%' ESCAPE '!'")
            .Where(r => System.Text.RegularExpressions.Regex.Match((string)r[0], @"__season_no=0\.json$").Success)
            .Select(r => JsonNode.Parse((string)r[1])).FirstOrDefault();
        foreach (var row in ((saved as JsonObject)?["data"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var key in (string[])["score", "distance"])
                if (row[key] is JsonObject rec && Py.IsNum(row["specie"]))
                {
                    var ts = (long)Num(rec["confirm_ts"]);
                    found.Add(new(Py.L(row["specie"]), Num(rec["score"]), Num(rec["distance"]), (JsonObject)rec.DeepClone(), ts, 1, 0));  // a restored kill is Season 1 whatever time its backup says
                }
        if (found.Count == 0)  // no saved bests (an archive claim): the restored trophy list and the animals mounted in the restored lodges, one row per trophy id, score only
            foreach (var row in Restored(con, "trophies", "/game/user/trophies").Concat(MountedRows(con)).OfType<JsonObject>().DistinctBy(r => Py.Str(r["id"])).Where(NotHidden))
                if (Py.IsNum(row["species"]) && Num(row["score"]) is > 0 and var score)
                {
                    var ts = Py.IsStr(row["timestamp"]) && DateTimeOffset.TryParse((string)row["timestamp"]!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeSeconds() : (long)Num(row["timestamp"]);
                    var (board, unsure) = RestoredBoard(Py.L(row["species"]), score);
                    var rec = new JsonObject  // no distance (it stays off the range boards) and no weapon
                    {
                        ["distance"] = 0, ["confirm_ts"] = ts, ["weapon_id"] = null, ["animal_id"] = C(row["id"]), ["gender"] = C(row["gender"]), ["texture"] = C(row["texture_id"]),
                        ["score"] = Py.Fixed(score, 4), ["species"] = board,
                    };
                    found.Add(new(board, score, 0, rec, ts, 1, 0, unsure));
                }
        foreach (var r in con.Rows("SELECT k.individual, k.harvest, h.body, k.kill_id FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                                   + " WHERE k.account_id = $a AND h.hidden = 0 ORDER BY k.kill_id"))
            try
            {
                var page = Parse(r[2]);
                if (Num(page["score"]) <= 0 || !Py.IsNum(page["species"])) continue;
                var last = KillHits(con, Py.FromSql(r[0]), (string)r[1]) is { Count: > 0 } hits ? hits[^1] : null;
                var ts = new DateTimeOffset(DateTime.ParseExact((string)page["timestamp"]!, "yyyy-MM-dd'T'HH:mm:ss'Z'", null,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)).ToUnixTimeSeconds();
                var rec = new JsonObject
                {
                    ["distance"] = Times10(G(last ?? new JsonObject(), "distance_cms", 0)), ["confirm_ts"] = ts,
                    ["weapon_id"] = C(last?["weapon_id"]), ["animal_id"] = (long)r[3], ["gender"] = C(page["gender"]), ["texture"] = C(page["texture_id"]),
                    ["score"] = Py.Fixed(Num(page["score"]), 4), ["species"] = C(page["species"]),
                };
                found.Add(new(Py.L(page["species"]), Num(page["score"]), Num(rec["distance"]), rec, ts, Math.Max(FirstServerSeason, Store.SeasonAt(seasons, ts)), (long)r[3]));
            }
            catch (Exception ex) when (ex is not Microsoft.Data.Sqlite.SqliteException)  // a value a player sent, of any kind: the kill is not counted
            {
                Console.WriteLine($"leaderboards: a kill is not counted: {ex.Message}");
            }
        return found;
    }

    /// <summary>The board species of a restored trophy row and whether that is a guess. The original's trophy rows give a non-typical rack as its typical species with nothing else to tell it by; a typical rack is scored
    /// up to the species' `max_score` at most (<see cref="Scoring.Variants"/>), so a score above it is the species' non-typical variant (the species + 100, where it has a row). At or above that variant's `min_score` a rack
    /// could be either: it stays typical and is unsure (counted, <see cref="ComputeBoards"/>); below it, it is typical for sure. A species with no variant is its own board.</summary>
    static (long Board, bool Unsure) RestoredBoard(long species, double score) =>
        species is >= 0 and < 100 && Scoring.Variants.TryGetValue((int)species, out var typical) && Scoring.Variants.TryGetValue((int)species + 100, out var nonTypical)
            ? score > typical.Hi ? (species + 100, false) : (species, score >= nonTypical.Lo)
            : (species, false);

    /// <summary>Each species' best score and best range among the kills of a season (0: all of them); the earlier kill wins a tie.</summary>
    static IEnumerable<(long Species, Cand Score, Cand Range)> Bests(IEnumerable<Cand> kills, long season) =>
        kills.Where(c => season == 0 || c.Season == season).OrderBy(c => c.Ts).GroupBy(c => c.Species)
            .Select(g => (g.Key, g.MaxBy(c => c.Score)!, g.MaxBy(c => c.Mm)!));

    /// <summary>`Leaderboard/personal` of an account for a season (0: all time): `{status: "done", data: [{specie, score, distance}]}`, one row per species with a kill in the season,
    /// `score` and `distance` the record kills (`{distance` in mm, `confirm_ts`, `weapon_id`, `animal_id`, `gender`, `texture`, `score` as text, `species}`).</summary>
    JsonObject PersonalBest(Db con, long season)
    {
        var data = new JsonArray();
        foreach (var (species, score, range) in Bests(Candidates(con, Store.Seasons()), season).OrderBy(b => b.Species))
            data.Add(new JsonObject { ["distance"] = range.Rec.DeepClone(), ["score"] = score.Rec.DeepClone(), ["specie"] = species });
        return new JsonObject { ["status"] = "done", ["data"] = data };
    }

    /// <summary>How long a computed set of boards is kept (15 minutes) and when <see cref="KeepBoardsWarm"/> replaces it (14 minutes after, so it is never missing).</summary>
    public static readonly TimeSpan BoardTtl = TimeSpan.FromMinutes(15), BoardRefresh = TimeSpan.FromMinutes(14);

    /// <summary>Every board of one computation: (range, season) with 0 for all time, each by species.</summary>
    sealed record CachedBoards(int Epoch, Lazy<Dictionary<(bool Range, long Season), SortedDictionary<long, List<BoardRow>>>> Boards, double Expires);

    static readonly SortedDictionary<long, List<BoardRow>> NoBoards = [];
    CachedBoards? boardCache;
    int boardEpoch;
    int unsureRestored = -1;

    /// <summary>How many restored trophies the last computation of the boards could not tell typical from non-typical (<see cref="RestoredBoard"/>); -1 before the first one. The operator finds it in the log at the start.</summary>
    public int UnsureRestored => Volatile.Read(ref unsureRestored);

    /// <summary>Drop the kept boards, for a change that must show at once: a season start, a deleted account, a hidden harvest. A set that is being computed meanwhile is not kept (it carries the epoch it started in).</summary>
    public void DropBoards()
    {
        Interlocked.Increment(ref boardEpoch);
        Volatile.Write(ref boardCache, null);
    }

    /// <summary>Start the next season (<see cref="Store.StartSeason"/>) and drop the kept boards: the ended season's are final now, the current one's are the new one's.</summary>
    public Store.Season? StartSeason(long now)
    {
        var started = Store.StartSeason(now);
        DropBoards();
        return started;
    }

    /// <summary>The kept set of boards; none, or an expired one: a new computation, which callers that miss at the same time share.</summary>
    CachedBoards KeptBoards()
    {
        while (true)
        {
            var (epoch, now, kept) = (Volatile.Read(ref boardEpoch), Clock(), Volatile.Read(ref boardCache));
            if (kept is not null && kept.Epoch == epoch && kept.Expires > now) return kept;
            var fresh = new CachedBoards(epoch, new(ComputeBoards), now + BoardTtl.TotalSeconds);
            if (Interlocked.CompareExchange(ref boardCache, fresh, kept) == kept) return fresh;
        }
    }

    /// <summary>The boards of a season (0: all time) by species id: the score board (`range` false) or the range board, every hunter with a best above 0 (a banned account and the recorded
    /// player's are left out), best first, a tie to the earlier kill, then the lower account id. The callers cut the top and must not change them: all boards come from one computation that is kept for <see cref="BoardTtl"/>
    /// and made again by <see cref="KeepBoardsWarm"/> before that, so a read waits only for the first computation after the start or after <see cref="DropBoards"/>; callers that miss at the same time share it.</summary>
    public SortedDictionary<long, List<BoardRow>> Boards(bool range, long season)
    {
        var kept = KeptBoards();
        try { return kept.Boards.Value.GetValueOrDefault((range, season)) ?? NoBoards; }
        catch { Interlocked.CompareExchange(ref boardCache, null, kept); throw; }  // a failed computation is not kept
    }

    /// <summary>Compute the boards again and replace the kept ones with them, off any request: the old ones answer until then. A computation that a <see cref="DropBoards"/> overtakes is thrown away. Without kept boards this is their first computation, the one the reads wait for.</summary>
    public void RefreshBoards()
    {
        if (Volatile.Read(ref boardCache) is not { } kept || kept.Epoch != Volatile.Read(ref boardEpoch) || kept.Expires <= Clock())
        {
            _ = Boards(false, 0);
            return;
        }
        var (epoch, expires) = (Volatile.Read(ref boardEpoch), Clock() + BoardTtl.TotalSeconds);
        var fresh = new CachedBoards(epoch, new(ComputeBoards()), expires);
        if (epoch == Volatile.Read(ref boardEpoch)) Volatile.Write(ref boardCache, fresh);
    }

    /// <summary>Compute the boards at the start and again every <paramref name="every"/>, so no request waits for a cold board (a failed round is tried again at the next one; the boards of a failed first one are computed by the first read).</summary>
    public async Task KeepBoardsWarm(TimeSpan every, CancellationToken stop)
    {
        using var tick = new PeriodicTimer(every);
        try
        {
            do
                try { RefreshBoards(); }
                catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine("leaderboards: " + e.Message); }  // whatever a round meets, the next one comes
            while (await tick.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>ponytail: scans every account once for all the boards (the per-player tables are read on one connection each, as <see cref="Store.GameBoard"/>); a harvest shows when the kept boards are replaced,
    /// store each hunter's best per species and season when the scan itself gets slow.</summary>
    Dictionary<(bool Range, long Season), SortedDictionary<long, List<BoardRow>>> ComputeBoards()
    {
        var seasons = Store.Seasons();
        List<(long Id, string Handle)> accounts;
        using (var con = Store.Open())
            accounts = [.. con.Rows("SELECT id, handle FROM accounts WHERE banned = 0 AND id IS NOT ? ORDER BY id", LocalAccount).Select(r => ((long)r[0], (string)r[1]))];
        var all = new Dictionary<(bool Range, long Season), SortedDictionary<long, List<(long Id, string Handle, Cand Kill)>>>();
        var unsure = 0;
        foreach (var (id, handle) in accounts)
        {
            using var con = Con(id);
            var kills = Candidates(con, seasons);
            unsure += kills.Count(k => k.Unsure);
            foreach (var season in (long[])[0, .. seasons.Select(s => s.No)])
                foreach (var (species, score, best) in Bests(kills, season))
                    foreach (var range in (bool[])[false, true])
                    {
                        var c = range ? best : score;
                        if ((range ? c.Mm : c.Score) <= 0) continue;
                        var board = all.TryGetValue((range, season), out var b) ? b : all[(range, season)] = [];
                        (board.TryGetValue(species, out var list) ? list : board[species] = []).Add((id, handle, c));
                    }
        }
        if (Interlocked.Exchange(ref unsureRestored, unsure) != unsure)  // at the start, and again when a restore or a claim changes it: not every round
            Console.WriteLine($"leaderboards: {unsure} restored trophies could be non-typical racks (their score fits both) and rank on the typical boards");
        return all.ToDictionary(kv => kv.Key, kv =>
        {
            var boards = new SortedDictionary<long, List<BoardRow>>();
            foreach (var (species, list) in kv.Value)
                boards[species] = [.. list.OrderByDescending(e => kv.Key.Range ? e.Kill.Mm : e.Kill.Score).ThenBy(e => e.Kill.Ts).ThenBy(e => e.Id)
                    .Select((e, i) => new BoardRow(i + 1, e.Id, e.Handle, kv.Key.Range ? e.Kill.Mm / 1000 : e.Kill.Score, e.Kill.Rec["weapon_id"], e.Kill.Ts, e.Kill.Kill, (long)Num(e.Kill.Rec["animal_id"])))];
            return boards;
        });
    }

    /// <summary>The Hall of Fame of an ended season: per species the best score and the best range; empty for season 0 and the current season.</summary>
    public List<(long Species, BoardRow? Score, BoardRow? Range)> HallOfFame(long season)
    {
        if (season == 0 || Store.Seasons().Last().No == season) return [];
        var (score, range) = (Boards(false, season), Boards(true, season));
        return [.. score.Keys.Union(range.Keys).Order().Select(s => (s, score.TryGetValue(s, out var a) ? a[0] : null, range.TryGetValue(s, out var b) ? b[0] : null))];
    }

    /// <summary>The name of a species by id for a board.</summary>
    public string SpeciesLabel(long species) => SpeciesName(species) ?? "#" + species.ToString(CultureInfo.InvariantCulture);

    /// <summary>The name of a weapon item ("" when unknown).</summary>
    public string WeaponName(JsonNode? weapon) => Lookup(items, weapon)?["name"] is { } w && Py.IsStr(w) ? (string)w! : "";

    // ---- the calls of the original's pages (Page_content/leaderboards_all, leaderboard_details, leaderboards_hall_of_fame, Leaderboard/current_personal), answered from the same boards

    /// <summary>A row of the original's boards: `{id, animal_id, rank, scoretype, score, scoreType, user: {id, handle, avatar}}`; `score` the score or the range in metres (the Hall of Fame's score a text); `scoretype` 1 score, 2 range, null in the Hall of Fame.</summary>
    JsonObject PageRow(BoardRow r, bool range, bool hall, string site) => new()
    {
        ["id"] = r.Animal, ["animal_id"] = r.Animal, ["rank"] = r.Rank, ["scoretype"] = hall ? null : range ? 2 : 1,
        ["score"] = !range && hall ? r.Value.ToString(CultureInfo.InvariantCulture) : r.Value, ["scoreType"] = null,
        ["user"] = new JsonObject { ["id"] = r.Account, ["handle"] = r.Handle, ["avatar"] = AvatarUrl(r.Account, site, game: true) },
    };

    JsonObject PageBoard(long species, List<BoardRow> rows, bool range, int top, string site) => new()
    {
        ["id"] = species, ["define"] = Py.Str(Lookup(this.species, (JsonNode)species)?["define"]).ToLowerInvariant(), ["name"] = Py.Str(Lookup(this.species, (JsonNode)species)?["name"] ?? SpeciesLabel(species)),
        ["scoretype"] = range ? 2 : 1, ["total"] = 1000, ["records"] = new JsonArray([.. rows.Take(top).Select(r => (JsonNode?)PageRow(r, range, false, site))]),
    };

    /// <summary>`Page_content/leaderboards_all` (`type` 1 score, 2 range): a block per species with a ranked hunter in the current season, its best 5.</summary>
    public byte[] BoardsAllReply(bool range, string site) =>
        Py.DumpBytes(new JsonArray([.. Boards(range, Store.Seasons().Last().No).Select(b => (JsonNode?)PageBoard(b.Key, b.Value, range, 5, site))]));

    /// <summary>`Page_content/leaderboard_details` (`type`, `species`): the species' best 100 of the current season, then the rows of <paramref name="caller"/>'s window past them (<see cref="Window"/>); null for a species the catalogue lacks.</summary>
    public byte[]? BoardDetailsReply(bool range, long species, string site, long caller)
    {
        if (Lookup(this.species, (JsonNode)species) is null) return null;
        var rows = Boards(range, Store.Seasons().Last().No).GetValueOrDefault(species) ?? [];
        var board = PageBoard(species, rows, range, BoardTop, site);
        var (from, count) = Window(rows.FirstOrDefault(r => r.Account == caller)?.Rank ?? 0);
        foreach (var r in rows.Skip(from).Take(count)) ((JsonArray)board["records"]!).Add(PageRow(r, range, false, site));
        board["position"] = null;  // the page asks Leaderboard/current_personal for the player's own place
        return Py.DumpBytes(board);
    }

    /// <summary>`Page_content/leaderboards_hall_of_fame` (`season_no`, default the season before the current one): `[{speciesId, score, range}]` of an ended season, a species with no winner left out; `[]` for the current season, all time and a season that does not exist.</summary>
    public byte[] HallOfFameReply(long? season, string site)
    {
        var seasons = Store.Seasons();
        var wanted = season ?? (seasons.Count > 1 ? seasons[^2].No : 0);
        var rows = seasons.Any(s => s.No == wanted) ? HallOfFame(wanted) : [];
        return Py.DumpBytes(new JsonArray([.. rows.Select(h => (JsonNode?)new JsonObject
        {
            ["speciesId"] = h.Species, ["score"] = h.Score is { } s ? PageRow(s, false, true, site) : null, ["range"] = h.Range is { } r ? PageRow(r, true, true, site) : null,
        })]));
    }

    /// <summary>`Leaderboard/current_personal` (`user_id`): the account's places on the boards of the current season, `[{species_id, score: {total, rank, animal_id, score}, distance_min: {…}}]`
    /// (`total` and `rank` the place); `[]` for a user id that is none or that <paramref name="caller"/> cannot see (<see cref="CanSee"/>).</summary>
    public byte[] CurrentPersonalReply(long caller, long userId)
    {
        if (PublicAccount(userId) is not { } account || !CanSee(caller, account)) return "[]"u8.ToArray();
        var season = Store.Seasons().Last().No;
        var (score, range) = (Boards(false, season), Boards(true, season));
        static JsonObject? Place(SortedDictionary<long, List<BoardRow>> boards, long species, long account) =>
            boards.GetValueOrDefault(species)?.FirstOrDefault(r => r.Account == account) is { } r
                ? new JsonObject { ["total"] = r.Rank, ["rank"] = r.Rank, ["animal_id"] = r.Animal, ["score"] = r.Value } : null;
        return Py.DumpBytes(new JsonArray([.. score.Keys.Union(range.Keys).Order().Select(species => (species, Score: Place(score, species, account), Range: Place(range, species, account)))
            .Where(p => p.Score is not null || p.Range is not null)
            .Select(p =>
            {
                var row = new JsonObject { ["species_id"] = p.species };
                if (p.Score is not null) row["score"] = p.Score;
                if (p.Range is not null) row["distance_min"] = p.Range;
                return (JsonNode?)row;
            })]));
    }

    /// <summary>An application reply with `season` the current season (a community server); anything that is no object stays as it is.</summary>
    public byte[] WithSeason(byte[] body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject reply)
            {
                reply["season"] = Store.Seasons().Last().No;
                return Py.DumpBytes(reply);
            }
        }
        catch (System.Text.Json.JsonException) { }  // a body that is no JSON object stays as it is
        return body;
    }
}
